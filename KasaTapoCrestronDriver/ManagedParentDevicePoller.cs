// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE file in the project root for full license information.

using Crestron.DeviceDrivers.EntityModel.Logging;

namespace KasaTapoCrestronDriver;

/// <summary>
/// Owns the single shared polling loop for one physical Kasa/Tapo hub, on behalf of every
/// registered <see cref="IKasaHubChildEntity"/> child (sensor/button) materialized for that hub.
/// Hub children never poll themselves - see <see cref="IKasaHubChildEntity"/> - so without this
/// coordinator every child would independently call <c>device.UpdateAsync</c> on its own timer,
/// each redundantly re-fetching the *entire* hub child list even though all children of one hub
/// already share a single connected <see cref="KasaDevice"/> (via
/// <see cref="Discover.GetOrConnectSharedAsync"/>). This class instead connects once, polls once
/// per tick, and pushes each child its own slice of the resulting child list.
///
/// The poll cadence is taken from the devices themselves - the shortest
/// <c>ChildDevice.ReportMode.ReportInterval</c> across this hub's children - since polling faster
/// than the children actually refresh their state cannot surface anything new. The configured
/// <see cref="IPlatformSharedConfiguration.SensorPollInterval"/> is used only as a fallback until
/// a child has reported its interval.
///
/// Lifetime is reference-counted by registration: the poller connects and starts polling when its
/// first child registers, and stops/releases its connection reference when its last child
/// unregisters. It does not dispose the shared <see cref="KasaDevice"/> itself, since that instance
/// is owned by the <see cref="Discover"/> connection cache and may still be in use by sibling
/// button/light/outlet children of the same hub.
/// </summary>
internal sealed class ManagedParentDevicePoller
	{
	private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds (20);

	// A hub that is unreachable/unresponsive fails every poll at the full ConnectTimeout cost, so
	// retrying at the normal SensorPollInterval cadence (as low as a few seconds) turns into a
	// near-continuous stream of full reconnect/handshake attempts against a hub that may only
	// tolerate one active session - this was observed to make the hub (and even unrelated clients
	// like the test console) unable to reach it at all. Back off exponentially on consecutive
	// failures, capped at MaxBackoff, and reset back to the configured SensorPollInterval as soon
	// as a poll succeeds again.
	private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes (2);

	private readonly object _gate = new ();
	private readonly Dictionary<string, IKasaHubChildEntity> _children = new (StringComparer.OrdinalIgnoreCase);
	private readonly IPlatformSharedConfiguration _sharedConfiguration;
	private readonly DriverControllerLogger? _logger;
	private readonly string _driverLogId;
	private readonly string _hostKey;
	private readonly CancellationTokenSource _lifetimeCancellationSource = new ();

	private DeviceConfiguration _configuration;
	private KasaDevice? _connectedDevice;
	private int _pollingGeneration;
	private Task? _pollingTask;
	private bool _disposed;
	private int _consecutiveFailureCount;

	public ManagedParentDevicePoller (
		string hostKey,
		DeviceConfiguration configuration,
		IPlatformSharedConfiguration sharedConfiguration,
		DriverControllerLogger? logger,
		string driverLogId)
		{
		_hostKey = hostKey;
		_configuration = configuration;
		_sharedConfiguration = sharedConfiguration;
		_logger = logger;
		_driverLogId = driverLogId;
		}

	public void UpdateConfiguration (DeviceConfiguration configuration)
		{
		lock (_gate)
			{
			_configuration = configuration;
			}
		}

	/// <summary>
	/// Registers a hub child entity with this poller. Polling is driven entirely by event
	/// subscription state (see <see cref="IKasaHubChildEntity.HasEventSubscribers"/>), so
	/// registering a child does not by itself start polling: this poller subscribes to the child's
	/// <see cref="IKasaHubChildEntity.EventSubscribersChanged"/> and begins polling as soon as that
	/// child (or any sibling) actually has a subscriber, and stops again when the last one goes
	/// away.
	/// </summary>
	public void RegisterChild (IKasaHubChildEntity child)
		{
		lock (_gate)
			{
			if (_disposed)
				{
				return;
				}

			if (_children.TryGetValue (child.ChildId, out IKasaHubChildEntity? existingChild))
				{
				existingChild.EventSubscribersChanged -= HandleChildEventSubscribersChanged;
				}

			_children[child.ChildId] = child;
			child.EventSubscribersChanged += HandleChildEventSubscribersChanged;
			}

		LogInfo ($"RegisterChild: childId='{child.ChildId}', totalChildren={_children.Count}.");

		EvaluatePollingState ("RegisterChild");
		}

	public void UnregisterChild (string childId)
		{
		lock (_gate)
			{
			if (_children.TryGetValue (childId, out IKasaHubChildEntity? existingChild))
				{
				existingChild.EventSubscribersChanged -= HandleChildEventSubscribersChanged;
				}

			_children.Remove (childId);
			}

		LogInfo ($"UnregisterChild: childId='{childId}', remainingChildren={_children.Count}.");

		EvaluatePollingState ("UnregisterChild");
		}

	private void HandleChildEventSubscribersChanged ()
		{
		EvaluatePollingState ("EventSubscribersChanged");
		}

	/// <summary>
	/// The single place that decides whether this hub should currently be polled. Polling runs if
	/// and only if at least one registered child has at least one event subscriber, so the loop
	/// starts the instant the first subscriber appears and stops the instant the last one goes
	/// away.
	/// </summary>
	private void EvaluatePollingState (string context)
		{
		bool shouldPoll;
		bool isPolling;
		lock (_gate)
			{
			if (_disposed)
				{
				return;
				}

			shouldPoll = _children.Values.Any (static child => child.HasEventSubscribers);
			isPolling = _pollingTask is not null;
			}

		if (shouldPoll == isPolling)
			{
			return;
			}

		if (shouldPoll)
			{
			LogInfo ($"EvaluatePollingState ({context}): a child now has event subscribers; starting polling.");
			StartPolling ();
			return;
			}

		LogInfo ($"EvaluatePollingState ({context}): no child has any event subscriber; stopping polling.");
		Stop ();
		}

	public void ApplyRuntimeConfiguration (PlatformSharedConfigurationSnapshot previousConfiguration, PlatformSharedConfigurationSnapshot currentConfiguration)
		{
		if (_disposed)
			{
			return;
			}

		if (previousConfiguration.SensorPollInterval == currentConfiguration.SensorPollInterval)
			{
			return;
			}

		lock (_gate)
			{
			// Only an already-running loop needs to be restarted to pick up the new cadence; if
			// nothing is subscribed the loop is (correctly) stopped and must stay that way.
			if (_pollingTask is null)
				{
				return;
				}

			// The configured interval is only a fallback - once the hub's children have reported
			// their own cadence that is what paces polling, so a configuration change is moot.
			if (_deviceReportedInterval is not null)
				{
				return;
				}
			}

		StartPolling ();
		}

	private void StartPolling ()
		{
		int generation = Interlocked.Increment (ref _pollingGeneration);
		lock (_gate)
			{
			_pollingTask = RunPollingCycleAsync (generation, immediateFirstPoll: true);
			}
		}

	private async Task RunPollingCycleAsync (int generation, bool immediateFirstPoll)
		{
		try
			{
			if (!immediateFirstPoll)
				{
				TimeSpan delay = ComputeNextDelay ();
				await Task.Delay (delay, _lifetimeCancellationSource.Token).ConfigureAwait (false);
				}

			if (_disposed || generation != Volatile.Read (ref _pollingGeneration))
				{
				return;
				}

			await PollOnceAsync (_lifetimeCancellationSource.Token).ConfigureAwait (false);

			if (_disposed || generation != Volatile.Read (ref _pollingGeneration))
				{
				return;
				}

			lock (_gate)
				{
				if (generation != Volatile.Read (ref _pollingGeneration))
					{
					return;
					}

				_pollingTask = RunPollingCycleAsync (generation, immediateFirstPoll: false);
				}
			}
		catch (OperationCanceledException)
			{
			}
		catch (Exception ex)
			{
			_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Hub poller '{_hostKey}' polling loop failed: {ex}");
			}
		}

	/// <summary>
	/// The base cadence for this hub, derived from the devices themselves: hub children report how
	/// often they actually push new state (<c>ChildDevice.ReportMode.ReportInterval</c>, in
	/// seconds), so polling any faster than the shortest such interval across this hub's children
	/// can only ever re-read values the devices have not refreshed yet - pure wasted network and
	/// hub load. Updated after each successful poll by <see cref="UpdateDeviceReportedInterval"/>;
	/// null until at least one child reports an interval, in which case the configured
	/// <see cref="IPlatformSharedConfiguration.SensorPollInterval"/> is used as the fallback.
	/// </summary>
	private TimeSpan? _deviceReportedInterval;

	/// <summary>
	/// Records the shortest <c>report_interval</c> reported by any of this hub's children on the
	/// most recent successful poll, so <see cref="ComputeNextDelay"/> can pace polling to match how
	/// fast the devices actually produce new data.
	/// </summary>
	private void UpdateDeviceReportedInterval (KasaDevice device, List<IKasaHubChildEntity> childrenSnapshot)
		{
		int? shortestSeconds = null;
		foreach (IKasaHubChildEntity child in childrenSnapshot)
			{
			try
				{
				ChildDevice? childDevice = device.GetChildDevice (child.ChildId);
				int? reportInterval = childDevice?.ReportMode.ReportInterval;
				if (reportInterval is not int intervalSeconds || intervalSeconds <= 0)
					{
					continue;
					}

				if (shortestSeconds is null || intervalSeconds < shortestSeconds)
					{
					shortestSeconds = intervalSeconds;
					}
				}
			catch (Exception ex)
				{
				_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Hub poller '{_hostKey}' failed to read report interval for child '{child.ChildId}': {ex}");
				}
			}

		TimeSpan? resolvedInterval = shortestSeconds is int seconds ? TimeSpan.FromSeconds (seconds) : null;
		lock (_gate)
			{
			if (resolvedInterval == _deviceReportedInterval)
				{
				return;
				}

			_deviceReportedInterval = resolvedInterval;
			}

		LogInfo (resolvedInterval is TimeSpan interval
			? $"UpdateDeviceReportedInterval: '{_hostKey}' shortest device-reported interval is now {interval.TotalSeconds:0.###}s; polling paced to match."
			: $"UpdateDeviceReportedInterval: '{_hostKey}' no child reported an interval; falling back to configured SensorPollInterval.");
		}

	private TimeSpan ComputeNextDelay ()
		{
		// Prefer the devices' own reporting cadence over the configured interval: polling faster
		// than the hub children actually refresh cannot surface anything new. The configured
		// SensorPollInterval is only the fallback for when no child has reported an interval yet
		// (e.g. before the first successful poll).
		TimeSpan? deviceReportedInterval;
		lock (_gate)
			{
			deviceReportedInterval = _deviceReportedInterval;
			}

		TimeSpan baseInterval = deviceReportedInterval ?? _sharedConfiguration.SensorPollInterval;
		int failureCount = Volatile.Read (ref _consecutiveFailureCount);
		if (failureCount <= 0)
			{
			return baseInterval;
			}

		// Exponential backoff: 2x, 4x, 8x, ... the base interval per additional consecutive
		// failure, capped at MaxBackoff so a persistently unreachable hub is retried only rarely
		// rather than being reconnected to on every tick.
		double multiplier = Math.Pow (2, Math.Min (failureCount, 10));
		double backoffMs = baseInterval.TotalMilliseconds * multiplier;
		TimeSpan backoff = TimeSpan.FromMilliseconds (Math.Min (backoffMs, MaxBackoff.TotalMilliseconds));
		return backoff > baseInterval ? backoff : baseInterval;
		}

	private async Task PollOnceAsync (CancellationToken cancellationToken)
		{
		List<IKasaHubChildEntity> childrenSnapshot;
		lock (_gate)
			{
			childrenSnapshot = new List<IKasaHubChildEntity> (_children.Values);
			}

		if (childrenSnapshot.Count == 0)
			{
			return;
			}

		var pollStopwatch = System.Diagnostics.Stopwatch.StartNew ();
		try
			{
			KasaDevice device = await EnsureConnectedAsync (cancellationToken).ConfigureAwait (false);
			LogInfo ($"PollOnceAsync: '{_hostKey}' connected after {pollStopwatch.ElapsedMilliseconds}ms; calling UpdateAsync for {childrenSnapshot.Count} child(ren).");

			var updateStopwatch = System.Diagnostics.Stopwatch.StartNew ();
			await device.UpdateAsync (cancellationToken).ConfigureAwait (false);
			LogInfo ($"PollOnceAsync: '{_hostKey}' UpdateAsync completed after {updateStopwatch.ElapsedMilliseconds}ms.");

			if (Interlocked.Exchange (ref _consecutiveFailureCount, 0) > 0)
				{
				LogInfo ($"PollOnceAsync: '{_hostKey}' poll succeeded; resetting consecutive-failure backoff.");
				}

			UpdateDeviceReportedInterval (device, childrenSnapshot);

			foreach (IKasaHubChildEntity child in childrenSnapshot)
				{
				try
					{
					ChildDevice? childDevice = device.GetChildDevice (child.ChildId);
					child.ApplyPushedState (childDevice, device);
					}
				catch (Exception ex)
					{
					_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Hub poller '{_hostKey}' failed to push state to child '{child.ChildId}': {ex}");
					}
				}
			}
		catch (OperationCanceledException)
			{
			_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Hub poller '{_hostKey}' poll canceled after {pollStopwatch.ElapsedMilliseconds}ms (cancellationRequested={cancellationToken.IsCancellationRequested}).");
			throw;
			}
		catch (Exception ex)
			{
			int failureCount = Interlocked.Increment (ref _consecutiveFailureCount);
			TimeSpan nextDelay = ComputeNextDelay ();
			_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Hub poller '{_hostKey}' poll failed after {pollStopwatch.ElapsedMilliseconds}ms ({ex.GetType().Name}), consecutiveFailureCount={failureCount}; next attempt backed off to {nextDelay.TotalSeconds:0}s: {ex}");

			foreach (IKasaHubChildEntity child in childrenSnapshot)
				{
				try
					{
					child.ApplyConnectionState (false);
					}
				catch (Exception applyEx)
					{
					_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Hub poller '{_hostKey}' failed to apply offline state to child '{child.ChildId}': {applyEx}");
					}
				}
			}
		}

	/// <summary>
	/// Connects to (or reuses) this hub's shared device, for callers outside the normal polling
	/// loop that need a one-shot connection to the same physical hub - e.g. hub child expansion
	/// during materialization (see ResolveHubChildDescriptorsAsync). Routing through the poller
	/// instead of calling Discover.GetOrConnectSharedAsync directly ensures there is only ever one
	/// call site that can initiate a *new* connection for this hub: GetOrConnectSharedAsync only
	/// reuses/serializes an existing shared instance on a cache hit, but on a cache miss (e.g. the
	/// very first connect, or right after the previous shared instance was disposed/replaced) it
	/// falls through to an independent connect, and KasaDevice's internal operation semaphore only
	/// serializes calls within one such instance - it does not stop a second, separate instance
	/// from being created concurrently by another caller. Some hubs only tolerate one active
	/// session at a time, so two independent sessions racing each other can wedge the hub.
	/// </summary>
	public Task<KasaDevice> ConnectSharedAsync (CancellationToken cancellationToken) =>
		EnsureConnectedAsync (cancellationToken);

	private async Task<KasaDevice> EnsureConnectedAsync (CancellationToken cancellationToken)
		{
		KasaDevice? existingDevice = Volatile.Read (ref _connectedDevice);
		if (existingDevice is not null && !existingDevice.IsDisposed)
			{
			return existingDevice;
			}

		DeviceConfiguration configuration;
		lock (_gate)
			{
			configuration = _configuration;
			}

		LogInfo ($"EnsureConnectedAsync: '{_hostKey}' no usable cached shared device (existingDevice={(existingDevice is null ? "null" : $"disposed={existingDevice.IsDisposed}")}); connecting via Discover.GetOrConnectSharedAsync with timeout={ConnectTimeout.TotalSeconds:0}s.");

		using CancellationTokenSource connectCancellationSource = CancellationTokenSource.CreateLinkedTokenSource (cancellationToken, _lifetimeCancellationSource.Token);
		connectCancellationSource.CancelAfter (ConnectTimeout);

		var connectStopwatch = System.Diagnostics.Stopwatch.StartNew ();
		KasaDevice connectedDevice;
		try
			{
			connectedDevice = await Discover.GetOrConnectSharedAsync (configuration, updateState: true, cancellationToken: connectCancellationSource.Token).ConfigureAwait (false);
			}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && connectCancellationSource.IsCancellationRequested)
			{
			_logger?.Log (_driverLogId, LogEntryLevel.Error, $"EnsureConnectedAsync: '{_hostKey}' connect timed out after {connectStopwatch.ElapsedMilliseconds}ms (limit={ConnectTimeout.TotalSeconds:0}s).");
			throw new TimeoutException ($"Hub poller '{_hostKey}' connect timed out after {ConnectTimeout.TotalSeconds:0} seconds.");
			}

		LogInfo ($"EnsureConnectedAsync: '{_hostKey}' connected after {connectStopwatch.ElapsedMilliseconds}ms.");
		_connectedDevice = connectedDevice;
		return connectedDevice;
		}

	private void Stop ()
		{
		Interlocked.Increment (ref _pollingGeneration);
		lock (_gate)
			{
			_pollingTask = null;
			}
		}

	public void Dispose ()
		{
		if (_disposed)
			{
			return;
			}

		_disposed = true;

		lock (_gate)
			{
			foreach (IKasaHubChildEntity child in _children.Values)
				{
				child.EventSubscribersChanged -= HandleChildEventSubscribersChanged;
				}

			_children.Clear ();
			}

		Stop ();
		_lifetimeCancellationSource.Cancel ();
		_lifetimeCancellationSource.Dispose ();

		// Deliberately does not Dispose _connectedDevice: it is a shared instance owned by the
		// Discover connection cache and may still be referenced by sibling light/outlet children
		// of the same physical hub.
		}

	private void LogInfo (string message)
		{
		_logger?.Log (_driverLogId, LogEntryLevel.Info, $"ManagedParentDevicePoller[{_hostKey}]: {message}");
		}
	}

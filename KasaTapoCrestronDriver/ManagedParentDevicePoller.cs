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
/// per <see cref="IPlatformSharedConfiguration.SensorPollInterval"/> tick, and pushes each child
/// its own slice of the resulting child list.
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
	/// Registers a hub child entity with this poller. An immediate poll is always kicked off
	/// (rather than waiting a full <see cref="IPlatformSharedConfiguration.SensorPollInterval"/>)
	/// so the newly-configured child gets fresh state right away, matching the previous
	/// per-entity "connect gives fresh initial state" behavior - even if other children of the
	/// same hub are already registered and polling.
	/// </summary>
	public void RegisterChild (IKasaHubChildEntity child)
		{
		lock (_gate)
			{
			if (_disposed)
				{
				return;
				}

			_children[child.ChildId] = child;
			}

		LogInfo ($"RegisterChild: childId='{child.ChildId}', totalChildren={_children.Count}.");

		RestartPolling ();
		}

	public void UnregisterChild (string childId)
		{
		bool shouldStop;
		lock (_gate)
			{
			_children.Remove (childId);
			shouldStop = _children.Count == 0;
			}

		LogInfo ($"UnregisterChild: childId='{childId}', remainingChildren={_children.Count}, shouldStop={shouldStop}.");

		if (shouldStop)
			{
			Stop ();
			}
		}

	public void ApplyRuntimeConfiguration (PlatformSharedConfigurationSnapshot previousConfiguration, PlatformSharedConfigurationSnapshot currentConfiguration)
		{
		if (_disposed)
			{
			return;
			}

		if (previousConfiguration.SensorPollInterval != currentConfiguration.SensorPollInterval)
			{
			lock (_gate)
				{
				if (_children.Count == 0)
					{
					return;
					}
				}

			RestartPolling ();
			}
		}

	private void RestartPolling ()
		{
		int generation = Interlocked.Increment (ref _pollingGeneration);
		_pollingTask = RunPollingCycleAsync (generation, immediateFirstPoll: true);
		}

	private async Task RunPollingCycleAsync (int generation, bool immediateFirstPoll)
		{
		try
			{
			if (!immediateFirstPoll)
				{
				await Task.Delay (_sharedConfiguration.SensorPollInterval, _lifetimeCancellationSource.Token).ConfigureAwait (false);
				}

			if (_disposed || generation != Volatile.Read (ref _pollingGeneration))
				{
				return;
				}

			// The initial poll on registration always runs (regardless of subscribers) so newly
			// configured/published children get a correct starting value read directly from the
			// device. After that, all continued property updates happen only as a side effect of
			// the delta checks that raise a child's events (see IKasaHubChildEntity.ApplyPushedState
			// implementations), so once none of a hub's children have any event subscriber there is
			// nothing for a further poll to usefully drive - skip actually hitting the device this
			// tick, but keep the loop alive so polling resumes automatically the moment a subscriber
			// appears.
			if (immediateFirstPoll || AnyChildHasEventSubscribers ())
				{
				await PollOnceAsync (_lifetimeCancellationSource.Token).ConfigureAwait (false);
				}

			if (_disposed || generation != Volatile.Read (ref _pollingGeneration))
				{
				return;
				}

			_pollingTask = RunPollingCycleAsync (generation, immediateFirstPoll: false);
			}
		catch (OperationCanceledException)
			{
			}
		catch (Exception ex)
			{
			_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Hub poller '{_hostKey}' polling loop failed: {ex}");
			}
		}

	private bool AnyChildHasEventSubscribers ()
		{
		lock (_gate)
			{
			foreach (IKasaHubChildEntity child in _children.Values)
				{
				if (child.HasEventSubscribers)
					{
					return true;
					}
				}

			return false;
			}
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

		try
			{
			KasaDevice device = await EnsureConnectedAsync (cancellationToken).ConfigureAwait (false);
			await device.UpdateAsync (cancellationToken).ConfigureAwait (false);

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
			throw;
			}
		catch (Exception ex)
			{
			_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Hub poller '{_hostKey}' poll failed: {ex}");

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

		using CancellationTokenSource connectCancellationSource = CancellationTokenSource.CreateLinkedTokenSource (cancellationToken, _lifetimeCancellationSource.Token);
		connectCancellationSource.CancelAfter (ConnectTimeout);

		KasaDevice connectedDevice;
		try
			{
			connectedDevice = await Discover.GetOrConnectSharedAsync (configuration, updateState: true, cancellationToken: connectCancellationSource.Token).ConfigureAwait (false);
			}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && connectCancellationSource.IsCancellationRequested)
			{
			throw new TimeoutException ($"Hub poller '{_hostKey}' connect timed out after {ConnectTimeout.TotalSeconds:0} seconds.");
			}

		_connectedDevice = connectedDevice;
		return connectedDevice;
		}

	private void Stop ()
		{
		Interlocked.Increment (ref _pollingGeneration);
		_pollingTask = null;
		}

	public void Dispose ()
		{
		if (_disposed)
			{
			return;
			}

		_disposed = true;
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

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Crestron.DeviceDrivers.EntityModel.Data;
using Crestron.DeviceDrivers.EntityModel.Logging;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel;
using Crestron.DeviceDrivers.SDK.EntityModel.Attributes;

using KasaTapoClient;

using KasaDeviceType = KasaTapoClient.DeviceType;

namespace KasaTapoCrestronDriver;

internal partial class KasaLightEntity
	{
	public virtual void UpdateDescriptor (ManagedLightDescriptor descriptor, DeviceConfiguration configuration)
		{
		if (_descriptor is null)
			{
			_descriptor = descriptor;
			}
		else
			{
			_descriptor.Name = SelectPreferredDescriptorName (_descriptor, descriptor);
			}

		_configuration = configuration;
		DeviceName = _descriptor.Name;
		ModelName = _descriptor.ModelName;
		SerialNumber = _descriptor.SerialNumber;
		Kind = _descriptor.Kind;
		ChildId = _descriptor.ChildId;

		RestartPolling ();
		}

	private static string SelectPreferredDescriptorName (ManagedLightDescriptor currentDescriptor, ManagedLightDescriptor incomingDescriptor)
		{
		if (!string.IsNullOrWhiteSpace (currentDescriptor.Name)
			&& incomingDescriptor.AwaitingConnectedIdentity)
			{
			return currentDescriptor.Name;
			}

		return !string.IsNullOrWhiteSpace (incomingDescriptor.Name)
			? incomingDescriptor.Name
			: currentDescriptor.Name;
		}

	public void UpdateConfiguration (DeviceConfiguration configuration)
		{
		DeviceConfiguration? previousConfiguration = _configuration;
		_configuration = configuration;

		if (previousConfiguration is not null && HasMaterialConfigurationChange (previousConfiguration, configuration))
			{
			ResetConnectionState ();
			}

		if (_isConfigured)
			{
			RestartPolling ();
			}
		}

	public void SetConfigured (bool configured, string context)
		{
		if (_disposed)
			{
			return;
			}

		if (_isConfigured == configured)
			{
			LogInfo ($"Light entity '{ControllerId}' SetConfigured ignored because configured={configured} is unchanged; context='{context}'.");
			return;
			}

		_isConfigured = configured;
		LogInfo ($"Light entity '{ControllerId}' SetConfigured: configured={configured}, context='{context}'.");

		if (!configured)
			{
			ResetConnectionState ();
			Interlocked.Increment (ref _pollingGeneration);
			_pollingTask = null;
			return;
			}

		RestartPolling ();
		StartBackgroundOperation (InitializeStartupAsync, $"child-configured:{context}");
		}

	public async Task SetConfiguredAsync (bool configured, string context, CancellationToken cancellationToken)
		{
		if (_disposed)
			{
			return;
			}

		if (_isConfigured == configured)
			{
			LogInfo ($"Light entity '{ControllerId}' SetConfiguredAsync ignored because configured={configured} is unchanged; context='{context}'.");
			return;
			}

		_isConfigured = configured;
		LogInfo ($"Light entity '{ControllerId}' SetConfiguredAsync: configured={configured}, context='{context}'.");

		if (!configured)
			{
			ResetConnectionState ();
			Interlocked.Increment (ref _pollingGeneration);
			_pollingTask = null;
			return;
			}

		RestartPolling ();
		await InitializeConnectedStateAsync (cancellationToken).ConfigureAwait (false);
		}

	public void NotifyChildPublished ()
		{
		if (_disposed)
			{
			return;
			}

		_childPublished = true;
		TryPublishDeferredStartupSnapshot ("NotifyChildPublished");
		}

	public void NotifyChildRunning (string context)
		{
		if (_disposed)
			{
			return;
			}

		if (_connectedDevice is null)
			{
			LogInfo ($"Light entity '{ControllerId}' child reached Running before connected device state was available; deferring current state snapshot; context='{context}'.");
			return;
			}

		LogInfo ($"Light entity '{ControllerId}' child reached Running with connected device state available; republishing current state snapshot; context='{context}'.");
		PublishStateSnapshot ();
		}

	public void ApplyRuntimeConfiguration (PlatformSharedConfigurationSnapshot previousConfiguration, PlatformSharedConfigurationSnapshot currentConfiguration)
		{
		if (_disposed)
			{
			return;
			}

		bool pollingChanged = previousConfiguration.EnableLightPolling != currentConfiguration.EnableLightPolling
			|| previousConfiguration.LightPollInterval != currentConfiguration.LightPollInterval;
		bool connectionChanged = !string.Equals (previousConfiguration.UserName, currentConfiguration.UserName, StringComparison.Ordinal)
			|| !string.Equals (previousConfiguration.Password, currentConfiguration.Password, StringComparison.Ordinal)
			|| previousConfiguration.DiscoveryTimeout != currentConfiguration.DiscoveryTimeout;

		if (pollingChanged)
			{
			RestartPolling ();
			}

		if (connectionChanged)
			{
			ResetConnectionState ();
			}
		}

	public void Stop ()
		{
		if (Interlocked.Exchange (ref _stopState, 1) != 0)
			{
			return;
			}

		CancelSliderInteraction ();
		_lifetimeCancellationSource.Cancel ();
		Interlocked.Increment (ref _pollingGeneration);
		_pollingTask = null;
		}

	public override void Dispose ()
		{
		if (_disposed)
			{
			return;
			}

		Stop ();
		_lifetimeCancellationSource.Dispose ();
		_disposed = true;
		}

	public async Task RefreshAsync (CancellationToken cancellationToken)
		{
		try
			{
			await RefreshAndApplyStateAsync (cancellationToken).ConfigureAwait (false);
			}
		catch (TimeoutException timeoutException)
			{
			try
				{
				await ReconnectAndRefreshAsync (timeoutException, cancellationToken).ConfigureAwait (false);
				OnlineIndicatorIsOnline = true;
				ReadyIndicatorIsReady = true;
				return;
				}
			catch (OperationCanceledException)
				{
				throw;
				}
			catch (Exception reconnectException)
				{
				ResetConnectionState ();
				_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Light entity '{ControllerId}' reconnect after refresh timeout failed: {reconnectException}");
				return;
				}
			}
		catch (OperationCanceledException)
			{
			throw;
			}
		catch (Exception ex)
			{
			ResetConnectionState ();
			_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Light entity '{ControllerId}' refresh failed: {ex}");
			}
		}

	private void RestartPolling ()
		{
		if (_disposed)
			{
			LogInfo ($"Light entity '{ControllerId}' RestartPolling skipped because the entity is disposed.");
			return;
			}

		if (!_isConfigured)
			{
			LogInfo ($"Light entity '{ControllerId}' RestartPolling skipped because the child is not configured/installed yet.");
			return;
			}

		Interlocked.Exchange (ref _stopState, 0);
		int generation = Interlocked.Increment (ref _pollingGeneration);

		// Even when full state polling is disabled (so external/out-of-band changes to the
		// device are intentionally not detected), a light that has gone offline while in use
		// (e.g. the device was unplugged/lost power/lost Wi-Fi) still needs some way to recover
		// automatically once it comes back, rather than staying offline forever until a user
		// happens to issue another command against it. RunPollingCycleAsync always runs on this
		// same timer, but only performs a reconnect-and-refresh attempt when EnableLightPolling
		// is off if the entity is currently offline; it does nothing else in that case, so no
		// external state changes are surfaced while the device remains reachable.
		_pollingTask = RunPollingCycleAsync (generation);
		LogInfo ($"Light entity '{ControllerId}' RestartPolling: enabled={_sharedConfiguration.EnableLightPolling}, generation={generation}, intervalMs={_sharedConfiguration.LightPollInterval.TotalMilliseconds:0}.");
		}

	private void ResetConnectionState (bool flipIndicatorsOffline = true)
		{
		KasaDevice? staleDevice = _connectedDevice;
		LogInfo ($"Light entity '{ControllerId}' ResetConnectionState: hadConnectedDevice={staleDevice is not null}, flipIndicatorsOffline={flipIndicatorsOffline}.");
		_connectedDevice = null;
		_pendingStartupSnapshotAfterConnectedState = false;

		// A single failed attempt that will be immediately retried (see ExecuteDeviceCommandAsync)
		// should not visibly flap the UI to offline - a transient one-off TCP connect hiccup (e.g.
		// a dropped SYN during the device's own state transition) can resolve itself on the very
		// next attempt a second later. Only surface offline/not-ready once retries are exhausted.
		if (flipIndicatorsOffline)
			{
			OnlineIndicatorIsOnline = false;
			ReadyIndicatorIsReady = false;
			}

		if (staleDevice is null)
			{
			return;
			}

		// Do not dispose while another in-flight command is still actively executing against this
		// same device instance (see _activeDeviceOperationCount remarks above) - disposing here would
		// pull the transport/semaphore out from under that other operation's still-running command.
		// Defer disposal until the last active operation against this device finishes.
		if (Volatile.Read (ref _activeDeviceOperationCount) > 0)
			{
			LogInfo ($"Light entity '{ControllerId}' ResetConnectionState: deferring disposal, {_activeDeviceOperationCount} operation(s) still active on the stale device.");
			_deviceAwaitingDisposal = staleDevice;
			return;
			}

		try
			{
			staleDevice.Dispose ();
			}
		catch (Exception ex)
			{
			_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Light entity '{ControllerId}' failed to dispose stale connected device: {ex}");
			}
		}

	private void ReleaseDeviceOperation ()
		{
		if (Interlocked.Decrement (ref _activeDeviceOperationCount) > 0)
			{
			return;
			}

		KasaDevice? deferredDevice = Interlocked.Exchange (ref _deviceAwaitingDisposal, null);
		if (deferredDevice is null)
			{
			return;
			}

		try
			{
			deferredDevice.Dispose ();
			}
		catch (Exception ex)
			{
			_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Light entity '{ControllerId}' failed to dispose deferred stale connected device: {ex}");
			}
		}

	private void TryPublishDeferredStartupSnapshot (string context)
		{
		if (!_pendingStartupSnapshotAfterConnectedState || !_childPublished)
			{
			return;
			}

		_pendingStartupSnapshotAfterConnectedState = false;
		LogInfo ($"Light entity '{ControllerId}' publishing deferred startup snapshot after connected state and child publication were both satisfied; context='{context}'.");
		PublishStateSnapshot ();

		// Diagnostic: unlike LogDynamicFeatureEntityState (which only ever logs once, before this
		// startup snapshot is published), capture the full GetState() payload - the actual combined
		// definition/value contract Crestron Home receives - immediately after this deferred
		// snapshot so a mismatched initial UI mode can be correlated against exactly what was sent,
		// not just the individual PublishProperty calls logged elsewhere.
		LogEntityStateSnapshot ($"TryPublishDeferredStartupSnapshot.{context}");
		}

	private void TryApplyDeferredDeviceStateAfterSliderInteraction (string context)
		{
		if (!_deferredDeviceStatePending || IsSliderInteractionActive ())
			{
			return;
			}

		KasaDevice? connectedDevice = _connectedDevice;
		if (connectedDevice is null)
			{
			return;
			}

		ApplyState (connectedDevice, allowDeferredDeviceStateDuringSliderInteraction: true);
		LogEntityStateSnapshot ($"{context}.AfterDeferredApplyState");
		LogInfo ($"Light entity '{ControllerId}' applied deferred device-driven state after slider interaction settled; context='{context}'.");
		}

	private static bool HasMaterialConfigurationChange (DeviceConfiguration previousConfiguration, DeviceConfiguration currentConfiguration)
		{
		if (!string.Equals (previousConfiguration.Host, currentConfiguration.Host, StringComparison.OrdinalIgnoreCase)
			|| previousConfiguration.Port != currentConfiguration.Port
			|| previousConfiguration.Timeout != currentConfiguration.Timeout)
			{
			return true;
			}

		if (!HaveEquivalentCredentials (previousConfiguration.Credentials, currentConfiguration.Credentials))
			{
			return true;
			}

		return !HaveEquivalentConnectionOptions (previousConfiguration.ConnectionOptions, currentConfiguration.ConnectionOptions);
		}

	private static bool HaveEquivalentCredentials (DeviceCredentials? previousCredentials, DeviceCredentials? currentCredentials)
		{
		if (ReferenceEquals (previousCredentials, currentCredentials))
			{
			return true;
			}

		if (previousCredentials is null || currentCredentials is null)
			{
			return false;
			}

		return string.Equals (previousCredentials.UserName, currentCredentials.UserName, StringComparison.Ordinal)
			&& string.Equals (previousCredentials.Password, currentCredentials.Password, StringComparison.Ordinal);
		}

	private static bool HaveEquivalentConnectionOptions (DeviceConnectionOptions previousOptions, DeviceConnectionOptions currentOptions)
		{
		return previousOptions.TransportKind == currentOptions.TransportKind
			&& string.Equals (previousOptions.ConnectionParameters?.ToString (), currentOptions.ConnectionParameters?.ToString (), StringComparison.Ordinal)
			&& previousOptions.UseSsl == currentOptions.UseSsl
			&& previousOptions.UseDefaultCredentials == currentOptions.UseDefaultCredentials
			&& previousOptions.DefaultCredentialProfile == currentOptions.DefaultCredentialProfile
			&& string.Equals (previousOptions.ApplicationPath, currentOptions.ApplicationPath, StringComparison.Ordinal)
			&& previousOptions.UseSecurePassthrough == currentOptions.UseSecurePassthrough
			&& previousOptions.TpapKeepAliveInterval == currentOptions.TpapKeepAliveInterval;
		}

	private async Task RunPollingCycleAsync (int generation)
		{
		try
			{
			await Task.Delay (_sharedConfiguration.LightPollInterval, _lifetimeCancellationSource.Token).ConfigureAwait (false);

			if (_disposed
				|| Volatile.Read (ref _stopState) != 0
				|| generation != Volatile.Read (ref _pollingGeneration))
				{
				return;
				}

			if (_sharedConfiguration.EnableLightPolling)
				{
				await RefreshAsync (_lifetimeCancellationSource.Token).ConfigureAwait (false);
				PublishStateSnapshot ();
				}
			else if (!OnlineIndicatorIsOnline)
				{
				// Polling is disabled (no external/out-of-band device-state detection), but an
				// offline device still needs a periodic chance to recover automatically - e.g. it
				// was unplugged and has since been plugged back in - rather than staying offline
				// indefinitely until a user happens to issue another command against it. Attempt
				// a lightweight reconnect-and-refresh here; ReconnectAndRefreshAsync already
				// restores OnlineIndicatorIsOnline/ReadyIndicatorIsReady and republishes the
				// current device state on success.
				try
					{
					await RefreshAsync (_lifetimeCancellationSource.Token).ConfigureAwait (false);
					PublishStateSnapshot ();
					}
				catch (OperationCanceledException)
					{
					throw;
					}
				catch (Exception ex)
					{
					LogInfo ($"Light entity '{ControllerId}' offline-recovery attempt failed; will retry on the next cycle: {ex.Message}");
					}
				}

			if (_disposed
				|| Volatile.Read (ref _stopState) != 0
				|| generation != Volatile.Read (ref _pollingGeneration))
				{
				return;
				}

			_pollingTask = RunPollingCycleAsync (generation);
			}
		catch (OperationCanceledException)
			{
			}
		catch (Exception ex)
			{
			_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Light entity '{ControllerId}' polling loop failed: {ex}");
			}
		}

	public void PublishStateSnapshot ()
		{
		LogPublishedState ();

		PublishProperty ("onlineIndicator:isOnline", new DriverEntityValue (OnlineIndicatorIsOnline), "PublishStateSnapshot");
		PublishProperty ("readyIndicator:isReady", new DriverEntityValue (ReadyIndicatorIsReady), "PublishStateSnapshot");
		if (_registeredOnOffMembers is not null)
			{
			PublishProperty ("light:isOn", new DriverEntityValue (LightIsOn), "PublishStateSnapshot");
			}

		if (_supportsBrightness)
			{
			PublishProperty ("lightDimmer:level", new DriverEntityValue (LightDimmerLevel), "PublishStateSnapshot");
			}

		PublishColorModeStateProperties ("PublishStateSnapshot");

		}

	private async Task ExecuteWithConnectedDeviceAsync (Func<KasaDevice, Task> work, CancellationToken cancellationToken)
		{
		KasaDevice device = await EnsureConnectedAsync (cancellationToken).ConfigureAwait (false);
		await work (device).ConfigureAwait (false);
		}

	private async Task InitializeConnectedStateAsync (CancellationToken cancellationToken)
		{
		KasaDevice device = await EnsureConnectedAsync (cancellationToken).ConfigureAwait (false);
		ConfigureDynamicFeatures (device, _descriptor);
		LogReportedState ("InitializeConnectedState", device);

		bool previousSuppressPropertyNotifications = _suppressPropertyNotifications;
		_suppressPropertyNotifications = true;
		try
			{
			ApplyState (device);
			}
		finally
			{
			_suppressPropertyNotifications = previousSuppressPropertyNotifications;
			}

		// Synchronize the processor baseline to the light's actual reported mode and AWAIT it to
		// completion before this entity reports itself online/ready. Live processor log evidence
		// (2026-07-26.log, 08:10:02-08:10:05) proved that dispatching this as a fire-and-forget
		// background operation loses the race on every reload: Crestron Home's own Load-layer
		// reconnect handshake renders its initial UI tile from the load's live
		// TunableChannelStates the moment the entity reports ready, which happens well before the
		// SSH console round-trip that corrects that stale state has a chance to land. Awaiting it
		// here closes that race deterministically. Without this, the baseline coordinator's cached
		// tuning mode can also go stale/out-of-sync with the bulb's true state across
		// reconnects/reloads (e.g. left over from a prior session), so a later spurious
		// pre-power-on mode command is wrongly treated as "already matches" and the real correction
		// is skipped.
		if (!string.IsNullOrWhiteSpace (DeviceName))
			{
			await DispatchProcessorBaselineSynchronizationAsync (LightTuningDecisions.ToProcessorTuningMode (IsCurrentColorTemperatureUiMode ()), "InitializeConnectedStateAsync.BeforeOnlineReady").ConfigureAwait (false);
			}

		OnlineIndicatorIsOnline = true;
		ReadyIndicatorIsReady = true;
		PublishCurrentLightModeProperties ("InitializeConnectedStateAsync.AfterOnlineReady", synchronizeProcessorBaseline: false);

		// Arm the startup dimmer-replay guard (see remarks on the fields above) whenever the bulb is
		// actually reported on, regardless of its real brightness level - even a genuinely low level
		// like 1% must be protected, since the replay pattern is a Load-layer artifact unrelated to
		// what the real level happens to be. If Crestron Home's Load layer replays a stale near-zero
		// level against this entity within the guard window, LightDimmerSetLevel will detect and
		// ignore it instead of turning the real bulb off and later back on at the wrong brightness.
		if (LightIsOn)
			{
			_startupDimmerReplayRealLevel = LightDimmerLevel;
			_startupDimmerReplayGuardUntilUtc = DateTime.UtcNow + StartupDimmerReplayGuardWindow;
			}
		else
			{
			_startupDimmerReplayGuardUntilUtc = null;
			}

		_pendingStartupSnapshotAfterConnectedState = true;
		TryPublishDeferredStartupSnapshot ("InitializeConnectedStateAsync");
		}

	private async Task InitializeStartupAsync ()
		{
		TimeSpan startupJitterDelay = GetStartupJitterDelay ();
		if (startupJitterDelay > TimeSpan.Zero)
			{
			LogInfo ($"Light entity '{ControllerId}' delaying initial startup connect to host '{_descriptor.Host}' by {startupJitterDelay.TotalMilliseconds:0} ms to stagger concurrent reload connects.");
			try
				{
				await Task.Delay (startupJitterDelay, _lifetimeCancellationSource.Token).ConfigureAwait (false);
				}
			catch (OperationCanceledException) when (!_disposed && Volatile.Read (ref _stopState) == 0)
				{
				return;
				}
			}

		int startupConnectAttempt = 0;
		while (!_disposed && Volatile.Read (ref _stopState) == 0)
			{
			TimeSpan retryInterval = startupConnectAttempt < StartupConnectRetryRampUp.Length
				? StartupConnectRetryRampUp[startupConnectAttempt]
				: StartupConnectRetryInterval;
			startupConnectAttempt++;

			try
				{
				LogInfo ($"Light entity '{ControllerId}' startup connecting to discovered host '{_descriptor.Host}' as {_descriptor.DiscoveredDeviceType}; driverId='{_driverLogId}'.");
				await StartupConnectConcurrencyGate.WaitAsync (_lifetimeCancellationSource.Token).ConfigureAwait (false);
				try
					{
					using (CancellationTokenSource timeoutCancellationSource = CancellationTokenSource.CreateLinkedTokenSource (_lifetimeCancellationSource.Token))
						{
						timeoutCancellationSource.CancelAfter (StartupConnectTimeout);
						await InitializeConnectedStateAsync (timeoutCancellationSource.Token).ConfigureAwait (false);
						}
					}
				finally
					{
					_ = StartupConnectConcurrencyGate.Release ();
					}
				return;
				}
			catch (OperationCanceledException) when (!_disposed && Volatile.Read (ref _stopState) == 0)
				{
				LogInfo ($"Light entity '{ControllerId}' startup connect canceled for host '{_descriptor.Host}'; retrying in {retryInterval.TotalSeconds:0} seconds.");
				OnlineIndicatorIsOnline = false;
				ReadyIndicatorIsReady = false;
				}
			catch (OperationCanceledException)
				{
				throw;
				}
			catch (Exception ex)
				{
				LogInfo ($"Light entity '{ControllerId}' startup connect failed for host '{_descriptor.Host}': {ex.Message}; retrying in {retryInterval.TotalSeconds:0} seconds.");
				OnlineIndicatorIsOnline = false;
				ReadyIndicatorIsReady = false;
				}

			await Task.Delay (retryInterval, _lifetimeCancellationSource.Token).ConfigureAwait (false);
			}
		}

	private async Task<KasaDevice> EnsureConnectedAsync (CancellationToken cancellationToken)
		{
		if (!_isConfigured)
			{
			throw new InvalidOperationException ($"Light entity '{ControllerId}' cannot connect before child configuration/installation has occurred.");
			}

		KasaDevice? existingDevice = _connectedDevice;
		// This entity connects via Discover.GetOrConnectSharedAsync (see
		// ConnectFromConfigurationAsync), which shares a single, long-lived KasaDevice per
		// Host:Port across all coordinated callers (this entity's command path and
		// PlatformDriver's alias-enrichment path). Some other caller of that same shared device
		// - e.g. alias-enrichment on a different controller, or another entity that resolved to
		// the same host - can dispose it independently of anything this entity did. Unlike a
		// purely local cache, this entity's own ResetConnectionState is not the only path that
		// can leave _connectedDevice stale, so IsDisposed must be checked here too before
		// reusing it, exactly like Discover's own shared-instance cache does for its entries.
		if (existingDevice is not null && !existingDevice.IsDisposed)
			{
			return existingDevice;
			}

		if (existingDevice is not null && existingDevice.IsDisposed)
			{
			LogInfo ($"Light entity '{ControllerId}' EnsureConnectedAsync detected a disposed shared device instance; reconnecting.");
			_ = Interlocked.CompareExchange (ref _connectedDevice, null, existingDevice);
			}

		cancellationToken.ThrowIfCancellationRequested ();
		_lifetimeCancellationSource.Token.ThrowIfCancellationRequested ();

		// KasaClient's Discover now owns the single-shared-device-per-Host:Port guarantee (it
		// coalesces concurrent connects for the same key and returns the same live KasaDevice
		// instance to every caller), so there is no longer a need to serialize connect attempts
		// behind a local gate here. If two commands race past the null check above, both calls
		// below resolve to the very same KasaDevice instance from Discover, so the final
		// assignment to _connectedDevice is idempotent regardless of which caller wins the race.
		using var connectCancellationSource = CancellationTokenSource.CreateLinkedTokenSource (cancellationToken, _lifetimeCancellationSource.Token);
		connectCancellationSource.CancelAfter (DeviceConnectTimeout);
		Stopwatch connectStopwatch = Stopwatch.StartNew ();
		KasaDevice connectedDevice;
		try
			{
			connectedDevice = await ConnectFromConfigurationAsync (updateState: true, connectCancellationSource.Token).ConfigureAwait (false);
			}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && connectCancellationSource.IsCancellationRequested)
			{
			throw new TimeoutException ($"Light entity '{ControllerId}' connect timed out after {DeviceConnectTimeout.TotalSeconds:0} seconds.");
			}
		connectStopwatch.Stop ();

		LogInfo ($"Light entity '{ControllerId}' connect succeeded after {connectStopwatch.Elapsed.TotalMilliseconds:0} ms: deviceAlias='{connectedDevice.Alias ?? "<null>"}', systemInfoAlias='{connectedDevice.SystemInfo?.Alias ?? "<null>"}', model='{connectedDevice.SystemInfo?.Model ?? "<null>"}', deviceId='{connectedDevice.SystemInfo?.DeviceId ?? "<null>"}'.");

		_connectedDevice = connectedDevice;
		UpdateDescriptorFromConnectedDevice (connectedDevice);
		return connectedDevice;
		}

	private async Task<KasaDevice> ConnectFromConfigurationAsync (bool updateState, CancellationToken cancellationToken)
		{
		DeviceConfiguration? configuration = _configuration;
		if (configuration is null)
			{
			throw new InvalidOperationException ($"Light entity '{ControllerId}' cannot connect because no device configuration was supplied by the platform.");
			}

		cancellationToken.ThrowIfCancellationRequested ();
		_lifetimeCancellationSource.Token.ThrowIfCancellationRequested ();

		// TpapTransport/LegacyTransport apply configuration.Timeout as their own internal per-request
		// timeout (see TpapTransport.CreateOperationTimeoutSource), independent of the outer
		// StartupConnectTimeout cancellation below. configuration.Timeout is bound to the user-facing
		// DiscoveryTimeoutSeconds setting (commonly 10s), which is far shorter than StartupConnectTimeout
		// and therefore always fires first, silently overriding the intended startup connect budget. Live
		// logs confirmed connects being canceled at ~10s (the configured DiscoveryTimeoutSeconds) rather
		// than at StartupConnectTimeout. Use a configuration with a timeout at least as long as
		// StartupConnectTimeout for startup connects so the outer timeout is the one that actually governs.
		DeviceConfiguration startupConfiguration = configuration.Timeout < StartupConnectTimeout
			? new DeviceConfiguration (configuration.Host, configuration.Port, configuration.Credentials, configuration.ConnectionOptions, StartupConnectTimeout)
			: configuration;

		// Use the explicit, opt-in shared-connection API: this entity's own command path and
		// PlatformDriver's alias-enrichment path are coordinated call sites that both target the
		// same device identity (host/port) and are expected to reuse one live connection rather
		// than each dialing their own, since some devices reject or reset additional concurrent
		// sessions. Discover.ConnectAsync (plain) no longer shares instances across separate
		// calls as of KasaClient 1.2.3.
		return await Discover.GetOrConnectSharedAsync (
			startupConfiguration,
			updateState,
			cancellationToken: cancellationToken).ConfigureAwait (false);
		}

	private void UpdateDescriptorFromConnectedDevice (KasaDevice device)
		{
		string resolvedAlias = !string.IsNullOrWhiteSpace (device.Alias)
			? device.Alias!
			: !string.IsNullOrWhiteSpace (device.SystemInfo?.Alias)
				? device.SystemInfo!.Alias!
				: _descriptor.Name;

		LogInfo ($"Light entity '{ControllerId}' UpdateDescriptorFromConnectedDevice: resolvedAlias='{resolvedAlias}', deviceAlias='{device.Alias ?? "<null>"}', systemInfoAlias='{device.SystemInfo?.Alias ?? "<null>"}', previousName='{_descriptor.Name ?? "<null>"}'.");

		_descriptor.Name = resolvedAlias;

		// ManagedLightDescriptor.Kind's setter already no-ops when the recomputed value matches what
		// is already assigned, and only throws if it would actually change after being resolved once
		// (see its setter) - by design, recomputing it here on every connect/reconnect is intentional
		// so a light whose Kind has not yet been resolved (still Unknown) gets classified as soon as
		// real device state is available. That contract only holds because InferManagedLightKind
		// itself must be deterministic for a given bulb's fixed hardware capability - it must never
		// depend on transient state (e.g. which mode happens to be active right now), since two
		// different connect attempts reading different transient states would then race to assign
		// two different Kind values and the second one would trip the immutability guard.
		_descriptor.Kind = InferManagedLightKind (device, _descriptor.DiscoveredDeviceType);

		DeviceName = _descriptor.Name;
		ModelName = _descriptor.ModelName;
		SerialNumber = _descriptor.SerialNumber;
		Kind = _descriptor.Kind;
		ChildId = _descriptor.ChildId;
		LogInfo ($"Light entity '{ControllerId}' invoking descriptor update callback with name='{_descriptor.Name}', model='{_descriptor.ModelName}', serial='{_descriptor.SerialNumber}'.");
		_descriptorUpdated?.Invoke (_descriptor);
		LogInfo ($"Light entity '{ControllerId}' descriptor update callback completed.");
		}

	private void RefreshDescriptorFromConnectedDevice (KasaDevice device, string context)
		{
		string previousName = _descriptor.Name;
		string previousModel = _descriptor.ModelName;
		string previousSerial = _descriptor.SerialNumber;

		UpdateDescriptorFromConnectedDevice (device);

		if (string.Equals (previousName, _descriptor.Name, StringComparison.Ordinal)
			&& string.Equals (previousModel, _descriptor.ModelName, StringComparison.Ordinal)
			&& string.Equals (previousSerial, _descriptor.SerialNumber, StringComparison.Ordinal))
			{
			LogInfo ($"Light entity '{ControllerId}' {context}: descriptor unchanged after refresh.");
			}
		else
			{
			LogInfo ($"Light entity '{ControllerId}' {context}: descriptor changed to name='{_descriptor.Name}', model='{_descriptor.ModelName}', serial='{_descriptor.SerialNumber}'.");
			}
		}

	private void ConfigureDynamicFeatures (KasaDevice device, ManagedLightDescriptor descriptor)
		{
		if (Interlocked.Exchange (ref _dynamicFeaturesConfigured, 1) != 0)
			{
			return;
			}

		bool supportsLightControls = descriptor.Kind != ManagedLightKind.OnOff;
		DriverEntityValueRange? lightColorTemperatureRange = null;

		_supportsBrightness = supportsLightControls && HasFeature (device.Features, BRIGHTNESS_FEATURE_ID);
		_supportsFullColor = supportsLightControls && HasCurrentFullColorState (device.LightState);
		bool supportsColorTemperatureApi = false;
		if (supportsLightControls)
			{
			supportsColorTemperatureApi = TryCreateColorTemperatureRange (device.Features, out DriverEntityValueRange detectedColorTemperatureRange);
			if (supportsColorTemperatureApi)
				{
				lightColorTemperatureRange = detectedColorTemperatureRange;
				}
			}
		_supportsColorTemperature = supportsColorTemperatureApi;
		LogInfo ($"Light entity '{ControllerId}' dynamic features: descriptorKind={descriptor.Kind}, supportsLightControls={supportsLightControls}, featureCount={device.Features.Count}, supportsBrightness={_supportsBrightness}, supportsFullColor={_supportsFullColor}, supportsColorTemperatureApi={supportsColorTemperatureApi}, supportsColorTemperature={_supportsColorTemperature}, colorTemperatureRange={FormatRange (lightColorTemperatureRange)}.");

		if (_supportsBrightness)
			{
			int? brightness = device.LightState?.Brightness;
			bool isOnAtConnect = ResolvePowerState (device);
			double initialDimmerLevel = isOnAtConnect && brightness.HasValue
				? Clamp01 (brightness.Value / 100d)
				: 0d;
			_registeredDimmableMembers = new DimmableMembers (this, initialDimmerLevel);
			RegisterObjectWithAttributes (_registeredDimmableMembers);
			}
		else
			{
			_registeredOnOffMembers = new OnOffMembers (this, ResolvePowerState (device));
			RegisterObjectWithAttributes (_registeredOnOffMembers);
			}

		if (_supportsColorTemperature)
			{
			bool hasActiveColorTemperature = HasCurrentColorTemperatureState (device.LightState);
			_isColorTemperatureUiModeActive = hasActiveColorTemperature;
			_colorTemperatureRangeMinimum = (long)TryGetColorTemperatureMinimum (device.Features);

			// In color mode there is no active color-temperature value to report; register the device's
			// declared Kelvin range and retain an in-range placeholder level for the reflected member.
			// The processor UI must not receive this value while HSV mode is active.
			bool useEmulatedColorTemperature = LightTuningDecisions.ShouldUseEmulatedColorTemperature (_supportsFullColor);
			long initialColorTemperatureLevel = device.LightState?.ColorTemperature is int activeCt && activeCt > 0
				? activeCt
				: _colorTemperatureRangeMinimum;
			DriverEntityValueRange initialColorTemperatureMemberRange = lightColorTemperatureRange!;

			// Capability identity is fixed for an entity's lifetime. A color-capable bulb always exposes
			// lightEmulatedColorTemperature, whether CT or HSV is presently active; a CT-only bulb always
			// exposes lightColorTemperature. Changing the reflected property ID after Home binds its built-in
			// LightTunable UI to it leaves that UI with a stale command/property contract.
			_registeredColorTemperatureMembers = useEmulatedColorTemperature
				? new EmulatedColorTemperatureMembers (this, initialColorTemperatureMemberRange, initialColorTemperatureLevel)
				: new ColorTemperatureMembers (this, initialColorTemperatureMemberRange, initialColorTemperatureLevel);
			RegisterObjectWithAttributes (_registeredColorTemperatureMembers);
			LogInfo ($"Light entity '{ControllerId}' registered color-temperature dynamic members: useEmulatedColorTemperature={useEmulatedColorTemperature}, initialColorTemperatureLevel={initialColorTemperatureLevel}.");
			}

		if (!_supportsFullColor)
			{
			LogInfo ($"Light entity '{ControllerId}' full-color members not registered because full-color state is not supported.");
			}
		else
			{
			// The device retains real hue/saturation values internally regardless of whether color
			// temperature is currently active (color temperature only overrides the displayed color
			// while active; it does not clear or alter the stored hue/saturation). However, just like
			// the color-temperature level uses an in-range placeholder while color mode is active (see
			// above), the initial lightColor:hue/saturation members must not surface the real retained
			// values while white/CT mode is active - Crestron Home infers the light is in color mode
			// purely from these properties being non-placeholder, regardless of publish order. Seed
			// them from the real values only while color mode is genuinely active; otherwise use
			// neutral placeholders (0).
			int? hue = device.LightState?.Hue ?? device.LightState?.Hsv?.Hue;
			int? saturation = device.LightState?.Saturation ?? device.LightState?.Hsv?.Saturation;
			bool seedRealHueSaturation = !_isColorTemperatureUiModeActive;
			double initialHue = seedRealHueSaturation && hue.HasValue ? Clamp01 (hue.Value / HUE_MAX_DEGREES) : 0d;
			double initialSaturation = seedRealHueSaturation && saturation.HasValue ? Clamp01 (saturation.Value / 100d) : 0d;

			_registeredFullColorMembers = new FullColorMembers (this, initialHue, initialSaturation);
			RegisterObjectWithAttributes (_registeredFullColorMembers);
			LogInfo ($"Light entity '{ControllerId}' registered full-color dynamic members: initialHue={initialHue:0.####}, initialSaturation={initialSaturation:0.####}, seedRealHueSaturation={seedRealHueSaturation}.");
			}

		RaiseDefinitionChangedEvent ();
		LogDynamicFeatureEntityState ("ConfigureDynamicFeatures.AfterDefinitionChanged");

		}

	// This timeout wraps EnsureConnectedAsync (which may need to fully reconnect if the connection was
	// previously reset/dropped) plus the command itself plus an optional post-command refresh. Live
	// logs show the exact same startup-connect slowdown pattern recurring here: after ResetConnectionState
	// drops a stale connection, the ensuing reconnect-then-command sequence routinely exceeds 8 seconds
	// under the same reload/network contention conditions that motivated raising StartupConnectTimeout,
	// causing the command to time out, retry, and often fail a second time while still reconnecting.
	// Align this with StartupConnectTimeout so a reconnect triggered by a command has the same realistic
	// budget as an initial startup connect.
	private static readonly TimeSpan DeviceCommandTimeout = StartupConnectTimeout;

	// A command against a long-idle bulb must first fully reconnect (cold TPAP PAKE handshake) before
	// the command itself can run. Live logs show that after ~8h idle the gate wait + cold handshake
	// alone consumed the entire single command budget (e.g. "acquired connection gate after 15356 ms"
	// then the handshake timed out at 20s), so the actual on/off request never reached the bulb and it
	// never physically switched. Give the connect phase its OWN budget, distinct from the command
	// execution budget, so a cold reconnect gets the same realistic time as an initial startup connect
	// and does not starve the command that follows it.
	private static readonly TimeSpan DeviceConnectTimeout = StartupConnectTimeout;
	private static readonly TimeSpan DeviceCommandRetryDelay = TimeSpan.FromSeconds (1);
	private const int DEVICE_COMMAND_MAX_ATTEMPTS = 2;

	protected async Task ExecuteDeviceCommandAsync (Func<KasaDevice, CancellationToken, Task> action, bool refreshAfterCommand = true, CancellationToken cancellationToken = default, bool reassertColorModeAfterRefresh = false, string? synchronizeProcessorBaselineForPowerOffContext = null)
		{
		for (int attempt = 1; ; attempt++)
			{
			bool isFinalAttempt = attempt >= DEVICE_COMMAND_MAX_ATTEMPTS;
			try
				{
				await ExecuteDeviceCommandAttemptAsync (action, refreshAfterCommand, reassertColorModeAfterRefresh, synchronizeProcessorBaselineForPowerOffContext, isFinalAttempt, cancellationToken).ConfigureAwait (false);
				return;
				}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
				throw;
				}
			catch (Exception ex) when (!isFinalAttempt)
				{
				_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Light entity '{ControllerId}' command attempt {attempt} of {DEVICE_COMMAND_MAX_ATTEMPTS} failed; retrying after {DeviceCommandRetryDelay.TotalSeconds:0} second(s): {ex}");
				await Task.Delay (DeviceCommandRetryDelay, cancellationToken).ConfigureAwait (false);
				}
			}
		}

	private async Task ExecuteDeviceCommandAttemptAsync (Func<KasaDevice, CancellationToken, Task> action, bool refreshAfterCommand, bool reassertColorModeAfterRefresh, string? synchronizeProcessorBaselineForPowerOffContext, bool isFinalAttempt, CancellationToken cancellationToken)
		{
		try
			{
			// Phase 1: connect. EnsureConnectedAsync itself only arms the DeviceConnectTimeout
			// around the actual connect attempt, AFTER the connection gate has been acquired, so
			// contention from other concurrently-dispatched commands (e.g. the paired
			// lightEmulatedColorTemperature:setLevel and lightDimmer:setLevel:on commands fired
			// together for a single power-on gesture) waiting on the same gate no longer eats into
			// the connect budget. Do not additionally timebox the gate wait here.
			KasaDevice device = await EnsureConnectedAsync (cancellationToken).ConfigureAwait (false);

			// Phase 2: command execution (separate budget), now that a live connection exists.
			using CancellationTokenSource timeoutCancellationSource = CancellationTokenSource.CreateLinkedTokenSource (cancellationToken);
			timeoutCancellationSource.CancelAfter (DeviceCommandTimeout);
			CancellationToken timeoutToken = timeoutCancellationSource.Token;

			// Hold a reference count against this device instance for the duration of Phase 2 so that
			// if another concurrently-dispatched command (e.g. the paired lightDimmer/lightEmulated
			// ColorTemperature commands fired together during power-on) times out and calls
			// ResetConnectionState() while THIS operation is still executing against the same device,
			// the device is not disposed until this operation also finishes (see ReleaseDeviceOperation).
			Interlocked.Increment (ref _activeDeviceOperationCount);
			try
				{
				try
					{
					timeoutToken.ThrowIfCancellationRequested ();
					await action (device, timeoutToken).ConfigureAwait (false);

					if (refreshAfterCommand)
						{
						await device.UpdateAsync (timeoutToken).ConfigureAwait (false);
						LogReportedState ("ExecuteDeviceCommandAsync.AfterCommand", device);
						}

					// The driver must never publish anything to the UI while the bulb is off - the only
					// permitted off-state UI action is the baseline sync dispatched below. This refresh
					// following a genuine power-off reads the bulb's real, settled state purely so that
					// baseline sync has accurate live data to work with; it must not surface as UI
					// property notifications, since in the overwhelming common case nothing about the
					// bulb's retained brightness/hue/saturation genuinely changed just because it was
					// turned off.
					bool isGenuinePowerOffRefresh = synchronizeProcessorBaselineForPowerOffContext is not null;
					bool previousSuppressPropertyNotifications = _suppressPropertyNotifications;
					if (isGenuinePowerOffRefresh)
						{
						_suppressPropertyNotifications = true;
						}

					try
						{
						ApplyState (device);
						}
					finally
						{
						if (isGenuinePowerOffRefresh)
							{
							_suppressPropertyNotifications = previousSuppressPropertyNotifications;
							}
						}

					OnlineIndicatorIsOnline = true;
					ReadyIndicatorIsReady = true;

					// On power-on, Crestron's Load layer drives the load's ColorTemp channel and dispatches
					// lightEmulatedColorTemperature:setLevel to the capability layer BEFORE our dimmer
					// power-on runs, switching the UI to white/CCT. PublishActiveColorModeProperties then
					// restores the full-color state when the bulb remains in color mode. The processor's
					// console-level baseline is intentionally NOT touched here: baseline synchronization
					// only ever occurs at startup and immediately before a genuine power-off (see
					// SynchronizeProcessorBaselineForPowerOff) - never during a power-on - so there is
					// nothing to correct on the console mid-power-on, and SynchronizeProcessorBaseline
					// itself is a no-op anyway while the light is on.
					if (reassertColorModeAfterRefresh && LightIsOn && _supportsFullColor && !IsCurrentColorTemperatureUiMode ())
						{
						PublishActiveColorModeProperties ("ExecuteDeviceCommandAsync.ReassertColorModeAfterPowerOn");
						}

					// Baseline synchronization for a genuine power-off must be based on the live state the
					// bulb itself reports AFTER it has actually been turned off (per the user's explicit
					// requirement), not on cached hue/saturation/color-temperature properties captured
					// before the physical off call ran. ApplyState (device) above has already refreshed
					// those properties from the just-completed device.UpdateAsync response, so dispatching
					// here reads exactly that live, settled state. This still runs through the normal
					// fire-and-forget StartBackgroundOperation dispatch, so it never delays returning
					// control back to the caller of the (already-completed) physical off command.
					if (synchronizeProcessorBaselineForPowerOffContext is string powerOffContext)
						{
						SynchronizeProcessorBaselineForPowerOff (powerOffContext);
						}
					}
				catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutToken.IsCancellationRequested)
					{
					throw new TimeoutException ($"Light entity '{ControllerId}' command timed out after {DeviceCommandTimeout.TotalSeconds:0} seconds.");
					}
				}
			finally
				{
				ReleaseDeviceOperation ();
				}
			}
		catch (Exception ex)
			{
			// Do not flip the UI to offline for an attempt that is about to be quietly retried - only
			// once the retry budget is exhausted does this become a real, user-visible offline state.
			// The stale connection is still cleared either way so the next attempt reconnects fresh.
			ResetConnectionState (flipIndicatorsOffline: isFinalAttempt);
			_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Light entity '{ControllerId}' command failed: {ex}");
			throw;
			}
		}

	private static Task SetBrightnessAsync (KasaDevice device, double relativeLevel, CancellationToken cancellationToken)
		{
		int brightness = (int)Math.Round (relativeLevel * 100d, MidpointRounding.AwayFromZero);
		return brightness <= 0
			? device.TurnLightOffAsync (cancellationToken)
			: device.SetBrightnessAsync (brightness, cancellationToken);
		}

	private async Task ExecutePowerAsync (KasaDevice device, bool on, CancellationToken cancellationToken)
		{
		// Both halves of a genuine physical power command may never be suppressed, delayed, or
		// silently dropped here - EXCEPT for the deterministic duration of the awaited startup
		// processor baseline synchronization: that window is the only time the processor console
		// workaround ever performs its own off->on SetLoadState toggle, and Crestron's own Load
		// layer relays BOTH halves of that toggle back to this entity as indistinguishable genuine
		// light:off/light:on (or dimmer) commands. Nothing the entity has done yet during that
		// window is a real user action - the entity has not even published its initial state - so
		// both relayed halves must be suppressed, not just the "on" half: letting the relayed "off"
		// half reach the physical device would actually turn off a bulb that was genuinely on before
		// the reload, even though the toggle that produced it never touched the real bulb itself.
		// The flag's narrow, startup-only scope means this can never suppress an actual
		// user-requested power command outside that window.
		if (_isInitializingProcessorBaseline)
			{
			LogInfo ($"Light entity '{ControllerId}' suppressing physical device power-{(on ? "on" : "off")} call relayed from the startup processor baseline synchronization toggle.");
			return;
			}

		string? childId = ChildId;
		if (!string.IsNullOrWhiteSpace (childId))
			{
			string resolvedChildId = childId!;
			if (on)
				{
				await device.TurnChildOnAsync (resolvedChildId, cancellationToken).ConfigureAwait (false);
				}
			else
				{
				await device.TurnChildOffAsync (resolvedChildId, cancellationToken).ConfigureAwait (false);
				}

			return;
			}

		if (Kind == ManagedLightKind.OnOff)
			{
			if (on)
				{
				await device.TurnOnAsync (cancellationToken).ConfigureAwait (false);
				}
			else
				{
				await device.TurnOffAsync (cancellationToken).ConfigureAwait (false);
				}

			return;
			}

		if (!on)
			{
			await device.TurnLightOffAsync (cancellationToken).ConfigureAwait (false);
			return;
			}

		await device.TurnLightOnAsync (cancellationToken).ConfigureAwait (false);
		}

	}

using System;
using System.Threading;
using System.Threading.Tasks;

using Crestron.DeviceDrivers.EntityModel.Logging;
using Crestron.DeviceDrivers.SDK.EntityModel.Attributes;

using KasaTapoClient;

namespace KasaTapoCrestronDriver;

internal partial class KasaLightEntity
	{
	public bool TryAttachConnectedDevice (KasaDevice device, string context)
		{
		if (device is null)
			{
			throw new ArgumentNullException (nameof (device));
			}

		if (_disposed || Volatile.Read (ref _stopState) != 0)
			{
			return false;
			}

		// KasaClient's Discover now guarantees a single shared, persistent KasaDevice per
		// Host:Port, so there is no longer a distinct-instance race to guard against here - the
		// only remaining concern is not clobbering an already-attached device on this entity.
		// A lock-free compare-and-set is sufficient in place of the old connection gate. Because
		// Discover can hand back the SAME shared instance this entity already has attached (e.g.
		// alias-enrichment resolving to the entity's own live device), treat that case as an
		// already-satisfied attach rather than a failure - the caller (EnrichDiscoveryAliasCacheAsync)
		// disposes the device on a false return, which must never happen to a device this entity is
		// actively using.
		KasaDevice? previousDevice = Interlocked.CompareExchange (ref _connectedDevice, device, null);
		if (ReferenceEquals (previousDevice, device))
			{
			return true;
			}

		if (previousDevice is not null)
			{
			return false;
			}

		UpdateDescriptorFromConnectedDevice (device);
		LogInfo ($"Light entity '{ControllerId}' adopted connected device from context='{context}'.");
		return true;
		}

	[EntityProperty (Id = "onlineIndicator:isOnline")]
	public bool OnlineIndicatorIsOnline
		{
		get;
		private set => SetAndNotify ("onlineIndicator:isOnline", value, ref field);
		}

	[EntityProperty (Id = "readyIndicator:isReady")]
	public bool ReadyIndicatorIsReady
		{
		get;
		private set => SetAndNotify ("readyIndicator:isReady", value, ref field);
		}

	// Individual channel command for full-color bulbs, paired with the lightColor:hue property. Crestron
	// Home's classic per-channel lighting engine (IRpcLights.SetTunableChannelState) invokes this when
	// the color wheel is dragged. Switches the bulb into color/HSV mode and re-dispatches the full
	// hue/saturation/brightness state.
	private void LightColorSetHue (double hue)
		{
		double hueLevel = Clamp01 (hue);
		LogCommandInvocation ("lightColor:setHue", $"hue={hueLevel:0.####}");

		if (!LightIsOn)
			{
			// While off, this can be a passive Crestron Home Load-layer replay of a stored hue value
			// against a bulb that is genuinely in white/CT mode (mirroring the dimmer replay pattern
			// documented elsewhere in this file), not a genuine user color selection. The driver must
			// never publish anything to the UI while the bulb is off - the only permitted off-state
			// UI action is the baseline sync that happens immediately after a genuine on->off
			// transition (see SynchronizeProcessorBaselineForPowerOff) - so only update this entity's
			// own internal tracking fields here, silently (no SetAndNotify/publish), and do not touch
			// the processor baseline.
			bool previousSuppressPropertyNotifications = _suppressPropertyNotifications;
			_suppressPropertyNotifications = true;
			try
				{
				_isColorTemperatureUiModeActive = false;
				LightColorHue = hueLevel;
				}
			finally
				{
				_suppressPropertyNotifications = previousSuppressPropertyNotifications;
				}
			return;
			}

		// While white/CT mode is active, ApplyStateCore intentionally leaves LightColorSaturation at
		// its neutral placeholder (0) rather than the device's real retained value (see its remarks).
		// If the user switches into color mode via the hue channel while that placeholder is still in
		// effect, dispatching hue alongside saturation=0 produces a fully desaturated (i.e. white/gray)
		// HSV command - the bulb never visibly changes color no matter what hue is selected, because
		// zero saturation IS "no color". Seed a full-saturation default in that specific transition so
		// the requested hue is actually visible; a genuine subsequent lightColor:setSaturation command
		// from the UI still overrides this default normally. This only applies to a live, powered-on
		// interaction (never the whileOff snapshot path above) so a passive replay while off cannot
		// fabricate a color state for a bulb that is really in CT mode.
		double saturationLevel = LightColorSaturation;
		if (_isColorTemperatureUiModeActive && saturationLevel <= 0d)
			{
			saturationLevel = 1d;
			}

		// Baseline synchronization is intentionally not performed here: SynchronizeProcessorBaseline
		// itself is a no-op whenever the light is already on (see its remarks), and this branch only
		// ever runs once LightIsOn has been confirmed true above, so any call here would always be
		// dead code. The console-level baseline is corrected at startup and immediately before the
		// next genuine power-off instead (see SynchronizeProcessorBaselineForPowerOff).
		_isColorTemperatureUiModeActive = false;
		LightColorHue = hueLevel;
		LightColorSaturation = saturationLevel;
		QueueSliderCommand (new DesiredLightCommand (DesiredLightMode.Hsv, LightDimmerLevel, hueLevel, saturationLevel, 0L));
		}

	private void LightColorSetSaturation (double saturation)
		{
		double saturationLevel = Clamp01 (saturation);
		LogCommandInvocation ("lightColor:setSaturation", $"saturation={saturationLevel:0.####}");

		if (!LightIsOn)
			{
			// See the identical remark in LightColorSetHue's whileOff branch: the driver must never
			// publish anything to the UI while the bulb is off, so only update internal tracking
			// fields silently here.
			bool previousSuppressPropertyNotifications = _suppressPropertyNotifications;
			_suppressPropertyNotifications = true;
			try
				{
				_isColorTemperatureUiModeActive = false;
				LightColorSaturation = saturationLevel;
				}
			finally
				{
				_suppressPropertyNotifications = previousSuppressPropertyNotifications;
				}
			return;
			}

		// See the identical remark in LightColorSetHue: this branch only runs once LightIsOn is
		// already confirmed true, so SynchronizeProcessorBaseline would always be a no-op here.
		_isColorTemperatureUiModeActive = false;
		LightColorSaturation = saturationLevel;
		QueueSliderCommand (new DesiredLightCommand (DesiredLightMode.Hsv, LightDimmerLevel, LightColorHue, saturationLevel, 0L));
		}

	private void LightDimmerSetLevel (double level)
		{
		double relativeLevel = Clamp01 (level);
		LogCommandInvocation ("lightDimmer:setLevel", $"level={relativeLevel:0.####}");

		// Detect and ignore Crestron Home's own post-reload Load-layer replay of a stale remembered
		// level (observed on-disk as level=0 then level=0.01 arriving within a couple of seconds of
		// InitializeConnectedStateAsync publishing the bulb's real, already-on, higher-brightness
		// state) - see the guard fields' remarks above. Applying these to the device was what dimmed
		// an already-on/high-brightness bulb down to 1% immediately after every reload.
		//
		// Both halves of that replay pair must be caught here, not just the second: the
		// lightDimmer:level slider representation cannot express "brightness while off" (level 0
		// always means off in that UI/entity model - see the DimmableMembers/LightIsOn comment
		// above), so the very fact that Crestron follows up level=0 with level=0.01 a moment later
		// proves the first level=0 was never a real user power-off either - it is simply the first
		// half of the same stale replay. Checking LightIsOn
		// here (rather than requiring it to still be true) intentionally also matches immediately
		// after we ourselves already let a replayed level=0 through and flipped LightIsOn to false, so
		// this must key off the guard window rather than the current on/off state.
		if (_startupDimmerReplayGuardUntilUtc is DateTime guardUntilUtc)
			{
			if (DateTime.UtcNow <= guardUntilUtc && relativeLevel <= STARTUP_DIMMER_REPLAY_SUSPECT_LEVEL_THRESHOLD)
				{
				LogInfo ($"Light entity '{ControllerId}' ignoring suspected post-startup Load-layer dimmer replay: level={relativeLevel:0.####}, realLevel={_startupDimmerReplayRealLevel:0.####}.");
				LightDimmerLevel = _startupDimmerReplayRealLevel;
				LightIsOn = true;
				PublishCurrentLightModeProperties ("lightDimmer:setLevel:ignoredStartupReplay", synchronizeProcessorBaseline: false);
				return;
				}

			_startupDimmerReplayGuardUntilUtc = null;
			}

		// Capture whether the bulb was on BEFORE mutating LightDimmerLevel below: for dimmable-only
		// bulbs (no OnOffMembers - see the LightIsOn getter's remarks), LightIsOn is derived live
		// from LightDimmerLevel itself (> 0 = on), so assigning LightDimmerLevel first would make
		// LightIsOn already read true by the time the off->on transition is checked below, causing
		// a genuine level=1 power-on request to be misidentified as "already on" and fall through
		// to the brightness dispatch path, which then explicitly sets the device to 100% brightness
		// instead of preserving whatever brightness the bulb already retained while off.
		bool wasOnBeforeThisCommand = LightIsOn;

		// For dimmable bulbs the extreme dimmer levels are the power commands: level 0 = power off,
		// level 1 = power on - but only genuinely when that represents an actual off->on transition.
		// If the bulb is already on and the user simply drags the slider to full brightness, level 1
		// is a real 100% brightness request, not a power command: routing it through ExecutePowerAsync
		// (which only calls TurnLightOnAsync and never touches brightness) leaves the device at
		// whatever brightness it already had, and the follow-up refreshAfterCommand then reads back
		// that unchanged (lower) brightness and republishes it - visibly "retreating" the slider/bulb
		// right after the drag reached 100%. So the power-on shortcut below only applies while the
		// bulb is confirmed off; otherwise level 1 falls through to the normal slider/brightness
		// dispatch path so the device is actually set to full brightness.
		//
		// These must NEVER be routed through the debounced slider path when they really are power
		// commands - even if they arrive during an in-flight slider drag - because that path marks the
		// subsequent device-driven state re-apply as deferred. During power-on Crestron's own Load
		// layer drives the load's ColorTemp channel (SetMultipleChannels ... ColorTemp), and deferring
		// our re-apply lets that Load-layer drive be the last word, flipping a color-mode bulb to
		// white/CCT in the UI. Cancel any pending slider interaction and dispatch the extremes directly
		// as power commands (identical to light:on / light:off), so the settled device state is applied
		// immediately and authoritatively.
		if (relativeLevel <= 0d)
			{
			LightDimmerLevel = relativeLevel;
			CancelSliderInteraction ();
			LightIsOn = false;

			// Only a genuine on->off transition warrants baseline synchronization: the processor's
			// stored baseline already reflects whatever mode was last actually applied, so replaying
			// (or re-sending) an off command while the bulb is already confirmed off - e.g. pressing
			// the UI off button again minutes later, or a Load-layer replay - has no real state change
			// to synchronize and must not re-invoke the baseline coordinator at all.
			string? powerOffContext = wasOnBeforeThisCommand ? "lightDimmer:setLevel:off" : null;
			StartBackgroundOperation (() => ExecuteDeviceCommandAsync ((device, cancellationToken) => ExecutePowerAsync (device, false, cancellationToken), refreshAfterCommand: true, _lifetimeCancellationSource.Token, synchronizeProcessorBaselineForPowerOffContext: powerOffContext), "lightDimmer:setLevel:off");
			return;
			}

		if (relativeLevel >= 1d && !wasOnBeforeThisCommand)
			{
			// The incoming level=1 here is Crestron's own power-on gesture value (its dimmer slider
			// UI always sends the "full" extreme to mean "turn on"), not a genuine 100% brightness
			// request - see the remarks above. ExecutePowerAsync only calls TurnLightOnAsync and
			// never touches brightness, so the device is actually about to come on at whatever
			// brightness it already retained while off. Publishing the raw level=1 here would
			// optimistically flash the UI to 100% for the ~1-2 seconds until refreshAfterCommand
			// reads the real (lower) brightness back and republishes it - exactly the visible delay
			// being reported. Publish the retained last-known real brightness instead so the UI
			// reflects the true settled state immediately, with no flash.
			LightDimmerLevel = GetEffectiveOnLevel ();
			CancelSliderInteraction ();
			LightIsOn = true;
			StartBackgroundOperation (() => ExecuteDeviceCommandAsync ((device, cancellationToken) => ExecutePowerAsync (device, true, cancellationToken), refreshAfterCommand: true, _lifetimeCancellationSource.Token, reassertColorModeAfterRefresh: true), "lightDimmer:setLevel:on");
			return;
			}

		LightDimmerLevel = relativeLevel;
		QueueSliderCommand (new DesiredLightCommand (DesiredLightMode.Brightness, relativeLevel, 0d, 0d, 0L));
		}

	// Registered only for simple on/off bulbs (see OnOffMembers/ConfigureDynamicFeatures) - those
	// bulbs have no color/CT mode and no processor console baseline concept at all, so this must
	// never touch SynchronizeProcessorBaselineForPowerOff.
	private void LightOff ()
		{
		LogCommandInvocation ("light:off", "requested power off");

		CancelSliderInteraction ();
		LightIsOn = false;
		StartBackgroundOperation (() => ExecuteDeviceCommandAsync ((device, cancellationToken) => ExecutePowerAsync (device, false, cancellationToken), refreshAfterCommand: true, _lifetimeCancellationSource.Token), "light:off");
		}

	private void LightOn ()
		{
		LogCommandInvocation ("light:on", "requested power on");

		_lastPowerOnRequestedUtc = DateTime.UtcNow;
		LightIsOn = true;
		StartBackgroundOperation (() => ExecuteDeviceCommandAsync ((device, cancellationToken) => ExecutePowerAsync (device, true, cancellationToken), refreshAfterCommand: true, _lifetimeCancellationSource.Token), "light:on");
		}

	private void SetColorTemperatureLevel (string commandId, long level)
		{
		string powerOnCorrelation = _lastPowerOnRequestedUtc is DateTime powerOnUtc
			? $"msSincePowerOn={(DateTime.UtcNow - powerOnUtc).TotalMilliseconds:0}"
			: "msSincePowerOn=<none>";
		LogCommandInvocation (commandId, $"level={level}, uiModeWasActive={_isColorTemperatureUiModeActive}, {powerOnCorrelation}");
		long temperatureLevel = Math.Max (1L, level);
		if (!LightIsOn)
			{
			// The UI sends this immediately before its dimmer power-on command, as an artifact of
			// Crestron's own Load layer driving the load's ColorTemp channel - not a genuine
			// user-requested mode change. The bulb's actual last-known state may still be full
			// color, so do NOT synchronize the processor baseline to White here: doing so caches a
			// White baseline before the device is ever confirmed to have changed mode, which then
			// causes the baseline coordinator to see a false "already matches" on the subsequent
			// power-on and skip the real color-mode correction.
			//
			// Likewise, do NOT flip _isColorTemperatureUiModeActive (and therefore do NOT publish
			// this as an active white-mode UI property) when the bulb's last confirmed real state
			// (from ApplyStateCore, reflecting the actual device) is genuinely in color mode. This
			// artifact fires unconditionally regardless of the bulb's real mode, so blindly trusting
			// it here would incorrectly flip the UI to White for a bulb that is really in Color mode
			// - the subsequent ExecuteDeviceCommandAsync.ReassertColorModeAfterPowerOn already
			// restores the real mode once the device is queried after power-on, so there is nothing
			// to gain by asserting White mode from this pre-power-on artifact alone. Only cache the
			// UI-facing level for later use; the mode/baseline is settled once the light is actually
			// turned on and its real state is read back.
			//
			// Crucially, do NOT call SetActiveColorTemperatureLevel here when confirmed color mode is
			// active either: that call directly sets the lightEmulatedColorTemperature:level property
			// via SetAndNotify, which pushes the new Kelvin value straight to the Crestron UI and is
			// enough on its own to flip the on-screen slider to White/CCT - independent of whatever
			// this driver's own _isColorTemperatureUiModeActive flag says afterward. Skipping the
			// level update entirely (in addition to skipping the mode flag and publish) is required
			// to fully suppress this artifact while the bulb is confirmed to be in color mode.
			// The driver must never publish anything to the UI while the bulb is off - the only
			// permitted off-state UI action is the baseline sync that runs immediately after a
			// genuine on->off transition (see SynchronizeProcessorBaselineForPowerOff). Crestron's
			// own Load layer optimistically renders its own UI slider from this artifact
			// independent of anything this driver does, so racing to counter-publish here gains
			// nothing: the real, settled color/CT mode is authoritatively restored once the light
			// actually turns on, via ExecuteDeviceCommandAsync.ReassertColorModeAfterPowerOn
			// re-applying it from live device state. Only cache the level internally, silently,
			// for later use when the bulb is confirmed to be in white/CT mode.
			if (_isColorTemperatureUiModeActive)
				{
				bool previousSuppressPropertyNotifications = _suppressPropertyNotifications;
				_suppressPropertyNotifications = true;
				try
					{
					SetActiveColorTemperatureLevel (temperatureLevel);
					}
				finally
					{
					_suppressPropertyNotifications = previousSuppressPropertyNotifications;
					}
				}
			return;
			}

		_isColorTemperatureUiModeActive = true;
		SetActiveColorTemperatureLevel (temperatureLevel);

		QueueSliderCommand (new DesiredLightCommand (DesiredLightMode.ColorTemperature, GetEffectiveOnLevel (), 0d, 0d, temperatureLevel));
		}

	private void QueueSliderCommand (DesiredLightCommand command)
		{
		CancellationTokenSource? cancellationSource = null;
		lock (_sliderGate)
			{
			// Always record the latest slider value; a running tracking worker will pick it up on its
			// next iteration. Only spin up a new worker (and its cancellation source) when none is
			// currently running, so a drag is served by a single long-lived worker that tracks the
			// light live instead of a fresh cancel-per-value debounce that only fires once on release.
			_pendingSliderCommand = command;
			if (!_sliderWorkerRunning)
				{
				_sliderWorkerRunning = true;
				_sliderCommandCancellationSource?.Dispose ();
				_sliderCommandCancellationSource = new CancellationTokenSource ();
				cancellationSource = _sliderCommandCancellationSource;
				}
			}

		if (cancellationSource is not null)
			{
			_ = ProcessSliderCommandAsync (cancellationSource.Token);
			}
		}

	private void StartBackgroundOperation (Func<Task> operation, string operationName)
		{
		// This must genuinely hand off to the thread pool via Task.Run rather than invoking
		// operation() directly here: some downstream work (e.g. ProcessorBaselineCoordinator's
		// console round-trip, which blocks the calling thread with Thread.Sleep while polling for
		// an SSH response) is not written to yield at a real async boundary until well after it has
		// already done its blocking work. Invoking operation() inline therefore ran that blocking
		// work synchronously on the caller's thread - stalling e.g. LightDimmerSetLevel's off-path
		// for the full multi-second SSH round-trip before it could even reach the line that starts
		// the actual physical power-off dispatch. Task.Run guarantees the delegate (and any
		// synchronous blocking prefix within it) runs on a thread-pool thread instead, so callers
		// of StartBackgroundOperation return immediately regardless of what the operation does
		// before its first real await.
		Task task = Task.Run (() => operation ());

		task.ContinueWith (
			continuationTask =>
				{
					if (continuationTask.IsFaulted)
						{
						Exception exception = continuationTask.Exception?.GetBaseException () ?? continuationTask.Exception!;
						_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Light entity '{ControllerId}' background operation '{operationName}' failed: {exception}");
						}
					else if (continuationTask.IsCanceled)
						{
						LogInfo ($"Light entity '{ControllerId}' background operation '{operationName}' was canceled.");
						}
				},
			CancellationToken.None,
			TaskContinuationOptions.None,
			TaskScheduler.Default);
		}

	private async Task ProcessSliderCommandAsync (CancellationToken cancellationToken)
		{
		bool reapplyResolvedState = false;
		try
			{
			// Optimistic tracking loop: keep dispatching the latest slider value to the physical light
			// as the drag progresses (throttled, no device refresh so we don't fight the incoming
			// stream), and only exit when the slider has settled - i.e. no new value arrived during a
			// full throttle interval. The final settled value is then dispatched authoritatively with a
			// device refresh outside the loop.
			DesiredLightCommand? finalCommand = null;
			CancellationToken lifetimeToken = _lifetimeCancellationSource.Token;

			while (true)
				{
				await Task.Delay (SliderTrackingDispatchInterval, cancellationToken).ConfigureAwait (false);

				DesiredLightCommand? command;
				bool settled;
				lock (_sliderGate)
					{
					if (cancellationToken.IsCancellationRequested || _sliderCommandCancellationSource is null || _sliderCommandCancellationSource.Token != cancellationToken)
						{
						return;
						}

					command = _pendingSliderCommand;
					_pendingSliderCommand = null;

					// No new value arrived during this interval: the slider has settled. Clear the
					// worker flag atomically so a subsequent slider value starts a fresh worker, and
					// exit the tracking loop to perform the final authoritative sync.
					settled = !command.HasValue;
					if (settled)
						{
						_sliderWorkerRunning = false;
						reapplyResolvedState = true;
						}
					}

				if (settled)
					{
					break;
					}

				finalCommand = command;
				lifetimeToken.ThrowIfCancellationRequested ();

				// Intermediate live-tracking dispatch: drive the device without a follow-up refresh so
				// the light visibly tracks the slider without the refresh fighting the ongoing drag.
				await DispatchSliderCommandAsync (command!.Value, refreshAfterCommand: false, lifetimeToken).ConfigureAwait (false);
				}

			// Final authoritative sync of the settled value (with device refresh). finalCommand holds
			// the last value we actually dispatched during the drag.
			if (finalCommand.HasValue)
				{
				lifetimeToken.ThrowIfCancellationRequested ();
				LogInfo ($"Light entity '{ControllerId}' final slider sync: {FormatDesiredLightCommand (finalCommand.Value)}.");
				await DispatchSliderCommandAsync (finalCommand.Value, refreshAfterCommand: true, lifetimeToken).ConfigureAwait (false);
				}
			}
		catch (OperationCanceledException)
			{
			}
		catch (Exception ex)
			{
			ResetConnectionState ();
			_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Light entity '{ControllerId}' slider command failed: {ex}");
			}
		finally
			{
			lock (_sliderGate)
				{
				if (_sliderCommandCancellationSource is not null && _sliderCommandCancellationSource.Token == cancellationToken)
					{
					_sliderWorkerRunning = false;
					_sliderCommandCancellationSource.Dispose ();
					_sliderCommandCancellationSource = null;
					}
				}

			if (reapplyResolvedState)
				{
				try
					{
					TryApplyDeferredDeviceStateAfterSliderInteraction ("ProcessSliderCommandAsync");
					}
				catch (Exception ex)
					{
					_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Light entity '{ControllerId}' post-slider state apply failed: {ex}");
					}
				}
			}
		}

	private async Task DispatchSliderCommandAsync (DesiredLightCommand command, bool refreshAfterCommand, CancellationToken lifetimeToken)
		{
		LogInfo ($"Light entity '{ControllerId}' dispatch slider command: {FormatDesiredLightCommand (command)} (refresh={refreshAfterCommand}).");

		await ExecuteDeviceCommandAsync (
			async (device, innerCancellationToken) =>
				{
					innerCancellationToken.ThrowIfCancellationRequested ();
					await ApplyDesiredLightCommandAsync (device, command, innerCancellationToken).ConfigureAwait (false);
				},
			refreshAfterCommand: refreshAfterCommand,
			lifetimeToken).ConfigureAwait (false);
		}

	private void CancelSliderInteraction ()
		{
		lock (_sliderGate)
			{
			_pendingSliderCommand = null;
			_sliderWorkerRunning = false;
			_sliderCommandCancellationSource?.Cancel ();
			_sliderCommandCancellationSource?.Dispose ();
			_sliderCommandCancellationSource = null;
			}
		}

	private bool IsSliderInteractionActive ()
		{
		lock (_sliderGate)
			{
			return _sliderCommandCancellationSource is not null || _pendingSliderCommand.HasValue;
			}
		}

	private async Task ApplyDesiredLightCommandAsync (KasaDevice device, DesiredLightCommand command, CancellationToken cancellationToken)
		{
		switch (command.Mode)
			{
			case DesiredLightMode.Off:
				await device.TurnLightOffAsync (cancellationToken).ConfigureAwait (false);
				return;

			case DesiredLightMode.On:
				await device.TurnLightOnAsync (cancellationToken).ConfigureAwait (false);
				return;

			case DesiredLightMode.Hsv:
				await device.SetHsvAsync (
					(int)Math.Round (Clamp01 (command.Hue) * HUE_MAX_DEGREES, MidpointRounding.AwayFromZero),
					(int)Math.Round (Clamp01 (command.Saturation) * 100d, MidpointRounding.AwayFromZero),
					ToBrightnessPercent (command.Level),
					cancellationToken).ConfigureAwait (false);
				return;

			case DesiredLightMode.ColorTemperature:
				await EnsurePoweredForLevelAsync (device, command.Level, cancellationToken).ConfigureAwait (false);
				await device.SetBrightnessAsync (ToBrightnessPercent (command.Level), cancellationToken).ConfigureAwait (false);
				await device.SetColorTemperatureAsync ((int)Math.Max (1L, command.ColorTemperature), cancellationToken).ConfigureAwait (false);
				return;

			case DesiredLightMode.Brightness:
				await ApplyBrightnessCommandAsync (device, command.Level, cancellationToken).ConfigureAwait (false);
				return;
			}
		}

	}

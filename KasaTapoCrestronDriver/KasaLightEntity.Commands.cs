// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE file in the project root for full license information.

using Crestron.DeviceDrivers.EntityModel.Logging;
using Crestron.DeviceDrivers.SDK.EntityModel.Attributes;

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

	// Desired channel values are kept separately from confirmed entity properties. Paired
	// hue/saturation commands must compose without publishing intent as device feedback.
	private DesiredLightCommand? _requestedSliderCommand;

	private DesiredLightCommand? GetRequestedSliderCommand ()
		{
		lock (_sliderGate) return _requestedSliderCommand;
		}

	private void LightColorSetHue (double hue)
		{
		double hueLevel = Clamp01 (hue);
		LogCommandInvocation ("lightColor:setHue", $"hue={hueLevel:0.####}");
		// Ignore passive Load-layer colour replays while the light is confirmed off.
		if (!LightIsOn) return;
		DesiredLightCommand? requested = GetRequestedSliderCommand ();
		double saturation = requested is { Mode: DesiredLightMode.Hsv } hsv ? hsv.Saturation : LightColorSaturation;
		if (requested is not { Mode: DesiredLightMode.Hsv } && _isColorTemperatureUiModeActive && saturation <= 0d)
			saturation = 1d;
		QueueSliderCommand (new DesiredLightCommand (DesiredLightMode.Hsv, requested?.Level ?? LightDimmerLevel, hueLevel, saturation, 0L));
		}

	private void LightColorSetSaturation (double saturation)
		{
		double saturationLevel = Clamp01 (saturation);
		LogCommandInvocation ("lightColor:setSaturation", $"saturation={saturationLevel:0.####}");
		if (!LightIsOn) return;
		DesiredLightCommand? requested = GetRequestedSliderCommand ();
		double hue = requested is { Mode: DesiredLightMode.Hsv } hsv ? hsv.Hue : LightColorHue;
		QueueSliderCommand (new DesiredLightCommand (DesiredLightMode.Hsv, requested?.Level ?? LightDimmerLevel, hue, saturationLevel, 0L));
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
		// Guard both halves of the stale replay without changing confirmed state.
		if (_startupDimmerReplayGuardUntilUtc is DateTime guardUntilUtc)
			{
			if (DateTime.UtcNow <= guardUntilUtc && relativeLevel <= STARTUP_DIMMER_REPLAY_SUSPECT_LEVEL_THRESHOLD)
				{
				LogInfo ($"Light entity '{ControllerId}' ignoring suspected post-startup Load-layer dimmer replay: level={relativeLevel:0.####}, realLevel={_startupDimmerReplayRealLevel:0.####}.");
				PublishCurrentLightModeProperties ("lightDimmer:setLevel:ignoredStartupReplay", synchronizeProcessorBaseline: false);
				return;
				}

			_startupDimmerReplayGuardUntilUtc = null;
			}

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
			CancelSliderInteraction ();

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
			// Crestron's on gesture restores retained brightness. Only readback may
			// publish that brightness or report the light as on.
			CancelSliderInteraction ();
			StartBackgroundOperation (() => ExecuteDeviceCommandAsync ((device, cancellationToken) => ExecutePowerAsync (device, true, cancellationToken), refreshAfterCommand: true, _lifetimeCancellationSource.Token, reassertColorModeAfterRefresh: true), "lightDimmer:setLevel:on");
			return;
			}

		DesiredLightCommand? requested = GetRequestedSliderCommand ();
		QueueSliderCommand (requested is { Mode: DesiredLightMode.Hsv } hsv
			? new DesiredLightCommand (DesiredLightMode.Hsv, relativeLevel, hsv.Hue, hsv.Saturation, 0L)
			: requested is { Mode: DesiredLightMode.ColorTemperature } white
				? new DesiredLightCommand (DesiredLightMode.ColorTemperature, relativeLevel, 0d, 0d, white.ColorTemperature)
				: new DesiredLightCommand (DesiredLightMode.Brightness, relativeLevel, 0d, 0d, 0L));
		}

	// Registered only for simple on/off bulbs (see OnOffMembers/ConfigureDynamicFeatures) - those
	// bulbs have no color/CT mode and no processor console baseline concept at all, so this must
	// never touch SynchronizeProcessorBaselineForPowerOff.
	private void LightOff ()
		{
		LogCommandInvocation ("light:off", "requested power off");

		CancelSliderInteraction ();
		StartBackgroundOperation (() => ExecuteDeviceCommandAsync ((device, cancellationToken) => ExecutePowerAsync (device, false, cancellationToken), refreshAfterCommand: true, _lifetimeCancellationSource.Token), "light:off");
		}

	private void LightOn ()
		{
		LogCommandInvocation ("light:on", "requested power on");

		_lastPowerOnRequestedUtc = DateTime.UtcNow;
		StartBackgroundOperation (() => ExecuteDeviceCommandAsync ((device, cancellationToken) => ExecutePowerAsync (device, true, cancellationToken), refreshAfterCommand: true, _lifetimeCancellationSource.Token), "light:on");
		}

	private void SetColorTemperatureLevel (string commandId, long level)
		{
		string powerOnCorrelation = _lastPowerOnRequestedUtc is DateTime powerOnUtc
			? $"msSincePowerOn={(DateTime.UtcNow - powerOnUtc).TotalMilliseconds:0}"
			: "msSincePowerOn=<none>";
		LogCommandInvocation (commandId, $"level={level}, uiModeWasActive={_isColorTemperatureUiModeActive}, {powerOnCorrelation}");
		long temperatureLevel = Math.Max (1L, level);
		// A colour-temperature command while off can be a passive Load-layer replay.
		// It must not mutate confirmed properties or change the retained colour mode.
		if (!LightIsOn) return;

		QueueSliderCommand (new DesiredLightCommand (DesiredLightMode.ColorTemperature, GetRequestedSliderCommand ()?.Level ?? GetEffectiveOnLevel (), 0d, 0d, temperatureLevel));
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
			_requestedSliderCommand = command;
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
		try
			{
			// Tracking loop: keep dispatching the latest slider value to the physical light
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
					_requestedSliderCommand = null;
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
			_requestedSliderCommand = null;
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
				await SetLightPowerAsync (device, false, cancellationToken).ConfigureAwait (false);
				return;

			case DesiredLightMode.On:
				await SetLightPowerAsync (device, true, cancellationToken).ConfigureAwait (false);
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

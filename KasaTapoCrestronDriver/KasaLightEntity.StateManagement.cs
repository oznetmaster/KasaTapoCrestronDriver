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

using KasaTapoClient;

using KasaDeviceType = KasaTapoClient.DeviceType;

namespace KasaTapoCrestronDriver;

internal partial class KasaLightEntity
	{
	private void ApplyState (KasaDevice device, bool allowDeferredDeviceStateDuringSliderInteraction = false)
		{
		if (!allowDeferredDeviceStateDuringSliderInteraction && IsSliderInteractionActive ())
			{
			_deferredDeviceStatePending = true;
			LogInfo ($"Light entity '{ControllerId}' deferring device-driven state apply while slider interaction is active.");
			return;
			}

		_deferredDeviceStatePending = false;

		// Do not force-republish state here: ApplyStateCore already publishes each property
		// individually (via SetAndNotify) only when its value actually changes. The UI is the one
		// that issued the command (e.g. on/off) and already knows all other property values from the
		// initial connect, so unconditionally re-publishing everything on every command - including a
		// plain power toggle that changes nothing else - is both unnecessary and can cause the UI to
		// misinterpret the forced re-publish order as an actual color/white mode change.
		using (PropertyChangeTracker.StartBatchedUpdate (true))
			{
			ApplyStateCore (device);
			}
		}

	private void ApplyStateCore (KasaDevice device)
		{
		bool isOn = ResolvePowerState (device);
		int? brightness = device.LightState?.Brightness;
		int? hue = device.LightState?.Hue ?? device.LightState?.Hsv?.Hue;
		int? saturation = device.LightState?.Saturation ?? device.LightState?.Hsv?.Saturation;
		int? colorTemperature = device.LightState?.ColorTemperature;
		bool hasActiveColorTemperature = HasCurrentColorTemperatureState (device.LightState);

		LightIsOn = isOn;

		_isColorTemperatureUiModeActive = _supportsColorTemperature && hasActiveColorTemperature;

		if (_supportsBrightness)
			{
			if (brightness.HasValue)
				{
				// Track the device's real reported brightness independent of power state: legacy
				// bulbs genuinely retain their own last brightness while off (confirmed via device
				// status showing State=Off with a real Brightness value), so this must not be
				// cleared just because the bulb is off.
				_lastKnownDeviceBrightnessLevel = Clamp01 (brightness.Value / 100d);
				}

			LightDimmerLevel = isOn && brightness.HasValue
				? Clamp01 (brightness.Value / 100d)
				: 0d;
			}

		if (_supportsColorTemperature && hasActiveColorTemperature)
			{
			// Only update the color-temperature level when the device is actually in white/CT mode and
			// reports a real value. Kasa/Tapo devices report color_temp=0 while in HSV/color mode and,
			// unlike hue/saturation, do not retain a "last known" Kelvin value that can be read back
			// once color mode is active - so there is nothing meaningful to apply in color mode.
			// The published level is always kept within the device's real declared range (its per-device
			// minimum is used as the in-range placeholder while in color mode - see ConfigureDynamicFeatures).
			// Go through the normal SetAndNotify path (not silently) so that a genuine external change
			// to the level (e.g. changed via the Kasa app while off) is actually published to the UI;
			// SetAndNotify already no-ops when the value is unchanged, so this never causes a spurious
			// publish on its own.
			SetActiveColorTemperatureLevel (colorTemperature!.Value);
			}
		if (_supportsFullColor)
			{
			// Kasa/Tapo devices retain their hue/saturation values independently of color temperature -
			// color temperature simply overrides the displayed color while active, without clearing or
			// altering the stored hue/saturation. However, surfacing those real retained values to the
			// UI while white/CT mode is genuinely active asserts color mode to Crestron Home regardless
			// of publish order (mirroring how publishing a CT level asserts white mode - see
			// ShouldPublishColorTemperatureLevel), so only apply the real values while color mode is
			// genuinely active; otherwise leave the published hue/saturation at their neutral
			// placeholders until the bulb actually returns to color mode.
			if (!hasActiveColorTemperature)
				{
				if (hue.HasValue)
					{
					LightColorHue = Clamp01 (hue.Value / HUE_MAX_DEGREES);
					}

				if (saturation.HasValue)
					{
					LightColorSaturation = Clamp01 (saturation.Value / 100d);
					}
				}
			}

		OnStateApplied (device);
		}

	private static bool HasCurrentColorTemperatureState (LightState? lightState)
		{
		// To the bulb itself, color_temp > 0 is the sole and authoritative signal that white/CT
		// mode is active; color_temp == 0 always means color/HSV mode, regardless of the reported
		// saturation value (some devices report a stale/meaningless saturation of 0 while still
		// genuinely in color mode). Do not infer white mode from saturation.
		return lightState?.ColorTemperature is int colorTemperature && colorTemperature > 0;
		}

	private static bool HasCurrentFullColorState (LightState? lightState)
		{
		return lightState?.Hsv is not null
			|| lightState?.Hue is not null
			|| lightState?.Saturation is not null;
		}

	private bool IsCurrentColorTemperatureUiMode ()
		{
		return _supportsColorTemperature && _isColorTemperatureUiModeActive;
		}

	private void PublishCurrentLightModeProperties (string context, bool synchronizeProcessorBaseline = true)
		{
		if (_supportsBrightness)
			{
			PublishProperty ("lightDimmer:level", new DriverEntityValue (LightDimmerLevel), context);
			}

		PublishColorModeStateProperties (context);
		if (synchronizeProcessorBaseline)
			{
			SynchronizeProcessorBaseline (LightTuningDecisions.ToProcessorTuningMode (IsCurrentColorTemperatureUiMode ()), context);
			}
		}

	private void SynchronizeProcessorBaseline (ProcessorLightTuningMode mode, string context)
		{
		// The processor baseline workaround exists solely to correct confusion between the two
		// tuning modes the processor's own Load layer can render (Color vs White/CT) - a bulb that
		// supports full color but has no white/CT mode at all (_supportsColorTemperature is false)
		// can only ever be in Color mode, so there is nothing for the baseline to ever disagree
		// about and this must be skipped entirely for such bulbs.
		if (!_supportsFullColor || !_supportsColorTemperature || !_sharedConfiguration.EnableProcessorBaselineWorkaround || _synchronizeProcessorBaselineAsync is null || string.IsNullOrWhiteSpace (DeviceName))
			{
			return;
			}

		// The processor only ever asserts BaselineSetting.TuningMode into the Crestron Home UI on an
		// actual off->on power transition (see ProcessorBaselineCoordinator.SynchronizeCoreAsync),
		// which is the only reason the coordinator forces a real SetLoadState off/on toggle in the
		// first place. While the light is already on, nothing currently rendered depends on the
		// console-level baseline, so synchronizing now would only force that toggle - and the
		// several-second physical flicker that comes with it - for no visible benefit. Skip it
		// entirely here; SynchronizeProcessorBaselineForPowerOff recomputes the actual mode from live
		// state and synchronizes right before the light is actually turned off instead.
		if (LightIsOn)
			{
			return;
			}

		DispatchProcessorBaselineSynchronization (mode, context);
		}

	// Synchronizes the processor's console-level baseline using the light's actual current mode,
	// called immediately before the light is commanded off. There is nothing to cache: by the time
	// the light is genuinely being turned off, LightColorHue/LightColorSaturation/color-temperature
	// already reflect the real, settled mode, so it can simply be read and applied here rather than
	// tracked speculatively while the light was on.
	private void SynchronizeProcessorBaselineForPowerOff (string context)
		{
		if (!_supportsFullColor || !_supportsColorTemperature)
			{
			return;
			}

		DispatchProcessorBaselineSynchronization (LightTuningDecisions.ToProcessorTuningMode (IsCurrentColorTemperatureUiMode ()), context);
		}

	private void DispatchProcessorBaselineSynchronization (ProcessorLightTuningMode mode, string context)
		{
		if (!_supportsFullColor || !_supportsColorTemperature || !_sharedConfiguration.EnableProcessorBaselineWorkaround || _synchronizeProcessorBaselineAsync is null || string.IsNullOrWhiteSpace (DeviceName))
			{
			return;
			}

		// Capture the current level/hue/saturation/color-temperature synchronously here, rather than
		// reading them lazily from inside the queued background delegate below. StartBackgroundOperation
		// only starts running once the console round-trip to resolve/initialize the processor baseline
		// load completes (can take several seconds). Only this driver process restarts on a reload;
		// Crestron Home's own Load/UI layer keeps running and performs its own reconnect handshake
		// against the freshly-restarted entity, re-asserting its last-known level/mode as genuine
		// commands against the load (e.g. lightDimmer:setLevel level=0 then 0.01 immediately after
		// InitializeConnectedStateAsync publishes the real state - the same documented Load-layer
		// channel-driving behavior seen elsewhere, e.g. around power-on ColorTemp commands). Reading
		// the properties lazily would race against that handshake and send the processor a stale value
		// that no longer matches what was actually true when this synchronization was requested - which
		// is exactly what produced a spurious low-brightness SetLoadState during initialization.
		double dimmerLevel = LightDimmerLevel;
		double hue = LightColorHue;
		double saturation = LightColorSaturation;
		long colorTemperature = GetActiveColorTemperatureLevel ();
		StartBackgroundOperation (
			() => InvokeProcessorBaselineSynchronization (mode, dimmerLevel, hue, saturation, colorTemperature),
			$"ProcessorBaseline:{context}");
		}

	// Awaited counterpart of DispatchProcessorBaselineSynchronization, used only from
	// InitializeConnectedStateAsync. Live processor log evidence (2026-07-26.log, 08:10:02-08:10:05)
	// showed the fire-and-forget background dispatch losing the race every time on a reload: the SSH
	// console round-trip that corrects the processor's stale TunableChannelStates.TuningMode does not
	// land until ~1-2 seconds after OnlineIndicatorIsOnline/ReadyIndicatorIsReady are published, and
	// Crestron Home's own Load-layer reconnect handshake renders its initial UI tile from the load's
	// live TunableChannelStates immediately once the entity reports ready - well before that toggle
	// commits. Awaiting the synchronization here, before the entity is marked online/ready, closes
	// that race deterministically instead of leaving the initial UI tile dependent on background
	// timing. Physical device power calls are still suppressed for the duration via
	// RunProcessorBaselineSynchronizationSuppressingPowerCalls.
	private async Task DispatchProcessorBaselineSynchronizationAsync (ProcessorLightTuningMode mode, string context)
		{
		if (!_supportsFullColor || !_supportsColorTemperature || !_sharedConfiguration.EnableProcessorBaselineWorkaround || _synchronizeProcessorBaselineAsync is null || string.IsNullOrWhiteSpace (DeviceName))
			{
			return;
			}

		double dimmerLevel = LightDimmerLevel;
		double hue = LightColorHue;
		double saturation = LightColorSaturation;
		long colorTemperature = GetActiveColorTemperatureLevel ();
		try
			{
			await RunProcessorBaselineSynchronizationSuppressingPowerCalls (mode, dimmerLevel, hue, saturation, colorTemperature).ConfigureAwait (false);
			}
		catch (Exception ex)
			{
			_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Light entity '{ControllerId}' awaited processor baseline synchronization '{context}' failed: {ex}");
			}
		}

	// Wraps the actual console round-trip with the deterministic power-call suppression flag: the
	// off->on SetLoadState toggle performed inside _synchronizeProcessorBaselineAsync (see
	// ProcessorBaselineCoordinator.SynchronizeCoreAsync) is what causes Crestron's Load layer to
	// relay a power command back at this entity, so the flag only ever needs to be armed for the
	// exact duration of this awaited call - no timeout guess required. This wrapper is only ever
	// invoked from the awaited startup path (DispatchProcessorBaselineSynchronizationAsync), so
	// _isInitializingProcessorBaseline is armed only for that startup window. The fire-and-forget
	// post-off/off-state synchronizations dispatched from DispatchProcessorBaselineSynchronization
	// call InvokeProcessorBaselineSynchronization directly instead, without arming the flag - a
	// genuine, user-requested power-on arriving while one of those background syncs happens to still
	// be in flight must never be suppressed.
	private async Task RunProcessorBaselineSynchronizationSuppressingPowerCalls (ProcessorLightTuningMode mode, double dimmerLevel, double hue, double saturation, long colorTemperature)
		{
		_isInitializingProcessorBaseline = true;
		try
			{
			await InvokeProcessorBaselineSynchronization (mode, dimmerLevel, hue, saturation, colorTemperature).ConfigureAwait (false);
			}
		finally
			{
			_isInitializingProcessorBaseline = false;
			}
		}

	private Task InvokeProcessorBaselineSynchronization (ProcessorLightTuningMode mode, double dimmerLevel, double hue, double saturation, long colorTemperature)
		{
		return _synchronizeProcessorBaselineAsync! (DeviceName, mode, dimmerLevel, hue, saturation, colorTemperature, _lifetimeCancellationSource.Token);
		}

	private long GetActiveColorTemperatureLevel ()
		{
		if (!_supportsColorTemperature)
			{
			return 0L;
			}

		return _registeredColorTemperatureMembers!.LightColorTemperatureLevel;
		}

	// The Crestron Home UI treats any published lightEmulatedColorTemperature:level as an assertion
	// that the color-temperature override is currently active. While a color-capable bulb is in HSV
	// mode, do not publish its device-level color_temp=0 sentinel through this UI property.
	private bool ShouldPublishColorTemperatureLevel (bool colorTemperatureUiModeActive)
		{
		if (!_supportsColorTemperature)
			{
			return false;
			}

		// The plain lightColorTemperature capability is only registered for TunableWhite bulbs, which
		// have no colour mode at all. For full-colour bulbs, only an active Kelvin value is publishable
		// without asserting White mode in the processor UI.
		if (_registeredColorTemperatureMembers is ColorTemperatureMembers)
			{
			return true;
			}

		return colorTemperatureUiModeActive;
		}

	private void PublishColorModeStateProperties (string context)
		{
		bool colorTemperatureUiModeActive = IsCurrentColorTemperatureUiMode ();
		bool publishColorTemperatureLevel = ShouldPublishColorTemperatureLevel (colorTemperatureUiModeActive);

		if (_supportsFullColor && !colorTemperatureUiModeActive)
			{
			PublishProperty ("lightColor:hue", new DriverEntityValue (LightColorHue), context);
			PublishProperty ("lightColor:saturation", new DriverEntityValue (LightColorSaturation), context);
			}

		if (publishColorTemperatureLevel)
			{
			PublishProperty (_registeredColorTemperatureMembers!.LevelPropertyId, new DriverEntityValue (GetActiveColorTemperatureLevel ()), context);
			}
		}

	private void PublishActiveColorModeProperties (string context)
		{
		bool colorTemperatureUiModeActive = IsCurrentColorTemperatureUiMode ();
		if (ShouldPublishColorTemperatureLevel (colorTemperatureUiModeActive))
			{
			PublishProperty (_registeredColorTemperatureMembers!.LevelPropertyId, new DriverEntityValue (_registeredColorTemperatureMembers!.LightColorTemperatureLevel), context);
			}

		if (_supportsFullColor && !colorTemperatureUiModeActive)
			{
			PublishProperty ("lightColor:hue", new DriverEntityValue (LightColorHue), context);
			PublishProperty ("lightColor:saturation", new DriverEntityValue (LightColorSaturation), context);
			}
		}

	private void PublishProperty (string propertyId, DriverEntityValue value, string context)
		{
		LogPublishedProperty (propertyId, "value", context, value);
		NotifyPropertyChanged (propertyId, value);
		}

	private void LogDynamicFeatureEntityState (string context)
		{
		if (Interlocked.Exchange (ref _dynamicFeatureStateDiagnosticLogged, 1) != 0)
			{
			return;
			}

		LogEntityStateSnapshot (context);
		}

	private void LogEntityStateSnapshot (string context)
		{

		try
			{
			object state = GetState ();
			string stateDescription = DescribeDiagnosticObject (state, 0, new List<object> ());
			LogInfo ($"Light entity '{ControllerId}' GetState snapshot after {context}: {stateDescription}");
			}
		catch (Exception ex)
			{
			_logger?.Log (_driverLogId, LogEntryLevel.Warning, $"Light entity '{ControllerId}' failed to capture GetState snapshot after {context}: {ex.Message}");
			}
		}

	private static string DescribeDiagnosticObject (object value, int depth, IList<object> visited)
		{
		if (value == null)
			{
			return "<null>";
			}

		Type valueType = value.GetType ();

		if (value is string text)
			{
			return $"\"{text}\"";
			}

		if (IsSimpleDiagnosticType (valueType))
			{
			if (value is IFormattable formattable)
				{
				return formattable.ToString (null, CultureInfo.InvariantCulture);
				}

			return value.ToString () ?? $"<{valueType.Name}>";
			}

		if (depth >= 4)
			{
			return $"<{valueType.Name}>";
			}

		if (!valueType.IsValueType)
			{
			for (int i = 0; i < visited.Count; i++)
				{
				if (ReferenceEquals (visited[i], value))
					{
					return $"<cycle:{valueType.Name}>";
					}
				}

			visited.Add (value);
			}

		try
			{
			if (value is IDictionary dictionary)
				{
				StringBuilder dictionaryBuilder = new ();
				dictionaryBuilder.Append (valueType.Name);
				dictionaryBuilder.Append ('{');

				bool firstEntry = true;
				foreach (DictionaryEntry entry in dictionary)
					{
					if (!firstEntry)
						{
						dictionaryBuilder.Append (", ");
						}

					firstEntry = false;
					dictionaryBuilder.Append (DescribeDiagnosticObject (entry.Key, depth + 1, visited));
					dictionaryBuilder.Append ('=');
					dictionaryBuilder.Append (DescribeDiagnosticObject (entry.Value, depth + 1, visited));
					}

				dictionaryBuilder.Append ('}');
				return dictionaryBuilder.ToString ();
				}

			if (value is IEnumerable enumerable)
				{
				StringBuilder sequenceBuilder = new ();
				sequenceBuilder.Append (valueType.Name);
				sequenceBuilder.Append ('[');

				bool firstItem = true;
				int itemCount = 0;
				foreach (object item in enumerable)
					{
					if (itemCount >= 20)
						{
						if (!firstItem)
							{
							sequenceBuilder.Append (", ");
							}

						sequenceBuilder.Append ("...");
						break;
						}

					if (!firstItem)
						{
						sequenceBuilder.Append (", ");
						}

					firstItem = false;
					sequenceBuilder.Append (DescribeDiagnosticObject (item, depth + 1, visited));
					itemCount++;
					}

				sequenceBuilder.Append (']');
				return sequenceBuilder.ToString ();
				}

			PropertyInfo[] properties = valueType
				.GetProperties (BindingFlags.Instance | BindingFlags.Public)
				.Where (property => property.CanRead && property.GetIndexParameters ().Length == 0)
				.OrderBy (property => property.Name, StringComparer.Ordinal)
				.ToArray ();

			if (properties.Length == 0)
				{
				return value.ToString () ?? $"<{valueType.Name}>";
				}

			StringBuilder objectBuilder = new ();
			objectBuilder.Append (valueType.Name);
			objectBuilder.Append ('{');

			for (int i = 0; i < properties.Length; i++)
				{
				if (i > 0)
					{
					objectBuilder.Append (", ");
					}

				PropertyInfo property = properties[i];
				objectBuilder.Append (property.Name);
				objectBuilder.Append ('=');

				try
					{
					object propertyValue = property.GetValue (value, null);
					objectBuilder.Append (DescribeDiagnosticObject (propertyValue, depth + 1, visited));
					}
				catch (Exception ex)
					{
					objectBuilder.Append ($"<error:{ex.GetType ().Name}>");
					}
				}

			objectBuilder.Append ('}');
			return objectBuilder.ToString ();
			}
		finally
			{
			if (!valueType.IsValueType && visited.Count > 0 && ReferenceEquals (visited[visited.Count - 1], value))
				{
				visited.RemoveAt (visited.Count - 1);
				}
			}
		}

	private static bool IsSimpleDiagnosticType (Type valueType)
		{
		Type underlyingType = Nullable.GetUnderlyingType (valueType) ?? valueType;

		return underlyingType.IsPrimitive
			|| underlyingType.IsEnum
			|| underlyingType == typeof (decimal)
			|| underlyingType == typeof (DateTime)
			|| underlyingType == typeof (DateTimeOffset)
			|| underlyingType == typeof (TimeSpan)
			|| underlyingType == typeof (Guid);
		}

	private void PublishProperty (string propertyId, DriverEntityValueRange value, string context)
		{
		DriverEntityValue entityValue = new (value);
		LogPublishedProperty (propertyId, "range", context, entityValue);
		NotifyPropertyChanged (propertyId, entityValue);
		}

	private void LogPublishedProperty (string propertyId, string valueKind, string context, DriverEntityValue value)
		{
		if (!IsLightModeDiagnosticProperty (propertyId))
			{
			return;
			}

		LogInfo ($"Light entity '{ControllerId}' publishing propertyId='{propertyId}', valueKind='{valueKind}', value={DescribeDiagnosticObject (value, 0, new List<object> ())}, context='{context}'.");
		}

	private static bool IsLightModeDiagnosticProperty (string propertyId)
		{
		return propertyId.StartsWith ("lightColor:", StringComparison.Ordinal)
			|| propertyId.StartsWith ("lightColorTemperature:", StringComparison.Ordinal)
			|| propertyId.StartsWith ("lightEmulatedColorTemperature:", StringComparison.Ordinal)
			|| propertyId.StartsWith ("lightTunable:", StringComparison.Ordinal);
		}

	private async Task RefreshAndApplyStateAsync (CancellationToken cancellationToken)
		{
		await ExecuteWithConnectedDeviceAsync (async device =>
			{
				cancellationToken.ThrowIfCancellationRequested ();
				await device.UpdateAsync (cancellationToken).ConfigureAwait (false);
				RefreshDescriptorFromConnectedDevice (device, "RefreshAsync.AfterUpdate");
				LogReportedState ("RefreshAsync.AfterUpdate", device);
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
			}, cancellationToken).ConfigureAwait (false);

		OnlineIndicatorIsOnline = true;
		ReadyIndicatorIsReady = true;
		}

	private async Task ReconnectAndRefreshAsync (TimeoutException timeoutException, CancellationToken cancellationToken)
		{
		await ExecuteWithConnectedDeviceAsync (async device =>
			{
				string host = device.Configuration.Host;
				LogInfo ($"Light entity '{ControllerId}' refresh timeout; reconnecting fresh client to host '{host}'. Original error: {timeoutException.Message}");

				KasaDevice reconnectedDevice = await ConnectFromConfigurationAsync (updateState: true, cancellationToken).ConfigureAwait (false);
				_connectedDevice = reconnectedDevice;
				RefreshDescriptorFromConnectedDevice (reconnectedDevice, "ReconnectRefresh.AfterConnect");
				LogReportedState ("ReconnectRefresh.AfterConnect", reconnectedDevice);

				bool previousSuppressPropertyNotifications = _suppressPropertyNotifications;
				_suppressPropertyNotifications = true;
				try
					{
					ApplyState (reconnectedDevice);
					}
				finally
					{
					_suppressPropertyNotifications = previousSuppressPropertyNotifications;
					}
			}, cancellationToken).ConfigureAwait (false);
		}

	private DesiredLightCommand CreateBrightnessCommand (double relativeLevel)
		{
		return new DesiredLightCommand (DesiredLightMode.Brightness, relativeLevel, 0d, 0d, 0L);
		}

	private async Task ApplyBrightnessCommandAsync (KasaDevice device, double relativeLevel, CancellationToken cancellationToken)
		{
		if (relativeLevel <= 0d)
			{
			await device.TurnLightOffAsync (cancellationToken).ConfigureAwait (false);
			return;
			}

		if (device.LightState?.IsOn != true && device.IsOn != true)
			{
			await device.TurnLightOnAsync (cancellationToken).ConfigureAwait (false);
			return;
			}

		await device.SetBrightnessAsync (ToBrightnessPercent (relativeLevel), cancellationToken).ConfigureAwait (false);
		}

	private static async Task EnsurePoweredForLevelAsync (KasaDevice device, double relativeLevel, CancellationToken cancellationToken)
		{
		if (relativeLevel <= 0d || device.LightState?.IsOn == true || device.IsOn == true)
			{
			return;
			}

		await device.TurnLightOnAsync (cancellationToken).ConfigureAwait (false);
		}

	private double GetEffectiveOnLevel ()
		{
		if (LightDimmerLevel > 0d)
			{
			return LightDimmerLevel;
			}

		if (_lastKnownDeviceBrightnessLevel is double lastKnownLevel && lastKnownLevel > 0d)
			{
			return lastKnownLevel;
			}

		return 1d;
		}

	private bool ResolvePowerState (KasaDevice device)
		{
		string? childId = ChildId;
		if (!string.IsNullOrWhiteSpace (childId))
			{
			string resolvedChildId = childId!;
			return device.GetChild (resolvedChildId)?.IsOn ?? false;
			}

		return Kind == ManagedLightKind.OnOff
			? device.IsOn ?? false
			: device.LightState?.IsOn ?? device.IsOn ?? false;
		}

	private static int ToBrightnessPercent (double relativeLevel) => Math.Max (1, (int)Math.Round (Clamp01 (relativeLevel) * 100d, MidpointRounding.AwayFromZero));

	private void LogReportedState (string stage, KasaDevice device)
		{
		int? brightness = device.LightState?.Brightness;
		int? hue = device.LightState?.Hue ?? device.LightState?.Hsv?.Hue;
		int? saturation = device.LightState?.Saturation ?? device.LightState?.Hsv?.Saturation;
		int? colorTemperature = device.LightState?.ColorTemperature;
		bool? isOn = device.LightState?.IsOn ?? device.IsOn;

		LogInfo ($"Light entity '{ControllerId}' {stage} raw state: isOn={FormatNullable (isOn)}, brightness={FormatNullable (brightness)}, hue={FormatNullable (hue)}, saturation={FormatNullable (saturation)}, colorTemperature={FormatNullable (colorTemperature)}.");
		}

	private void LogCommandInvocation (string commandId, string details)
		{
		LogInfo ($"Light entity '{ControllerId}' command invoked: commandId='{commandId}', details='{details}'.");
		}

	private void LogPublishedState ()
		{
		_ = IsCurrentColorTemperatureUiMode ();
		LogInfo ($"Light entity '{ControllerId}' published snapshot: isOn={LightIsOn}, dimmer={LightDimmerLevel:0.####}, hue={LightColorHue:0.####}, saturation={LightColorSaturation:0.####}, colorTemperature={GetActiveColorTemperatureLevel ()}, colorTemperatureUiMode={IsCurrentColorTemperatureUiMode ()}.");
		}

	private string FormatDesiredLightCommand (DesiredLightCommand command)
		{
		return $"mode={command.Mode}, level={command.Level:0.####}, hue={command.Hue:0.####}, saturation={command.Saturation:0.####}, colorTemperature={command.ColorTemperature}";
		}

	private static string FormatRange (DriverEntityValueRange? range)
		{
		return range is null
			? "<none>"
			: range.ToString ()!;
		}

	[Conditional ("DEBUG")]
	private void LogInfo (string message)
		{
		_logger?.Log (_driverLogId, LogEntryLevel.Info, message);
		}

	private static string FormatNullable<T> (T? value)
		where T : struct
		{
		return value.HasValue ? value.Value.ToString ()! : "null";
		}

	protected virtual void OnStateApplied (KasaDevice device)
		{
		}

	private static bool HasFeature (IReadOnlyList<DeviceFeature> features, string featureId)
		{
		return features.Any (feature => string.Equals (feature.Id, featureId, StringComparison.Ordinal));
		}

	private static ManagedLightKind InferManagedLightKind (KasaDevice device, KasaDeviceType discoveredDeviceType)
		{
		if (discoveredDeviceType == KasaDeviceType.Plug || discoveredDeviceType == KasaDeviceType.Strip && device.LightState is null)
			{
			return ManagedLightKind.OnOff;
			}

		IReadOnlyList<DeviceFeature> features = device.Features;
		bool supportsBrightness = HasFeature (features, BRIGHTNESS_FEATURE_ID);
		bool supportsColorTemperature = HasFeature (features, COLOR_TEMPERATURE_FEATURE_ID);
		bool supportsHue = HasFeature (features, HUE_FEATURE_ID);
		bool supportsSaturation = HasFeature (features, SATURATION_FEATURE_ID);
		bool hasColorState = HasCurrentFullColorState (device.LightState);
		bool supportsFullColor = (supportsHue && supportsSaturation) || hasColorState;

		// ManagedLightDescriptor.Kind is a one-time/locked classification (see
		// ManagedLightDescriptor.Kind's setter) that PlatformDriver uses to select the managed
		// entity type and identity for this controllerId for the entity's entire lifetime - it must
		// therefore reflect the bulb's fixed hardware *capability*, never its transient currently
		// active mode. A full-color-capable bulb (e.g. KL130) always reports retained hue/saturation
		// values from the device, even while it is currently operating in white/color-temperature
		// mode - the bulb keeps its last HSV color in memory purely so it can be restored later - so
		// supportsFullColor already reflects the real, permanent capability on its own. Gating this on
		// whichever mode happens to be active at the moment this particular connect attempt reads the
		// device is a race: a bulb sitting in white mode would infer TunableWhite here, but if an
		// earlier or later reconnect within the same startup retry loop reads it while in color mode
		// it would infer Color instead, and the second assignment then throws (Kind cannot change
		// after initialization), aborting startup.
		if (supportsFullColor)
			{
			return ManagedLightKind.Color;
			}

		if (supportsColorTemperature)
			{
			return ManagedLightKind.TunableWhite;
			}

		return supportsBrightness
			? ManagedLightKind.Dimmable
			: ManagedLightKind.OnOff;
		}

	private static bool TryCreateColorTemperatureRange (IReadOnlyList<DeviceFeature> features, out DriverEntityValueRange range)
		{
		DeviceFeature? feature = features.FirstOrDefault (feature => string.Equals (feature.Id, COLOR_TEMPERATURE_FEATURE_ID, StringComparison.Ordinal));
		if (feature is not null && feature.MinimumValue.HasValue && feature.MaximumValue.HasValue)
			{
			range = new DriverEntityValueRange ((long)feature.MinimumValue.Value, (long)feature.MaximumValue.Value, 1);
			return true;
			}

		range = new DriverEntityValueRange (0, 0, 1);
		return false;
		}

	private static double TryGetColorTemperatureMinimum (IReadOnlyList<DeviceFeature> features)
		{
		DeviceFeature? feature = features.FirstOrDefault (feature => string.Equals (feature.Id, COLOR_TEMPERATURE_FEATURE_ID, StringComparison.Ordinal));
		return feature?.MinimumValue ?? 0d;
		}

	private static double Clamp01 (double value)
		{
		if (value < 0)
			{
			return 0;
			}

		if (value > 1)
			{
			return 1;
			}

		return value;
		}

	private void SetAndNotify (string propertyId, bool value, ref bool field)
		{
		if (field == value)
			{
			return;
			}

		field = value;
		if (_suppressPropertyNotifications)
			{
			return;
			}

		PublishProperty (propertyId, new DriverEntityValue (value), "SetAndNotify<bool>");
		}

	private void SetAndNotify (string propertyId, double value, ref double field)
		{
		if (Math.Abs (field - value) < 0.0001d)
			{
			return;
			}

		field = value;
		if (_suppressPropertyNotifications)
			{
			return;
			}

		PublishProperty (propertyId, new DriverEntityValue (value), "SetAndNotify<double>");
		}

	private void SetAndNotify (string propertyId, long value, ref long field)
		{
		if (field == value)
			{
			return;
			}

		field = value;
		if (_suppressPropertyNotifications)
			{
			return;
			}

		PublishProperty (propertyId, new DriverEntityValue (value), "SetAndNotify<long>");
		}

	private void SetAndNotify<T> (string propertyId, T value, ref T field)
		{
		if (EqualityComparer<T>.Default.Equals (field, value))
			{
			return;
			}

		field = value;
		if (_suppressPropertyNotifications)
			{
			return;
			}

		PublishProperty (propertyId, CreateValueForObject (value!), $"SetAndNotify<{typeof (T).Name}>");
		}

	}

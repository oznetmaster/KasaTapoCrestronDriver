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

internal class KasaLightEntity : ReflectedAttributeDriverEntity, IKasaManagedLightEntity
	{
	private const string BRIGHTNESS_FEATURE_ID = "brightness";
	private const string COLOR_TEMPERATURE_FEATURE_ID = "color_temperature";
	private const string HUE_FEATURE_ID = "hue";
	private const string SATURATION_FEATURE_ID = "saturation";
	private const double HUE_MAX_DEGREES = 359d;
	private enum DesiredLightMode
		{
		Off,
		On,
		Brightness,
		ColorTemperature,
		Hsv
		}

	public enum LightTunableTuningMode
		{
		Color,
		White
		}

	private readonly struct DesiredLightCommand
		{
		public DesiredLightCommand (DesiredLightMode mode, double level, double hue, double saturation, long colorTemperature)
			{
			Mode = mode;
			Level = level;
			Hue = hue;
			Saturation = saturation;
			ColorTemperature = colorTemperature;
			}

		public DesiredLightMode Mode
			{
			get;
			}
		public double Level
			{
			get;
			}
		public double Hue
			{
			get;
			}
		public double Saturation
			{
			get;
			}
		public long ColorTemperature
			{
			get;
			}
		}

	private sealed class WhiteLightColorTemperatureMembers
		{
		private readonly KasaLightEntity _owner;
		private long _lightColorTemperatureLevel;

		[EntityDataType (Id = "lightTunable:TuningMode")]
		public enum TuningMode
			{
			Color,
			White
			}

		public WhiteLightColorTemperatureMembers (KasaLightEntity owner, DriverEntityValueRange range, long level)
			{
			_owner = owner;
			LightColorTemperatureRange = range;
			_lightColorTemperatureLevel = level;
			}

		[EntityProperty (Id = "lightTunable:mode")]
		public TuningMode LightTunableMode => TuningMode.White;

		[EntityProperty (Id = "lightColorTemperature:range", Units = "Kelvin")]
		public DriverEntityValueRange LightColorTemperatureRange { get; set; } = new DriverEntityValueRange (0, 0, 1);

		[EntityProperty (Id = "lightColorTemperature:level", RangeProperty = "lightColorTemperature:range", Units = "Kelvin")]
		public long LightColorTemperatureLevel
			{
			get => _lightColorTemperatureLevel;
			set => _owner.SetAndNotify ("lightColorTemperature:level", value, ref _lightColorTemperatureLevel);
			}

		[EntityCommand (Id = "lightColorTemperature:setLevel")]
		public void SetColorTemperatureLevel ([EntityParameter (RangeProperty = "lightColorTemperature:range", Units = "Kelvin")] long level)
			{
			_owner.SetColorTemperatureLevel ("lightColorTemperature:setLevel", level);
			}

		}

	private sealed class ColorLightEmulatedColorTemperatureMembers
		{
		private readonly KasaLightEntity _owner;
		private long _lightEmulatedColorTemperatureLevel;
		private TuningMode _lightTunableMode;

		[EntityDataType (Id = "lightTunable:TuningMode")]
		public enum TuningMode
			{
			Color,
			White
			}

		public ColorLightEmulatedColorTemperatureMembers (KasaLightEntity owner, DriverEntityValueRange range, long level, TuningMode mode)
			{
			_owner = owner;
			LightEmulatedColorTemperatureRange = range;
			_lightEmulatedColorTemperatureLevel = level;
			_lightTunableMode = mode;
			}

		[EntityProperty (Id = "lightTunable:mode")]
		public TuningMode LightTunableMode
			{
			get => _lightTunableMode;
			set => _owner.SetAndNotify ("lightTunable:mode", value, ref _lightTunableMode);
			}

		[EntityProperty (Id = "lightEmulatedColorTemperature:range", Units = "Kelvin")]
		public DriverEntityValueRange LightEmulatedColorTemperatureRange { get; set; } = new DriverEntityValueRange (0, 0, 1);

		[EntityProperty (Id = "lightEmulatedColorTemperature:level", RangeProperty = "lightEmulatedColorTemperature:range", Units = "Kelvin")]
		public long LightEmulatedColorTemperatureLevel
			{
			get => _lightEmulatedColorTemperatureLevel;
			set => _owner.SetAndNotify ("lightEmulatedColorTemperature:level", value, ref _lightEmulatedColorTemperatureLevel);
			}

		[EntityCommand (Id = "lightEmulatedColorTemperature:setLevel")]
		public void SetEmulatedColorTemperatureLevel ([EntityParameter (RangeProperty = "lightEmulatedColorTemperature:range", Units = "Kelvin")] long level)
			{
			_owner.SetColorTemperatureLevel ("lightEmulatedColorTemperature:setLevel", level);
			}

		}

	private sealed class FullColorMembers
		{
		private readonly KasaLightEntity _owner;
		private double _lightColorHue;
		private double _lightColorSaturation;

		public FullColorMembers (KasaLightEntity owner, double hue, double saturation)
			{
			_owner = owner;
			_lightColorHue = hue;
			_lightColorSaturation = saturation;
			}

		[EntityProperty (Id = "lightColor:hueRange")]
		public DriverEntityValueRelativeRange LightColorHueRange { get; } = new (1d / HUE_MAX_DEGREES);

		[EntityProperty (Id = "lightColor:hue", RelativeRangeProperty = "lightColor:hueRange")]
		public double LightColorHue
			{
			get => _lightColorHue;
			set => _owner.SetAndNotify ("lightColor:hue", value, ref _lightColorHue);
			}

		[EntityProperty (Id = "lightColor:saturationRange")]
		public DriverEntityValueRelativeRange LightColorSaturationRange { get; } = new (0.01);

		[EntityProperty (Id = "lightColor:saturation", RelativeRangeProperty = "lightColor:saturationRange")]
		public double LightColorSaturation
			{
			get => _lightColorSaturation;
			set => _owner.SetAndNotify ("lightColor:saturation", value, ref _lightColorSaturation);
			}

		[EntityCommand (Id = "lightColor:setHue")]
		public void LightColorSetHue ([EntityParameter (RangeMinimum = 0, RangeMaximum = 1, RangeStepSize = 1d / HUE_MAX_DEGREES)] double level)
			{
			_owner.LightColorSetHue (level);
			}

		[EntityCommand (Id = "lightColor:setSaturation")]
		public void LightColorSetSaturation ([EntityParameter (RangeMinimum = 0, RangeMaximum = 1, RangeStepSize = 0.01)] double level)
			{
			_owner.LightColorSetSaturation (level);
			}

		}

	private void SetColorTemperatureLevelSilently (long value)
		{
		WithSuppressedPropertyNotifications (() => SetActiveColorTemperatureLevel (value));
		}

	private void WithSuppressedPropertyNotifications (Action action)
		{
		bool previousSuppressPropertyNotifications = _suppressPropertyNotifications;
		_suppressPropertyNotifications = true;
		try
			{
			action ();
			}
		finally
			{
			_suppressPropertyNotifications = previousSuppressPropertyNotifications;
			}
		}

	private void SetActiveColorTemperatureLevel (long value)
		{
		if (_usesEmulatedColorTemperature)
			{
			_registeredEmulatedColorTemperatureMembers!.LightEmulatedColorTemperatureLevel = value;
			return;
			}

		_registeredWhiteLightColorTemperatureMembers!.LightColorTemperatureLevel = value;
		}

	private double LightColorHue
		{
		get => _registeredFullColorMembers?.LightColorHue ?? 0d;
		set
			{
			if (_registeredFullColorMembers is not null)
				{
				_registeredFullColorMembers.LightColorHue = value;
				}
			}
		}

	private double LightColorSaturation
		{
		get => _registeredFullColorMembers?.LightColorSaturation ?? 0d;
		set
			{
			if (_registeredFullColorMembers is not null)
				{
				_registeredFullColorMembers.LightColorSaturation = value;
				}
			}
		}

	private static readonly TimeSpan SliderDebounceInterval = TimeSpan.FromMilliseconds (250);
	private static int _dynamicFeatureStateDiagnosticLogged;

	private SemaphoreSlim _connectionGate { get; } = new (1, 1);
	private object _sliderGate { get; } = new ();
	protected WorkQueue<KasaDevice> DeviceQueue { get; } = new ();
	private DriverControllerLogger _logger { get; }
	private string _driverLogId { get; }
	private Action<ManagedLightDescriptor>? _descriptorUpdated { get; }
	private IPlatformSharedConfiguration _sharedConfiguration { get; }
	private CancellationTokenSource _lifetimeCancellationSource { get; } = new ();

	private ManagedLightDescriptor _descriptor { get; set; } = null!;
	private DeviceConfiguration? _configuration { get; set; }
	private KasaDevice? _connectedDevice { get; set; }
	private Task? _pollingTask { get; set; }
	private ManagedLightKind Kind { get; set; }
	private string? ChildId { get; set; }
	private bool _supportsBrightness { get; set; }
	private bool _supportsBaseLight { get; set; } = true;
	private bool _supportsFullColor { get; set; }
	private bool _supportsColorTemperature { get; set; }
	private bool _supportsTunable { get; set; }
	private bool _usesEmulatedColorTemperature { get; set; }
	private bool _usesWhiteLightTunableSetLevels { get; set; }
	private int _dynamicFeaturesConfigured;
	private static readonly TimeSpan StartupConnectRetryInterval = TimeSpan.FromSeconds (5);
	private DesiredLightCommand? _pendingSliderCommand { get; set; }
	private CancellationTokenSource? _sliderCommandCancellationSource { get; set; }
	private bool _suppressPropertyNotifications { get; set; } = true;
	private bool _isConfigured { get; set; }
	private bool _childPublished { get; set; }
	private bool _pendingStartupSnapshotAfterConnectedState { get; set; }
	private bool _deferredDeviceStatePending { get; set; }
	private FullColorMembers? _registeredFullColorMembers { get; set; }
	private WhiteLightColorTemperatureMembers? _registeredWhiteLightColorTemperatureMembers { get; set; }
	private ColorLightEmulatedColorTemperatureMembers? _registeredEmulatedColorTemperatureMembers { get; set; }
	private int _stopState;
	private int _pollingGeneration;
	private bool _disposed { get; set; }

	public KasaLightEntity (
		string controllerId,
		ManagedLightDescriptor descriptor,
		DeviceConfiguration configuration,
			Action<ManagedLightDescriptor>? descriptorUpdated,
		IPlatformSharedConfiguration sharedConfiguration,
		DriverImplementationResources resources,
		DriverControllerLogger logger,
		string driverLogId)
		: base (controllerId)
		{
		_descriptorUpdated = descriptorUpdated;
		_sharedConfiguration = sharedConfiguration;
		_logger = logger;
		_driverLogId = driverLogId;

		UpdateDescriptor (descriptor, configuration);
		_suppressPropertyNotifications = false;
		LogInfo ($"Light entity '{ControllerId}' created in passive discovered state; awaiting child configuration callback before activation.");
		}

	public string DeviceName { get; private set; } = string.Empty;

	public string ModelName { get; private set; } = string.Empty;

	public string SerialNumber { get; private set; } = string.Empty;

	[EntityProperty (Id = "lightDimmer:levelRange")]
	public DriverEntityValueRelativeRange LightDimmerLevelRange { get; } = new (0.01);

	[EntityProperty (Id = "lightDimmer:level", RelativeRangeProperty = "lightDimmer:levelRange")]
	public double LightDimmerLevel
		{
		get;
		private set => SetAndNotify ("lightDimmer:level", value, ref field);
		}

	[EntityProperty (Id = "light:isOn")]
	public bool LightIsOn
		{
		get;
		private set => SetAndNotify ("light:isOn", value, ref field);
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

	private void LightColorSetHue (double level)
		{
		double hueLevel = Clamp01 (level);
		LogCommandInvocation ("lightColor:setHue", $"hue={hueLevel:0.####}");

		ClearColorTemperatureUiMode ();
		LightColorHue = hueLevel;
		PublishActiveColorModeProperties ("lightColor:setHue");

		QueueSliderCommand (new DesiredLightCommand (DesiredLightMode.Hsv, GetEffectiveOnLevel (), hueLevel, LightColorSaturation, 0L));
		}

	private void LightColorSetSaturation (double level)
		{
		double saturationLevel = Clamp01 (level);
		LogCommandInvocation ("lightColor:setSaturation", $"saturation={saturationLevel:0.####}");

		ClearColorTemperatureUiMode ();
		LightColorSaturation = saturationLevel;
		PublishActiveColorModeProperties ("lightColor:setSaturation");

		QueueSliderCommand (new DesiredLightCommand (DesiredLightMode.Hsv, GetEffectiveOnLevel (), LightColorHue, saturationLevel, 0L));
		}

	[EntityCommand (Id = "lightDimmer:setLevel")]
	public void LightDimmerSetLevel ([EntityParameter (RangeMinimum = 0, RangeMaximum = 1, RangeStepSize = 0.01)] double level)
		{
		double relativeLevel = Clamp01 (level);
		LogCommandInvocation ("lightDimmer:setLevel", $"level={relativeLevel:0.####}");
		LightDimmerLevel = relativeLevel;

		QueueSliderCommand (new DesiredLightCommand (DesiredLightMode.Brightness, relativeLevel, 0d, 0d, 0L));
		}

	[EntityCommand (Id = "light:off")]
	public void LightOff ()
		{
		LogCommandInvocation ("light:off", "requested power off");

		CancelSliderInteraction ();
		StartBackgroundOperation (() => ExecuteDeviceCommandAsync (device => ExecutePowerAsync (device, false)), "light:off");
		}

	[EntityCommand (Id = "light:on")]
	public void LightOn ()
		{
		LogCommandInvocation ("light:on", "requested power on");

		StartBackgroundOperation (() => ExecuteDeviceCommandAsync (device => ExecutePowerAsync (device, true)), "light:on");
		}

	private void HandleColorLightTunableSetLevels (
		double? hue,
		double? saturation,
		double? intensity,
		long? emulatedColorTemperature)
		{
		LogCommandInvocation ("lightTunable:setLevels", $"mode=color, hue={hue?.ToString ("0.####") ?? "<null>"}, saturation={saturation?.ToString ("0.####") ?? "<null>"}, intensity={intensity?.ToString ("0.####") ?? "<null>"}, emulatedColorTemperature={emulatedColorTemperature?.ToString () ?? "<null>"}");
		bool wasOn = LightIsOn;

		if (TryApplyTunableIntensityOff (intensity, "lightTunable:setLevels.off"))
			{
			return;
			}

		if (intensity.HasValue)
			{
			ApplyTunableIntensityLevel (intensity.Value);
			}

		bool hasColorCommand = hue.HasValue || saturation.HasValue;
		bool hasModeOnlyColorRequest = emulatedColorTemperature.HasValue && !hasColorCommand;
		long requestedColorTemperature = hasModeOnlyColorRequest
			? 0L
			: Math.Max (0L, emulatedColorTemperature.GetValueOrDefault (0L));
		bool hasActiveColorTemperatureRequest = requestedColorTemperature > 0L;
		bool preserveCurrentColorModeOnPowerRestore = !wasOn
			&& intensity.HasValue
			&& intensity.Value > 0d
			&& !hasColorCommand
			&& hasActiveColorTemperatureRequest
			&& GetLightTunableMode () == LightTunableTuningMode.Color;

		if (hue.HasValue)
			{
			LightColorHue = Clamp01 (hue.Value);
			}

		if (saturation.HasValue)
			{
			LightColorSaturation = Clamp01 (saturation.Value);
			}

		if (preserveCurrentColorModeOnPowerRestore)
			{
			QueueSliderCommand (CreateCurrentModeCommand (GetEffectiveOnLevel ()));
			return;
			}

		if (hasActiveColorTemperatureRequest)
			{
			SetActiveColorTemperatureLevel (requestedColorTemperature);
			SetLightTunableMode (LightTunableTuningMode.White);
			QueueSliderCommand (new DesiredLightCommand (DesiredLightMode.ColorTemperature, GetEffectiveOnLevel (), 0d, 0d, requestedColorTemperature));
			return;
			}

		if (emulatedColorTemperature.HasValue)
			{
			SetColorTemperatureLevelSilently (Math.Max (0L, emulatedColorTemperature.Value));
			}

		if (hasColorCommand || hasModeOnlyColorRequest)
			{
			ClearColorTemperatureUiMode ();
			SetLightTunableMode (LightTunableTuningMode.Color);
			PublishActiveColorModeProperties ("lightTunable:setLevels.Color");
			QueueSliderCommand (new DesiredLightCommand (DesiredLightMode.Hsv, GetEffectiveOnLevel (), LightColorHue, LightColorSaturation, 0L));
			return;
			}

		if (intensity.HasValue)
			{
			QueueSliderCommand (CreateCurrentModeCommand (GetEffectiveOnLevel ()));
			}
		}

	private void HandleWhiteLightTunableSetLevels (
		double? intensity,
		long? colorTemperature)
		{
		LogCommandInvocation ("lightTunable:setLevels", $"mode=white, intensity={intensity?.ToString ("0.####") ?? "<null>"}, colorTemperature={colorTemperature?.ToString () ?? "<null>"}");

		if (TryApplyTunableIntensityOff (intensity, "lightTunable:setLevels.off"))
			{
			return;
			}

		if (intensity.HasValue)
			{
			ApplyTunableIntensityLevel (intensity.Value);
			}

		long requestedColorTemperature = Math.Max (0L, colorTemperature.GetValueOrDefault (0L));
		if (requestedColorTemperature > 0L)
			{
			LightColorHue = 0d;
			LightColorSaturation = 0d;
			SetActiveColorTemperatureLevel (requestedColorTemperature);
			QueueSliderCommand (new DesiredLightCommand (DesiredLightMode.ColorTemperature, GetEffectiveOnLevel (), 0d, 0d, requestedColorTemperature));
			return;
			}

		if (colorTemperature.HasValue)
			{
			SetColorTemperatureLevelSilently (0L);
			}

		if (intensity.HasValue)
			{
			QueueSliderCommand (CreateWhiteModeCommand (GetEffectiveOnLevel ())); 
			}
		}

	private bool TryApplyTunableIntensityOff (double? intensity, string operationName)
		{
		if (!intensity.HasValue)
			{
			return false;
			}

		double dimmerLevel = Clamp01 (intensity.Value);
		if (dimmerLevel > 0d)
			{
			return false;
			}

		LightDimmerLevel = 0d;
		CancelSliderInteraction ();
		StartBackgroundOperation (() => ExecuteDeviceCommandAsync (device => ExecutePowerAsync (device, false)), operationName);
		return true;
		}

	private void ApplyTunableIntensityLevel (double intensity)
		{
		double dimmerLevel = Clamp01 (intensity);
		LightDimmerLevel = dimmerLevel;
		}

	private DesiredLightCommand CreateCurrentModeCommand (double level)
		{
		if (_supportsColorTemperature && IsCurrentColorTemperatureUiMode ())
			{
			return new DesiredLightCommand (DesiredLightMode.ColorTemperature, level, 0d, 0d, GetActiveColorTemperatureLevel ());
			}

		return new DesiredLightCommand (DesiredLightMode.Hsv, level, LightColorHue, LightColorSaturation, 0L);
		}

	private DesiredLightCommand CreateWhiteModeCommand (double level)
		{
		long activeColorTemperature = GetActiveColorTemperatureLevel ();
		return activeColorTemperature > 0L
			? new DesiredLightCommand (DesiredLightMode.ColorTemperature, level, 0d, 0d, activeColorTemperature)
			: new DesiredLightCommand (DesiredLightMode.Brightness, level, 0d, 0d, 0L);
		}

	private void SetColorTemperatureLevel (string commandId, long level)
		{
		LogCommandInvocation (commandId, $"level={level}");

		if (level <= 0L)
			{
			SetColorTemperatureLevelSilently (0L);

			QueueSliderCommand (new DesiredLightCommand (DesiredLightMode.Hsv, GetEffectiveOnLevel (), LightColorHue, LightColorSaturation, 0L));
			return;
			}

		long temperatureLevel = Math.Max (1L, level);
		SetLightTunableMode (LightTunableTuningMode.White);
		SetActiveColorTemperatureLevel (temperatureLevel);

		QueueSliderCommand (new DesiredLightCommand (DesiredLightMode.ColorTemperature, GetEffectiveOnLevel (), 0d, 0d, temperatureLevel));
		}

	private void QueueSliderCommand (DesiredLightCommand command)
		{
		CancellationTokenSource cancellationSource;
		lock (_sliderGate)
			{
			_pendingSliderCommand = command;
			_sliderCommandCancellationSource?.Cancel ();
			_sliderCommandCancellationSource?.Dispose ();
			_sliderCommandCancellationSource = new CancellationTokenSource ();
			cancellationSource = _sliderCommandCancellationSource;
			}

		_ = ProcessSliderCommandAsync (cancellationSource.Token);
		}

	private void StartBackgroundOperation (Func<Task> operation, string operationName)
		{
		Task task;
		try
			{
			task = operation ();
			}
		catch (Exception ex)
			{
			_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Light entity '{ControllerId}' background operation '{operationName}' failed before dispatch: {ex}");
			return;
			}

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
			await Task.Delay (SliderDebounceInterval, cancellationToken).ConfigureAwait (false);

			DesiredLightCommand? command;
			lock (_sliderGate)
				{
				if (cancellationToken.IsCancellationRequested || _sliderCommandCancellationSource is null || _sliderCommandCancellationSource.Token != cancellationToken)
					{
					return;
					}

				command = _pendingSliderCommand;
				_pendingSliderCommand = null;
				}

			if (!command.HasValue)
				{
				return;
				}

			LogInfo ($"Light entity '{ControllerId}' dispatch slider command: {FormatDesiredLightCommand (command.Value)}.");

			await ExecuteDeviceCommandAsync (
				async device =>
					{
						cancellationToken.ThrowIfCancellationRequested ();
						await ApplyDesiredLightCommandAsync (device, command.Value).ConfigureAwait (false);
					},
				refreshAfterCommand: true,
				cancellationToken).ConfigureAwait (false);

			}
		catch (OperationCanceledException)
			{
			}
		catch (Exception ex)
			{
			OnlineIndicatorIsOnline = false;
			ReadyIndicatorIsReady = false;
			_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Light entity '{ControllerId}' slider command failed: {ex}");
			}
		finally
			{
			lock (_sliderGate)
				{
				if (_sliderCommandCancellationSource is not null && _sliderCommandCancellationSource.Token == cancellationToken)
					{
					reapplyResolvedState = _pendingSliderCommand is null;
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

	private void CancelSliderInteraction ()
		{
		lock (_sliderGate)
			{
			_pendingSliderCommand = null;
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

	private async Task ApplyDesiredLightCommandAsync (KasaDevice device, DesiredLightCommand command)
		{
		switch (command.Mode)
			{
			case DesiredLightMode.Off:
				await device.TurnLightOffAsync ().ConfigureAwait (false);
				return;

			case DesiredLightMode.On:
				await device.TurnLightOnAsync ().ConfigureAwait (false);
				return;

			case DesiredLightMode.Hsv:
				await device.SetHsvAsync (
					(int)Math.Round (Clamp01 (command.Hue) * HUE_MAX_DEGREES, MidpointRounding.AwayFromZero),
					(int)Math.Round (Clamp01 (command.Saturation) * 100d, MidpointRounding.AwayFromZero),
					ToBrightnessPercent (command.Level)).ConfigureAwait (false);
				return;

			case DesiredLightMode.ColorTemperature:
				await EnsurePoweredForLevelAsync (device, command.Level).ConfigureAwait (false);
				await device.SetBrightnessAsync (ToBrightnessPercent (command.Level)).ConfigureAwait (false);
				await device.SetColorTemperatureAsync ((int)Math.Max (1L, command.ColorTemperature)).ConfigureAwait (false);
				return;

			case DesiredLightMode.Brightness:
				await ApplyBrightnessCommandAsync (device, command.Level).ConfigureAwait (false);
				return;
			}
		}

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
		DeviceQueue.Stop ();
		}

	public override void Dispose ()
		{
		if (_disposed)
			{
			return;
			}

		Stop ();
		_lifetimeCancellationSource.Dispose ();
		_connectionGate.Dispose ();
		DeviceQueue.Dispose ();
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
				OnlineIndicatorIsOnline = false;
				ReadyIndicatorIsReady = false;
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
			OnlineIndicatorIsOnline = false;
			ReadyIndicatorIsReady = false;
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

		_pollingTask = _sharedConfiguration.EnableLightPolling
			? RunPollingCycleAsync (generation)
			: null;
		LogInfo ($"Light entity '{ControllerId}' RestartPolling: enabled={_sharedConfiguration.EnableLightPolling}, generation={generation}, intervalMs={_sharedConfiguration.LightPollInterval.TotalMilliseconds:0}.");
		}

	private void ResetConnectionState ()
		{
		LogInfo ($"Light entity '{ControllerId}' ResetConnectionState: hadConnectedDevice={_connectedDevice is not null}.");
		_connectedDevice = null;
		DeviceQueue.ClearClient ();
		_pendingStartupSnapshotAfterConnectedState = false;
		OnlineIndicatorIsOnline = false;
		ReadyIndicatorIsReady = false;
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
		}

	private void TryApplyDeferredDeviceStateAfterSliderInteraction (string context)
		{
		if (!_deferredDeviceStatePending || IsSliderInteractionActive ())
			{
			return;
			}

		DeviceQueue.EnqueueAsync (device =>
			{
				ApplyState (device, allowDeferredDeviceStateDuringSliderInteraction: true);
				LogInfo ($"Light entity '{ControllerId}' applied deferred device-driven state after slider interaction settled; context='{context}'.");
				return Task.CompletedTask;
			}).GetAwaiter ().GetResult ();
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
				|| generation != Volatile.Read (ref _pollingGeneration)
				|| !_sharedConfiguration.EnableLightPolling)
				{
				return;
				}

			await RefreshAsync (_lifetimeCancellationSource.Token).ConfigureAwait (false);
			PublishStateSnapshot ();

			if (_disposed
				|| Volatile.Read (ref _stopState) != 0
				|| generation != Volatile.Read (ref _pollingGeneration)
				|| !_sharedConfiguration.EnableLightPolling)
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
		if (_supportsBaseLight)
			{
			PublishProperty ("light:isOn", new DriverEntityValue (LightIsOn), "PublishStateSnapshot");
			}

		if (_supportsBrightness)
			{
			PublishProperty ("lightDimmer:level", new DriverEntityValue (LightDimmerLevel), "PublishStateSnapshot");
			}

		PublishTunableModeStateProperties ("PublishStateSnapshot", IsCurrentColorTemperatureUiMode ());

		}

	private async Task ExecuteWithConnectedDeviceAsync (Func<KasaDevice, Task> work, CancellationToken cancellationToken)
		{
		await EnsureConnectedAsync (cancellationToken).ConfigureAwait (false);
		await DeviceQueue.EnqueueAsync (work).ConfigureAwait (false);
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

		OnlineIndicatorIsOnline = true;
		ReadyIndicatorIsReady = true;
		PublishCurrentLightModeProperties ("InitializeConnectedStateAsync.AfterOnlineReady");
		_pendingStartupSnapshotAfterConnectedState = true;
		}

	private async Task InitializeStartupAsync ()
		{
		while (!_disposed && Volatile.Read (ref _stopState) == 0)
			{
			try
				{
				LogInfo ($"Light entity '{ControllerId}' startup connecting to discovered host '{_descriptor.Host}' as {_descriptor.DiscoveredDeviceType}; driverId='{_driverLogId}'.");
				await InitializeConnectedStateAsync (_lifetimeCancellationSource.Token).ConfigureAwait (false);
				return;
				}
			catch (OperationCanceledException) when (!_disposed && Volatile.Read (ref _stopState) == 0)
				{
				LogInfo ($"Light entity '{ControllerId}' startup connect canceled for host '{_descriptor.Host}'; retrying in {StartupConnectRetryInterval.TotalSeconds:0} seconds.");
				OnlineIndicatorIsOnline = false;
				ReadyIndicatorIsReady = false;
				}
			catch (OperationCanceledException)
				{
				throw;
				}
			catch (Exception ex)
				{
				LogInfo ($"Light entity '{ControllerId}' startup connect failed for host '{_descriptor.Host}': {ex.Message}; retrying in {StartupConnectRetryInterval.TotalSeconds:0} seconds.");
				OnlineIndicatorIsOnline = false;
				ReadyIndicatorIsReady = false;
				}

			await Task.Delay (StartupConnectRetryInterval, _lifetimeCancellationSource.Token).ConfigureAwait (false);
			}
		}

	private async Task<KasaDevice> EnsureConnectedAsync (CancellationToken cancellationToken)
		{
		if (!_isConfigured)
			{
			throw new InvalidOperationException ($"Light entity '{ControllerId}' cannot connect before child configuration/installation has occurred.");
			}

		KasaDevice? existingDevice = _connectedDevice;
		if (existingDevice is not null)
			{
			return existingDevice;
			}

		cancellationToken.ThrowIfCancellationRequested ();
		_lifetimeCancellationSource.Token.ThrowIfCancellationRequested ();

		using var linkedCancellationSource = CancellationTokenSource.CreateLinkedTokenSource (cancellationToken, _lifetimeCancellationSource.Token);
		await _connectionGate.WaitAsync (linkedCancellationSource.Token).ConfigureAwait (false);
		try
			{
			existingDevice = _connectedDevice;
			if (existingDevice is not null)
				{
				return existingDevice;
				}

			KasaDevice connectedDevice = await ConnectFromConfigurationAsync (updateState: true, linkedCancellationSource.Token).ConfigureAwait (false);

			LogInfo ($"Light entity '{ControllerId}' connect succeeded: deviceAlias='{connectedDevice.Alias ?? "<null>"}', systemInfoAlias='{connectedDevice.SystemInfo?.Alias ?? "<null>"}', model='{connectedDevice.SystemInfo?.Model ?? "<null>"}', deviceId='{connectedDevice.SystemInfo?.DeviceId ?? "<null>"}'.");

			_connectedDevice = connectedDevice;
			DeviceQueue.SetClient (connectedDevice);
			UpdateDescriptorFromConnectedDevice (connectedDevice);
			return connectedDevice;
			}
		finally
			{
			_ = _connectionGate.Release ();
			}
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

		return await Discover.ConnectAsync (
			configuration,
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
		_usesEmulatedColorTemperature = _supportsFullColor && _supportsColorTemperature;
		_supportsTunable = _supportsColorTemperature;
		_usesWhiteLightTunableSetLevels = _supportsColorTemperature && !_usesEmulatedColorTemperature;
		LogInfo ($"Light entity '{ControllerId}' dynamic features: descriptorKind={descriptor.Kind}, supportsLightControls={supportsLightControls}, featureCount={device.Features.Count}, supportsBrightness={_supportsBrightness}, supportsFullColor={_supportsFullColor}, supportsColorTemperatureApi={supportsColorTemperatureApi}, supportsColorTemperature={_supportsColorTemperature}, usesEmulatedColorTemperature={_usesEmulatedColorTemperature}, supportsTunable={_supportsTunable}, usesWhiteLightTunableSetLevels={_usesWhiteLightTunableSetLevels}, colorTemperatureRange={FormatRange (lightColorTemperatureRange)}.");

		if (!_supportsBrightness)
			{
			RemoveProperty ("lightDimmer:level");
			RemoveCommand ("lightDimmer:setLevel");
			}
		else
			{
			_supportsBaseLight = false;
			RemoveProperty ("light:isOn");
			RemoveCommand ("light:on");
			RemoveCommand ("light:off");
			}

		if (_supportsColorTemperature && _usesEmulatedColorTemperature)
			{
			bool hasActiveColorTemperature = HasCurrentColorTemperatureState (device.LightState);
			ColorLightEmulatedColorTemperatureMembers.TuningMode initialMode = hasActiveColorTemperature
				? ColorLightEmulatedColorTemperatureMembers.TuningMode.White
				: ColorLightEmulatedColorTemperatureMembers.TuningMode.Color;
			long initialColorTemperatureLevel = hasActiveColorTemperature
				? device.LightState!.ColorTemperature!.Value
				: 0L;
			int? initialHue = device.LightState?.Hue ?? device.LightState?.Hsv?.Hue;
			int? initialSaturation = device.LightState?.Saturation ?? device.LightState?.Hsv?.Saturation;
			LogInfo ($"Light entity '{ControllerId}' initializing color dynamic members before registration without setter notification: hasActiveColorTemperature={hasActiveColorTemperature}, initialMode={initialMode}, initialEmulatedColorTemperatureLevel={initialColorTemperatureLevel}, hue={initialHue?.ToString () ?? "<null>"}, saturation={initialSaturation?.ToString () ?? "<null>"}.");
			_registeredEmulatedColorTemperatureMembers = new ColorLightEmulatedColorTemperatureMembers (this, lightColorTemperatureRange!, initialColorTemperatureLevel, initialMode);
			RegisterObjectWithAttributes (_registeredEmulatedColorTemperatureMembers);
			LogInfo ($"Light entity '{ControllerId}' registered color dynamic members: initialMode={initialMode}, initialEmulatedColorTemperatureLevel={initialColorTemperatureLevel}.");
			}

		if (_usesWhiteLightTunableSetLevels)
			{
			bool hasActiveColorTemperature = HasCurrentColorTemperatureState (device.LightState);
			long initialColorTemperatureLevel = hasActiveColorTemperature
				? device.LightState!.ColorTemperature!.Value
				: 0L;
			_registeredWhiteLightColorTemperatureMembers = new WhiteLightColorTemperatureMembers (this, lightColorTemperatureRange!, initialColorTemperatureLevel);
			RegisterObjectWithAttributes (_registeredWhiteLightColorTemperatureMembers);
			}

		if (!_supportsFullColor)
			{
			LogInfo ($"Light entity '{ControllerId}' full-color members not registered because full-color state is not supported.");
			}
		else
			{
			bool hasActiveColorTemperature = HasCurrentColorTemperatureState (device.LightState);
			double initialHue = 0d;
			double initialSaturation = 0d;
			if (!hasActiveColorTemperature)
				{
				int? hue = device.LightState?.Hue ?? device.LightState?.Hsv?.Hue;
				int? saturation = device.LightState?.Saturation ?? device.LightState?.Hsv?.Saturation;
				initialHue = hue.HasValue ? Clamp01 (hue.Value / HUE_MAX_DEGREES) : 0d;
				initialSaturation = saturation.HasValue ? Clamp01 (saturation.Value / 100d) : 0d;
				}

			_registeredFullColorMembers = new FullColorMembers (this, initialHue, initialSaturation);
			RegisterObjectWithAttributes (_registeredFullColorMembers);
			LogInfo ($"Light entity '{ControllerId}' registered full-color dynamic members: hasActiveColorTemperature={hasActiveColorTemperature}, initialHue={initialHue:0.####}, initialSaturation={initialSaturation:0.####}.");
			}

		RaiseDefinitionChangedEvent ();
		LogDynamicFeatureEntityState ("ConfigureDynamicFeatures.AfterDefinitionChanged");

		}

	protected Task ExecuteDeviceCommandAsync (Func<KasaDevice, Task> action, bool refreshAfterCommand = true, CancellationToken cancellationToken = default)
		{
		try
			{
			return ExecuteWithConnectedDeviceAsync (async device =>
				{
					cancellationToken.ThrowIfCancellationRequested ();
					await action (device).ConfigureAwait (false);

					if (refreshAfterCommand)
						{
						await device.UpdateAsync (cancellationToken).ConfigureAwait (false);
						LogReportedState ("ExecuteDeviceCommandAsync.AfterCommand", device);
						}

					ApplyState (device);
					OnlineIndicatorIsOnline = true;
					ReadyIndicatorIsReady = true;
				}, cancellationToken);
			}
		catch (Exception ex)
			{
			OnlineIndicatorIsOnline = false;
			ReadyIndicatorIsReady = false;
			_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Light entity '{ControllerId}' command failed: {ex}");
			return Task.FromException (ex);
			}
		}

	private static Task SetBrightnessAsync (KasaDevice device, double relativeLevel)
		{
		int brightness = (int)Math.Round (relativeLevel * 100d, MidpointRounding.AwayFromZero);
		return brightness <= 0
			? device.TurnLightOffAsync ()
			: device.SetBrightnessAsync (brightness);
		}

	private async Task ExecutePowerAsync (KasaDevice device, bool on)
		{
		string? childId = ChildId;
		if (!string.IsNullOrWhiteSpace (childId))
			{
			string resolvedChildId = childId!;
			if (on)
				{
				await device.TurnChildOnAsync (resolvedChildId).ConfigureAwait (false);
				}
			else
				{
				await device.TurnChildOffAsync (resolvedChildId).ConfigureAwait (false);
				}

			return;
			}

		if (Kind == ManagedLightKind.OnOff)
			{
			if (on)
				{
				await device.TurnOnAsync ().ConfigureAwait (false);
				}
			else
				{
				await device.TurnOffAsync ().ConfigureAwait (false);
				}

			return;
			}

		if (!on)
			{
			await device.TurnLightOffAsync ().ConfigureAwait (false);
			return;
			}

		await device.TurnLightOnAsync ().ConfigureAwait (false);
		}

	private void ApplyState (KasaDevice device, bool allowDeferredDeviceStateDuringSliderInteraction = false)
		{
		if (!allowDeferredDeviceStateDuringSliderInteraction && IsSliderInteractionActive ())
			{
			_deferredDeviceStatePending = true;
			LogInfo ($"Light entity '{ControllerId}' deferring device-driven state apply while slider interaction is active.");
			return;
			}

		_deferredDeviceStatePending = false;
		using (PropertyChangeTracker.StartBatchedUpdate (true))
			{
			ApplyStateCore (device);
			}

		if (!_suppressPropertyNotifications)
			{
			PublishCurrentLightModeProperties ("ApplyState");
			if (_supportsBaseLight)
				{
				PublishProperty ("light:isOn", new DriverEntityValue (LightIsOn), "ApplyState");
				}
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

		if (_supportsBaseLight)
			{
			LightIsOn = isOn;
			}

		if (_supportsBrightness)
			{
			LightDimmerLevel = isOn && brightness.HasValue
				? Clamp01 (brightness.Value / 100d)
				: 0d;
			}

		if (_supportsColorTemperature)
			{
			SetColorTemperatureLevelSilently (hasActiveColorTemperature
				? colorTemperature!.Value
				: 0L);
			}

		if (_supportsTunable)
			{
			SetLightTunableMode (hasActiveColorTemperature
				? LightTunableTuningMode.White
				: LightTunableTuningMode.Color);
			}

		if (_supportsFullColor && !hasActiveColorTemperature)
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

		OnStateApplied (device);
		}

	private static bool HasCurrentColorTemperatureState (LightState? lightState)
		{
		if (lightState?.ColorTemperature is not int colorTemperature || colorTemperature <= 0)
			{
			return false;
			}

		return true;
		}

	private static bool HasCurrentFullColorState (LightState? lightState)
		{
		return lightState?.Hsv is not null
			|| lightState?.Hue is not null
			|| lightState?.Saturation is not null;
		}

	private bool IsCurrentUiColorMode ()
		{
		return _supportsFullColor && LightColorSaturation > 0d;
		}

	private bool IsCurrentColorTemperatureUiMode ()
		{
		if (!_supportsColorTemperature)
			{
			return false;
			}

		if (_usesEmulatedColorTemperature && GetLightTunableMode () != LightTunableTuningMode.White)
			{
			return false;
			}

		return GetActiveColorTemperatureLevel () > 0L;
		}

	private void ClearColorTemperatureUiMode ()
		{
		if (_usesEmulatedColorTemperature)
			{
			SetLightTunableMode (LightTunableTuningMode.Color);
			return;
			}

		bool hasColorTemperatureLevel = _supportsColorTemperature && GetActiveColorTemperatureLevel () > 0L;
		if (!hasColorTemperatureLevel)
			{
			return;
			}

		SetColorTemperatureLevelSilently (0L);
		}

	private LightTunableTuningMode GetLightTunableMode ()
		{
		if (!_supportsColorTemperature)
			{
			return LightTunableTuningMode.Color;
			}

		if (!_usesEmulatedColorTemperature)
			{
			return LightTunableTuningMode.White;
			}

		return _registeredEmulatedColorTemperatureMembers!.LightTunableMode == ColorLightEmulatedColorTemperatureMembers.TuningMode.White
			? LightTunableTuningMode.White
			: LightTunableTuningMode.Color;
		}

	private void SetLightTunableMode (LightTunableTuningMode mode)
		{
		if (!_supportsTunable || !_usesEmulatedColorTemperature)
			{
			return;
			}

		_registeredEmulatedColorTemperatureMembers!.LightTunableMode = mode == LightTunableTuningMode.White
			? ColorLightEmulatedColorTemperatureMembers.TuningMode.White
			: ColorLightEmulatedColorTemperatureMembers.TuningMode.Color;
		}

	private void PublishCurrentLightModeProperties (string context)
		{
		bool hasActiveColorTemperature = IsCurrentColorTemperatureUiMode ();

		if (_supportsBrightness)
			{
			PublishProperty ("lightDimmer:level", new DriverEntityValue (LightDimmerLevel), context);
			}

		PublishTunableModeStateProperties (context, hasActiveColorTemperature);
		}

	private long GetActiveColorTemperatureLevel ()
		{
		if (!_supportsColorTemperature)
			{
			return 0L;
			}

		return _usesEmulatedColorTemperature
			? _registeredEmulatedColorTemperatureMembers!.LightEmulatedColorTemperatureLevel
			: _registeredWhiteLightColorTemperatureMembers!.LightColorTemperatureLevel;
		}

	private void PublishTunableModeStateProperties (string context, bool hasActiveColorTemperature)
		{
		PublishColorLightTunableModeProperty (context);

		if (_supportsFullColor && !hasActiveColorTemperature)
			{
			PublishProperty ("lightColor:saturation", new DriverEntityValue (LightColorSaturation), context);
			PublishProperty ("lightColor:hue", new DriverEntityValue (LightColorHue), context);
			}

		if (_supportsColorTemperature)
			{
			PublishProperty (_usesEmulatedColorTemperature ? "lightEmulatedColorTemperature:level" : "lightColorTemperature:level", new DriverEntityValue (GetActiveColorTemperatureLevel ()), context);
			}
		}

	private void PublishActiveColorModeProperties (string context)
		{
		PublishColorLightTunableModeProperty (context);

		if (_usesEmulatedColorTemperature)
			{
			PublishProperty ("lightEmulatedColorTemperature:level", new DriverEntityValue (_registeredEmulatedColorTemperatureMembers!.LightEmulatedColorTemperatureLevel), context);
			}

		if (_supportsFullColor)
			{
			PublishProperty ("lightColor:saturation", new DriverEntityValue (LightColorSaturation), context);
			PublishProperty ("lightColor:hue", new DriverEntityValue (LightColorHue), context);
			}
		}

	private void PublishColorLightTunableModeProperty (string context)
		{
		if (!_usesEmulatedColorTemperature)
			{
			return;
			}

		PublishProperty ("lightTunable:mode", CreateValueForObject (_registeredEmulatedColorTemperatureMembers!.LightTunableMode), context);
		}

	private void PublishProperty (string propertyId, DriverEntityValue value, string context)
		{
		LogPublishedProperty (propertyId, "value", context);
		NotifyPropertyChanged (propertyId, value);
		}

	private void LogDynamicFeatureEntityState (string context)
		{
		if (Interlocked.Exchange (ref _dynamicFeatureStateDiagnosticLogged, 1) != 0)
			{
			return;
			}

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
		LogPublishedProperty (propertyId, "range", context);
		DriverEntityValue entityValue = new (value);
		NotifyPropertyChanged (propertyId, entityValue);
		}

	private void LogPublishedProperty (string propertyId, string valueKind, string context)
		{
		if (!IsLightModeDiagnosticProperty (propertyId))
			{
			return;
			}

		LogInfo ($"Light entity '{ControllerId}' publishing propertyId='{propertyId}', valueKind='{valueKind}', context='{context}'.");
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
				DeviceQueue.SetClient (reconnectedDevice);
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

	private async Task ApplyBrightnessCommandAsync (KasaDevice device, double relativeLevel)
		{
		if (relativeLevel <= 0d)
			{
			await device.TurnLightOffAsync ().ConfigureAwait (false);
			return;
			}

		await EnsurePoweredForLevelAsync (device, relativeLevel).ConfigureAwait (false);
		await device.SetBrightnessAsync (ToBrightnessPercent (relativeLevel)).ConfigureAwait (false);
		}

	private static async Task EnsurePoweredForLevelAsync (KasaDevice device, double relativeLevel)
		{
		if (relativeLevel <= 0d || device.LightState?.IsOn == true || device.IsOn == true)
			{
			return;
			}

		await device.TurnLightOnAsync ().ConfigureAwait (false);
		}

	private double GetEffectiveOnLevel () => LightDimmerLevel > 0d ? LightDimmerLevel : 1d;

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
		LogInfo ($"Light entity '{ControllerId}' published snapshot: isOn={LightIsOn}, dimmer={LightDimmerLevel:0.####}, hue={LightColorHue:0.####}, saturation={LightColorSaturation:0.####}, colorTemperature={GetActiveColorTemperatureLevel ()}, tunableMode={GetLightTunableMode ()}, colorTemperatureUiMode={IsCurrentColorTemperatureUiMode ()}.");
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
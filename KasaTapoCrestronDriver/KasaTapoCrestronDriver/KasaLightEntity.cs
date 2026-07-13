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

	// Kasa/Tapo devices expose color_temp, hue, and saturation as independent device-level fields;
	// setting color_temp never causes the device to recompute hue/saturation (confirmed against the
	// KasaTapoClient transport, which passes these three fields through unmodified). Even so, per the
	// Crestron Lights API docs, lightEmulatedColorTemperature is the correct capability to register for
	// lights that support hue/saturation but also have a color-temperature control that overrides the
	// displayed color (Color-kind bulbs here), while lightColorTemperature remains correct for lights
	// that only support color temperature and not hue/saturation at all (TunableWhite-kind bulbs).
	// Shared abstraction over the two mutually-exclusive color-temperature capability shapes we can
	// register (lightColorTemperature vs lightEmulatedColorTemperature - see ConfigureDynamicFeatures),
	// so the owner class can publish/read/write the active level without needing to know which
	// concrete capability is currently registered.
	private interface IColorTemperatureLevelHolder
		{
		string LevelPropertyId { get; }
		long LightColorTemperatureLevel { get; set; }
		}

	// Per the Crestron Lights API docs, lightColorTemperature is intended for lights where color
	// temperature operates in combination with hue/saturation (e.g. saturation blends between hue and
	// color temperature), or for lights that only support color temperature and not hue/saturation at
	// all. This capability is therefore only registered for TunableWhite-kind lights (CT only, no
	// hue/saturation) - see ConfigureDynamicFeatures.
	private sealed class ColorTemperatureMembers : IColorTemperatureLevelHolder
		{
		private readonly KasaLightEntity _owner;
		private long _lightColorTemperatureLevel;

		public ColorTemperatureMembers (KasaLightEntity owner, DriverEntityValueRange range, long level)
			{
			_owner = owner;
			LightColorTemperatureRange = range;
			_lightColorTemperatureLevel = level;
			}

		string IColorTemperatureLevelHolder.LevelPropertyId => "lightColorTemperature:level";

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

	// Per the Crestron Lights API docs, lightEmulatedColorTemperature is intended for lights that
	// support hue/saturation but also have a color-temperature control that overrides/supersedes the
	// displayed hue/saturation color. This is a much closer structural match than lightColorTemperature
	// for Color-kind Kasa/Tapo bulbs (KL130, L530, L900, etc.), which independently retain HSV and
	// color-temperature values, but only ever display one or the other at a time. This capability is
	// therefore registered instead of lightColorTemperature whenever full color is also supported - see
	// ConfigureDynamicFeatures.
	private sealed class EmulatedColorTemperatureMembers : IColorTemperatureLevelHolder
		{
		private readonly KasaLightEntity _owner;
		private long _lightColorTemperatureLevel;

		public EmulatedColorTemperatureMembers (KasaLightEntity owner, DriverEntityValueRange range, long level)
			{
			_owner = owner;
			LightEmulatedColorTemperatureRange = range;
			_lightColorTemperatureLevel = level;
			}

		string IColorTemperatureLevelHolder.LevelPropertyId => "lightEmulatedColorTemperature:level";

		[EntityProperty (Id = "lightEmulatedColorTemperature:range", Units = "Kelvin")]
		public DriverEntityValueRange LightEmulatedColorTemperatureRange { get; set; } = new DriverEntityValueRange (0, 0, 1);

		[EntityProperty (Id = "lightEmulatedColorTemperature:level", RangeProperty = "lightEmulatedColorTemperature:range", Units = "Kelvin")]
		public long LightColorTemperatureLevel
			{
			get => _lightColorTemperatureLevel;
			set => _owner.SetAndNotify ("lightEmulatedColorTemperature:level", value, ref _lightColorTemperatureLevel);
			}

		[EntityCommand (Id = "lightEmulatedColorTemperature:setLevel")]
		public void SetColorTemperatureLevel ([EntityParameter (RangeProperty = "lightEmulatedColorTemperature:range", Units = "Kelvin")] long level)
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

	// Registered only for lights that report brightness support; mutually exclusive with
	// OnOffMembers - see ConfigureDynamicFeatures. Kept as its own dynamic object (rather than static
	// reflected members on the owner) so nothing ever needs to be added/removed after connect.
	private sealed class DimmableMembers
		{
		private readonly KasaLightEntity _owner;
		private double _lightDimmerLevel;

		public DimmableMembers (KasaLightEntity owner, double initialLevel)
			{
			_owner = owner;
			_lightDimmerLevel = initialLevel;
			}

		[EntityProperty (Id = "lightDimmer:levelRange")]
		public DriverEntityValueRelativeRange LightDimmerLevelRange { get; } = new (0.01);

		[EntityProperty (Id = "lightDimmer:level", RelativeRangeProperty = "lightDimmer:levelRange")]
		public double LightDimmerLevel
			{
			get => _lightDimmerLevel;
			set => _owner.SetAndNotify ("lightDimmer:level", value, ref _lightDimmerLevel);
			}

		[EntityCommand (Id = "lightDimmer:setLevel")]
		public void LightDimmerSetLevel ([EntityParameter (RangeMinimum = 0, RangeMaximum = 1, RangeStepSize = 0.01)] double level)
			{
			_owner.LightDimmerSetLevel (level);
			}
		}

	// Registered only for lights that do not report brightness support (simple on/off lights);
	// mutually exclusive with DimmableMembers - see ConfigureDynamicFeatures. Kept as its own dynamic
	// object (rather than static reflected members on the owner) so nothing ever needs to be
	// added/removed after connect.
	private sealed class OnOffMembers
		{
		private readonly KasaLightEntity _owner;
		private bool _lightIsOn;

		public OnOffMembers (KasaLightEntity owner, bool initialIsOn)
			{
			_owner = owner;
			_lightIsOn = initialIsOn;
			}

		[EntityProperty (Id = "light:isOn")]
		public bool LightIsOn
			{
			get => _lightIsOn;
			set => _owner.SetAndNotify ("light:isOn", value, ref _lightIsOn);
			}

		[EntityCommand (Id = "light:off")]
		public void LightOff ()
			{
			_owner.LightOff ();
			}

		[EntityCommand (Id = "light:on")]
		public void LightOn ()
			{
			_owner.LightOn ();
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
		_registeredColorTemperatureMembers!.LightColorTemperatureLevel = value;
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

	private double LightDimmerLevel
		{
		get => _registeredDimmableMembers?.LightDimmerLevel ?? 0d;
		set
			{
			if (_registeredDimmableMembers is not null)
				{
				_registeredDimmableMembers.LightDimmerLevel = value;
				}
			}
		}

	private bool LightIsOn
		{
		get => _registeredOnOffMembers?.LightIsOn ?? false;
		set
			{
			if (_registeredOnOffMembers is not null)
				{
				_registeredOnOffMembers.LightIsOn = value;
				}
			}
		}

	private static readonly TimeSpan SliderDebounceInterval = TimeSpan.FromMilliseconds (250);
	private static int _dynamicFeatureStateDiagnosticLogged;

	private SemaphoreSlim _connectionGate { get; }
	private object _sliderGate { get; } = new ();
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
	private bool _supportsFullColor { get; set; }
	private bool _supportsColorTemperature { get; set; }
	private bool _isColorTemperatureUiModeActive { get; set; }
	private int _dynamicFeaturesConfigured;
	private static readonly TimeSpan StartupConnectRetryInterval = TimeSpan.FromSeconds (10);
	private static readonly TimeSpan StartupConnectTimeout = TimeSpan.FromSeconds (8);
	private DesiredLightCommand? _pendingSliderCommand { get; set; }
	private CancellationTokenSource? _sliderCommandCancellationSource { get; set; }
	private bool _suppressPropertyNotifications { get; set; } = true;
	private bool _isConfigured { get; set; }
	private bool _childPublished { get; set; }
	private bool _pendingStartupSnapshotAfterConnectedState { get; set; }
	private bool _deferredDeviceStatePending { get; set; }
	private FullColorMembers? _registeredFullColorMembers { get; set; }
	private IColorTemperatureLevelHolder? _registeredColorTemperatureMembers { get; set; }
	private DriverEntityValueRange? _colorTemperatureLevelRange { get; set; }
	private DimmableMembers? _registeredDimmableMembers { get; set; }
	private OnOffMembers? _registeredOnOffMembers { get; set; }
	private int _stopState;
	private int _pollingGeneration;
	private bool _disposed { get; set; }

	public KasaLightEntity (
		string controllerId,
		ManagedLightDescriptor descriptor,
		DeviceConfiguration configuration,
		SemaphoreSlim connectionGate,
			Action<ManagedLightDescriptor>? descriptorUpdated,
		IPlatformSharedConfiguration sharedConfiguration,
		DriverImplementationResources resources,
		DriverControllerLogger logger,
		string driverLogId)
		: base (controllerId)
		{
		_connectionGate = connectionGate ?? throw new ArgumentNullException (nameof (connectionGate));
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

		if (!_connectionGate.Wait (0))
			{
			return false;
			}

		try
			{
			if (_connectedDevice is not null)
				{
				return false;
				}

			_connectedDevice = device;
			UpdateDescriptorFromConnectedDevice (device);
			LogInfo ($"Light entity '{ControllerId}' adopted connected device from context='{context}'.");
			return true;
			}
		finally
			{
			_ = _connectionGate.Release ();
			}
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
		if (IgnoreValueCommandWhileOff ("lightColor:setHue"))
			{
			return;
			}

		_isColorTemperatureUiModeActive = false;
		if (_supportsColorTemperature)
			{
			ReconcileColorTemperatureCapability (hasActiveColorTemperature: false, colorTemperature: null);
			}
		LightColorHue = hueLevel;
		PublishActiveColorModeProperties ("lightColor:setHue");

		QueueSliderCommand (new DesiredLightCommand (DesiredLightMode.Hsv, GetEffectiveOnLevel (), hueLevel, LightColorSaturation, 0L));
		}

	private void LightColorSetSaturation (double level)
		{
		double saturationLevel = Clamp01 (level);
		LogCommandInvocation ("lightColor:setSaturation", $"saturation={saturationLevel:0.####}");
		if (IgnoreValueCommandWhileOff ("lightColor:setSaturation"))
			{
			return;
			}

		_isColorTemperatureUiModeActive = false;
		if (_supportsColorTemperature)
			{
			ReconcileColorTemperatureCapability (hasActiveColorTemperature: false, colorTemperature: null);
			}
		LightColorSaturation = saturationLevel;
		PublishActiveColorModeProperties ("lightColor:setSaturation");

		QueueSliderCommand (new DesiredLightCommand (DesiredLightMode.Hsv, GetEffectiveOnLevel (), LightColorHue, saturationLevel, 0L));
		}

	private void LightDimmerSetLevel (double level)
		{
		double relativeLevel = Clamp01 (level);
		LogCommandInvocation ("lightDimmer:setLevel", $"level={relativeLevel:0.####}");
		LightDimmerLevel = relativeLevel;

		QueueSliderCommand (new DesiredLightCommand (DesiredLightMode.Brightness, relativeLevel, 0d, 0d, 0L));
		}

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

		LightIsOn = true;
		StartBackgroundOperation (() => ExecuteDeviceCommandAsync ((device, cancellationToken) => ExecutePowerAsync (device, true, cancellationToken), refreshAfterCommand: true, _lifetimeCancellationSource.Token), "light:on");
		}

	private void SetColorTemperatureLevel (string commandId, long level)
		{
		LogCommandInvocation (commandId, $"level={level}");
		if (IgnoreValueCommandWhileOff (commandId))
			{
			return;
			}

		long temperatureLevel = Math.Max (1L, level);
		_isColorTemperatureUiModeActive = true;
		ReconcileColorTemperatureCapability (hasActiveColorTemperature: true, colorTemperature: (int)temperatureLevel);
		SetActiveColorTemperatureLevel (temperatureLevel);

		QueueSliderCommand (new DesiredLightCommand (DesiredLightMode.ColorTemperature, GetEffectiveOnLevel (), 0d, 0d, temperatureLevel));
		}

	private bool IgnoreValueCommandWhileOff (string commandId)
		{
		if (LightIsOn)
			{
			return false;
			}

		LogInfo ($"Light entity '{ControllerId}' ignoring value command '{commandId}' while off; waiting for dimmer power-on command.");

		// The UI (e.g. a slider) may optimistically switch its displayed mode before the command
		// result is known. Since this command is being dropped rather than applied, republish the
		// current authoritative mode/property state so the UI reverts to reality instead of being
		// left showing a mode the device never actually entered.
		PublishCurrentLightModeProperties ($"IgnoreValueCommandWhileOff:{commandId}");
		return true;
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

			// Once the debounce window has elapsed we are committed to dispatching this
			// command to the physical device. From this point on, a newly superseding
			// slider command must NOT cancel this in-flight hardware call — only entity
			// lifetime/disposal (via _lifetimeCancellationSource) may cancel it. The
			// KasaTapoClient device itself already serializes concurrent operations, so
			// no additional dispatch gate is needed here.
			CancellationToken lifetimeToken = _lifetimeCancellationSource.Token;
			lifetimeToken.ThrowIfCancellationRequested ();

			LogInfo ($"Light entity '{ControllerId}' dispatch slider command: {FormatDesiredLightCommand (command.Value)}.");

			await ExecuteDeviceCommandAsync (
				async (device, innerCancellationToken) =>
					{
						innerCancellationToken.ThrowIfCancellationRequested ();
						await ApplyDesiredLightCommandAsync (device, command.Value, innerCancellationToken).ConfigureAwait (false);
					},
				refreshAfterCommand: true,
				lifetimeToken).ConfigureAwait (false);
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

		LogInfo ($"Light entity '{ControllerId}' child reached Running; republishing current state snapshot; context='{context}'.");
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

		_pollingTask = _sharedConfiguration.EnableLightPolling
			? RunPollingCycleAsync (generation)
			: null;
		LogInfo ($"Light entity '{ControllerId}' RestartPolling: enabled={_sharedConfiguration.EnableLightPolling}, generation={generation}, intervalMs={_sharedConfiguration.LightPollInterval.TotalMilliseconds:0}.");
		}

	private void ResetConnectionState ()
		{
		KasaDevice? staleDevice = _connectedDevice;
		LogInfo ($"Light entity '{ControllerId}' ResetConnectionState: hadConnectedDevice={staleDevice is not null}.");
		_connectedDevice = null;
		_pendingStartupSnapshotAfterConnectedState = false;
		OnlineIndicatorIsOnline = false;
		ReadyIndicatorIsReady = false;

		if (staleDevice is not null)
			{
			try
				{
				staleDevice.Dispose ();
				}
			catch (Exception ex)
				{
				_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Light entity '{ControllerId}' failed to dispose stale connected device: {ex}");
				}
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

		OnlineIndicatorIsOnline = true;
		ReadyIndicatorIsReady = true;
		PublishCurrentLightModeProperties ("InitializeConnectedStateAsync.AfterOnlineReady");
		_pendingStartupSnapshotAfterConnectedState = true;
		TryPublishDeferredStartupSnapshot ("InitializeConnectedStateAsync");
		}

	private async Task InitializeStartupAsync ()
		{
		while (!_disposed && Volatile.Read (ref _stopState) == 0)
			{
			try
				{
				LogInfo ($"Light entity '{ControllerId}' startup connecting to discovered host '{_descriptor.Host}' as {_descriptor.DiscoveredDeviceType}; driverId='{_driverLogId}'.");
				using (CancellationTokenSource timeoutCancellationSource = CancellationTokenSource.CreateLinkedTokenSource (_lifetimeCancellationSource.Token))
					{
					timeoutCancellationSource.CancelAfter (StartupConnectTimeout);
					await InitializeConnectedStateAsync (timeoutCancellationSource.Token).ConfigureAwait (false);
					}
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
		LogInfo ($"Light entity '{ControllerId}' EnsureConnectedAsync waiting on connection gate.");
		Stopwatch gateWaitStopwatch = Stopwatch.StartNew ();
		await _connectionGate.WaitAsync (linkedCancellationSource.Token).ConfigureAwait (false);
		gateWaitStopwatch.Stop ();
		LogInfo ($"Light entity '{ControllerId}' EnsureConnectedAsync acquired connection gate after {gateWaitStopwatch.Elapsed.TotalMilliseconds:0} ms.");
		try
			{
			existingDevice = _connectedDevice;
			if (existingDevice is not null)
				{
				return existingDevice;
				}

			Stopwatch connectStopwatch = Stopwatch.StartNew ();
			KasaDevice connectedDevice = await ConnectFromConfigurationAsync (updateState: true, linkedCancellationSource.Token).ConfigureAwait (false);
			connectStopwatch.Stop ();

			LogInfo ($"Light entity '{ControllerId}' connect succeeded after {connectStopwatch.Elapsed.TotalMilliseconds:0} ms: deviceAlias='{connectedDevice.Alias ?? "<null>"}', systemInfoAlias='{connectedDevice.SystemInfo?.Alias ?? "<null>"}', model='{connectedDevice.SystemInfo?.Model ?? "<null>"}', deviceId='{connectedDevice.SystemInfo?.DeviceId ?? "<null>"}'.");

			_connectedDevice = connectedDevice;
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

			// When the device is not currently in color-temperature mode (e.g. it is in HSV/color
			// mode), Kasa/Tapo devices report color_temp=0, which is out of range of the device's
			// declared lightColorTemperature:range (e.g. 2500-9000K). Publishing an out-of-range
			// level causes the Crestron Home UI to misinterpret/clamp the value. Since there is no
			// device-side "last known" color-temperature value to fall back on at initial connect,
			// use the range minimum as an in-range placeholder; the UI mode itself is tracked
			// separately via _isColorTemperatureUiModeActive, not inferred from this level.
			long initialColorTemperatureLevel = hasActiveColorTemperature
				? device.LightState!.ColorTemperature!.Value
				: (long)(lightColorTemperatureRange!.Minimum ?? 0d);

			// The capability shape is chosen from the bulb's *current* mode, not its static kind: a
			// bulb that supports full color and is currently in color mode (no active color
			// temperature) needs lightEmulatedColorTemperature so the CT slider is understood as an
			// override of the displayed color, while a bulb currently in white/CT mode needs plain
			// lightColorTemperature so the persisted-but-currently-irrelevant HSV values are not
			// mistaken for the active mode. Bulbs without full color (TunableWhite) always use the
			// plain capability. This selection is re-evaluated on every mode change - see
			// ReconcileColorTemperatureCapability.
			bool useEmulatedColorTemperature = _supportsFullColor && !hasActiveColorTemperature;
			_colorTemperatureLevelRange = lightColorTemperatureRange;
			_registeredColorTemperatureMembers = useEmulatedColorTemperature
				? new EmulatedColorTemperatureMembers (this, lightColorTemperatureRange!, initialColorTemperatureLevel)
				: new ColorTemperatureMembers (this, lightColorTemperatureRange!, initialColorTemperatureLevel);
			RegisterObjectWithAttributes (_registeredColorTemperatureMembers);
			LogInfo ($"Light entity '{ControllerId}' registered color-temperature dynamic members: useEmulatedColorTemperature={useEmulatedColorTemperature}, initialColorTemperatureLevel={initialColorTemperatureLevel}.");
			}

		if (!_supportsFullColor)
			{
			LogInfo ($"Light entity '{ControllerId}' full-color members not registered because full-color state is not supported.");
			}
		else
			{
			// The device reports real hue/saturation values regardless of whether color temperature is
			// currently active (color temperature only overrides the displayed color while active; it
			// does not clear or alter the stored hue/saturation), so always seed the initial members
			// from those real values.
			int? hue = device.LightState?.Hue ?? device.LightState?.Hsv?.Hue;
			int? saturation = device.LightState?.Saturation ?? device.LightState?.Hsv?.Saturation;
			double initialHue = hue.HasValue ? Clamp01 (hue.Value / HUE_MAX_DEGREES) : 0d;
			double initialSaturation = saturation.HasValue ? Clamp01 (saturation.Value / 100d) : 0d;

			_registeredFullColorMembers = new FullColorMembers (this, initialHue, initialSaturation);
			RegisterObjectWithAttributes (_registeredFullColorMembers);
			LogInfo ($"Light entity '{ControllerId}' registered full-color dynamic members: initialHue={initialHue:0.####}, initialSaturation={initialSaturation:0.####}.");
			}

		RaiseDefinitionChangedEvent ();
		LogDynamicFeatureEntityState ("ConfigureDynamicFeatures.AfterDefinitionChanged");

		}

	// Bulbs that support both full color and color temperature (Color-kind Kasa/Tapo bulbs) can be
	// switched between color mode and white/CT mode at any time, and Crestron Home infers which mode a
	// light is in from which color-temperature capability is currently registered rather than from the
	// bulb kind. lightEmulatedColorTemperature signals "this is a color light; CT overrides the
	// displayed color", which is correct while the bulb is in color mode. lightColorTemperature signals
	// "this light's color state is defined purely by CT", which is correct while the bulb is in
	// white/CT mode - any persisted HSV is irrelevant to the UI until the bulb returns to color mode.
	// This method re-registers the appropriate capability whenever the device's active mode has
	// flipped since the last time dynamic features were configured/reconciled.
	private void ReconcileColorTemperatureCapability (bool hasActiveColorTemperature, int? colorTemperature)
		{
		if (_registeredColorTemperatureMembers is null || _colorTemperatureLevelRange is null)
			{
			return;
			}

		bool shouldUseEmulatedColorTemperature = _supportsFullColor && !hasActiveColorTemperature;
		bool isCurrentlyEmulated = _registeredColorTemperatureMembers is EmulatedColorTemperatureMembers;
		if (shouldUseEmulatedColorTemperature == isCurrentlyEmulated)
			{
			return;
			}

		long currentLevel = hasActiveColorTemperature && colorTemperature.HasValue
			? colorTemperature.Value
			: _registeredColorTemperatureMembers.LightColorTemperatureLevel;

		UnregisterObjectWithAttributes (_registeredColorTemperatureMembers);

		_registeredColorTemperatureMembers = shouldUseEmulatedColorTemperature
			? new EmulatedColorTemperatureMembers (this, _colorTemperatureLevelRange, currentLevel)
			: new ColorTemperatureMembers (this, _colorTemperatureLevelRange, currentLevel);
		RegisterObjectWithAttributes (_registeredColorTemperatureMembers);
		RaiseDefinitionChangedEvent ();

		LogInfo ($"Light entity '{ControllerId}' switched color-temperature capability: useEmulatedColorTemperature={shouldUseEmulatedColorTemperature}, currentLevel={currentLevel}.");
		}

	private static readonly TimeSpan DeviceCommandTimeout = TimeSpan.FromSeconds (8);
	private static readonly TimeSpan DeviceCommandRetryDelay = TimeSpan.FromSeconds (1);
	private const int DEVICE_COMMAND_MAX_ATTEMPTS = 2;

	protected async Task ExecuteDeviceCommandAsync (Func<KasaDevice, CancellationToken, Task> action, bool refreshAfterCommand = true, CancellationToken cancellationToken = default)
		{
		for (int attempt = 1; ; attempt++)
			{
			try
				{
				await ExecuteDeviceCommandAttemptAsync (action, refreshAfterCommand, cancellationToken).ConfigureAwait (false);
				return;
				}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
				throw;
				}
			catch (Exception ex) when (attempt < DEVICE_COMMAND_MAX_ATTEMPTS)
				{
				_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Light entity '{ControllerId}' command attempt {attempt} of {DEVICE_COMMAND_MAX_ATTEMPTS} failed; retrying after {DeviceCommandRetryDelay.TotalSeconds:0} second(s): {ex}");
				await Task.Delay (DeviceCommandRetryDelay, cancellationToken).ConfigureAwait (false);
				}
			}
		}

	private async Task ExecuteDeviceCommandAttemptAsync (Func<KasaDevice, CancellationToken, Task> action, bool refreshAfterCommand, CancellationToken cancellationToken)
		{
		try
			{
			using CancellationTokenSource timeoutCancellationSource = CancellationTokenSource.CreateLinkedTokenSource (cancellationToken);
			timeoutCancellationSource.CancelAfter (DeviceCommandTimeout);
			CancellationToken timeoutToken = timeoutCancellationSource.Token;

			try
				{
				await ExecuteWithConnectedDeviceAsync (async device =>
					{
						timeoutToken.ThrowIfCancellationRequested ();
						await action (device, timeoutToken).ConfigureAwait (false);

						if (refreshAfterCommand)
							{
							await device.UpdateAsync (timeoutToken).ConfigureAwait (false);
							LogReportedState ("ExecuteDeviceCommandAsync.AfterCommand", device);
							}

						ApplyState (device);
						OnlineIndicatorIsOnline = true;
						ReadyIndicatorIsReady = true;
					}, timeoutToken).ConfigureAwait (false);
				}
			catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutToken.IsCancellationRequested)
				{
				throw new TimeoutException ($"Light entity '{ControllerId}' command timed out after {DeviceCommandTimeout.TotalSeconds:0} seconds.");
				}
			}
		catch (Exception ex)
			{
			ResetConnectionState ();
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
			if (_registeredOnOffMembers is not null)
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

		LightIsOn = isOn;

		// Kasa/Tapo devices report the real, current color-temperature mode regardless of power state
		// (colorTemperature is 0 while in HSV/color mode and a real Kelvin value while in white/CT
		// mode, whether the bulb is on or off), so the reconciliation always uses the live reading
		// rather than being gated on isOn. Gating this on isOn previously allowed a stale/deferred
		// state apply from an earlier command to be applied after a fresher one, causing the
		// capability to flip to the wrong shape and then immediately flip back.
		if (_supportsColorTemperature)
			{
			ReconcileColorTemperatureCapability (hasActiveColorTemperature, colorTemperature);
			}

		_isColorTemperatureUiModeActive = _supportsColorTemperature && hasActiveColorTemperature;

		if (_supportsBrightness)
			{
			LightDimmerLevel = isOn && brightness.HasValue
				? Clamp01 (brightness.Value / 100d)
				: 0d;
			}

		if (_supportsColorTemperature && hasActiveColorTemperature)
			{
			// Kasa/Tapo devices report color_temp=0 whenever the bulb is in HSV/color mode; unlike
			// hue/saturation, the device does not retain a "last known" color-temperature value that
			// can be read back once color mode is active. The Crestron lightColorTemperature:level
			// property must always stay within the declared lightColorTemperature:range (e.g.
			// 2500-9000K); publishing 0 while in color mode is out of range and causes the Crestron
			// Home UI to misinterpret/clamp the value, displaying the light as white/CCT mode even
			// though it is actually in color mode. So retain the last known valid level locally and
			// only update it when the device actually reports an active color-temperature value.
			SetColorTemperatureLevelSilently (colorTemperature!.Value);
			}

		if (_supportsFullColor)
			{
			// Kasa/Tapo devices retain their hue/saturation values independently of color temperature -
			// color temperature simply overrides the displayed color while active, without clearing or
			// altering the stored hue/saturation. The device reports the real hue/saturation values
			// regardless of whether color temperature is currently active, so always surface those
			// real values here; it is the color-temperature capability's job (lightEmulatedColorTemperature
			// for Color-kind bulbs) to signal the override to the Crestron Home UI, not zeroing hue/saturation.
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

	private bool IsCurrentColorTemperatureUiMode ()
		{
		return _supportsColorTemperature && _isColorTemperatureUiModeActive;
		}

	private void PublishCurrentLightModeProperties (string context)
		{
		if (_supportsBrightness)
			{
			PublishProperty ("lightDimmer:level", new DriverEntityValue (LightDimmerLevel), context);
			}

		PublishColorModeStateProperties (context);
		}

	private long GetActiveColorTemperatureLevel ()
		{
		if (!_supportsColorTemperature)
			{
			return 0L;
			}

		return _registeredColorTemperatureMembers!.LightColorTemperatureLevel;
		}

	private void PublishColorModeStateProperties (string context)
		{
		// The Crestron Home UI infers whether a light is currently in "color" or "white/CCT" mode from
		// whichever of lightColor:*/lightColorTemperature:level was most recently published, so the
		// publish order here must reflect the device's actual active mode (see IsCurrentColorTemperatureUiMode),
		// matching the convention used by PublishActiveColorModeProperties. Publishing them in a fixed
		// order regardless of active mode causes the UI to always show the last-published mode (white)
		// on reload even when the bulb is actually in color mode.
		bool colorTemperatureUiModeActive = IsCurrentColorTemperatureUiMode ();

		if (colorTemperatureUiModeActive)
			{
			if (_supportsFullColor)
				{
				PublishProperty ("lightColor:saturation", new DriverEntityValue (LightColorSaturation), context);
				PublishProperty ("lightColor:hue", new DriverEntityValue (LightColorHue), context);
				}

			if (_supportsColorTemperature)
				{
				PublishProperty (_registeredColorTemperatureMembers!.LevelPropertyId, new DriverEntityValue (GetActiveColorTemperatureLevel ()), context);
				}
			}
		else
			{
			if (_supportsColorTemperature)
				{
				PublishProperty (_registeredColorTemperatureMembers!.LevelPropertyId, new DriverEntityValue (GetActiveColorTemperatureLevel ()), context);
				}

			if (_supportsFullColor)
				{
				PublishProperty ("lightColor:saturation", new DriverEntityValue (LightColorSaturation), context);
				PublishProperty ("lightColor:hue", new DriverEntityValue (LightColorHue), context);
				}
			}
		}

	private void PublishActiveColorModeProperties (string context)
		{
		if (_supportsColorTemperature)
			{
			PublishProperty (_registeredColorTemperatureMembers!.LevelPropertyId, new DriverEntityValue (_registeredColorTemperatureMembers!.LightColorTemperatureLevel), context);
			}

		if (_supportsFullColor)
			{
			PublishProperty ("lightColor:saturation", new DriverEntityValue (LightColorSaturation), context);
			PublishProperty ("lightColor:hue", new DriverEntityValue (LightColorHue), context);
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
			|| propertyId.StartsWith ("lightEmulatedColorTemperature:", StringComparison.Ordinal);
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
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Crestron.DeviceDrivers.EntityModel.Data;
using Crestron.DeviceDrivers.EntityModel.Logging;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel;
using Crestron.DeviceDrivers.SDK.EntityModel.Attributes;

using KasaTapoClient;

namespace KasaTapoCrestronDriver;

internal class KasaLightEntity : ReflectedAttributeDriverEntity, IKasaManagedLightEntity
	{
	private const string BRIGHTNESS_FEATURE_ID = "brightness";
	private const string COLOR_TEMPERATURE_FEATURE_ID = "color_temperature";
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

	private static readonly TimeSpan SliderDebounceInterval = TimeSpan.FromMilliseconds (250);

	private readonly SemaphoreSlim _connectionGate = new (1, 1);
	private readonly object _sliderGate = new ();
	protected readonly WorkQueue<KasaDevice> DeviceQueue = new ();
	private readonly DriverControllerLogger _logger;
	private readonly string _driverLogId;
	private readonly Action<ManagedLightDescriptor>? _descriptorUpdated;
	private readonly IPlatformSharedConfiguration _sharedConfiguration;
	private readonly CancellationTokenSource _lifetimeCancellationSource = new ();

	private ManagedLightDescriptor _descriptor = null!;
	private DeviceConfiguration? _configuration;
	private KasaDevice? _connectedDevice;
	private Task? _pollingTask;
	private string _deviceName = string.Empty;
	private string _modelName = string.Empty;
	private string _serialNumber = string.Empty;
	private ManagedLightKind _kind;
	private string? _childId;
	private bool _supportsBrightness;
	private bool _supportsFullColor;
	private bool _supportsColorTemperature;
	private bool _supportsEmulatedColorTemperature;
	private static readonly TimeSpan StartupConnectRetryInterval = TimeSpan.FromSeconds (5);
	private DesiredLightCommand? _pendingSliderCommand;
	private CancellationTokenSource? _sliderCommandCancellationSource;
	private bool _sliderInteractionActive;
	private bool _suppressPropertyNotifications = true;
	private bool _isConfigured;
	private int _stopState;
	private int _pollingGeneration;
	private bool _disposed;

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
		OnlineIndicatorIsOnline = false;
		ReadyIndicatorIsReady = false;
		_suppressPropertyNotifications = false;
		LogInfo ($"Light entity '{ControllerId}' created in passive discovered state; awaiting child configuration callback before activation.");
		}

	public string DeviceName => _deviceName;

	public string ModelName => _modelName;

	public string SerialNumber => _serialNumber;

	[EntityProperty (Id = "light:isOn")]
	public bool LightIsOn
		{
		get;
		private set => SetAndNotify ("light:isOn", value, ref field);
		}

	[EntityCommand (Id = "light:on")]
	public void LightOn ()
		{
		LogCommandInvocation ("light:on", "requested local optimistic power on");
		LightIsOn = true;
		if (_supportsBrightness)
			{
			LightDimmerLevel = 0.5d;
			}
		StartBackgroundOperation (() => ExecuteDeviceCommandAsync (device => ExecutePowerAsync (device, true)), "light:on");
		}

	[EntityCommand (Id = "light:off")]
	public void LightOff ()
		{
		LogCommandInvocation ("light:off", "requested local optimistic power off");
		LightIsOn = false;
		if (_supportsBrightness)
			{
			LightDimmerLevel = 0d;
			}
		CancelSliderInteraction ();
		StartBackgroundOperation (() => ExecuteDeviceCommandAsync (device => ExecutePowerAsync (device, false)), "light:off");
		}

	[EntityProperty (Id = "lightDimmer:level", RelativeRangeProperty = "lightDimmer:levelRange")]
	public double LightDimmerLevel
		{
		get;
		private set => SetAndNotify ("lightDimmer:level", value, ref field);
		}

	[EntityProperty (Id = "lightDimmer:levelRange")]
	public DriverEntityValueRelativeRange LightDimmerLevelRange
		{
		get => field!;
		private set
			{
			if (field is not null && EqualityComparer<DriverEntityValueRelativeRange>.Default.Equals (field, value))
				{
				return;
				}

			field = value;
			if (_suppressPropertyNotifications)
				{
				return;
				}

			PublishProperty ("lightDimmer:levelRange", value, nameof (LightDimmerLevelRange));
			}
		}

	[EntityProperty (Id = "lightColor:hue", RelativeRangeProperty = "lightColor:hueRange")]
	public double LightColorHue
		{
		get;
		private set => SetAndNotify ("lightColor:hue", value, ref field);
		}

	[EntityProperty (Id = "lightColor:hueRange")]
	public DriverEntityValueRelativeRange LightColorHueRange
		{
		get => field!;
		private set
			{
			if (field is not null && EqualityComparer<DriverEntityValueRelativeRange>.Default.Equals (field, value))
				{
				return;
				}

			field = value;
			if (_suppressPropertyNotifications)
				{
				return;
				}

			PublishProperty ("lightColor:hueRange", value, nameof (LightColorHueRange));
			}
		}

	[EntityProperty (Id = "lightColor:saturation", RelativeRangeProperty = "lightColor:saturationRange")]
	public double LightColorSaturation
		{
		get;
		private set => SetAndNotify ("lightColor:saturation", value, ref field);
		}

	[EntityProperty (Id = "lightColor:saturationRange")]
	public DriverEntityValueRelativeRange LightColorSaturationRange
		{
		get => field!;
		private set
			{
			if (field is not null && EqualityComparer<DriverEntityValueRelativeRange>.Default.Equals (field, value))
				{
				return;
				}

			field = value;
			if (_suppressPropertyNotifications)
				{
				return;
				}

			PublishProperty ("lightColor:saturationRange", value, nameof (LightColorSaturationRange));
			}
		}

	[EntityProperty (Id = "lightColorTemperature:level", RangeProperty = "lightColorTemperature:range", Units = "Kelvin")]
	public long LightColorTemperatureLevel
		{
		get;
		private set => SetAndNotify ("lightColorTemperature:level", value, ref field);
		}

	[EntityProperty (Id = "lightColorTemperature:range", Units = "Kelvin")]
	public DriverEntityValueRange LightColorTemperatureRange
		{
		get => field!;
		private set
			{
			if (field is not null && EqualityComparer<DriverEntityValueRange>.Default.Equals (field, value))
				{
				return;
				}

			field = value;
			if (_suppressPropertyNotifications)
				{
				return;
				}

			PublishProperty ("lightColorTemperature:range", value, nameof (LightColorTemperatureRange));
			}
		}

	[EntityProperty (Id = "lightEmulatedColorTemperature:level", RangeProperty = "lightEmulatedColorTemperature:range", Units = "Kelvin")]
	public long LightEmulatedColorTemperatureLevel
		{
		get;
		private set => SetAndNotify ("lightEmulatedColorTemperature:level", value, ref field);
		}

	[EntityProperty (Id = "lightEmulatedColorTemperature:range", Units = "Kelvin")]
	public DriverEntityValueRange LightEmulatedColorTemperatureRange
		{
		get => field!;
		private set
			{
			if (field is not null && EqualityComparer<DriverEntityValueRange>.Default.Equals (field, value))
				{
				return;
				}

			field = value;
			if (_suppressPropertyNotifications)
				{
				return;
				}

			PublishProperty ("lightEmulatedColorTemperature:range", value, nameof (LightEmulatedColorTemperatureRange));
			}
		}

	[EntityCommand (Id = "lightDimmer:setLevel")]
	public void LightDimmerSetLevel (
		[EntityParameter (RelativeRangeProperty = "lightDimmer:levelRange")] double level,
		[EntityParameter (OptionalFeature = true, OptionalParameter = true, DefaultValue = 0, Units = "Milliseconds")] long transitionTime)
		{
		double relativeLevel = Clamp01 (level);
		LogCommandInvocation ("lightDimmer:setLevel", $"level={relativeLevel:0.####}, transitionMs={transitionTime}");

		LightIsOn = relativeLevel > 0d;
		LightDimmerLevel = relativeLevel;

		QueueSliderCommand (new DesiredLightCommand (DesiredLightMode.Brightness, relativeLevel, 0d, 0d, 0L));
		}

	[EntityCommand (Id = "lightColor:setHue")]
	public void LightColorSetHue (
		[EntityParameter (RelativeRangeProperty = "lightColor:hueRange")] double level,
		[EntityParameter (OptionalFeature = true, OptionalParameter = true, DefaultValue = 0, Units = "Milliseconds")] long transitionTime)
		{
		double hueLevel = Clamp01 (level);
		LogCommandInvocation ("lightColor:setHue", $"hue={hueLevel:0.####}, transitionMs={transitionTime}");

		SetAndNotifyColorTemperatureLevel (0L);
		LightColorHue = hueLevel;
		if (!LightIsOn)
			{
			LightIsOn = true;
			}

		QueueSliderCommand (new DesiredLightCommand (DesiredLightMode.Hsv, GetEffectiveOnLevel (), hueLevel, LightColorSaturation, 0L));
		}

	[EntityCommand (Id = "lightColor:setSaturation")]
	public void LightColorSetSaturation (
		[EntityParameter (RelativeRangeProperty = "lightColor:saturationRange")] double level,
		[EntityParameter (OptionalFeature = true, OptionalParameter = true, DefaultValue = 0, Units = "Milliseconds")] long transitionTime)
		{
		double saturationLevel = Clamp01 (level);
		LogCommandInvocation ("lightColor:setSaturation", $"saturation={saturationLevel:0.####}, transitionMs={transitionTime}");

		SetAndNotifyColorTemperatureLevel (0L);
		LightColorSaturation = saturationLevel;
		if (!LightIsOn)
			{
			LightIsOn = true;
			}

		QueueSliderCommand (new DesiredLightCommand (DesiredLightMode.Hsv, GetEffectiveOnLevel (), LightColorHue, saturationLevel, 0L));
		}

	[EntityCommand (Id = "lightColorTemperature:setLevel")]
	public void LightColorTemperatureSetLevel (
		[EntityParameter (RangeProperty = "lightColorTemperature:range", Units = "Kelvin")] long level,
		[EntityParameter (OptionalFeature = true, OptionalParameter = true, DefaultValue = 0, Units = "Milliseconds")] long transitionTime)
		{
		SetColorTemperatureLevel ("lightColorTemperature:setLevel", level, transitionTime);
		}

	[EntityCommand (Id = "lightEmulatedColorTemperature:setLevel")]
	public void LightEmulatedColorTemperatureSetLevel (
		[EntityParameter (RangeProperty = "lightEmulatedColorTemperature:range", Units = "Kelvin")] long level,
		[EntityParameter (OptionalFeature = true, OptionalParameter = true, DefaultValue = 0, Units = "Milliseconds")] long transitionTime)
		{
		SetColorTemperatureLevel ("lightEmulatedColorTemperature:setLevel", level, transitionTime);
		}

	private void SetColorTemperatureLevel (string commandId, long level, long transitionTime)
		{
		LogCommandInvocation (commandId, $"level={level}, transitionMs={transitionTime}");
		bool isCurrentlyOn = LightIsOn;

		if (level <= 0L)
			{
			if (isCurrentlyOn)
				{
				SetAndNotifyColorTemperatureLevel (0L);
				}

			QueueSliderCommand (new DesiredLightCommand (DesiredLightMode.Hsv, GetEffectiveOnLevel (), LightColorHue, LightColorSaturation, 0L));
			return;
			}

		long temperatureLevel = Math.Max (1L, level);

		if (isCurrentlyOn)
			{
			LightColorHue = 0d;
			LightColorSaturation = 0d;
			SetAndNotifyColorTemperatureLevel (temperatureLevel);
			}
		else
			{
			LogInfo ($"Light entity '{ControllerId}' deferring optimistic color-temperature UI projection while the light is off; waiting for command resolution.");
			}

		QueueSliderCommand (new DesiredLightCommand (DesiredLightMode.ColorTemperature, GetEffectiveOnLevel (), 0d, 0d, temperatureLevel));
		}

	[EntityProperty (Id = "onlineIndicator:isOnline")]
	public bool OnlineIndicatorIsOnline
		{
		get;
		private set => SetAndNotify ("onlineIndicator:isOnline", value, ref field);
		}

	private void QueueSliderCommand (DesiredLightCommand command)
		{
		CancellationTokenSource cancellationSource;
		lock (_sliderGate)
			{
			_pendingSliderCommand = command;
			_sliderInteractionActive = true;
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
						_logger?.Log (_driverLogId, LogEntryLevel.Info, $"Light entity '{ControllerId}' background operation '{operationName}' was canceled.");
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
					_sliderInteractionActive = false;
					_sliderCommandCancellationSource.Dispose ();
					_sliderCommandCancellationSource = null;
					}
				}

			if (reapplyResolvedState)
				{
				try
					{
					await DeviceQueue.EnqueueAsync (device =>
						{
							ApplyState (device);
							return Task.CompletedTask;
						}).ConfigureAwait (false);
					}
				catch (Exception ex)
					{
					_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Light entity '{ControllerId}' post-slider state apply failed: {ex}");
					}
				}
			}
		}

	private bool IsSliderInteractionActive ()
		{
		lock (_sliderGate)
			{
			return _sliderInteractionActive;
			}
		}

	private void CancelSliderInteraction ()
		{
		lock (_sliderGate)
			{
			_sliderInteractionActive = false;
			_pendingSliderCommand = null;
			_sliderCommandCancellationSource?.Cancel ();
			_sliderCommandCancellationSource?.Dispose ();
			_sliderCommandCancellationSource = null;
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

	[EntityProperty (Id = "readyIndicator:isReady")]
	public bool ReadyIndicatorIsReady
		{
		get;
		private set => SetAndNotify ("readyIndicator:isReady", value, ref field);
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
		_deviceName = _descriptor.Name;
		_modelName = _descriptor.ModelName;
		_serialNumber = _descriptor.SerialNumber;
		_kind = _descriptor.Kind;
		_childId = _descriptor.ChildId;

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
		}

	private void ResetConnectionState ()
		{
		_connectedDevice = null;
		DeviceQueue.ClearClient ();
		OnlineIndicatorIsOnline = false;
		ReadyIndicatorIsReady = false;
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
		bool supportsAnyColorTemperature = _supportsColorTemperature || _supportsEmulatedColorTemperature;
		bool hasActiveColorTemperature = supportsAnyColorTemperature && LightColorTemperatureLevel > 0L;

		AlignSnapshotStateForPublication (hasActiveColorTemperature);

		PublishProperty ("onlineIndicator:isOnline", new DriverEntityValue (OnlineIndicatorIsOnline), "PublishStateSnapshot");
		PublishProperty ("readyIndicator:isReady", new DriverEntityValue (ReadyIndicatorIsReady), "PublishStateSnapshot");
		if (_supportsBrightness)
			{
			PublishProperty ("lightDimmer:levelRange", LightDimmerLevelRange, "PublishStateSnapshot");
			PublishProperty ("lightDimmer:level", new DriverEntityValue (LightDimmerLevel), "PublishStateSnapshot");
			}

		if (_supportsFullColor)
			{
			PublishProperty ("lightColor:hueRange", LightColorHueRange, "PublishStateSnapshot");
			PublishProperty ("lightColor:saturationRange", LightColorSaturationRange, "PublishStateSnapshot");

			if (hasActiveColorTemperature)
				{
				PublishProperty ("lightColor:hue", new DriverEntityValue (0d), "PublishStateSnapshot.CTActive");
				PublishProperty ("lightColor:saturation", new DriverEntityValue (0d), "PublishStateSnapshot.CTActive");
				}
			else
				{
				PublishProperty ("lightColor:hue", new DriverEntityValue (LightColorHue), "PublishStateSnapshot.ColorActive");
				PublishProperty ("lightColor:saturation", new DriverEntityValue (LightColorSaturation), "PublishStateSnapshot.ColorActive");
				}
			}

		if (hasActiveColorTemperature)
			{
			PublishColorTemperatureRangeProperties ("PublishStateSnapshot");
			NotifyPublishedColorTemperatureLevelDirect ("PublishStateSnapshot");
			}

		if (!hasActiveColorTemperature)
			{
			PublishColorTemperatureRangeProperties ("PublishStateSnapshot");
			}

		PublishProperty ("light:isOn", new DriverEntityValue (LightIsOn), "PublishStateSnapshot");
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
		SeedInitialUiState (device);

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

		_deviceName = _descriptor.Name;
		_modelName = _descriptor.ModelName;
		_serialNumber = _descriptor.SerialNumber;
		_kind = _descriptor.Kind;
		_childId = _descriptor.ChildId;
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

	private static ManagedLightKind ResolveManagedLightKind (KasaDevice device, ManagedLightDescriptor descriptor)
		{
		if (descriptor.ChildId is not null)
			{
			return descriptor.Kind;
			}

		if (descriptor.DiscoveredDeviceType == KasaTapoClient.DeviceType.Plug || descriptor.DiscoveredDeviceType == KasaTapoClient.DeviceType.Strip)
			{
			return ManagedLightKind.OnOff;
			}

		if (device.LightState?.Hsv is not null)
			{
			return ManagedLightKind.Color;
			}

		return HasFeature (device.Features, COLOR_TEMPERATURE_FEATURE_ID)
			? ManagedLightKind.TunableWhite
			: ManagedLightKind.Dimmable;
		}

	private void PublishColorTemperatureRangeProperties (string context)
		{
		if (_supportsColorTemperature)
			{
			PublishProperty ("lightColorTemperature:range", LightColorTemperatureRange, context);
			}

		if (_supportsEmulatedColorTemperature)
			{
			PublishProperty ("lightEmulatedColorTemperature:range", LightEmulatedColorTemperatureRange, context);
			}
		}

	private void AlignSnapshotStateForPublication (bool hasActiveColorTemperature)
		{
		bool previousSuppressPropertyNotifications = _suppressPropertyNotifications;
		_suppressPropertyNotifications = true;
		try
			{
			if (hasActiveColorTemperature && _supportsFullColor)
				{
				LightColorHue = 0d;
				LightColorSaturation = 0d;
				}
			}
		finally
			{
			_suppressPropertyNotifications = previousSuppressPropertyNotifications;
			}
		}

	private void ConfigureDynamicFeatures (KasaDevice device, ManagedLightDescriptor descriptor)
		{
		bool supportsLightControls = descriptor.Kind != ManagedLightKind.OnOff;
		DriverEntityValueRange lightColorTemperatureRange = new (0, 0, 1);

		_supportsBrightness = supportsLightControls && HasFeature (device.Features, BRIGHTNESS_FEATURE_ID);
		_supportsFullColor = supportsLightControls && device.LightState?.Hsv is not null;
		bool supportsColorTemperatureApi = supportsLightControls && TryCreateColorTemperatureRange (device.Features, out lightColorTemperatureRange);
		_supportsColorTemperature = supportsColorTemperatureApi && !_supportsFullColor;
		_supportsEmulatedColorTemperature = supportsColorTemperatureApi && _supportsFullColor;
		bool definitionChanged = false;

		if (!_supportsBrightness)
			{
			RemoveProperty ("lightDimmer:level");
			RemoveProperty ("lightDimmer:levelRange");
			RemoveCommand ("lightDimmer:setLevel");
			definitionChanged = true;
			}
		else
			{
			LightDimmerLevelRange = new DriverEntityValueRelativeRange (0.01);
			}

		if (!_supportsFullColor)
			{
			RemoveProperty ("lightColor:hue");
			RemoveProperty ("lightColor:hueRange");
			RemoveProperty ("lightColor:saturation");
			RemoveProperty ("lightColor:saturationRange");
			RemoveCommand ("lightColor:setHue");
			RemoveCommand ("lightColor:setSaturation");
			definitionChanged = true;
			}
		else
			{
			LightColorHueRange = new DriverEntityValueRelativeRange (1d / HUE_MAX_DEGREES);
			LightColorSaturationRange = new DriverEntityValueRelativeRange (0.01);
			}

		if (!_supportsColorTemperature)
			{
			RemoveProperty ("lightColorTemperature:level");
			RemoveProperty ("lightColorTemperature:range");
			RemoveCommand ("lightColorTemperature:setLevel");
			definitionChanged = true;
			}
		else
			{
			LightColorTemperatureRange = lightColorTemperatureRange;
			}

		if (!_supportsEmulatedColorTemperature)
			{
			RemoveProperty ("lightEmulatedColorTemperature:level");
			RemoveProperty ("lightEmulatedColorTemperature:range");
			RemoveCommand ("lightEmulatedColorTemperature:setLevel");
			definitionChanged = true;
			}
		else
			{
			LightEmulatedColorTemperatureRange = lightColorTemperatureRange;
			}

		if (definitionChanged)
			{
			RaiseDefinitionChangedEvent ();
			}
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

	private Task ExecutePowerAsync (KasaDevice device, bool on)
		{
		string? childId = _childId;
		if (!string.IsNullOrWhiteSpace (childId))
			{
			string resolvedChildId = childId!;
			return on ? device.TurnChildOnAsync (resolvedChildId) : device.TurnChildOffAsync (resolvedChildId);
			}

		if (_kind == ManagedLightKind.OnOff)
			{
			return on ? device.TurnOnAsync () : device.TurnOffAsync ();
			}

		return on ? device.TurnLightOnAsync () : device.TurnLightOffAsync ();
		}

	private void ApplyState (KasaDevice device)
		{
		using (PropertyChangeTracker.StartBatchedUpdate (true))
			{
			ApplyStateCore (device);
			}

		if (!_suppressPropertyNotifications)
			{
			PublishCurrentLightModeProperties ();
			PublishProperty ("light:isOn", new DriverEntityValue (LightIsOn), "ApplyState");
			}
		}

	private void SeedInitialUiState (KasaDevice device)
		{
		ApplyState (device);
		OnlineIndicatorIsOnline = true;
		ReadyIndicatorIsReady = true;
		}

	private void ApplyStateCore (KasaDevice device)
		{
		bool sliderInteractionActive = IsSliderInteractionActive ();
		bool isOn = ResolvePowerState (device);
		int? brightness = device.LightState?.Brightness;
		int? hue = device.LightState?.Hue ?? device.LightState?.Hsv?.Hue;
		int? saturation = device.LightState?.Saturation ?? device.LightState?.Hsv?.Saturation;
		int? colorTemperature = device.LightState?.ColorTemperature;
		bool hasActiveColorTemperature = HasCurrentColorTemperatureState (device.LightState);
		LightIsOn = isOn;

		if (_supportsBrightness && !sliderInteractionActive)
			{
			LightDimmerLevel = isOn && brightness.HasValue
				? Clamp01 (brightness.Value / 100d)
				: 0d;
			}

		if ((_supportsColorTemperature || _supportsEmulatedColorTemperature) && !sliderInteractionActive)
			{
			SetAndNotifyColorTemperatureLevel (hasActiveColorTemperature
				? colorTemperature!.Value
				: 0L);
			}

		if (_supportsFullColor && !sliderInteractionActive)
			{
			if (hasActiveColorTemperature)
				{
				LightColorHue = 0d;
				LightColorSaturation = 0d;
				}
			else
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
		return lightState?.ColorTemperature is int colorTemperature && colorTemperature > 0;
		}

	private void SetAndNotifyColorTemperatureLevel (long value)
		{
		if (LightColorTemperatureLevel == value)
			{
			return;
			}

		LightColorTemperatureLevel = value;
		LightEmulatedColorTemperatureLevel = value;
		if (_suppressPropertyNotifications)
			{
			return;
			}

		NotifyPublishedColorTemperatureLevel ("SetAndNotifyColorTemperatureLevel");
		}

	private void PublishCurrentLightModeProperties ()
		{
		bool hasActiveColorTemperature = (_supportsColorTemperature || _supportsEmulatedColorTemperature) && LightColorTemperatureLevel > 0L;

		if (_supportsBrightness)
			{
			PublishProperty ("lightDimmer:level", new DriverEntityValue (LightDimmerLevel), "ApplyState");
			}

		if (_supportsFullColor)
			{
			if (hasActiveColorTemperature)
				{
				PublishProperty ("lightColor:hue", new DriverEntityValue (0d), "ApplyState.CTActive");
				PublishProperty ("lightColor:saturation", new DriverEntityValue (0d), "ApplyState.CTActive");
				}
			else
				{
				PublishProperty ("lightColor:hue", new DriverEntityValue (LightColorHue), "ApplyState.ColorActive");
				PublishProperty ("lightColor:saturation", new DriverEntityValue (LightColorSaturation), "ApplyState.ColorActive");
				}
			}

		if (_supportsColorTemperature || _supportsEmulatedColorTemperature)
			{
			NotifyPublishedColorTemperatureLevelDirect ("ApplyState");
			}
		}

	private void NotifyPublishedColorTemperatureLevel (string context)
		{
		DriverEntityValue value = new (LightColorTemperatureLevel);
		if (_supportsColorTemperature)
			{
			PublishProperty ("lightColorTemperature:level", value, context);
			}

		if (_supportsEmulatedColorTemperature)
			{
			PublishProperty ("lightEmulatedColorTemperature:level", value, context);
			}
		}

	private void NotifyPublishedColorTemperatureLevelDirect (string context)
		{
		DriverEntityValue value = new (LightColorTemperatureLevel);
		if (_supportsColorTemperature)
			{
			PublishProperty ("lightColorTemperature:level", value, context);
			}

		if (_supportsEmulatedColorTemperature)
			{
			PublishProperty ("lightEmulatedColorTemperature:level", value, context);
			}
		}

	private void PublishProperty (string propertyId, DriverEntityValue value, string context)
		{
		NotifyPropertyChanged (propertyId, value);
		}

	private void PublishProperty (string propertyId, DriverEntityValueRange value, string context)
		{
		DriverEntityValue entityValue = new (value);
		NotifyPropertyChanged (propertyId, entityValue);
		}

	private void PublishProperty (string propertyId, DriverEntityValueRelativeRange value, string context)
		{
		DriverEntityValue entityValue = new (value);
		NotifyPropertyChanged (propertyId, entityValue);
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
		string? childId = _childId;
		if (!string.IsNullOrWhiteSpace (childId))
			{
			string resolvedChildId = childId!;
			return device.GetChild (resolvedChildId)?.IsOn ?? false;
			}

		return _kind == ManagedLightKind.OnOff
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

		_logger?.Log (
			_driverLogId,
			LogEntryLevel.Info,
			$"Light entity '{ControllerId}' {stage} raw state: isOn={FormatNullable (isOn)}, brightness={FormatNullable (brightness)}, hue={FormatNullable (hue)}, saturation={FormatNullable (saturation)}, colorTemperature={FormatNullable (colorTemperature)}.");
		}

	private void LogCommandInvocation (string commandId, string details)
		{
		}

	private void LogPublishedState ()
		{
		_logger?.Log (
			_driverLogId,
			LogEntryLevel.Info,
			$"Light entity '{ControllerId}' published snapshot: isOn={LightIsOn}, dimmer={LightDimmerLevel:0.####}, hue={LightColorHue:0.####}, saturation={LightColorSaturation:0.####}, colorTemperature={LightColorTemperatureLevel}.");
		}

	private string FormatDesiredLightCommand (DesiredLightCommand command)
		{
		return $"mode={command.Mode}, level={command.Level:0.####}, hue={command.Hue:0.####}, saturation={command.Saturation:0.####}, colorTemperature={command.ColorTemperature}";
		}

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
		for (int index = 0; index < features.Count; index++)
			{
			if (string.Equals (features[index].Id, featureId, StringComparison.Ordinal))
				{
				return true;
				}
			}

		return false;
		}

	private static bool TryCreateColorTemperatureRange (IReadOnlyList<DeviceFeature> features, out DriverEntityValueRange range)
		{
		for (int index = 0; index < features.Count; index++)
			{
			DeviceFeature feature = features[index];
			if (!string.Equals (feature.Id, COLOR_TEMPERATURE_FEATURE_ID, StringComparison.Ordinal))
				{
				continue;
				}

			if (!feature.MinimumValue.HasValue || !feature.MaximumValue.HasValue)
				{
				break;
				}

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

	}
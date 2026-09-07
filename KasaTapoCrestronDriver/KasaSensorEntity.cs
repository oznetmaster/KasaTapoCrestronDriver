// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.IO;

using Crestron.DeviceDrivers.EntityModel;
using Crestron.DeviceDrivers.EntityModel.Data;
using Crestron.DeviceDrivers.EntityModel.Logging;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel;
using Crestron.DeviceDrivers.SDK.EntityModel.Attributes;

namespace KasaTapoCrestronDriver;

/// <summary>
/// Standalone managed entity for hub child sensor devices (<see cref="ManagedChildKind.Sensor"/>),
/// such as a Tapo H100 hub's T310/T315 temperature+humidity sensors or T100 contact/motion
/// sensor. Unlike <see cref="KasaOutletEntity"/>, this entity exposes no on/off control - hub
/// sensor children are read-only telemetry - and instead surfaces whichever of
/// temperature/humidity/contact/motion the connected child actually reports (see
/// <see cref="ManagedLightDescriptor.HubChildCategory"/>). It deliberately does not derive from
/// or share implementation with <see cref="KasaLightEntity"/>/<see cref="KasaOutletEntity"/>: each
/// child kind has its own, simpler UI definition and lifecycle needs.
/// </summary>
internal sealed partial class KasaSensorEntity : ReflectedAttributeDriverEntity, IKasaHubChildEntity
	{
	private static readonly TimeSpan StartupConnectTimeout = TimeSpan.FromSeconds (20);
	private static readonly TimeSpan DeviceConnectTimeout = StartupConnectTimeout;
	private static readonly TimeSpan DeviceCommandTimeout = StartupConnectTimeout;
	private const int DEVICE_COMMAND_MAX_ATTEMPTS = 2;

	private readonly DriverControllerLogger _logger;
	private readonly string _driverLogId;
	private readonly Action<ManagedLightDescriptor>? _descriptorUpdated;
	private readonly IPlatformSharedConfiguration _sharedConfiguration;
	private readonly ManagedParentDevicePoller? _hubPoller;
	private readonly CancellationTokenSource _lifetimeCancellationSource = new ();
	private readonly IComponentLogger? _uiDefinitionLogger;
	private readonly string? _uiDefinitionFilePath;
	private UiDefinitionProperty? _uiDefinition;

	private ManagedLightDescriptor _descriptor = null!;
	private DeviceConfiguration? _configuration;
	private KasaDevice? _connectedDevice;
	private bool _isConfigured;
	private bool _childPublished;
	private int _stopState;
	private bool _disposed;
	private bool _capabilitiesResolved;
	private bool _registeredWithHubPoller;

	/// <summary>
	/// This hub child's own <c>ChildId</c>, used by the owning <see cref="ManagedParentDevicePoller"/>
	/// to look up this entity's own slice of the hub's child list on each poll tick. See
	/// <see cref="IKasaHubChildEntity"/>.
	/// </summary>
	public string ChildId
		{
		get { return _descriptor.ChildId ?? string.Empty; }
		}

	public string DeviceName { get; private set; } = string.Empty;

	public string ModelName { get; private set; } = string.Empty;

	public string SerialNumber { get; private set; } = string.Empty;

	[EntityProperty (Id = "deviceLabel")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string DeviceLabel
		{
		get;
		private set => SetAndNotify ("deviceLabel", value, ref field);
		} = string.Empty;

	[EntityProperty (Id = "onlineIndicatorIsOnline")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool OnlineIndicatorIsOnline
		{
		get;
		private set => SetAndNotify ("onlineIndicatorIsOnline", value, ref field);
		}

	[EntityProperty (Id = "readyIndicatorIsReady")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool ReadyIndicatorIsReady
		{
		get;
		private set => SetAndNotify ("readyIndicatorIsReady", value, ref field);
		}

	[EntityProperty (Id = "batteryLevelPercent")]
	[EntityPropertyMetadata (Programmable = true, ExtensionUiProperty = true)]
	public double BatteryLevelPercent
		{
		get;
		private set => SetAndNotify ("batteryLevelPercent", value, ref field);
		}

	[EntityProperty (Id = "batteryIsLow")]
	[EntityPropertyMetadata (Programmable = true, ExtensionUiProperty = true)]
	public bool BatteryIsLow
		{
		get;
		private set => SetAndNotify ("batteryIsLow", value, ref field);
		}

	[EntityProperty (Id = "batteryStatusLabel")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string BatteryStatusLabel
		{
		get;
		private set => SetAndNotify ("batteryStatusLabel", value, ref field);
		} = string.Empty;

	[EntityProperty (Id = "hasBattery")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool HasBattery
		{
		get;
		private set => SetAndNotify ("hasBattery", value, ref field);
		}

	[EntityProperty (Id = "hasTemperature")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool HasTemperature
		{
		get;
		private set => SetAndNotify ("hasTemperature", value, ref field);
		}

	[EntityProperty (Id = "temperatureValue")]
	[EntityPropertyMetadata (Programmable = true, ExtensionUiProperty = true)]
	public double TemperatureValue
		{
		get;
		private set => SetAndNotify ("temperatureValue", value, ref field);
		}

	[EntityProperty (Id = "temperatureUnit")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string TemperatureUnit
		{
		get;
		private set => SetAndNotify ("temperatureUnit", value, ref field);
		} = string.Empty;

	[EntityProperty (Id = "temperatureDisplay")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string TemperatureDisplay
		{
		get;
		private set => SetAndNotify ("temperatureDisplay", value, ref field);
		} = string.Empty;

	[EntityProperty (Id = "hasHumidity")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool HasHumidity
		{
		get;
		private set => SetAndNotify ("hasHumidity", value, ref field);
		}

	[EntityProperty (Id = "humidityValuePercent")]
	[EntityPropertyMetadata (Programmable = true, ExtensionUiProperty = true)]
	public double HumidityValuePercent
		{
		get;
		private set => SetAndNotify ("humidityValuePercent", value, ref field);
		}

	[EntityProperty (Id = "humidityDisplay")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string HumidityDisplay
		{
		get;
		private set => SetAndNotify ("humidityDisplay", value, ref field);
		} = string.Empty;

	[EntityProperty (Id = "temperatureWarningIsActive")]
	[EntityPropertyMetadata (Programmable = true, ExtensionUiProperty = true)]
	public bool TemperatureWarningIsActive
		{
		get;
		private set => SetAndNotify ("temperatureWarningIsActive", value, ref field);
		}

	[EntityProperty (Id = "humidityWarningIsActive")]
	[EntityPropertyMetadata (Programmable = true, ExtensionUiProperty = true)]
	public bool HumidityWarningIsActive
		{
		get;
		private set => SetAndNotify ("humidityWarningIsActive", value, ref field);
		}

	[EntityProperty (Id = "hasLeak")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool HasLeak
		{
		get;
		private set => SetAndNotify ("hasLeak", value, ref field);
		}

	[EntityProperty (Id = "leakDetected")]
	[EntityPropertyMetadata (Programmable = true, ExtensionUiProperty = true)]
	public bool LeakDetected
		{
		get;
		private set => SetAndNotify ("leakDetected", value, ref field);
		}

	[EntityProperty (Id = "leakStatusLabel")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string LeakStatusLabel
		{
		get;
		private set => SetAndNotify ("leakStatusLabel", value, ref field);
		} = string.Empty;

	[EntityProperty (Id = "hasContact")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool HasContact
		{
		get;
		private set => SetAndNotify ("hasContact", value, ref field);
		}

	[EntityProperty (Id = "contactIsOpen")]
	[EntityPropertyMetadata (Programmable = true, ExtensionUiProperty = true)]
	public bool ContactIsOpen
		{
		get;
		private set => SetAndNotify ("contactIsOpen", value, ref field);
		}

	[EntityProperty (Id = "contactStatusLabel")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string ContactStatusLabel
		{
		get;
		private set => SetAndNotify ("contactStatusLabel", value, ref field);
		} = string.Empty;

	[EntityProperty (Id = "hasMotion")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool HasMotion
		{
		get;
		private set => SetAndNotify ("hasMotion", value, ref field);
		}

	[EntityProperty (Id = "motionDetected")]
	[EntityPropertyMetadata (Programmable = true, ExtensionUiProperty = true)]
	public bool MotionDetected
		{
		get;
		private set => SetAndNotify ("motionDetected", value, ref field);
		}

	[EntityProperty (Id = "motionStatusLabel")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string MotionStatusLabel
		{
		get;
		private set => SetAndNotify ("motionStatusLabel", value, ref field);
		} = string.Empty;

	[EntityProperty (Id = "sensorStatus")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string SensorStatus
		{
		get;
		private set => SetAndNotify ("sensorStatus", value, ref field);
		} = string.Empty;

	[EntityProperty (Id = "sensorIcon")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string SensorIcon
		{
		get;
		private set => SetAndNotify ("sensorIcon", value, ref field);
		} = "icGenericDeviceOff";

	// Discrete transition events give Crestron Home's Actions & Events / sequences a way to
	// trigger directly off a state change, mirroring OutletTurnedOn/OutletTurnedOff.
	[EntityEvent (Id = "contactOpened", FriendlyName = "Contact Opened", NameLocalizationKey = "Event_ContactOpened")]
	[EntityEventMetadata (Programmable = true)]
	public event EventHandler ContactOpened = null!;

	[EntityEvent (Id = "contactClosed", FriendlyName = "Contact Closed", NameLocalizationKey = "Event_ContactClosed")]
	[EntityEventMetadata (Programmable = true)]
	public event EventHandler ContactClosed = null!;

	[EntityEvent (Id = "motionDetectedEvent", FriendlyName = "Motion Detected", NameLocalizationKey = "Event_MotionDetected")]
	[EntityEventMetadata (Programmable = true)]
	public event EventHandler MotionDetectedEvent = null!;

	[EntityEvent (Id = "motionCleared", FriendlyName = "Motion Cleared", NameLocalizationKey = "Event_MotionCleared")]
	[EntityEventMetadata (Programmable = true)]
	public event EventHandler MotionCleared = null!;

	[EntityEvent (Id = "leakDetectedEvent", FriendlyName = "Leak Detected", NameLocalizationKey = "Event_LeakDetected")]
	[EntityEventMetadata (Programmable = true)]
	public event EventHandler LeakDetectedEvent = null!;

	[EntityEvent (Id = "leakCleared", FriendlyName = "Leak Cleared", NameLocalizationKey = "Event_LeakCleared")]
	[EntityEventMetadata (Programmable = true)]
	public event EventHandler LeakCleared = null!;

	[EntityEvent (Id = "batteryLowEvent", FriendlyName = "Battery Low", NameLocalizationKey = "Event_BatteryLow")]
	[EntityEventMetadata (Programmable = true)]
	public event EventHandler BatteryLowEvent = null!;

	[EntityEvent (Id = "batteryNormal", FriendlyName = "Battery Normal", NameLocalizationKey = "Event_BatteryNormal")]
	[EntityEventMetadata (Programmable = true)]
	public event EventHandler BatteryNormal = null!;

	[EntityEvent (Id = "temperatureWarningDetected", FriendlyName = "Temperature Warning Detected", NameLocalizationKey = "Event_TemperatureWarningDetected")]
	[EntityEventMetadata (Programmable = true)]
	public event EventHandler TemperatureWarningDetected = null!;

	[EntityEvent (Id = "temperatureWarningCleared", FriendlyName = "Temperature Warning Cleared", NameLocalizationKey = "Event_TemperatureWarningCleared")]
	[EntityEventMetadata (Programmable = true)]
	public event EventHandler TemperatureWarningCleared = null!;

	[EntityEvent (Id = "humidityWarningDetected", FriendlyName = "Humidity Warning Detected", NameLocalizationKey = "Event_HumidityWarningDetected")]
	[EntityEventMetadata (Programmable = true)]
	public event EventHandler HumidityWarningDetected = null!;

	[EntityEvent (Id = "humidityWarningCleared", FriendlyName = "Humidity Warning Cleared", NameLocalizationKey = "Event_HumidityWarningCleared")]
	[EntityEventMetadata (Programmable = true)]
	public event EventHandler HumidityWarningCleared = null!;

	public KasaSensorEntity (
		string controllerId,
		ManagedLightDescriptor descriptor,
		DeviceConfiguration configuration,
		Action<ManagedLightDescriptor>? descriptorUpdated,
		IPlatformSharedConfiguration sharedConfiguration,
		DriverImplementationResources resources,
		DriverControllerLogger logger,
		string driverLogId,
		string? driverDataDirectoryPath = null,
		ManagedParentDevicePoller? hubPoller = null)
		: base (controllerId)
		{
		_descriptorUpdated = descriptorUpdated;
		_sharedConfiguration = sharedConfiguration;
		_hubPoller = hubPoller;
		_logger = logger;
		_driverLogId = driverLogId;

		UpdateDescriptor (descriptor, configuration);

		// Sensor is a Crestron extension device type (unlike Light, which is native), so it
		// requires its own packaged UI definition/translation assets, mirroring KasaOutletEntity's
		// "{root}/outlet/uidefinitions/" layout.
		var baseDir = driverDataDirectoryPath ?? Path.GetTempPath ();
		var sensorUiDir = Path.Combine (baseDir, "sensor", "uidefinitions");
		_uiDefinitionLogger = resources.InitLogger;
		_uiDefinitionFilePath = Path.Combine (sensorUiDir, "UiDefinitionBasic.xml");
		LogInfo ($"Sensor entity '{ControllerId}' UiDefinition: driverDataDirectoryPath='{driverDataDirectoryPath}', sensorUiDir='{sensorUiDir}', exists={Directory.Exists (sensorUiDir)}.");
		try
			{
			if (File.Exists (_uiDefinitionFilePath))
				{
				_uiDefinition = new UiDefinitionProperty (_uiDefinitionFilePath, _uiDefinitionLogger);
				}
			else
				{
				LogError ($"Sensor entity '{ControllerId}' UiDefinition load failed: file not found at '{_uiDefinitionFilePath}'.");
				}
			}
		catch (Exception ex)
			{
			LogError ($"Sensor entity '{ControllerId}' UiDefinition load failed: {ex.Message}");
			}

		LogInfo ($"Sensor entity '{ControllerId}' UiDefinition: loaded={_uiDefinition != null}.");

		try
			{
			AddProperty (this, UiDefinitionProperty.Name, _uiDefinition);
			}
		catch (Exception ex)
			{
			LogError ($"Sensor entity '{ControllerId}' AddProperty UiDefinition failed: {ex.Message}");
			}

		// See KasaOutletEntity's constructor comment for why a per-controllerId component logger
		// (rather than the shared resources.Logger) is required here.
		IComponentLogger extensionExecutorLogger = resources.Logger.GetComponentLogger ("CAPA - ", $"ExtensionExecutor:{controllerId}");
		var doCommand = new ExtensionDoCommandExecutor (GetCommand, extensionExecutorLogger);
		AddCommand (this, ExtensionDoCommandExecutor.CommandName, doCommand);

		var setPropertyValue = new ExtensionSetPropertyValueExecutor (GetCommand, extensionExecutorLogger);
		AddCommand (this, ExtensionSetPropertyValueExecutor.CommandName, setPropertyValue);

		LogInfo ($"Sensor entity '{ControllerId}' created in passive discovered state; awaiting child configuration callback before activation.");
		}

	public void UpdateDescriptor (ManagedLightDescriptor descriptor, DeviceConfiguration configuration)
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
		DeviceLabel = _descriptor.Name;
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
		_configuration = configuration;
		}

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
		LogInfo ($"Sensor entity '{ControllerId}' adopted connected device from context='{context}'.");
		return true;
		}

	public void SetConfigured (bool configured, string context)
		{
		if (_disposed)
			{
			return;
			}

		if (_isConfigured == configured)
			{
			return;
			}

		_isConfigured = configured;
		LogInfo ($"Sensor entity '{ControllerId}' SetConfigured: configured={configured}, context='{context}'.");

		if (!configured)
			{
			UnregisterFromHubPoller ();
			return;
			}

		RegisterWithHubPollerIfConfigured ();
		}

	public async Task SetConfiguredAsync (bool configured, string context, CancellationToken cancellationToken)
		{
		if (_disposed)
			{
			return;
			}

		if (_isConfigured == configured)
			{
			return;
			}

		_isConfigured = configured;
		LogInfo ($"Sensor entity '{ControllerId}' SetConfiguredAsync: configured={configured}, context='{context}'.");

		if (!configured)
			{
			UnregisterFromHubPoller ();
			return;
			}

		RegisterWithHubPollerIfConfigured ();
		await Task.CompletedTask.ConfigureAwait (false);
		}

	public void ApplyRuntimeConfiguration (PlatformSharedConfigurationSnapshot previousConfiguration, PlatformSharedConfigurationSnapshot currentConfiguration)
		{
		// Polling cadence is now owned entirely by the shared ManagedParentDevicePoller for this
		// hub (see PlatformDriver's per-host poller registry); ManagedParentDevicePoller.ApplyRuntimeConfiguration
		// is what reacts to a SensorPollInterval change, not this entity.
		}

	public void Stop ()
		{
		if (Interlocked.Exchange (ref _stopState, 1) != 0)
			{
			return;
			}

		UnregisterFromHubPoller ();
		_lifetimeCancellationSource.Cancel ();
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

	private void RegisterWithHubPollerIfConfigured ()
		{
		if (_hubPoller is null || _registeredWithHubPoller)
			{
			return;
			}

		_registeredWithHubPoller = true;
		_hubPoller.RegisterChild (this);
		}

	private void UnregisterFromHubPoller ()
		{
		if (_hubPoller is null || !_registeredWithHubPoller)
			{
			return;
			}

		_registeredWithHubPoller = false;
		_hubPoller.UnregisterChild (ChildId);
		}

	/// <summary>
	/// Invoked by the owning <see cref="ManagedParentDevicePoller"/> once per successful poll tick
	/// with this entity's current child slice (or <c>null</c> if the child was not found on the
	/// hub's most recent child list). See <see cref="IKasaHubChildEntity"/>.
	/// </summary>
	public void ApplyPushedState (ChildDevice? child, KasaDevice parentDevice)
		{
		if (_disposed)
			{
			return;
			}

		UpdateDescriptorFromConnectedDevice (parentDevice);
		ApplyState (child);
		OnlineIndicatorIsOnline = true;
		ReadyIndicatorIsReady = true;

		if (_childPublished)
			{
			PublishStateSnapshot ();
			}
		}

	/// <summary>
	/// Invoked by the owning <see cref="ManagedParentDevicePoller"/> when a poll tick's connect or
	/// <c>UpdateAsync</c> call fails, so this entity can reflect the hub being unreachable without
	/// receiving stale pushed state. See <see cref="IKasaHubChildEntity"/>.
	/// </summary>
	public void ApplyConnectionState (bool online)
		{
		if (_disposed)
			{
			return;
			}

		OnlineIndicatorIsOnline = online;
		ReadyIndicatorIsReady = online;

		if (_childPublished)
			{
			PublishStateSnapshot ();
			}
		}

	public void NotifyChildPublished ()
		{
		if (_disposed)
			{
			return;
			}

		_childPublished = true;

		LogInfo ($"Sensor entity '{ControllerId}' NotifyChildPublished invoked.");
		PublishStateSnapshot ();
		}

	public void NotifyChildRunning (string context)
		{
		if (_disposed)
			{
			return;
			}

		LogInfo ($"Sensor entity '{ControllerId}' NotifyChildRunning invoked, context='{context}'.");
		PublishStateSnapshot ();
		}

	public void PublishStateSnapshot ()
		{
		LogInfo ($"Sensor entity '{ControllerId}' PublishStateSnapshot invoked, uiDefinitionLoaded={_uiDefinition != null}.");
		if (_uiDefinition is not null)
			{
			DriverEntityValue? uiDefinitionValue = _uiDefinition.GetValue (this, null);
			if (uiDefinitionValue.HasValue)
				{
				NotifyPropertyChanged (UiDefinitionProperty.Name, uiDefinitionValue.Value);
				}
			}

		PublishProperty ("deviceLabel", new DriverEntityValue (DeviceLabel), "PublishStateSnapshot");
		PublishProperty ("batteryLevelPercent", new DriverEntityValue (BatteryLevelPercent), "PublishStateSnapshot");
		PublishProperty ("batteryIsLow", new DriverEntityValue (BatteryIsLow), "PublishStateSnapshot");
		PublishProperty ("batteryStatusLabel", new DriverEntityValue (BatteryStatusLabel), "PublishStateSnapshot");
		PublishProperty ("hasTemperature", new DriverEntityValue (HasTemperature), "PublishStateSnapshot");
		if (HasTemperature)
			{
			PublishProperty ("temperatureValue", new DriverEntityValue (TemperatureValue), "PublishStateSnapshot");
			PublishProperty ("temperatureUnit", new DriverEntityValue (TemperatureUnit), "PublishStateSnapshot");
			PublishProperty ("temperatureDisplay", new DriverEntityValue (TemperatureDisplay), "PublishStateSnapshot");
			PublishProperty ("temperatureWarningIsActive", new DriverEntityValue (TemperatureWarningIsActive), "PublishStateSnapshot");
			}

		PublishProperty ("hasHumidity", new DriverEntityValue (HasHumidity), "PublishStateSnapshot");
		if (HasHumidity)
			{
			PublishProperty ("humidityValuePercent", new DriverEntityValue (HumidityValuePercent), "PublishStateSnapshot");
			PublishProperty ("humidityDisplay", new DriverEntityValue (HumidityDisplay), "PublishStateSnapshot");
			PublishProperty ("humidityWarningIsActive", new DriverEntityValue (HumidityWarningIsActive), "PublishStateSnapshot");
			}

		PublishProperty ("hasContact", new DriverEntityValue (HasContact), "PublishStateSnapshot");
		if (HasContact)
			{
			PublishProperty ("contactIsOpen", new DriverEntityValue (ContactIsOpen), "PublishStateSnapshot");
			}

		PublishProperty ("hasMotion", new DriverEntityValue (HasMotion), "PublishStateSnapshot");
		if (HasMotion)
			{
			PublishProperty ("motionDetected", new DriverEntityValue (MotionDetected), "PublishStateSnapshot");
			}

		PublishProperty ("hasLeak", new DriverEntityValue (HasLeak), "PublishStateSnapshot");
		if (HasLeak)
			{
			PublishProperty ("leakDetected", new DriverEntityValue (LeakDetected), "PublishStateSnapshot");
			}

		PublishProperty ("sensorStatus", new DriverEntityValue (SensorStatus), "PublishStateSnapshot");
		PublishProperty ("sensorIcon", new DriverEntityValue (SensorIcon), "PublishStateSnapshot");
		PublishProperty ("onlineIndicatorIsOnline", new DriverEntityValue (OnlineIndicatorIsOnline), "PublishStateSnapshot");
		PublishProperty ("readyIndicatorIsReady", new DriverEntityValue (ReadyIndicatorIsReady), "PublishStateSnapshot");
		}

	private void PublishProperty (string propertyId, DriverEntityValue value, string context)
		{
		NotifyPropertyChanged (propertyId, DriverEntityValueUpdate.Create (value));
		}

	private void SetAndNotify (string propertyId, bool value, ref bool field)
		{
		if (field == value)
			{
			return;
			}

		field = value;
		PublishProperty (propertyId, new DriverEntityValue (value), "SetAndNotify<bool>");
		}

	private void SetAndNotify (string propertyId, double value, ref double field)
		{
		if (Math.Abs (field - value) < 0.0001d)
			{
			return;
			}

		field = value;
		PublishProperty (propertyId, new DriverEntityValue (value), "SetAndNotify<double>");
		}

	private void SetAndNotify (string propertyId, string value, ref string field)
		{
		if (string.Equals (field, value, StringComparison.Ordinal))
			{
			return;
			}

		field = value;
		PublishProperty (propertyId, new DriverEntityValue (value), "SetAndNotify<string>");
		}

	// Hub children never poll on their own - state arrives via ApplyPushedState from the shared
	// ManagedParentDevicePoller - so this legacy IKasaManagedChildEntity member is a no-op here.
	public Task RefreshAsync (CancellationToken cancellationToken)
		{
		return Task.CompletedTask;
		}

	private void ApplyState (ChildDevice? child)
		{
		bool anyState = false;

		if (child is not null)
			{
			if (!_capabilitiesResolved)
				{
				ResolveCapabilitiesFromDeclaredFeatures (child);
				}

			int? batteryLevel = child.Battery.BatteryLevel;
			bool? batteryLow = child.Battery.BatteryLow;
			if (batteryLevel.HasValue || batteryLow.HasValue)
				{
				HasBattery = true;
				anyState = true;
				}

			if (batteryLevel.HasValue)
				{
				BatteryLevelPercent = batteryLevel.Value;
				BatteryStatusLabel = $"{batteryLevel.Value:0}%";
				}

			if (batteryLow.HasValue)
				{
				bool wasLow = BatteryIsLow;
				BatteryIsLow = batteryLow.Value;
				if (!batteryLevel.HasValue)
					{
					BatteryStatusLabel = batteryLow.Value ? "Low" : "Normal";
					}
				if (wasLow != batteryLow.Value)
					{
					if (batteryLow.Value)
						{
						BatteryLowEvent?.Invoke (this, EventArgs.Empty);
						}
					else
						{
						BatteryNormal?.Invoke (this, EventArgs.Empty);
						}
					}
				}

			double? temperature = child.Temperature.Temperature;
			if (temperature.HasValue)
				{
				HasTemperature = true;
				TemperatureValue = temperature.Value;
				TemperatureUnit = AbbreviateTemperatureUnit (child.Temperature.Unit) ?? TemperatureUnit;
				TemperatureDisplay = $"{TemperatureValue:0.#}\u00b0{TemperatureUnit}";
				anyState = true;
				}

			bool? temperatureWarning = child.Temperature.Warning;
			if (temperatureWarning.HasValue)
				{
				bool wasWarning = TemperatureWarningIsActive;
				TemperatureWarningIsActive = temperatureWarning.Value;
				if (wasWarning != temperatureWarning.Value)
					{
					if (temperatureWarning.Value)
						{
						TemperatureWarningDetected?.Invoke (this, EventArgs.Empty);
						}
					else
						{
						TemperatureWarningCleared?.Invoke (this, EventArgs.Empty);
						}
					}
				}

			int? humidity = child.Humidity.Humidity;
			if (humidity.HasValue)
				{
				HasHumidity = true;
				HumidityValuePercent = humidity.Value;
				HumidityDisplay = $"{HumidityValuePercent:0}%";
				anyState = true;
				}

			bool? humidityWarning = child.Humidity.Warning;
			if (humidityWarning.HasValue)
				{
				bool wasWarning = HumidityWarningIsActive;
				HumidityWarningIsActive = humidityWarning.Value;
				if (wasWarning != humidityWarning.Value)
					{
					if (humidityWarning.Value)
						{
						HumidityWarningDetected?.Invoke (this, EventArgs.Empty);
						}
					else
						{
						HumidityWarningCleared?.Invoke (this, EventArgs.Empty);
						}
					}
				}

			bool? isOpen = child.Contact.IsOpen;
			if (isOpen.HasValue)
				{
				HasContact = true;
				bool wasOpen = ContactIsOpen;
				ContactIsOpen = isOpen.Value;
				ContactStatusLabel = isOpen.Value ? "Open" : "Closed";
				if (wasOpen != isOpen.Value)
					{
					if (isOpen.Value)
						{
						ContactOpened?.Invoke (this, EventArgs.Empty);
						}
					else
						{
						ContactClosed?.Invoke (this, EventArgs.Empty);
						}
					}

				anyState = true;
				}

			bool? motionDetected = child.Motion.MotionDetected;
			if (motionDetected.HasValue)
				{
				HasMotion = true;
				bool wasDetected = MotionDetected;
				MotionDetected = motionDetected.Value;
				MotionStatusLabel = motionDetected.Value ? "Motion Detected" : "No Motion";
				if (wasDetected != motionDetected.Value)
					{
					if (motionDetected.Value)
						{
						MotionDetectedEvent?.Invoke (this, EventArgs.Empty);
						}
					else
						{
						MotionCleared?.Invoke (this, EventArgs.Empty);
						}
					}

				anyState = true;
				}

			bool? leakAlert = child.WaterLeak.Alert;
			if (leakAlert.HasValue)
				{
				HasLeak = true;
				bool wasLeaking = LeakDetected;
				LeakDetected = leakAlert.Value;
				LeakStatusLabel = leakAlert.Value ? "Leak Detected" : "Dry";
				if (wasLeaking != leakAlert.Value)
					{
					if (leakAlert.Value)
						{
						LeakDetectedEvent?.Invoke (this, EventArgs.Empty);
						}
					else
						{
						LeakCleared?.Invoke (this, EventArgs.Empty);
						}
					}

				anyState = true;
				}
			}

		SensorStatus = BuildSensorStatus (anyState);
		SensorIcon = DetermineSensorIcon (anyState);
		}

	// Prioritizes leak detection and low battery above all else (conditions that demand immediate
	// attention regardless of sensor type), then falls back to a type-specific icon reflecting the
	// sensor's primary telemetry, and finally the generic on/off icon for anything else (e.g.
	// contact-only).
	private string DetermineSensorIcon (bool anyState)
		{
		if (LeakDetected)
			{
			return "icAlertOn";
			}

		if (BatteryIsLow)
			{
			return "icBatteryLow";
			}

		if (HasMotion)
			{
			return MotionDetected ? "icStarOn" : "icStarOff";
			}

		if (HasTemperature)
			{
			return "icClimateRegular";
			}

		return anyState ? "icGenericDeviceOn" : "icGenericDeviceOff";
		}

	// Crestron's status tile favors compact text, and the device reports full unit words
	// ("celsius"/"fahrenheit"); abbreviate those down to a single letter for display.
	private static string? AbbreviateTemperatureUnit (string? unit)
		{
		if (string.IsNullOrEmpty (unit))
			{
			return unit;
			}

		if (unit!.StartsWith ("f", StringComparison.OrdinalIgnoreCase))
			{
			return "F";
			}

		if (unit.StartsWith ("c", StringComparison.OrdinalIgnoreCase))
			{
			return "C";
			}

		return unit;
		}

	// Per the SDK's Dynamic Features guidance: declare the superset of properties/commands/events
	// via attributes, then remove whichever ones this specific child device does not support. Unlike
	// inferring support from observed telemetry (which can misfire if a capability's value simply
	// hasn't arrived yet on a given poll - e.g. a slower battery-check cadence), this reads the
	// child's own declared component/feature list (ChildDeviceInfo.Features, populated from the
	// device's SMART component_nego / get_child_device_component_list response), the same source
	// PlatformDriver.ResolveHubChildKind already trusts for child classification. That list is known
	// as soon as the child is discovered, so capabilities can be resolved on the very first
	// ApplyState call instead of waiting on telemetry to prove a negative.
	private void ResolveCapabilitiesFromDeclaredFeatures (ChildDevice child)
		{
		ChildDeviceInfo? info = child.Info;
		if (info is null)
			{
			// Info is not yet available (e.g. component list not queried yet this cycle) - try again
			// next ApplyState call rather than guessing.
			return;
			}

		_capabilitiesResolved = true;

		bool hasFeature (string featureId) =>
			info.Features.Any (feature => string.Equals (feature.Id, featureId, StringComparison.OrdinalIgnoreCase));

		bool hasBattery = hasFeature ("battery_level") || hasFeature ("battery_low");
		bool hasBatteryLevel = hasFeature ("battery_level");
		bool hasTemperature = hasFeature ("temperature");
		bool hasTemperatureWarning = hasFeature ("temperature_warning");
		bool hasHumidity = hasFeature ("humidity");
		bool hasHumidityWarning = hasFeature ("humidity_warning");
		bool hasContact = hasFeature ("is_open") || hasFeature ("open");
		bool hasMotion = hasFeature ("motion_detected") || hasFeature ("detected");
		bool hasLeak = hasFeature ("water_leak");

		try
			{
			if (!hasBattery)
				{
				RemoveProperty ("batteryLevelPercent");
				RemoveProperty ("batteryIsLow");
				RemoveProperty ("batteryStatusLabel");
				RemoveEvent ("batteryLowEvent");
				RemoveEvent ("batteryNormal");
				}
			else if (!hasBatteryLevel)
				{
				RemoveProperty ("batteryLevelPercent");
				}

			if (!hasTemperature)
				{
				RemoveProperty ("temperatureValue");
				RemoveProperty ("temperatureUnit");
				RemoveProperty ("temperatureDisplay");
				}

			if (!hasTemperatureWarning)
				{
				RemoveProperty ("temperatureWarningIsActive");
				RemoveEvent ("temperatureWarningDetected");
				RemoveEvent ("temperatureWarningCleared");
				}

			if (!hasHumidity)
				{
				RemoveProperty ("humidityValuePercent");
				RemoveProperty ("humidityDisplay");
				}

			if (!hasHumidityWarning)
				{
				RemoveProperty ("humidityWarningIsActive");
				RemoveEvent ("humidityWarningDetected");
				RemoveEvent ("humidityWarningCleared");
				}

			if (!hasContact)
				{
				RemoveProperty ("contactIsOpen");
				RemoveProperty ("contactStatusLabel");
				RemoveEvent ("contactOpened");
				RemoveEvent ("contactClosed");
				}

			if (!hasMotion)
				{
				RemoveProperty ("motionDetected");
				RemoveProperty ("motionStatusLabel");
				RemoveEvent ("motionDetectedEvent");
				RemoveEvent ("motionCleared");
				}

			if (!hasLeak)
				{
				RemoveProperty ("leakDetected");
				RemoveProperty ("leakStatusLabel");
				RemoveEvent ("leakDetectedEvent");
				RemoveEvent ("leakCleared");
				}

			RaiseDefinitionChangedEvent ();
			LogInfo ($"Sensor entity '{ControllerId}' capabilities resolved from declared features: hasBattery={hasBattery}, hasTemperature={hasTemperature}, hasHumidity={hasHumidity}, hasContact={hasContact}, hasMotion={hasMotion}, hasLeak={hasLeak}.");
			}
		catch (Exception ex)
			{
			LogError ($"Sensor entity '{ControllerId}' RemoveUnsupportedCapabilities failed: {ex.Message}");
			}
		}

	private string BuildSensorStatus (bool anyState)
		{
		if (!anyState)
			{
			return "Unknown";
			}

		List<string> lines = new List<string> (3);

		if (HasLeak)
			{
			lines.Add (LeakDetected ? "Leak Detected" : "Dry");
			}

		if (HasMotion)
			{
			lines.Add (MotionDetected ? "Motion Detected" : "No Motion");
			}

		if (HasContact)
			{
			lines.Add (ContactIsOpen ? "Open" : "Closed");
			}

		if (HasTemperature && HasHumidity)
			{
			lines.Add ($"{TemperatureValue:0.#}\u00b0{TemperatureUnit} / {HumidityValuePercent:0}%");
			}
		else if (HasTemperature)
			{
			lines.Add ($"{TemperatureValue:0.#}\u00b0{TemperatureUnit}");
			}
		else if (HasHumidity)
			{
			lines.Add ($"{HumidityValuePercent:0}% Humidity");
			}

		if (lines.Count == 0)
			{
			return "Reporting";
			}

		if (lines.Count > 3)
			{
			lines = lines.GetRange (0, 3);
			}

		return string.Join ("\n", lines);
		}

	private void UpdateDescriptorFromConnectedDevice (KasaDevice device)
		{
		string? childAlias = !string.IsNullOrWhiteSpace (_descriptor.ChildId)
			? device.GetChild (_descriptor.ChildId!)?.Alias
			: null;

		string resolvedAlias = !string.IsNullOrWhiteSpace (childAlias)
			? childAlias!
			: _descriptor.Name;

		_descriptor.Name = resolvedAlias;
		_descriptor.AwaitingConnectedIdentity = false;

		DeviceName = _descriptor.Name;
		ModelName = _descriptor.ModelName;
		SerialNumber = _descriptor.SerialNumber;
		DeviceLabel = _descriptor.Name;
		}

	[Conditional ("DEBUG")]
	private void LogInfo (string message)
		{
		_logger?.Log (_driverLogId, LogEntryLevel.Info, message);
		}

	private void LogError (string message)
		{
		_logger?.Log (_driverLogId, LogEntryLevel.Error, message);
		}
	}

// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.Globalization;
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
	private ChildBatterySensorState? _lastBatteryState;
	private ChildContactSensorState? _lastContactState;
	private ChildMotionSensorState? _lastMotionState;
	private ChildWaterLeakSensorState? _lastWaterLeakState;
	private ChildTemperatureSensorState? _lastTemperatureState;
	private ChildHumiditySensorState? _lastHumidityState;
	private bool _lastStateWasNull = true;
	// 0 means "leave the device's own default reporting interval alone". A positive value is
	// pushed to the device via ChildReportModeModule.SetIntervalAsync when it differs from what
	// the device currently reports.
	private int _desiredReportIntervalSeconds;
	private bool _reportIntervalApplyAttempted;
	private ChildDevice? _lastChild;

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

	// Timestamp of the most recent motion detection. Unlike MotionDetected/MotionStatusLabel,
	// this is intentionally never cleared when motion goes back to "not triggered" - it always
	// reflects the last time motion was seen, even while the sensor currently reports no motion.
	[EntityProperty (Id = "lastMotionTime")]
	[EntityPropertyMetadata (Programmable = true, ExtensionUiProperty = true)]
	public double LastMotionTime
		{
		get;
		private set => SetAndNotify ("lastMotionTime", value, ref field);
		}

	[EntityProperty (Id = "lastMotionTimeDisplay")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string LastMotionTimeDisplay
		{
		get;
		private set => SetAndNotify ("lastMotionTimeDisplay", value, ref field);
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
	//
	// Each event below uses explicit add/remove accessors (backed by an explicit field, since the
	// C# 13 'field' keyword only applies to property accessors, not event accessors) rather than a
	// plain field-like event, so subscription changes are directly observable: they are both
	// logged and, more importantly, reported to the owning ManagedParentDevicePoller via
	// EventSubscribersChanged so it can start polling this hub the instant the first subscriber
	// appears and stop the instant the last one goes away.
	private EventHandler? _contactOpened;

	[EntityEvent (Id = "contactOpened", FriendlyName = "Contact Opened", NameLocalizationKey = "Event_ContactOpened")]
	[EntityEventMetadata (Programmable = true)]
	public event EventHandler ContactOpened
		{
		add { _contactOpened += value; LogInfo ($"Sensor entity '{ControllerId}' ContactOpened subscriber added; totalSubscribers={_contactOpened?.GetInvocationList ().Length ?? 0}."); EventSubscribersChanged?.Invoke (); }
		remove { _contactOpened -= value; LogInfo ($"Sensor entity '{ControllerId}' ContactOpened subscriber removed; totalSubscribers={_contactOpened?.GetInvocationList ().Length ?? 0}."); EventSubscribersChanged?.Invoke (); }
		}

	private EventHandler? _contactClosed;

	[EntityEvent (Id = "contactClosed", FriendlyName = "Contact Closed", NameLocalizationKey = "Event_ContactClosed")]
	[EntityEventMetadata (Programmable = true)]
	public event EventHandler ContactClosed
		{
		add { _contactClosed += value; LogInfo ($"Sensor entity '{ControllerId}' ContactClosed subscriber added; totalSubscribers={_contactClosed?.GetInvocationList ().Length ?? 0}."); EventSubscribersChanged?.Invoke (); }
		remove { _contactClosed -= value; LogInfo ($"Sensor entity '{ControllerId}' ContactClosed subscriber removed; totalSubscribers={_contactClosed?.GetInvocationList ().Length ?? 0}."); EventSubscribersChanged?.Invoke (); }
		}

	private EventHandler? _motionDetectedEvent;

	[EntityEvent (Id = "motionDetectedEvent", FriendlyName = "Motion Detected", NameLocalizationKey = "Event_MotionDetected")]
	[EntityEventMetadata (Programmable = true)]
	public event EventHandler MotionDetectedEvent
		{
		add { _motionDetectedEvent += value; LogInfo ($"Sensor entity '{ControllerId}' MotionDetectedEvent subscriber added; totalSubscribers={_motionDetectedEvent?.GetInvocationList ().Length ?? 0}."); EventSubscribersChanged?.Invoke (); }
		remove { _motionDetectedEvent -= value; LogInfo ($"Sensor entity '{ControllerId}' MotionDetectedEvent subscriber removed; totalSubscribers={_motionDetectedEvent?.GetInvocationList ().Length ?? 0}."); EventSubscribersChanged?.Invoke (); }
		}

	private EventHandler? _motionCleared;

	[EntityEvent (Id = "motionCleared", FriendlyName = "Motion Cleared", NameLocalizationKey = "Event_MotionCleared")]
	[EntityEventMetadata (Programmable = true)]
	public event EventHandler MotionCleared
		{
		add { _motionCleared += value; LogInfo ($"Sensor entity '{ControllerId}' MotionCleared subscriber added; totalSubscribers={_motionCleared?.GetInvocationList ().Length ?? 0}."); EventSubscribersChanged?.Invoke (); }
		remove { _motionCleared -= value; LogInfo ($"Sensor entity '{ControllerId}' MotionCleared subscriber removed; totalSubscribers={_motionCleared?.GetInvocationList ().Length ?? 0}."); EventSubscribersChanged?.Invoke (); }
		}

	// Records when motion was last seen. Deliberately only called while motion is detected, so
	// LastMotionTime/LastMotionTimeDisplay keep their previous value across a "not triggered"
	// transition instead of being cleared alongside MotionDetected.
	private void UpdateLastMotionTimestamp (DateTime timestamp)
		{
		LastMotionTime = (double)new DateTimeOffset (timestamp).ToUnixTimeSeconds ();
		LastMotionTimeDisplay = timestamp.ToString ("g", CultureInfo.InvariantCulture);
		}

	private EventHandler? _leakDetectedEvent;

	[EntityEvent (Id = "leakDetectedEvent", FriendlyName = "Leak Detected", NameLocalizationKey = "Event_LeakDetected")]
	[EntityEventMetadata (Programmable = true)]
	public event EventHandler LeakDetectedEvent
		{
		add { _leakDetectedEvent += value; LogInfo ($"Sensor entity '{ControllerId}' LeakDetectedEvent subscriber added; totalSubscribers={_leakDetectedEvent?.GetInvocationList ().Length ?? 0}."); EventSubscribersChanged?.Invoke (); }
		remove { _leakDetectedEvent -= value; LogInfo ($"Sensor entity '{ControllerId}' LeakDetectedEvent subscriber removed; totalSubscribers={_leakDetectedEvent?.GetInvocationList ().Length ?? 0}."); EventSubscribersChanged?.Invoke (); }
		}

	private EventHandler? _leakCleared;

	[EntityEvent (Id = "leakCleared", FriendlyName = "Leak Cleared", NameLocalizationKey = "Event_LeakCleared")]
	[EntityEventMetadata (Programmable = true)]
	public event EventHandler LeakCleared
		{
		add { _leakCleared += value; LogInfo ($"Sensor entity '{ControllerId}' LeakCleared subscriber added; totalSubscribers={_leakCleared?.GetInvocationList ().Length ?? 0}."); EventSubscribersChanged?.Invoke (); }
		remove { _leakCleared -= value; LogInfo ($"Sensor entity '{ControllerId}' LeakCleared subscriber removed; totalSubscribers={_leakCleared?.GetInvocationList ().Length ?? 0}."); EventSubscribersChanged?.Invoke (); }
		}

	private EventHandler? _batteryLowEvent;

	[EntityEvent (Id = "batteryLowEvent", FriendlyName = "Battery Low", NameLocalizationKey = "Event_BatteryLow")]
	[EntityEventMetadata (Programmable = true)]
	public event EventHandler BatteryLowEvent
		{
		add { _batteryLowEvent += value; LogInfo ($"Sensor entity '{ControllerId}' BatteryLowEvent subscriber added; totalSubscribers={_batteryLowEvent?.GetInvocationList ().Length ?? 0}."); EventSubscribersChanged?.Invoke (); }
		remove { _batteryLowEvent -= value; LogInfo ($"Sensor entity '{ControllerId}' BatteryLowEvent subscriber removed; totalSubscribers={_batteryLowEvent?.GetInvocationList ().Length ?? 0}."); EventSubscribersChanged?.Invoke (); }
		}

	private EventHandler? _batteryNormal;

	[EntityEvent (Id = "batteryNormal", FriendlyName = "Battery Normal", NameLocalizationKey = "Event_BatteryNormal")]
	[EntityEventMetadata (Programmable = true)]
	public event EventHandler BatteryNormal
		{
		add { _batteryNormal += value; LogInfo ($"Sensor entity '{ControllerId}' BatteryNormal subscriber added; totalSubscribers={_batteryNormal?.GetInvocationList ().Length ?? 0}."); EventSubscribersChanged?.Invoke (); }
		remove { _batteryNormal -= value; LogInfo ($"Sensor entity '{ControllerId}' BatteryNormal subscriber removed; totalSubscribers={_batteryNormal?.GetInvocationList ().Length ?? 0}."); EventSubscribersChanged?.Invoke (); }
		}

	private EventHandler? _temperatureWarningDetected;

	[EntityEvent (Id = "temperatureWarningDetected", FriendlyName = "Temperature Warning Detected", NameLocalizationKey = "Event_TemperatureWarningDetected")]
	[EntityEventMetadata (Programmable = true)]
	public event EventHandler TemperatureWarningDetected
		{
		add { _temperatureWarningDetected += value; LogInfo ($"Sensor entity '{ControllerId}' TemperatureWarningDetected subscriber added; totalSubscribers={_temperatureWarningDetected?.GetInvocationList ().Length ?? 0}."); EventSubscribersChanged?.Invoke (); }
		remove { _temperatureWarningDetected -= value; LogInfo ($"Sensor entity '{ControllerId}' TemperatureWarningDetected subscriber removed; totalSubscribers={_temperatureWarningDetected?.GetInvocationList ().Length ?? 0}."); EventSubscribersChanged?.Invoke (); }
		}

	private EventHandler? _temperatureWarningCleared;

	[EntityEvent (Id = "temperatureWarningCleared", FriendlyName = "Temperature Warning Cleared", NameLocalizationKey = "Event_TemperatureWarningCleared")]
	[EntityEventMetadata (Programmable = true)]
	public event EventHandler TemperatureWarningCleared
		{
		add { _temperatureWarningCleared += value; LogInfo ($"Sensor entity '{ControllerId}' TemperatureWarningCleared subscriber added; totalSubscribers={_temperatureWarningCleared?.GetInvocationList ().Length ?? 0}."); EventSubscribersChanged?.Invoke (); }
		remove { _temperatureWarningCleared -= value; LogInfo ($"Sensor entity '{ControllerId}' TemperatureWarningCleared subscriber removed; totalSubscribers={_temperatureWarningCleared?.GetInvocationList ().Length ?? 0}."); EventSubscribersChanged?.Invoke (); }
		}

	private EventHandler? _humidityWarningDetected;

	[EntityEvent (Id = "humidityWarningDetected", FriendlyName = "Humidity Warning Detected", NameLocalizationKey = "Event_HumidityWarningDetected")]
	[EntityEventMetadata (Programmable = true)]
	public event EventHandler HumidityWarningDetected
		{
		add { _humidityWarningDetected += value; LogInfo ($"Sensor entity '{ControllerId}' HumidityWarningDetected subscriber added; totalSubscribers={_humidityWarningDetected?.GetInvocationList ().Length ?? 0}."); EventSubscribersChanged?.Invoke (); }
		remove { _humidityWarningDetected -= value; LogInfo ($"Sensor entity '{ControllerId}' HumidityWarningDetected subscriber removed; totalSubscribers={_humidityWarningDetected?.GetInvocationList ().Length ?? 0}."); EventSubscribersChanged?.Invoke (); }
		}

	private EventHandler? _humidityWarningCleared;

	[EntityEvent (Id = "humidityWarningCleared", FriendlyName = "Humidity Warning Cleared", NameLocalizationKey = "Event_HumidityWarningCleared")]
	[EntityEventMetadata (Programmable = true)]
	public event EventHandler HumidityWarningCleared
		{
		add { _humidityWarningCleared += value; LogInfo ($"Sensor entity '{ControllerId}' HumidityWarningCleared subscriber added; totalSubscribers={_humidityWarningCleared?.GetInvocationList ().Length ?? 0}."); EventSubscribersChanged?.Invoke (); }
		remove { _humidityWarningCleared -= value; LogInfo ($"Sensor entity '{ControllerId}' HumidityWarningCleared subscriber removed; totalSubscribers={_humidityWarningCleared?.GetInvocationList ().Length ?? 0}."); EventSubscribersChanged?.Invoke (); }
		}

	/// <summary>
	/// See <see cref="IKasaHubChildEntity.EventSubscribersChanged"/>.
	/// </summary>
	public event Action? EventSubscribersChanged;

	/// <summary>
	/// See <see cref="IKasaHubChildEntity.HasEventSubscribers"/>. Checked via each event's own
	/// explicit backing field.
	/// </summary>
	public bool HasEventSubscribers
		{
		get
			{
			return _contactOpened is not null
				|| _contactClosed is not null
				|| _motionDetectedEvent is not null
				|| _motionCleared is not null
				|| _leakDetectedEvent is not null
				|| _leakCleared is not null
				|| _batteryLowEvent is not null
				|| _batteryNormal is not null
				|| _temperatureWarningDetected is not null
				|| _temperatureWarningCleared is not null
				|| _humidityWarningDetected is not null
				|| _humidityWarningCleared is not null;
			}
		}

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
		ManagedParentDevicePoller? hubPoller = null,
		int reportIntervalSeconds = 0)
		: base (controllerId)
		{
		_descriptorUpdated = descriptorUpdated;
		_sharedConfiguration = sharedConfiguration;
		_hubPoller = hubPoller;
		_logger = logger;
		_driverLogId = driverLogId;
		_desiredReportIntervalSeconds = reportIntervalSeconds;

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

		// UpdateSubControllers can synchronously expose GetState to the host. Finalize
		// the cached surface before this entity can be configured, polled or published.
		// NotifyChildPublished is called AFTER UpdateSubControllers and is too late.
		ResolveCapabilitiesFromHubChildCategory ();

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

	// Mirrors KasaButtonEntity's EnsureDoubleClickEnabled: reconciles the device's actual
	// reporting interval against the desired "Report Interval (Seconds)" configuration
	// preference on every poll tick, so a live configuration change is applied the next time
	// this hub child is polled even if no device was connected yet when
	// SetReportIntervalSeconds was called. 0 means "leave the device's own default alone", so no
	// attempt is made in that case.
	private void EnsureReportIntervalApplied (ChildDevice? child)
		{
		if (child is null || _desiredReportIntervalSeconds <= 0)
			{
			return;
			}

		int? currentReportInterval = child.ReportMode.ReportInterval;
		if (currentReportInterval == _desiredReportIntervalSeconds)
			{
			return;
			}

		if (_reportIntervalApplyAttempted)
			{
			return;
			}

		_reportIntervalApplyAttempted = true;
		LogInfo ($"Sensor entity '{ControllerId}' EnsureReportIntervalApplied: device reports interval={currentReportInterval}; setting it to {_desiredReportIntervalSeconds} seconds on the device.");
		_ = SetReportIntervalAsync (child, _desiredReportIntervalSeconds);
		}

	/// <summary>
	/// Invoked when the "Report Interval (Seconds)" configuration item changes. Applies the new
	/// preference to the most recently pushed hub child device (if any); if no child has been
	/// pushed yet, the preference is still recorded and will be applied the next time
	/// <see cref="ApplyPushedState"/> runs against a connected child. A value of 0 leaves the
	/// device's own default interval unchanged.
	/// </summary>
	public void SetReportIntervalSeconds (int reportIntervalSeconds)
		{
		if (_desiredReportIntervalSeconds == reportIntervalSeconds)
			{
			return;
			}

		_desiredReportIntervalSeconds = reportIntervalSeconds;
		_reportIntervalApplyAttempted = false;
		LogInfo ($"Sensor entity '{ControllerId}' SetReportIntervalSeconds: reportIntervalSeconds={reportIntervalSeconds}.");

		EnsureReportIntervalApplied (_lastChild);
		}

	private async Task SetReportIntervalAsync (ChildDevice child, int reportIntervalSeconds)
		{
		try
			{
			await child.ReportMode.SetIntervalAsync (reportIntervalSeconds).ConfigureAwait (false);
			LogInfo ($"Sensor entity '{ControllerId}' SetReportIntervalAsync: report interval set to {reportIntervalSeconds} seconds successfully.");
			}
		catch (Exception ex)
			{
			LogError ($"Sensor entity '{ControllerId}' SetReportIntervalAsync failed: {ex.Message}");
			}
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
		_lastChild = child;
		EnsureReportIntervalApplied (child);
			bool stateChanged = HasChildStateChanged (child);
		if (stateChanged)
			{
			ApplyState (child);
			}

		bool wasOnline = OnlineIndicatorIsOnline && ReadyIndicatorIsReady;
		OnlineIndicatorIsOnline = true;
		ReadyIndicatorIsReady = true;

		if (_childPublished && (stateChanged || !wasOnline))
			{
			PublishStateSnapshot ();
			}
		}

	// Compares the freshly pushed child's typed state records (now value-comparable per KasaClient
	// v1.6.0's record types) against the last-seen snapshot, so a poll tick that returns identical
	// state can skip both ApplyState's property/event work and the subsequent publish entirely,
	// rather than gating only the final publish after unconditionally re-applying state.
	private bool HasChildStateChanged (ChildDevice? child)
		{
		if (child is null)
			{
			bool changed = !_lastStateWasNull;
			_lastStateWasNull = true;
			_lastBatteryState = null;
			_lastContactState = null;
			_lastMotionState = null;
			_lastWaterLeakState = null;
			_lastTemperatureState = null;
			_lastHumidityState = null;
			return changed;
			}

		ChildBatterySensorState? batteryState = child.Battery.State;
		ChildContactSensorState? contactState = child.Contact.State;
		ChildMotionSensorState? motionState = child.Motion.State;
		ChildWaterLeakSensorState? waterLeakState = child.WaterLeak.State;
		ChildTemperatureSensorState? temperatureState = child.Temperature.State;
		ChildHumiditySensorState? humidityState = child.Humidity.State;

		bool stateChanged = _lastStateWasNull
			|| _lastBatteryState != batteryState
			|| _lastContactState != contactState
			|| _lastMotionState != motionState
			|| _lastWaterLeakState != waterLeakState
			|| _lastTemperatureState != temperatureState
			|| _lastHumidityState != humidityState;

		_lastStateWasNull = false;
		_lastBatteryState = batteryState;
		_lastContactState = contactState;
		_lastMotionState = motionState;
		_lastWaterLeakState = waterLeakState;
		_lastTemperatureState = temperatureState;
		_lastHumidityState = humidityState;

		return stateChanged;
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

		bool changed = OnlineIndicatorIsOnline != online || ReadyIndicatorIsReady != online;
		OnlineIndicatorIsOnline = online;
		ReadyIndicatorIsReady = online;

		if (_childPublished && changed)
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

		LogInfo ($"Sensor entity '{ControllerId}' NotifyChildPublished invoked, capabilitiesResolved={_capabilitiesResolved}, hasConnectedDevice={_connectedDevice is not null}, isConfigured={_isConfigured}, registeredWithHubPoller={_registeredWithHubPoller}.");
		PublishStateSnapshot ();
		}

	public void NotifyChildRunning (string context)
		{
		if (_disposed)
			{
			return;
			}

		LogInfo ($"Sensor entity '{ControllerId}' NotifyChildRunning invoked, context='{context}'.");
		LogCapabilityResolutionDiagnostics ($"NotifyChildRunning:{context}");
		PublishStateSnapshot ();
		}

	// DIAGNOSTIC (temporary): capability resolution is the one thing a Sensor child has that the
	// adopted Outlet/Button children do not. RemoveUnsupportedCapabilities can only run once
	// ChildDeviceInfo.Features has arrived from the hub, which on the cached-publish path happens
	// only after the shared poller's first successful UpdateAsync - i.e. potentially AFTER the
	// host's ApplyAll pass has already decided which children to advance. If a cached sensor is
	// still carrying the undtrimmed declared SUPERSET (contact + motion + leak + temperature +
	// humidity simultaneously - a combination no real device has) at the moment the host inspects
	// it, that is a plausible reason the host declines it while accepting the outlets/buttons,
	// whose surface is static. This logs exactly when resolution happens relative to publication.
	private void LogCapabilityResolutionDiagnostics (string phase)
		{
		LogInfo ($"SENSOR-CAP-DIAG: controllerId='{ControllerId}', phase='{phase}', capabilitiesResolved={_capabilitiesResolved}, childPublished={_childPublished}, isConfigured={_isConfigured}, hasConnectedDevice={_connectedDevice is not null}, registeredWithHubPoller={_registeredWithHubPoller}.");
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
			PublishProperty ("motionStatusLabel", new DriverEntityValue (MotionStatusLabel), "PublishStateSnapshot");
			PublishProperty ("lastMotionTime", new DriverEntityValue (LastMotionTime), "PublishStateSnapshot");
			PublishProperty ("lastMotionTimeDisplay", new DriverEntityValue (LastMotionTimeDisplay), "PublishStateSnapshot");
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
						_batteryLowEvent?.Invoke (this, EventArgs.Empty);
						}
					else
						{
						_batteryNormal?.Invoke (this, EventArgs.Empty);
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
						_temperatureWarningDetected?.Invoke (this, EventArgs.Empty);
						}
					else
						{
						_temperatureWarningCleared?.Invoke (this, EventArgs.Empty);
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
						_humidityWarningDetected?.Invoke (this, EventArgs.Empty);
						}
					else
						{
						_humidityWarningCleared?.Invoke (this, EventArgs.Empty);
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
						_contactOpened?.Invoke (this, EventArgs.Empty);
						}
					else
						{
						_contactClosed?.Invoke (this, EventArgs.Empty);
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
				if (motionDetected.Value)
					{
					UpdateLastMotionTimestamp (DateTime.Now);
					}

				if (wasDetected != motionDetected.Value)
					{
					if (motionDetected.Value)
						{
						_motionDetectedEvent?.Invoke (this, EventArgs.Empty);
						}
					else
						{
						_motionCleared?.Invoke (this, EventArgs.Empty);
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
						_leakDetectedEvent?.Invoke (this, EventArgs.Empty);
						}
					else
						{
						_leakCleared?.Invoke (this, EventArgs.Empty);
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

		ApplyResolvedCapabilities (
			hasBattery,
			hasBatteryLevel,
			hasTemperature,
			hasTemperatureWarning,
			hasHumidity,
			hasHumidityWarning,
			hasContact,
			hasMotion,
			hasLeak,
			"declared-features");
		}

	// Resolves the same capability set from the descriptor's persisted HubChildCategory instead of
	// live telemetry. On the cached-reload path ChildDeviceInfo.Features is not available until the
	// shared poller's first successful UpdateAsync, which happens AFTER the child is published - so
	// trimming there raises RaiseDefinitionChangedEvent while the host is still registering the
	// child, and the host drops it (outlets/buttons survive because their surface is static). The
	// category is known at discovery and is now persisted in the managed-device cache, so the
	// surface can be finalized before publication, matching the initial-add ordering that works.
	private void ResolveCapabilitiesFromHubChildCategory ()
		{
		HubChildCategory category = _descriptor.HubChildCategory;
		HubChildCategory modelCategory = HubChildCategoryResolver.FromModel (_descriptor.ModelName);
		if (category == HubChildCategory.None
			|| (category == HubChildCategory.Contact && modelCategory == HubChildCategory.Motion))
			{
			// Older caches omit the category; earlier discovery also mislabeled T100
			// as Contact. Repair these known cases without deleting room configuration.
			category = modelCategory;
			_descriptor.HubChildCategory = category;
			}

		if (category == HubChildCategory.None)
			{
			// An unknown model still needs the device's declared features.
			return;
			}

		bool hasTemperature = category is HubChildCategory.Temperature or HubChildCategory.TemperatureHumidity;
		bool hasHumidity = category is HubChildCategory.Humidity or HubChildCategory.TemperatureHumidity;
		bool hasContact = category == HubChildCategory.Contact;
		bool hasMotion = category == HubChildCategory.Motion;
		bool hasLeak = category == HubChildCategory.WaterLeak;

		// Every battery-powered hub child reports battery, and the warning properties travel with
		// their matching telemetry, so these follow directly from the category.
		ApplyResolvedCapabilities (
			hasBattery: true,
			hasBatteryLevel: true,
			hasTemperature: hasTemperature,
			hasTemperatureWarning: hasTemperature,
			hasHumidity: hasHumidity,
			hasHumidityWarning: hasHumidity,
			hasContact: hasContact,
			hasMotion: hasMotion,
			hasLeak: hasLeak,
			$"hub-child-category:{category}");
		}

	private void ApplyResolvedCapabilities (
		bool hasBattery,
		bool hasBatteryLevel,
		bool hasTemperature,
		bool hasTemperatureWarning,
		bool hasHumidity,
		bool hasHumidityWarning,
		bool hasContact,
		bool hasMotion,
		bool hasLeak,
		string source)
		{
		if (_capabilitiesResolved)
			{
			return;
			}

		_capabilitiesResolved = true;

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
				RemoveProperty ("lastMotionTime");
				RemoveProperty ("lastMotionTimeDisplay");
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

			// Only signal a definition change if the child has already been published. When the
			// surface is finalized before publication (the cached-reload path resolving from the
			// persisted HubChildCategory), the host has not registered anything yet and raising
			// this during its registration pass is precisely what makes it drop the child.
			if (_childPublished)
				{
				RaiseDefinitionChangedEvent ();
				}

			LogInfo ($"Sensor entity '{ControllerId}' capabilities resolved from {source}: hasBattery={hasBattery}, hasTemperature={hasTemperature}, hasHumidity={hasHumidity}, hasContact={hasContact}, hasMotion={hasMotion}, hasLeak={hasLeak}, childPublished={_childPublished}.");
			LogCapabilityResolutionDiagnostics ("RemoveUnsupportedCapabilities-complete");
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

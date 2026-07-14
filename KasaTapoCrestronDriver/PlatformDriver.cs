using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Crestron.DeviceDrivers.EntityModel;
using Crestron.DeviceDrivers.EntityModel.Data;
using Crestron.DeviceDrivers.EntityModel.Logging;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel;
using Crestron.DeviceDrivers.SDK.EntityModel.Attributes;
using Crestron.DeviceDrivers.SDK.EntityModel.Data;

using KasaTapoClient;

using KasaDeviceType = KasaTapoClient.DeviceType;

namespace KasaTapoCrestronDriver;

public sealed class PlatformDriver : ReflectedAttributeDriverEntity, IDisposable
	{
	private enum ConfigurationApplyMode
		{
		InitialConfiguration,
		SavedConfiguration,
		RealtimeChange
		}

	private sealed class PendingMaterialization
		{
		public PendingMaterialization (DiscoveryResult discoveryResult, ManagedLightDescriptor descriptor, DeviceConfiguration configuration, bool deferPublicationUntilIdentityResolved)
			{
			DiscoveryResult = discoveryResult;
			Descriptor = descriptor;
			Configuration = configuration;
			DeferPublicationUntilIdentityResolved = deferPublicationUntilIdentityResolved;
			}

		public DiscoveryResult DiscoveryResult
			{
			get;
			}

		public ManagedLightDescriptor Descriptor
			{
			get;
			}

		public DeviceConfiguration Configuration
			{
			get;
			}

		public bool DeferPublicationUntilIdentityResolved
			{
			get;
			}
		}

	private sealed class LoggingDriverConfigurationController : IDriverConfigurationController
		{
		private readonly string _controllerId;
		private readonly IDriverConfigurationController _inner;
		private readonly Action<string> _logInfo;

		public LoggingDriverConfigurationController (string controllerId, IDriverConfigurationController inner, Action<string> logInfo)
			{
			_controllerId = controllerId;
			_inner = inner;
			_logInfo = logInfo;

			_inner.ConfigurationItemsUpdated += HandleConfigurationItemsUpdated;
			_inner.ConfigurationListChanged += HandleConfigurationListChanged;
			_inner.StatusChanged += HandleStatusChanged;
			}

		public event EventHandler<ConfigurationItemsUpdatedEventArgs> ConfigurationItemsUpdated
			{
			add => _inner.ConfigurationItemsUpdated += value;
			remove => _inner.ConfigurationItemsUpdated -= value;
			}

		public event EventHandler<ConfigurationListChangedEventArgs> ConfigurationListChanged
			{
			add => _inner.ConfigurationListChanged += value;
			remove => _inner.ConfigurationListChanged -= value;
			}

		public event EventHandler<StatusChangedEventArgs> StatusChanged
			{
			add => _inner.StatusChanged += value;
			remove => _inner.StatusChanged -= value;
			}

		public Crestron.DeviceDrivers.EntityModel.Data.DeviceConfiguration.ApplyConfigurationResult ApplyConfiguration (IDictionary<string, string> values)
			{
			Log ($"ApplyConfiguration called with keys=[{FormatKeys (values?.Keys)}].");
			Crestron.DeviceDrivers.EntityModel.Data.DeviceConfiguration.ApplyConfigurationResult result = _inner.ApplyConfiguration (values);
			Log ($"ApplyConfiguration returned remainingItems={result?.RemainingConfigurationItemsToSet?.Count ?? 0}, invalidIds={FormatKeys (result?.InvalidConfigurationItemIdsReceived)}, errorKeys={FormatKeys (result?.ConfigurationErrorsByItemId?.Keys)}.");
			return result!;
			}

		public Crestron.DeviceDrivers.EntityModel.Data.DeviceConfiguration.ApplyConfigurationStepResult ApplyConfigurationStep (string stepId, IDictionary<string, string> values)
			{
			Log ($"ApplyConfigurationStep called for stepId='{stepId}' with keys=[{FormatKeys (values?.Keys)}].");
			Crestron.DeviceDrivers.EntityModel.Data.DeviceConfiguration.ApplyConfigurationStepResult result = _inner.ApplyConfigurationStep (stepId, values);
			Log ($"ApplyConfigurationStep returned nextStep='{result?.NextConfigurationStep?.Id}', invalidIds={FormatKeys (result?.InvalidConfigurationItemIdsReceived)}, errorKeys={FormatKeys (result?.ConfigurationErrorsByItemId?.Keys)}, errorMessage='{result?.ErrorMessage ?? string.Empty}'.");
			return result!;
			}

		public Crestron.DeviceDrivers.EntityModel.Data.DeviceConfiguration.ConfigurationValueCollection GetAllConfigurationValues ()
			{
			Log ("GetAllConfigurationValues called.");
			Crestron.DeviceDrivers.EntityModel.Data.DeviceConfiguration.ConfigurationValueCollection values = _inner.GetAllConfigurationValues ();
			Log ($"GetAllConfigurationValues returned keys=[{FormatKeys (values?.ConfigurationSettings?.Keys)}].");
			return values!;
			}

		public Crestron.DeviceDrivers.EntityModel.Data.DeviceConfiguration.ConfigurationStep GetFirstConfigurationStep ()
			{
			Log ("GetFirstConfigurationStep called.");
			Crestron.DeviceDrivers.EntityModel.Data.DeviceConfiguration.ConfigurationStep step = _inner.GetFirstConfigurationStep ();
			Log ($"GetFirstConfigurationStep returned stepId='{step?.Id}', itemCount={step?.ConfigurationItems?.Count ?? 0}.");
			return step!;
			}

		public DriverControllerStatus GetStatus ()
			{
			Log ("GetStatus called.");
			DriverControllerStatus status = _inner.GetStatus ();
			Log ($"GetStatus returned '{status}'.");
			return status;
			}

		public DriverControllerStatus PeekStatus ()
			{
			DriverControllerStatus status = _inner.GetStatus ();
			Log ($"PeekStatus returned '{status}'.");
			return status;
			}

		public void SetTransportImplementation (
			TransportTx transportTx,
			Action<string, Action<string>> sendAction,
			Action<string, Action<string>> subscribeAction)
			{
			Log ($"SetTransportImplementation called for transportType='{transportTx?.GetType ().FullName ?? string.Empty}'.");
			_inner.SetTransportImplementation (transportTx, sendAction, subscribeAction);
			}

		public void UnsetTransportImplementation ()
			{
			Log ("UnsetTransportImplementation called.");
			_inner.UnsetTransportImplementation ();
			}

		private void HandleConfigurationItemsUpdated (object? sender, ConfigurationItemsUpdatedEventArgs args)
			{
			Log ($"ConfigurationItemsUpdated event: controllerId='{args?.ControllerId}', itemIds=[{FormatKeys (args?.ConfigurationItems?.Select (item => item.Id))}].");
			}

		private void HandleConfigurationListChanged (object? sender, ConfigurationListChangedEventArgs args)
			{
			Log ($"ConfigurationListChanged event: controllerId='{args?.ControllerId}', added=[{FormatKeys (args?.ConfigurationItemsAdded?.Select (item => item.Id))}], removed=[{FormatKeys (args?.ConfigurationItemIdsRemoved)}].");
			}

		private void HandleStatusChanged (object? sender, StatusChangedEventArgs args)
			{
			Log ($"StatusChanged event: controllerId='{args?.ControllerId}', status='{args?.Status}'.");
			}

		private void Log (string message)
			{
			if (!PlatformDriver.IsDebugLoggingEnabled ())
				{
				return;
				}

			_logInfo ($"Child configuration controller [{_controllerId}]: {message}");
			}

		private static string FormatKeys (IEnumerable<string>? keys)
			{
			if (keys is null)
				{
				return string.Empty;
				}

			return string.Join (", ", keys.Where (key => !string.IsNullOrWhiteSpace (key)).OrderBy (key => key, StringComparer.OrdinalIgnoreCase));
			}
		}

	[DataContract]
	private sealed class ManagedDeviceCacheDocument
		{
		[DataMember (Name = "version")]
		public int Version
			{
			get;
			set;
			}

		[DataMember (Name = "devices")]
		public List<ManagedDeviceCacheEntry> Devices
			{
			get;
			set;
			} = new ();
		}

	[DataContract]
	private sealed class ManagedDeviceImmutableCacheFields
		{
		[DataMember (Name = "controllerId")]
		public string ControllerId
			{
			get;
			set;
			} = string.Empty;

		[DataMember (Name = "uxCategory")]
		public DeviceUxCategory UxCategory
			{
			get;
			set;
			}

		[DataMember (Name = "manufacturer")]
		public string Manufacturer
			{
			get;
			set;
			} = string.Empty;

		[DataMember (Name = "model")]
		public string Model
			{
			get;
			set;
			} = string.Empty;

		[DataMember (Name = "discoveredDeviceType", EmitDefaultValue = false)]
		public KasaDeviceType DiscoveredDeviceType
			{
			get;
			set;
			}

		[DataMember (Name = "managedLightKind", EmitDefaultValue = false)]
		public ManagedLightKind ManagedLightKind
			{
			get;
			set;
			}

		[DataMember (Name = "serialNumber")]
		public string SerialNumber
			{
			get;
			set;
			} = string.Empty;
		}

	[DataContract]
	private sealed class ManagedDeviceMutableCacheFields
		{
		[DataMember (Name = "name")]
		public string Name
			{
			get;
			set;
			} = string.Empty;

		[DataMember (Name = "host", EmitDefaultValue = false)]
		public string Host
			{
			get;
			set;
			} = string.Empty;

		[DataMember (Name = "awaitingConnectedIdentity", EmitDefaultValue = false)]
		public bool AwaitingConnectedIdentity
			{
			get;
			set;
			}

		[DataMember (Name = "isConfigured", EmitDefaultValue = false)]
		public bool IsConfigured
			{
			get;
			set;
			}

		[DataMember (Name = "port", EmitDefaultValue = false)]
		public int Port
			{
			get;
			set;
			}

		[DataMember (Name = "transportKind", EmitDefaultValue = false)]
		public DeviceTransportKind TransportKind
			{
			get;
			set;
			}

		[DataMember (Name = "deviceFamily", EmitDefaultValue = false)]
		public DeviceFamilyKind DeviceFamily
			{
			get;
			set;
			}

		[DataMember (Name = "encryptionKind", EmitDefaultValue = false)]
		public DeviceEncryptionKind EncryptionKind
			{
			get;
			set;
			}

		[DataMember (Name = "loginVersion", EmitDefaultValue = false)]
		public int? LoginVersion
			{
			get;
			set;
			}

		[DataMember (Name = "useHttps", EmitDefaultValue = false)]
		public bool UseHttps
			{
			get;
			set;
			}

		[DataMember (Name = "httpPort", EmitDefaultValue = false)]
		public int? HttpPort
			{
			get;
			set;
			}

		[DataMember (Name = "useSsl", EmitDefaultValue = false)]
		public bool UseSsl
			{
			get;
			set;
			}

		[DataMember (Name = "useDefaultCredentials", EmitDefaultValue = false)]
		public bool UseDefaultCredentials
			{
			get;
			set;
			}

		[DataMember (Name = "defaultCredentialProfile", EmitDefaultValue = false)]
		public DefaultCredentialProfile DefaultCredentialProfile
			{
			get;
			set;
			}

		[DataMember (Name = "applicationPath", EmitDefaultValue = false)]
		public string ApplicationPath
			{
			get;
			set;
			} = string.Empty;

		[DataMember (Name = "useSecurePassthrough", EmitDefaultValue = false)]
		public bool UseSecurePassthrough
			{
			get;
			set;
			}

		[DataMember (Name = "tpapKeepAliveIntervalMs", EmitDefaultValue = false)]
		public long? TpapKeepAliveIntervalMs
			{
			get;
			set;
			}
		}

	[DataContract]
	private sealed class ManagedDeviceCacheEntry
		{
		[DataMember (Name = "immutable")]
		public ManagedDeviceImmutableCacheFields Immutable
			{
			get;
			set;
			} = new ();

		[DataMember (Name = "mutable")]
		public ManagedDeviceMutableCacheFields Mutable
			{
			get;
			set;
			} = new ();

		public string ControllerId
			{
			get => Immutable.ControllerId;
			set => Immutable.ControllerId = value ?? string.Empty;
			}

		public DeviceUxCategory UxCategory
			{
			get => Immutable.UxCategory;
			set => Immutable.UxCategory = value;
			}

		public string Name
			{
			get => Mutable.Name;
			set => Mutable.Name = value ?? string.Empty;
			}

		public string Manufacturer
			{
			get => Immutable.Manufacturer;
			set => Immutable.Manufacturer = value ?? string.Empty;
			}

		public string Model
			{
			get => Immutable.Model;
			set => Immutable.Model = value ?? string.Empty;
			}

		public KasaDeviceType DiscoveredDeviceType
			{
			get => Immutable.DiscoveredDeviceType;
			set => Immutable.DiscoveredDeviceType = value;
			}

		public ManagedLightKind ManagedLightKind
			{
			get => Immutable.ManagedLightKind;
			set => Immutable.ManagedLightKind = value;
			}

		public string SerialNumber
			{
			get => Immutable.SerialNumber;
			set => Immutable.SerialNumber = value ?? string.Empty;
			}

		public string Host
			{
			get => Mutable.Host;
			set => Mutable.Host = value ?? string.Empty;
			}

		public bool AwaitingConnectedIdentity
			{
			get => Mutable.AwaitingConnectedIdentity;
			set => Mutable.AwaitingConnectedIdentity = value;
			}

		public bool IsConfigured
			{
			get => Mutable.IsConfigured;
			set => Mutable.IsConfigured = value;
			}

		public int Port
			{
			get => Mutable.Port;
			set => Mutable.Port = value;
			}

		public DeviceTransportKind TransportKind
			{
			get => Mutable.TransportKind;
			set => Mutable.TransportKind = value;
			}

		public DeviceFamilyKind DeviceFamily
			{
			get => Mutable.DeviceFamily;
			set => Mutable.DeviceFamily = value;
			}

		public DeviceEncryptionKind EncryptionKind
			{
			get => Mutable.EncryptionKind;
			set => Mutable.EncryptionKind = value;
			}

		public int? LoginVersion
			{
			get => Mutable.LoginVersion;
			set => Mutable.LoginVersion = value;
			}

		public bool UseHttps
			{
			get => Mutable.UseHttps;
			set => Mutable.UseHttps = value;
			}

		public int? HttpPort
			{
			get => Mutable.HttpPort;
			set => Mutable.HttpPort = value;
			}

		public bool UseSsl
			{
			get => Mutable.UseSsl;
			set => Mutable.UseSsl = value;
			}

		public bool UseDefaultCredentials
			{
			get => Mutable.UseDefaultCredentials;
			set => Mutable.UseDefaultCredentials = value;
			}

		public DefaultCredentialProfile DefaultCredentialProfile
			{
			get => Mutable.DefaultCredentialProfile;
			set => Mutable.DefaultCredentialProfile = value;
			}

		public string ApplicationPath
			{
			get => Mutable.ApplicationPath;
			set => Mutable.ApplicationPath = value ?? string.Empty;
			}

		public bool UseSecurePassthrough
			{
			get => Mutable.UseSecurePassthrough;
			set => Mutable.UseSecurePassthrough = value;
			}

		public long? TpapKeepAliveIntervalMs
			{
			get => Mutable.TpapKeepAliveIntervalMs;
			set => Mutable.TpapKeepAliveIntervalMs = value;
			}
		}

	private const string TP_LINK_MANUFACTURER = "TP-Link";
	private const string PERSISTENT_STORAGE_ROOT = "/user/Data/ThirdParty/NeilColvin/KasaTapoCrestronDriver";
	private const string MANAGED_DEVICE_CACHE_FILE_NAME = "managed-devices-cache.json";
	private const int MANAGED_DEVICE_CACHE_VERSION = 6;

	private static readonly TimeSpan InitialDiscoveryRefreshInterval = TimeSpan.FromSeconds (10);
	private static readonly TimeSpan DefaultDiscoveryTimeout = TimeSpan.FromSeconds (20);
	private static readonly TimeSpan DefaultDiscoveryRefreshInterval = TimeSpan.FromMinutes (5);
	private const int MAX_FAST_REFRESH_SHRINK_RETRIES = 6;
	private const int MANAGED_DEVICE_REMOVAL_MISS_THRESHOLD = 6;
	private static readonly TimeSpan DefaultLightPollInterval = TimeSpan.FromSeconds (15);
	private static readonly TimeSpan DefaultSensorPollInterval = TimeSpan.FromSeconds (3);

	private readonly DriverControllerCreationArgs _args;
	private readonly DriverImplementationResources _resources;
	private readonly DriverControllerLogger _logger;
	private readonly string _driverLogId;
	private readonly Dictionary<string, ConfigurableDriverEntity> _childControllers = new (StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, LoggingDriverConfigurationController> _childConfigurationControllers = new (StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, DeviceConfiguration> _deviceConfigurations = new (StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, DiscoveryResult> _discoveryResults = new (StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, ManagedLightDescriptor> _knownDescriptors = new (StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, ManagedDeviceCacheEntry> _managedDeviceCacheMetadata = new (StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, string> _resolvedDeviceNames = new (StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, IKasaManagedLightEntity> _lightEntities = new (StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, int> _pendingRemovalMissCounts = new (StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<string> _configuredChildControllerIds = new (StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<string> _inUseChildControllerIds = new (StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<string> _connectedIdentityResolvedControllerIds = new (StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<string, byte> _aliasResolutionInFlightControllerIds = new (StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<string, SemaphoreSlim> _connectionGates = new (StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<string> _materializationInFlightControllerIds = new (StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<string> _previousDiscoveredControllerIds = new (StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<string, PlatformManagedDevice> _managedDevices = new (StringComparer.OrdinalIgnoreCase);
	private readonly PlatformSharedConfiguration _sharedConfiguration = new ();
	private readonly SemaphoreSlim _refreshGate = new (1, 1);
	private readonly SemaphoreSlim _scheduledRefreshGate = new (1, 1);
	private readonly SemaphoreSlim _managedDeviceCacheWriteGate = new (1, 1);
	private readonly DataDrivenConfigurationControllerArgs _configurationArgs;
	private readonly Func<string, ICondition>? _conditionLookup;
	private readonly Func<string, ITransformation>? _transformationLookup;
	private readonly IComponentLogger? _componentLogger;
	private string _userName = string.Empty;
	private string _password = string.Empty;
	private string _discoveryTimeoutSeconds = ((int)DefaultDiscoveryTimeout.TotalSeconds).ToString (CultureInfo.InvariantCulture);
	private string _lightPollIntervalSeconds = ((int)DefaultLightPollInterval.TotalSeconds).ToString (CultureInfo.InvariantCulture);
	private string _sensorPollIntervalSeconds = ((int)DefaultSensorPollInterval.TotalSeconds).ToString (CultureInfo.InvariantCulture);
	private bool _enableLightPolling;
	private bool _treatPlugsAsLights;
	private bool _disposed;
	private bool _initialDiscoveryLoadPending = true;
	private bool _initialShortRefreshPending = true;
	private bool _initialMaterializationStageActive = true;
	private bool _normalRemovalRefreshPhaseReached;
	private int _fastRefreshShrinkRetryCount;
	private bool _runtimeDiscoveryStarted;
	private readonly CancellationTokenSource _runtimeCancellationSource = new ();
	private int _discoveryRefreshGeneration;
	private Task? _discoveryRefreshTask;

	internal DataDrivenConfigurationController ConfigurationController
		{
		get;
		}

	[EntityProperty (Id = "platform:managedDevices", Type = DriverEntityValueType.DeviceDictionary, ItemTypeRef = "platform:ManagedDevice", FriendlyName = "Managed Devices")]
	public IDictionary<string, PlatformManagedDevice> ManagedDevices
		{
		get => _managedDevices;
		}

	[EntityProperty (Id = "onlineIndicator:isOnline", Type = DriverEntityValueType.Boolean)]
	public bool OnlineIndicatorIsOnline
		{
		get;
		private set => SetAndNotify ("onlineIndicator:isOnline", value, ref field);
		}

	[EntityProperty (Id = "readyIndicator:isReady", Type = DriverEntityValueType.Boolean)]
	public bool ReadyIndicatorIsReady
		{
		get;
		private set => SetAndNotify ("readyIndicator:isReady", value, ref field);
		}

	public PlatformDriver (DriverControllerCreationArgs args, DriverImplementationResources resources)
		: base (DriverController.RootControllerId)
		{
		_args = args;
		_resources = resources;
		_logger = args.Logger;
		_driverLogId = args.DriverId;

		#if DEBUG
		RegisterKasaClientDebugListener ();
		#endif

		LogInfo ($"Platform driver instance starting: driverId='{_driverLogId}', dataDirectory='{_args.DriverDataDirectoryPath}'.");

		ThreadPool.GetMinThreads (out int minWorkerThreads, out int minIoThreads);
		ThreadPool.GetMaxThreads (out int maxWorkerThreads, out int maxIoThreads);
		ThreadPool.GetAvailableThreads (out int availableWorkerThreads, out int availableIoThreads);
		LogInfo ($"ThreadPool state at driver startup: processorCount={Environment.ProcessorCount}, minWorkerThreads={minWorkerThreads}, minIoThreads={minIoThreads}, maxWorkerThreads={maxWorkerThreads}, maxIoThreads={maxIoThreads}, availableWorkerThreads={availableWorkerThreads}, availableIoThreads={availableIoThreads}.");

		_configurationArgs = DataDrivenConfigurationControllerArgs.FromResources (args, resources, ControllerId);
		_conditionLookup = _configurationArgs.ConditionLookup;
		_transformationLookup = _configurationArgs.TransformationLookup;
		_componentLogger = _configurationArgs.Logger;
		ConfigurationController = new DelegateDataDrivenConfigurationController (_configurationArgs, ApplyConfigurationItems, null, null);
		}

	public void Stop ()
		{
		if (_disposed)
			{
			return;
			}

		LogInfo ($"PlatformDriver.Stop: childControllerCount={_childControllers.Count}, lightEntityCount={_lightEntities.Count}.");

		CancelRuntimeRefreshes ();

		foreach (IKasaManagedLightEntity lightEntity in _lightEntities.Values.ToArray ())
			{
			lightEntity.Stop ();
			}
		}

	public override void Dispose ()
		{
		if (_disposed)
			{
			return;
			}

		LogInfo ("PlatformDriver.Dispose: disposing platform driver.");

		Stop ();

		foreach (IKasaManagedLightEntity lightEntity in _lightEntities.Values.ToArray ())
			{
			lightEntity.Dispose ();
			}

		_scheduledRefreshGate.Dispose ();
		_managedDeviceCacheWriteGate.Dispose ();
		_refreshGate.Dispose ();
		_disposed = true;
		}

	private void CancelRuntimeRefreshes ()
		{
		LogInfo ($"CancelRuntimeRefreshes: runtimeDiscoveryStarted={_runtimeDiscoveryStarted}, discoveryTaskActive={_discoveryRefreshTask is not null}.");
		_runtimeCancellationSource.Cancel ();
		_discoveryRefreshTask = null;
		}

	#if DEBUG
	private void RegisterKasaClientDebugListener ()
		{
		// KasaTapoClient routes its internal diagnostics (discovery, TPAP handshake, etc.) through
		// System.Diagnostics.Debug.WriteLine, which is compiled out entirely in Release builds. In
		// DEBUG builds we add a listener so those messages flow into the driver's own log instead of
		// only being visible through OutputDebugString/attached debuggers.
		Debug.Listeners.Add (new ForwardingTraceListener (message => LogInfo ($"KasaClientDiagnostic: {message}")));
		}

	private sealed class ForwardingTraceListener : TraceListener
		{
		private readonly Action<string> _forward;
		private readonly StringBuilder _pendingLine = new ();

		public ForwardingTraceListener (Action<string> forward)
			{
			_forward = forward;
			}

		public override void Write (string? message)
			{
			if (message is not null)
				{
				_pendingLine.Append (message);
				}
			}

		public override void WriteLine (string? message)
			{
			_pendingLine.Append (message);
			_forward (_pendingLine.ToString ());
			_pendingLine.Clear ();
			}
		}
	#endif

	private ConfigurationItemErrors? ApplyConfigurationItems (
		DataDrivenConfigurationController.ApplyConfigurationAction action,
		string stepId,
		IDictionary<string, DriverEntityValue?> values)
		{
		PlatformSharedConfigurationSnapshot previousConfiguration = _sharedConfiguration.Snapshot ();
		ConfigurationApplyMode applyMode = ResolveConfigurationApplyMode (action);
		LogInfo ($"ApplyConfigurationItems: action='{action}', stepId='{stepId}', mode={applyMode}.");

		ApplyValues (values);

		bool hasUserName = !string.IsNullOrWhiteSpace (_userName);
		bool hasPassword = !string.IsNullOrWhiteSpace (_password);
		if (hasUserName != hasPassword)
			{
			const string tapoCredentialError = "Tapo user name and password must both be provided, or both be left blank.";
			return new ConfigurationItemErrors (
				new Dictionary<string, string>
					{
					["UserName"] = tapoCredentialError,
					["Password"] = tapoCredentialError
					},
				tapoCredentialError);
			}

		if (!TryParseDiscoveryTimeout (_discoveryTimeoutSeconds, out var timeout, out var timeoutError))
			{
			return new ConfigurationItemErrors (
				new Dictionary<string, string>
					{
					["DiscoveryTimeoutSeconds"] = timeoutError
					},
				timeoutError);
			}

		_discoveryTimeoutSeconds = ((int)timeout.TotalSeconds).ToString (CultureInfo.InvariantCulture);

		if (!TryParsePollInterval (_lightPollIntervalSeconds, DefaultLightPollInterval, out TimeSpan lightPollInterval, out string lightPollError))
			{
			return new ConfigurationItemErrors (
				new Dictionary<string, string>
					{
					["LightPollIntervalSeconds"] = lightPollError
					},
				lightPollError);
			}

		_lightPollIntervalSeconds = ((int)lightPollInterval.TotalSeconds).ToString (CultureInfo.InvariantCulture);

		if (!TryParsePollInterval (_sensorPollIntervalSeconds, DefaultSensorPollInterval, out TimeSpan sensorPollInterval, out string sensorPollError))
			{
			return new ConfigurationItemErrors (
				new Dictionary<string, string>
					{
					["SensorPollIntervalSeconds"] = sensorPollError
					},
				sensorPollError);
			}

		_sensorPollIntervalSeconds = ((int)sensorPollInterval.TotalSeconds).ToString (CultureInfo.InvariantCulture);

		_sharedConfiguration.Update (
			_userName,
			_password,
			timeout,
			_enableLightPolling,
			lightPollInterval,
			sensorPollInterval,
			_treatPlugsAsLights);

		PlatformSharedConfigurationSnapshot currentConfiguration = _sharedConfiguration.Snapshot ();
		LogInfo ($"ApplyConfigurationItems resolved configuration: mode={applyMode}, timeoutSeconds={currentConfiguration.DiscoveryTimeout.TotalSeconds:0.###}, enableLightPolling={currentConfiguration.EnableLightPolling}, lightPollIntervalSeconds={currentConfiguration.LightPollInterval.TotalSeconds:0.###}, sensorPollIntervalSeconds={currentConfiguration.SensorPollInterval.TotalSeconds:0.###}, treatPlugsAsLights={currentConfiguration.TreatPlugsAsLights}, hasTapoCredentials={HasTapoCredentials ()}.");

		switch (applyMode)
			{
			case ConfigurationApplyMode.InitialConfiguration:
			case ConfigurationApplyMode.SavedConfiguration:
				_managedDevices.Clear ();
					_managedDeviceCacheMetadata.Clear ();
					_resolvedDeviceNames.Clear ();
					LoadManagedDeviceCacheIntoMemory ();
				PublishCachedChildControllers (currentConfiguration);
				NotifyPropertyChanged ("platform:managedDevices", CreateValueForEntries (ManagedDevices));
				SetOnline (false);
				SetReady (false);
					_ = StartCachedIdentityResolutionsAsync (currentConfiguration);
				_ = ScheduleRefreshAsync ();
				break;

			default:
				bool credentialsChanged = !string.Equals (previousConfiguration.UserName, currentConfiguration.UserName, StringComparison.Ordinal)
					|| !string.Equals (previousConfiguration.Password, currentConfiguration.Password, StringComparison.Ordinal);
				bool discoveryTimeoutChanged = previousConfiguration.DiscoveryTimeout != currentConfiguration.DiscoveryTimeout;
				bool connectionInputsChanged = credentialsChanged
					|| discoveryTimeoutChanged;
				bool discoveryInputsChanged = connectionInputsChanged
					|| previousConfiguration.TreatPlugsAsLights != currentConfiguration.TreatPlugsAsLights;

				if (connectionInputsChanged)
					{
					RefreshExistingDeviceConfigurations (currentConfiguration);
					}

				foreach (IKasaManagedLightEntity lightEntity in _lightEntities.Values)
					{
					lightEntity.ApplyRuntimeConfiguration (previousConfiguration, currentConfiguration);
					}

				if (discoveryInputsChanged)
					{
					_ = ScheduleRefreshAsync ();
					}
				break;
			}

		return null;
		}

	private void ApplyValues (IDictionary<string, DriverEntityValue?> values)
		{
		bool hasUserNameValue = values.TryGetValue ("UserName", out var userNameValue) && userNameValue.HasValue;
		bool hasPasswordValue = values.TryGetValue ("Password", out var passwordValue) && passwordValue.HasValue;

		string? configuredUserName = hasUserNameValue
			? userNameValue!.Value.GetValue<string> ()?.Trim ()
			: null;
		string? configuredPassword = hasPasswordValue
			? passwordValue!.Value.GetValue<string> ()
			: null;

		LogInfo ($"ApplyValues credential shape: userNamePresent={hasUserNameValue}, userNameState={DescribeConfiguredValueState (configuredUserName)}, passwordPresent={hasPasswordValue}, passwordState={DescribeConfiguredValueState (configuredPassword)}.");

		if (hasUserNameValue)
			{
			_userName = configuredUserName ?? string.Empty;
			}

		if (hasPasswordValue)
			{
			_password = configuredPassword ?? string.Empty;
			}

		if (values.TryGetValue ("DiscoveryTimeoutSeconds", out var timeoutValue) && timeoutValue.HasValue)
			{
			_discoveryTimeoutSeconds = timeoutValue.Value.GetValue<string> ()?.Trim () ?? _discoveryTimeoutSeconds;
			}

		if (values.TryGetValue ("EnableLightPolling", out var enableLightPollingValue) && enableLightPollingValue.HasValue)
			{
			_enableLightPolling = enableLightPollingValue.Value.GetValue<bool> ();
			}

		if (values.TryGetValue ("LightPollIntervalSeconds", out var lightPollIntervalValue) && lightPollIntervalValue.HasValue)
			{
			_lightPollIntervalSeconds = lightPollIntervalValue.Value.GetValue<string> ()?.Trim () ?? _lightPollIntervalSeconds;
			}

		if (values.TryGetValue ("SensorPollIntervalSeconds", out var sensorPollIntervalValue) && sensorPollIntervalValue.HasValue)
			{
			_sensorPollIntervalSeconds = sensorPollIntervalValue.Value.GetValue<string> ()?.Trim () ?? _sensorPollIntervalSeconds;
			}

		if (values.TryGetValue ("TreatPlugsAsLights", out var treatPlugsValue) && treatPlugsValue.HasValue)
			{
			_treatPlugsAsLights = treatPlugsValue.Value.GetValue<bool> ();
			}
		}

	private static string DescribeConfiguredValueState (string? value)
		{
		if (value is null)
			{
			return "null";
			}

		return value.Length == 0
			? "empty"
			: "non-empty";
		}

	private async Task RefreshPlatformAsync (CancellationToken cancellationToken)
		{
		bool isInitialLoad = _initialDiscoveryLoadPending;
		bool isFastRefreshPhase = _initialDiscoveryLoadPending || _initialShortRefreshPending || !_normalRemovalRefreshPhaseReached;
		bool allowRemovals = !_initialMaterializationStageActive && _normalRemovalRefreshPhaseReached;
		_runtimeDiscoveryStarted = true;
		var timeout = _sharedConfiguration.DiscoveryTimeout;
		var credentials = CreateCredentials ();
		bool hasTapoCredentials = HasTapoCredentials ();

		IReadOnlyList<DiscoveryResult> discoveredDevices = await DiscoverDevicesAsync (timeout, isInitialLoad, cancellationToken).ConfigureAwait (false);
		LogInfo ($"RefreshPlatformAsync: timeoutSeconds={timeout.TotalSeconds:0.###}, discoveredDeviceCount={discoveredDevices.Count}, hasTapoCredentials={hasTapoCredentials}.");

		if (discoveredDevices.Count == 0)
			{
			LogInfo ("RefreshPlatformAsync: discovery returned 0 devices; treating pass as a no-op and retrying in 5 seconds without mutating discovery state.");
			RestartDiscoveryRefreshLoop (InitialDiscoveryRefreshInterval);
			return;
			}

		foreach (DiscoveryResult discoveredDevice in discoveredDevices)
			{
			LogInfo ($"Discovery result: host='{discoveredDevice.Host}', type={discoveredDevice.DeviceType}, alias='{discoveredDevice.Alias ?? "<null>"}', model='{discoveredDevice.Model ?? "<null>"}', deviceId='{discoveredDevice.DeviceId ?? "<null>"}'.");
			}
		DiscoveryResult[] rawStrips = discoveredDevices.Where (result => result.DeviceType == KasaDeviceType.Strip).ToArray ();

		if (rawStrips.Length > 0)
			{
			foreach (DiscoveryResult strip in rawStrips)
				{
				LogInfo ($"Raw Strip candidate '{ResolveDiscoveryName (strip)}' host='{strip.Host}' model='{strip.Model ?? "<null>"}' deviceId='{strip.DeviceId ?? "<null>"}'.");
				}
			}
		_deviceConfigurations.Clear ();
		_discoveryResults.Clear ();
		List<string>? controllersToRemove = null;
		var activeControllerIds = new HashSet<string> (StringComparer.OrdinalIgnoreCase);
		var descriptorsByControllerId = new Dictionary<string, ManagedLightDescriptor> (StringComparer.OrdinalIgnoreCase);
		var discoveryErrors = new List<string> ();
		var pendingMaterializations = new List<PendingMaterialization> ();
		var discoveredControllerIds = new HashSet<string> (StringComparer.OrdinalIgnoreCase);

		foreach (DiscoveryResult discoveryResult in discoveredDevices.OrderBy (ResolveDiscoveryName, StringComparer.OrdinalIgnoreCase))
			{
			string controllerId = CreateControllerId (discoveryResult);
			_discoveryResults[controllerId] = discoveryResult;

			DeviceConfiguration configuration = CreateDeviceConfiguration (discoveryResult, credentials, timeout);
			_deviceConfigurations[controllerId] = configuration;

			if (discoveryResult.DeviceId is string discoveryDeviceId && !string.IsNullOrWhiteSpace (discoveryDeviceId))
				{
				_discoveryResults[discoveryDeviceId] = discoveryResult;
				_deviceConfigurations[discoveryDeviceId] = configuration;
				}

			if (!IsSupportedLightDeviceType (discoveryResult.DeviceType, _sharedConfiguration.TreatPlugsAsLights))
				{
				LogInfo ($"Skipping unsupported discovery result: host='{discoveryResult.Host}', type={discoveryResult.DeviceType}, alias='{discoveryResult.Alias ?? "<null>"}', model='{discoveryResult.Model ?? "<null>"}', deviceId='{discoveryResult.DeviceId ?? "<null>"}'.");
				continue;
				}

			try
				{
				if (IsTapoDiscoveryResult (discoveryResult) && string.IsNullOrWhiteSpace (discoveryResult.Alias))
					{
					string resolvedDiscoveryName = ResolveManagedDeviceName (controllerId, ResolveDiscoveryName (discoveryResult), discoveryResult.DeviceId, discoveryResult.Host);
					if (IsResolvedFriendlyDeviceName (controllerId, resolvedDiscoveryName, discoveryResult.DeviceId, discoveryResult.Host))
						{
						LogInfo ($"EnrichDiscoveryResultAliasAsync: skipped one-shot alias fetch for controllerId='{controllerId}', host='{discoveryResult.Host}' because the resolved discovery name '{resolvedDiscoveryName}' is already friendly/publishable.");
						}
					else if (_lightEntities.ContainsKey (controllerId))
						{
						// A materialized light entity already exists for this controller and will perform
						// its own startup/reconnect (KasaLightEntity.InitializeStartupAsync), which acquires
						// the SAME per-controller connection gate (GetConnectionGate) and resolves the alias
						// itself via UpdateDescriptorFromConnectedDevice once connected. Starting a redundant
						// background alias-enrichment connect here would compete for that gate: the
						// enrichment connect can take up to DiscoveryTimeout+2s (~10s) to complete or time
						// out, while the entity's own startup attempt only allows 8 seconds
						// (StartupConnectTimeout) before canceling and backing off. With discovery refresh
						// re-triggering this every ~10 seconds during the fast-refresh startup phase, the
						// entity's connect attempts were repeatedly starved of the gate and canceled,
						// producing a multi-minute delay before devices actually connected after a reload.
						LogInfo ($"EnrichDiscoveryResultAliasAsync: skipped one-shot alias fetch for controllerId='{controllerId}', host='{discoveryResult.Host}' because a light entity already exists and will resolve its own alias via its startup connect.");
						}
					else
						{
						_ = StartAliasEnrichmentAsync (discoveryResult, configuration);
						}
					}

				foreach (ManagedLightDescriptor descriptor in CreateManagedLightDescriptors (discoveryResult))
					{
					_knownDescriptors[descriptor.ControllerId] = descriptor;
					discoveredControllerIds.Add (descriptor.ControllerId);
					activeControllerIds.Add (descriptor.ControllerId);
					_pendingRemovalMissCounts.Remove (descriptor.ControllerId);

					descriptorsByControllerId[descriptor.ControllerId] = descriptor;

					if (_lightEntities.TryGetValue (descriptor.ControllerId, out IKasaManagedLightEntity? existingLightEntity))
						{
						existingLightEntity.UpdateDescriptor (descriptor, configuration);
						existingLightEntity.UpdateConfiguration (configuration);

						// Once a child has been configured/activated, do not re-derive its configured
						// state from a point-in-time PeekStatus() snapshot on every rediscovery pass.
						// Doing so could transiently observe a non-Running status (a race, not an actual
						// deactivation) and tear down an otherwise healthy connection, leaving the device
						// disconnected until an unrelated status-changed event happened to fire again.
						// Only newly-discovered/never-configured children need their configured state
						// derived from the child configuration controller here.
						if (!_configuredChildControllerIds.Contains (descriptor.ControllerId))
							{
							bool childConfigurationRunning = _childControllers.TryGetValue (descriptor.ControllerId, out ConfigurableDriverEntity? existingController)
								&& IsChildConfigurationControllerRunning (existingController);
							if (childConfigurationRunning)
								{
								_configuredChildControllerIds.Add (descriptor.ControllerId);
								_inUseChildControllerIds.Add (descriptor.ControllerId);
								}

							existingLightEntity.SetConfigured (childConfigurationRunning, "rediscovery-existing-child");
							}

						if (!descriptor.AwaitingConnectedIdentity)
							{
							HandleManagedLightDescriptorNameChanged (descriptor);
							}
						continue;
						}

					if (_lightEntities.ContainsKey (descriptor.ControllerId))
						{
						continue;
						}

					if (IsMaterializationInFlight (descriptor.ControllerId))
						{
						LogInfo ($"Skipping materialization queue for controllerId='{descriptor.ControllerId}' because materialization is already queued or in progress.");
						continue;
						}

					if (isInitialLoad)
						{
						AddInitialManagedDeviceEntry (descriptor);

						TryMarkMaterializationInFlight (descriptor.ControllerId);
						pendingMaterializations.Add (new PendingMaterialization (
							discoveryResult,
							descriptor,
							configuration,
							false));
						LogInfo ($"Queued pending materialization for controllerId='{descriptor.ControllerId}', host='{descriptor.Host}', type={descriptor.DiscoveredDeviceType}, name='{descriptor.Name}', model='{descriptor.ModelName}', serial='{descriptor.SerialNumber}'.");
						continue;
						}

					if (HasManagedDeviceEntry (descriptor.ControllerId))
						{
						LogInfo ($"Discovered existing managed-device entry for controllerId='{descriptor.ControllerId}' without successful materialization; requeueing materialization attempt.");
						}
					else
						{
						LogInfo ($"Discovered unmanaged controllerId='{descriptor.ControllerId}' on non-initial refresh; attempting managed-device add before materialization. name='{descriptor.Name}', model='{descriptor.ModelName}', serial='{descriptor.SerialNumber}'.");
						if (!PublishManagedDeviceAddition (
							descriptor.ControllerId,
							descriptor.Name,
							descriptor.ModelName,
							descriptor.SerialNumber,
							descriptorsByControllerId))
							{
							LogInfo ($"Skipping materialization scheduling for controllerId='{descriptor.ControllerId}' because managed-device add did not succeed.");
							continue;
							}
						}

					TryMarkMaterializationInFlight (descriptor.ControllerId);
					pendingMaterializations.Add (new PendingMaterialization (
						discoveryResult,
						descriptor,
						configuration,
						false));
					LogInfo ($"Queued pending materialization for controllerId='{descriptor.ControllerId}', host='{descriptor.Host}', type={descriptor.DiscoveredDeviceType}, name='{descriptor.Name}', model='{descriptor.ModelName}', serial='{descriptor.SerialNumber}'.");

					}
				}
			catch (Exception ex)
				{
				discoveryErrors.Add ($"{ResolveDiscoveryName (discoveryResult)}: {ex.Message}");
				LogError ($"Failed to materialize discovered device '{ResolveDiscoveryName (discoveryResult)}': {ex}");
				}
			}

		if (isInitialLoad)
			{
			_initialDiscoveryLoadPending = false;
			}

		SetOnline (true);
		SetReady (true);

		var previousDiscoveredControllerIds = new HashSet<string> (_previousDiscoveredControllerIds, StringComparer.OrdinalIgnoreCase);
		int previousDiscoveredCount = previousDiscoveredControllerIds.Count;
		bool hasAddedDiscoveryChange = discoveredControllerIds.Except (previousDiscoveredControllerIds, StringComparer.OrdinalIgnoreCase).Any ();
		bool hasRemovedDiscoveryChange = previousDiscoveredControllerIds.Except (discoveredControllerIds, StringComparer.OrdinalIgnoreCase).Any ();
		bool ignoreRemovalSignalsThisPass = isFastRefreshPhase && hasRemovedDiscoveryChange;

		if (hasAddedDiscoveryChange)
			{
			_fastRefreshShrinkRetryCount = 0;
			}
		else if (ignoreRemovalSignalsThisPass)
			{
			_fastRefreshShrinkRetryCount++;
			if (_fastRefreshShrinkRetryCount > MAX_FAST_REFRESH_SHRINK_RETRIES)
				{
				ignoreRemovalSignalsThisPass = false;
				LogInfo ($"RefreshPlatformAsync: fast-refresh shrink retry limit ({MAX_FAST_REFRESH_SHRINK_RETRIES}) reached for this missing-device streak; allowing the normal removal-refresh phase to proceed instead of continuing indefinite fast retries.");
				}
			}
		else
			{
			_fastRefreshShrinkRetryCount = 0;
			}

		if (ignoreRemovalSignalsThisPass)
			{
			_previousDiscoveredControllerIds.UnionWith (discoveredControllerIds);
			}
		else
			{
			_previousDiscoveredControllerIds.Clear ();
			_previousDiscoveredControllerIds.UnionWith (discoveredControllerIds);
			}

		TimeSpan nextRefreshInterval = DefaultDiscoveryRefreshInterval;
		if (isInitialLoad)
			{
			nextRefreshInterval = InitialDiscoveryRefreshInterval;
			}
		else if (_initialShortRefreshPending)
			{
			nextRefreshInterval = InitialDiscoveryRefreshInterval;
			_initialShortRefreshPending = false;
			}
		else if (hasAddedDiscoveryChange)
			{
			nextRefreshInterval = InitialDiscoveryRefreshInterval;
			}
		else if (ignoreRemovalSignalsThisPass)
			{
			nextRefreshInterval = InitialDiscoveryRefreshInterval;
			LogInfo ($"RefreshPlatformAsync: ignoring shrinking fast-refresh pass from {previousDiscoveredCount} discovered devices to {discoveredControllerIds.Count}; removals remain deferred until the 5-minute two-strike cycle, and the fast-refresh interval is retained so still-missing devices keep being retried quickly.");
			}
		else
			{
			_normalRemovalRefreshPhaseReached = true;
			}

		if (pendingMaterializations.Count > 0)
			{
			CompletePendingMaterializationsAsync (
				pendingMaterializations,
				discoveryErrors,
				cancellationToken,
				nextRefreshInterval);
			}
		else
			{
			if (isInitialLoad)
				{
				NotifyPropertyChanged ("platform:managedDevices", CreateValueForEntries (ManagedDevices));
				LogInfo ($"Managed-device publish after initial materialization skipped/no-op: count={_managedDevices.Count}, controllerIds=[{string.Join (", ", _managedDevices.Keys.OrderBy (key => key, StringComparer.OrdinalIgnoreCase))}].");
				}

			RestartDiscoveryRefreshLoop (nextRefreshInterval);
			}

		if (allowRemovals)
			{
			foreach (string existingControllerId in _childControllers.Keys.Except (activeControllerIds, StringComparer.OrdinalIgnoreCase).ToArray ())
				{
				if (_inUseChildControllerIds.Contains (existingControllerId))
					{
					_pendingRemovalMissCounts.Remove (existingControllerId);
					LogInfo ($"Managed-device removal suppressed for in-use controllerId='{existingControllerId}' because the child configuration callback marked it active for this driver instance.");
					continue;
					}

				int missedCount = _pendingRemovalMissCounts.TryGetValue (existingControllerId, out int currentMissedCount)
					? currentMissedCount + 1
					: 1;
				_pendingRemovalMissCounts[existingControllerId] = missedCount;

				if (missedCount < MANAGED_DEVICE_REMOVAL_MISS_THRESHOLD)
					{
					LogInfo ($"Managed-device removal deferred for controllerId='{existingControllerId}' after miss {missedCount}/{MANAGED_DEVICE_REMOVAL_MISS_THRESHOLD} on the normal 5-minute refresh cycle.");
					continue;
					}

				if (!PublishManagedDeviceRemoval (existingControllerId))
					{
					continue;
					}

				if (_lightEntities.TryGetValue (existingControllerId, out IKasaManagedLightEntity? removedEntity))
					{
					removedEntity.Stop ();
					removedEntity.Dispose ();
					}

				ClearChildRuntimeState (existingControllerId, "managed-device-removal");
				controllersToRemove ??= new List<string> ();
				controllersToRemove.Add (existingControllerId);
				}
			}
		else
			{
			if (isFastRefreshPhase)
				{
				foreach (string existingControllerId in _childControllers.Keys.Except (activeControllerIds, StringComparer.OrdinalIgnoreCase))
					{
					LogInfo ($"Ignoring first missed discovery for controllerId='{existingControllerId}' during fast-refresh phase.");
					}
				}

			LogInfo ("RefreshPlatformAsync: removals suppressed during startup/fast-refresh stage; discovery remains additive only.");
			}

		if ((controllersToRemove?.Count ?? 0) > 0)
			{
			UpdateSubControllers (null, controllersToRemove);
			}

		if (_managedDevices.Count > 0)
			{
			return;
			}
		}

	private void CompletePendingMaterializationsAsync (
		List<PendingMaterialization> pendingMaterializations,
		List<string> discoveryErrors,
		CancellationToken cancellationToken,
		TimeSpan nextRefreshInterval)
		{
		_ = Task.Run (async () =>
			{
				List<ConfigurableDriverEntity>? controllersToAdd = null;
				bool managedDevicesChanged = false;
				var materializationTasks = pendingMaterializations
					.Select (pendingMaterialization => new
						{
						PendingMaterialization = pendingMaterialization,
						Task = Task.Run (() => CreateManagedLightEntity (pendingMaterialization.Descriptor, pendingMaterialization.Configuration), cancellationToken)
						})
					.ToArray ();

				try
					{
					foreach (var materialization in materializationTasks)
						{
						PendingMaterialization pendingMaterialization = materialization.PendingMaterialization;
						string controllerId = pendingMaterialization.Descriptor.ControllerId;
						try
							{
							if (!HasManagedDeviceEntry (controllerId))
								{
								if (!pendingMaterialization.DeferPublicationUntilIdentityResolved)
									{
									LogInfo ($"Discarding async materialization for controllerId='{controllerId}' because no managed-device entry exists before materialization completes.");
									continue;
									}
								}

							IKasaManagedLightEntity lightEntity = await materialization.Task.ConfigureAwait (false);
							LogInfo ($"Async materialization completed for controllerId='{controllerId}', deviceName='{lightEntity.DeviceName}', model='{lightEntity.ModelName}', serial='{lightEntity.SerialNumber}'.");

							bool hasManagedDeviceEntry = HasManagedDeviceEntry (controllerId);
							if (!hasManagedDeviceEntry && !pendingMaterialization.DeferPublicationUntilIdentityResolved)
								{
								LogInfo ($"Discarding async materialized controller for controllerId='{controllerId}' because the managed-device entry was removed while materialization was in progress.");
								lightEntity.Stop ();
								continue;
								}

							if (!hasManagedDeviceEntry && !TryPublishDeferredManagedDevice (pendingMaterialization.Descriptor))
								{
								LogInfo ($"Deferred publication still pending for controllerId='{controllerId}' after materialization; waiting for connected identity callback.");
								}
							else if (!hasManagedDeviceEntry)
								{
								managedDevicesChanged = true;
								}

							lightEntity.SetConfigured (_configuredChildControllerIds.Contains (controllerId), "materialization-complete");
							LoggingDriverConfigurationController childConfigurationController = CreateChildConfigurationController (pendingMaterialization.Descriptor);
							var controller = new ConfigurableDriverEntity (controllerId, (ReflectedAttributeDriverEntity)lightEntity, childConfigurationController);
							_lightEntities[controllerId] = lightEntity;
							_childControllers[controllerId] = controller;
							_childConfigurationControllers[controllerId] = childConfigurationController;
							LogChildPublicationState ("Async materialization stored child publication state", controllerId);
							if (HasManagedDeviceEntry (controllerId))
								{
								controllersToAdd ??= new List<ConfigurableDriverEntity> ();
								controllersToAdd.Add (controller);
								LogInfo ($"Async materialization accepted for controllerId='{controllerId}' and controller queued for publication.");
								}
							else
								{
								LogInfo ($"Async materialization accepted for controllerId='{controllerId}' but controller publication remains deferred until identity resolves.");
								}
							}
						catch (Exception ex) when (!(ex is OperationCanceledException))
							{
							discoveryErrors.Add ($"{ResolveDiscoveryName (pendingMaterialization.DiscoveryResult)}: {ex.Message}");
							LogError ($"Failed to materialize discovered device '{ResolveDiscoveryName (pendingMaterialization.DiscoveryResult)}': {ex}");
							}
						finally
							{
							ClearMaterializationInFlight (controllerId);
							}
						}

					if ((controllersToAdd?.Count ?? 0) > 0)
						{
						List<ConfigurableDriverEntity> controllersToPublish = controllersToAdd!;
						foreach (ConfigurableDriverEntity controller in controllersToPublish)
							{
							LogChildPublicationState ("Before UpdateSubControllers async publish", controller.ControllerId);
							}
						UpdateSubControllers (controllersToPublish, null);

						foreach (ConfigurableDriverEntity controller in controllersToPublish)
							{
							LogChildPublicationState ("After UpdateSubControllers async publish", controller.ControllerId);
							if (_lightEntities.TryGetValue (controller.ControllerId, out IKasaManagedLightEntity? lightEntity))
								{
								lightEntity.NotifyChildPublished ();
								}
							}
						}

					if (managedDevicesChanged || pendingMaterializations.Count > 0)
						{
						NotifyPropertyChanged ("platform:managedDevices", CreateValueForEntries (ManagedDevices));
						}

					if (_initialMaterializationStageActive)
						{
						_initialMaterializationStageActive = false;
						LogInfo ("CompletePendingMaterializationsAsync: initial materialization stage completed; future discovery refreshes may remove missing devices.");
						}

					LogInfo ($"Managed-device publish: count={_managedDevices.Count}, controllerIds=[{string.Join (", ", _managedDevices.Keys.OrderBy (key => key, StringComparer.OrdinalIgnoreCase))}].");
					}
				catch (OperationCanceledException)
					{
					LogInfo ("CompletePendingMaterializationsAsync: canceled.");
					}
				catch (Exception ex)
					{
					LogError ($"CompletePendingMaterializationsAsync failed: {ex}");
					}
				finally
					{
					RestartDiscoveryRefreshLoop (nextRefreshInterval);

					if (pendingMaterializations.Count == 0 && _initialMaterializationStageActive)
						{
						_initialMaterializationStageActive = false;
						LogInfo ("CompletePendingMaterializationsAsync: no pending materializations remained; future discovery refreshes may remove missing devices.");
						}
					}
			}, cancellationToken);
		}

	private bool AddInitialManagedDeviceEntry (ManagedLightDescriptor descriptor)
		{
		string controllerId = descriptor.ControllerId;
		string modelName = descriptor.ModelName;
		string serialNumber = descriptor.SerialNumber;
		string name = ResolveManagedDeviceName (controllerId, descriptor.Name, descriptor.DiscoveryDeviceId ?? descriptor.SerialNumber, descriptor.Host);
		LogInfo ($"AddInitialManagedDeviceEntry: controllerId='{controllerId}', descriptorName='{descriptor.Name}', resolvedName='{name}', model='{modelName}', serial='{serialNumber}', host='{descriptor.Host}', awaitingConnectedIdentity={descriptor.AwaitingConnectedIdentity}.");

		if (string.IsNullOrWhiteSpace (name))
			{
			LogInfo ($"Skipping managed-device publish for '{controllerId}' because the resolved device name is blank.");
			return false;
			}

		_managedDevices[controllerId] = CreateManagedDeviceEntry (controllerId, name, modelName, serialNumber);
		_managedDeviceCacheMetadata[controllerId] = CreateManagedDeviceCacheEntry (descriptor, name);
		RememberResolvedDeviceName (controllerId, name, descriptor.DiscoveryDeviceId ?? descriptor.SerialNumber, descriptor.Host);
		PersistManagedDeviceCache ();
		LogInfo ($"Managed-device entry added: controllerId='{controllerId}', name='{name}', model='{modelName}', serial='{serialNumber}'.");
		LogChildPublicationState ("Managed-device entry added state", controllerId);
		return true;
		}

	private PlatformManagedDevice CreateManagedDeviceEntry (string controllerId, string name, string modelName, string serialNumber)
		{
		return new PlatformManagedDevice (
			DeviceUxCategory.Light,
			name,
			TP_LINK_MANUFACTURER,
			modelName,
			serialNumber);
		}

	private ManagedDeviceCacheEntry CreateManagedDeviceCacheEntry (ManagedLightDescriptor descriptor, string name)
		{
		if (!_deviceConfigurations.TryGetValue (descriptor.ControllerId, out DeviceConfiguration? configuration))
			{
			throw new InvalidOperationException ($"Cannot create cache metadata for controllerId='{descriptor.ControllerId}' because no device configuration is available.");
			}

		DeviceConnectionOptions options = configuration.ConnectionOptions;
		DeviceConnectionParameters parameters = options.ConnectionParameters
			?? throw new InvalidOperationException ($"Cannot create cache metadata for controllerId='{descriptor.ControllerId}' because the device connection parameters are unavailable.");
		return new ManagedDeviceCacheEntry
			{
			Immutable = new ManagedDeviceImmutableCacheFields
				{
				ControllerId = descriptor.ControllerId,
				UxCategory = DeviceUxCategory.Light,
				Manufacturer = TP_LINK_MANUFACTURER,
				Model = descriptor.ModelName,
				DiscoveredDeviceType = descriptor.DiscoveredDeviceType,
				ManagedLightKind = descriptor.Kind,
				SerialNumber = descriptor.SerialNumber
				},
			Mutable = new ManagedDeviceMutableCacheFields
				{
				Name = name,
				Host = configuration.Host,
				AwaitingConnectedIdentity = descriptor.AwaitingConnectedIdentity,
				IsConfigured = _configuredChildControllerIds.Contains (descriptor.ControllerId),
				Port = configuration.Port,
				TransportKind = options.TransportKind,
				DeviceFamily = parameters.DeviceFamily,
				EncryptionKind = parameters.EncryptionKind,
				LoginVersion = parameters.LoginVersion,
				UseHttps = parameters.UseHttps,
				HttpPort = parameters.HttpPort,
				UseSsl = options.UseSsl,
				UseDefaultCredentials = options.UseDefaultCredentials,
				DefaultCredentialProfile = options.DefaultCredentialProfile,
				ApplicationPath = options.ApplicationPath ?? string.Empty,
				UseSecurePassthrough = options.UseSecurePassthrough,
				TpapKeepAliveIntervalMs = options.TpapKeepAliveInterval.HasValue
					? (long?)options.TpapKeepAliveInterval.Value.TotalMilliseconds
					: null
				}
			};
		}

	private void PublishManagedDeviceEntryUpdate (string controllerId, string context)
		{
		if (!_managedDevices.TryGetValue (controllerId, out PlatformManagedDevice? existingEntry))
			{
			LogInfo ($"PublishManagedDeviceEntryUpdate: no managed-device entry exists for controllerId='{controllerId}', context='{context}'; this is expected after a child has been added to a room.");
			return;
			}

		PlatformManagedDevice updatedEntry = CreateManagedDeviceEntry (controllerId, existingEntry.Name, existingEntry.Model, existingEntry.SerialNumber);
		_managedDevices[controllerId] = updatedEntry;
		NotifyManagedDevicesSnapshotChanged ();
		LogInfo ($"PublishManagedDeviceEntryUpdate: published managed-device entry update for controllerId='{controllerId}', name='{updatedEntry.Name}', model='{updatedEntry.Model}', serial='{updatedEntry.SerialNumber}', configured={_configuredChildControllerIds.Contains (controllerId)}, context='{context}'.");
		}

	private void NotifyManagedDevicesSnapshotChanged ()
		{
		NotifyPropertyChanged ("platform:managedDevices", CreateValueForEntries (ManagedDevices));
		}

	private bool HasManagedDeviceEntry (string controllerId)
		{
		return _managedDevices.ContainsKey (controllerId);
		}

	private void PublishCachedChildControllers (PlatformSharedConfigurationSnapshot configuration)
		{
		List<ConfigurableDriverEntity>? controllersToAdd = null;
		foreach (ManagedDeviceCacheEntry entry in _managedDeviceCacheMetadata.Values.ToArray ())
			{
			if (_childControllers.ContainsKey (entry.ControllerId))
				{
				continue;
				}

			if (!TryCreateCachedDescriptorAndConfiguration (entry, configuration, out ManagedLightDescriptor? descriptor, out DeviceConfiguration? deviceConfiguration))
				{
				continue;
				}

			_knownDescriptors[descriptor.ControllerId] = descriptor;
			_deviceConfigurations[descriptor.ControllerId] = deviceConfiguration;
			IKasaManagedLightEntity lightEntity = CreateManagedLightEntity (descriptor, deviceConfiguration);
			lightEntity.SetConfigured (_configuredChildControllerIds.Contains (descriptor.ControllerId), "cached-child-controller-publication");

			LoggingDriverConfigurationController childConfigurationController = CreateChildConfigurationController (descriptor);
			var controller = new ConfigurableDriverEntity (descriptor.ControllerId, (ReflectedAttributeDriverEntity)lightEntity, childConfigurationController);
			_lightEntities[descriptor.ControllerId] = lightEntity;
			_childControllers[descriptor.ControllerId] = controller;
			_childConfigurationControllers[descriptor.ControllerId] = childConfigurationController;
			LogChildPublicationState ("Cached child controller staged for publication", descriptor.ControllerId);
			controllersToAdd ??= new List<ConfigurableDriverEntity> ();
			controllersToAdd.Add (controller);
			LogInfo ($"Cached child controller published early for controllerId='{descriptor.ControllerId}', name='{descriptor.Name}', model='{descriptor.ModelName}', host='{descriptor.Host}'.");
			}

		if ((controllersToAdd?.Count ?? 0) > 0)
			{
			List<ConfigurableDriverEntity> controllersToPublish = controllersToAdd!;
			foreach (ConfigurableDriverEntity controller in controllersToPublish)
				{
				LogChildPublicationState ("Before UpdateSubControllers cached publish", controller.ControllerId);
				}
			UpdateSubControllers (controllersToPublish, null);
			foreach (ConfigurableDriverEntity controller in controllersToPublish)
				{
				LogChildPublicationState ("After UpdateSubControllers cached publish", controller.ControllerId);
				if (_lightEntities.TryGetValue (controller.ControllerId, out IKasaManagedLightEntity? lightEntity))
					{
					lightEntity.NotifyChildPublished ();
					}
				}
			}
		}

	private bool TryCreateCachedDescriptorAndConfiguration (
		ManagedDeviceCacheEntry entry,
		PlatformSharedConfigurationSnapshot configuration,
		out ManagedLightDescriptor descriptor,
		out DeviceConfiguration deviceConfiguration)
		{
		descriptor = null!;
		deviceConfiguration = null!;

		if (string.IsNullOrWhiteSpace (entry.ControllerId)
			|| string.IsNullOrWhiteSpace (entry.Host)
			|| entry.Port <= 0)
			{
			LogInfo ($"Cached child controller skipped for controllerId='{entry.ControllerId}' because reconnect metadata is incomplete: host='{entry.Host}', port={entry.Port}.");
			return false;
			}

		KasaDeviceType deviceType = ResolveCachedDeviceType (entry);
		ManagedLightKind lightKind = ResolveCachedLightKind (entry, deviceType);
		string serialNumber = ResolveCachedSerialNumber (entry);

		descriptor = new ManagedLightDescriptor (
			entry.ControllerId,
			entry.Host,
			deviceType,
			entry.Name,
			entry.Model,
			serialNumber,
			lightKind,
			entry.AwaitingConnectedIdentity,
			serialNumber);

		DeviceCredentials? credentials = string.IsNullOrWhiteSpace (configuration.UserName) || string.IsNullOrWhiteSpace (configuration.Password)
			? null
			: new DeviceCredentials (configuration.UserName, configuration.Password);

		DeviceConnectionParameters connectionParameters = new (
			entry.DeviceFamily,
			entry.EncryptionKind,
			entry.LoginVersion,
			entry.UseHttps,
			entry.HttpPort);

		DeviceConnectionOptions connectionOptions = new (
			entry.TransportKind,
			connectionParameters,
			entry.UseSsl,
			entry.UseDefaultCredentials,
			entry.DefaultCredentialProfile,
			entry.ApplicationPath ?? string.Empty,
			entry.UseSecurePassthrough,
			entry.TpapKeepAliveIntervalMs.HasValue ? TimeSpan.FromMilliseconds (entry.TpapKeepAliveIntervalMs.Value) : null);

		deviceConfiguration = new DeviceConfiguration (
			entry.Host,
			entry.Port,
			credentials,
			connectionOptions,
			configuration.DiscoveryTimeout);

		return true;
		}

	private static string ResolveCachedSerialNumber (ManagedDeviceCacheEntry entry)
		{
		if (!string.IsNullOrWhiteSpace (entry.SerialNumber)
			&& !LooksLikeModelIdentifier (entry.SerialNumber, entry.Model))
			{
			return entry.SerialNumber;
			}

		const string controllerPrefix = "device_";
		if (!string.IsNullOrWhiteSpace (entry.ControllerId)
			&& entry.ControllerId.StartsWith (controllerPrefix, StringComparison.OrdinalIgnoreCase)
			&& entry.ControllerId.Length > controllerPrefix.Length)
			{
			return entry.ControllerId.Substring (controllerPrefix.Length).ToUpperInvariant ();
			}

		return !string.IsNullOrWhiteSpace (entry.SerialNumber)
			? entry.SerialNumber
			: entry.ControllerId;
		}

	private static bool LooksLikeModelIdentifier (string? candidate, string? model)
		{
		string candidateValue = candidate ?? string.Empty;
		if (string.IsNullOrWhiteSpace (candidateValue))
			{
			return false;
			}

		string normalizedCandidate = candidateValue.Trim ();
		string normalizedModel = (model ?? string.Empty).Trim ();

		if (!string.IsNullOrWhiteSpace (normalizedModel)
			&& string.Equals (normalizedCandidate, normalizedModel, StringComparison.OrdinalIgnoreCase))
			{
			return true;
			}

		return normalizedCandidate.Contains ("(")
			&& normalizedCandidate.Contains (")")
			&& normalizedCandidate.IndexOfAny (new[] { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9' }) >= 0;
		}

	private static KasaDeviceType ResolveCachedDeviceType (ManagedDeviceCacheEntry entry)
		{
		if (!string.IsNullOrWhiteSpace (entry.Model))
			{
			string model = entry.Model.Trim ();
			if (model.StartsWith ("KP", StringComparison.OrdinalIgnoreCase)
				|| model.StartsWith ("P", StringComparison.OrdinalIgnoreCase))
				{
				return KasaDeviceType.Plug;
				}

			if (model.StartsWith ("L900", StringComparison.OrdinalIgnoreCase)
				|| model.StartsWith ("KL4", StringComparison.OrdinalIgnoreCase))
				{
				return KasaDeviceType.LightStrip;
				}
			}

		return entry.DiscoveredDeviceType == default
			? KasaDeviceType.Bulb
			: entry.DiscoveredDeviceType;
		}

	private static ManagedLightKind ResolveCachedLightKind (ManagedDeviceCacheEntry entry, KasaDeviceType deviceType)
		{
		if (deviceType == KasaDeviceType.Plug || deviceType == KasaDeviceType.Strip)
			{
			return ManagedLightKind.OnOff;
			}

		return entry.ManagedLightKind;
		}

	private bool IsMaterializationInFlight (string controllerId)
		{
		return _materializationInFlightControllerIds.Contains (controllerId);
		}

	private bool TryMarkMaterializationInFlight (string controllerId)
		{
		return _materializationInFlightControllerIds.Add (controllerId);
		}

	private void ClearMaterializationInFlight (string controllerId)
		{
		_materializationInFlightControllerIds.Remove (controllerId);
		}

	private void LogManagedDeviceSnapshot (string context, IDictionary<string, PlatformManagedDevice> entries)
		{
		LogInfo ($"{context}: entries=[{string.Join (", ", entries.OrderBy (entry => entry.Key, StringComparer.OrdinalIgnoreCase).Select (entry => $"{entry.Key}='{entry.Value.Name}'"))}].");
		}

	private void LogChildPublicationState (string context, string controllerId)
		{
		bool hasManagedDevice = _managedDevices.TryGetValue (controllerId, out PlatformManagedDevice? managedDevice);
		bool hasChildController = _childControllers.TryGetValue (controllerId, out ConfigurableDriverEntity? childController);
		bool hasLightEntity = _lightEntities.TryGetValue (controllerId, out IKasaManagedLightEntity? lightEntity);
		bool isConfigured = _configuredChildControllerIds.Contains (controllerId);
		bool isInUse = _inUseChildControllerIds.Contains (controllerId);

		LogInfo (
			$"{context}: controllerId='{controllerId}', hasManagedDevice={hasManagedDevice}, managedDeviceName='{managedDevice?.Name ?? string.Empty}', managedDeviceModel='{managedDevice?.Model ?? string.Empty}', hasChildController={hasChildController}, childControllerType='{childController?.GetType ().FullName ?? string.Empty}', hasLightEntity={hasLightEntity}, lightEntityType='{lightEntity?.GetType ().FullName ?? string.Empty}', isConfigured={isConfigured}, isInUse={isInUse}.");
		}

	private async Task RefreshPlatformSafelyAsync (CancellationToken cancellationToken)
		{
		LogInfo ("RefreshPlatformSafelyAsync: waiting for refresh gate.");
		await _refreshGate.WaitAsync (cancellationToken).ConfigureAwait (false);
		try
			{
			if (_disposed || cancellationToken.IsCancellationRequested)
				{
				return;
				}

			LogInfo ("RefreshPlatformSafelyAsync: entered refresh execution.");
			await RefreshPlatformAsync (cancellationToken).ConfigureAwait (false);
			LogInfo ("RefreshPlatformSafelyAsync: refresh execution completed.");
			}
		catch (OperationCanceledException)
			{
			LogInfo ("RefreshPlatformSafelyAsync: canceled.");
			}
		catch (Exception ex)
			{
			HandleRefreshFailure (ex, "Discovery failed.");
			}
		finally
			{
			_ = _refreshGate.Release ();
			LogInfo ("RefreshPlatformSafelyAsync: released refresh gate.");
			}
		}

	private async Task ScheduleRefreshAsync ()
		{
		if (_disposed)
			{
			return;
			}

		LogInfo ("ScheduleRefresh: queueing immediate refresh.");
		await Task.Yield ();

		if (_disposed)
			{
			return;
			}

		if (!await _scheduledRefreshGate.WaitAsync (0, _runtimeCancellationSource.Token).ConfigureAwait (false))
			{
			LogInfo ("ScheduleRefresh: ignored because a refresh is already queued or running.");
			return;
			}

		try
			{
			if (_disposed)
				{
				return;
				}

			await RefreshPlatformSafelyAsync (_runtimeCancellationSource.Token).ConfigureAwait (false);
			}
		catch (OperationCanceledException)
			{
			LogInfo ("ScheduleRefresh: canceled before refresh execution.");
			}
		finally
			{
			_scheduledRefreshGate.Release ();
			}
		}

	private ConfigurationApplyMode ResolveConfigurationApplyMode (DataDrivenConfigurationController.ApplyConfigurationAction action)
		{
		string actionName = action.ToString ();
		if (actionName.IndexOf ("Step", StringComparison.OrdinalIgnoreCase) >= 0)
			{
			return ConfigurationApplyMode.InitialConfiguration;
			}

		return _runtimeDiscoveryStarted
			? ConfigurationApplyMode.RealtimeChange
			: ConfigurationApplyMode.SavedConfiguration;
		}

	private static bool IsSupportedLightDeviceType (KasaDeviceType deviceType, bool treatPlugsAsLights)
		{
		if (deviceType is KasaDeviceType.Bulb or KasaDeviceType.LightStrip or KasaDeviceType.Dimmer)
			{
			return true;
			}

		return treatPlugsAsLights && (deviceType == KasaDeviceType.Plug || deviceType == KasaDeviceType.Strip);
		}

	private SemaphoreSlim GetConnectionGate (string controllerId)
		{
		return _connectionGates.GetOrAdd (controllerId, _ => new SemaphoreSlim (1, 1));
		}

	private IKasaManagedLightEntity CreateManagedLightEntity (ManagedLightDescriptor descriptor, DeviceConfiguration configuration)
		{
		return new KasaLightEntity (
			descriptor.ControllerId,
			descriptor,
			configuration,
			GetConnectionGate (descriptor.ControllerId),
			HandleManagedLightDescriptorNameChanged,
			_sharedConfiguration,
			_resources,
			_logger,
			_driverLogId);
		}

	private LoggingDriverConfigurationController CreateChildConfigurationController (ManagedLightDescriptor descriptor)
		{
		var definition = new ConfigurationStepsDefinition
			{
			Items = new List<ConfigurationItemDefinition>
				{
				new ()
					{
					Id = "ActivationMarker",
					Title = "Activate Device",
					Description = "Persistent marker used to observe child configuration install/reload flow.",
					Availability = Crestron.DeviceDrivers.EntityModel.Data.DeviceConfiguration.ConfigurationItemAvailability.Always,
					ValueType = Crestron.DeviceDrivers.EntityModel.Data.DeviceConfiguration.ConfigurationItemValueType.Boolean,
					UsageContext = ConfigurationItemContext.Generic.Prompt,
					DefaultValue = "true",
					Required = true,
					Persistent = true,
					},
				},
			Steps = new List<ConfigurationStepDefinition>
				{
				new ()
					{
					StepId = "Activation",
					Items = new List<string>
						{
						"ActivationMarker",
						},
					},
				},
			FirstStep = "Activation",
			};

		string childDeviceModel = !string.IsNullOrWhiteSpace (descriptor.ModelName)
			? descriptor.ModelName
			: "KasaTapoChild";

		IComponentLogger componentLogger = _componentLogger ?? throw new InvalidOperationException ($"Child configuration controller cannot be created for controllerId='{descriptor.ControllerId}' because the SDK component logger is unavailable.");

		var childConfigurationArgs = new DataDrivenConfigurationControllerArgs (
			childDeviceModel,
			descriptor.ControllerId,
			definition,
			Enumerable.Empty<KeyValuePair<string, IList<ConfigurationItemDefinition>>> (),
			_conditionLookup ?? (_ => null!),
			_transformationLookup ?? (_ => null!),
			componentLogger);

		var controller = new DelegateDataDrivenConfigurationController (
			childConfigurationArgs,
			(action, stepId, values) => ApplyChildConfigurationItems (descriptor.ControllerId, action, stepId, values),
			null,
			null);

		var loggingController = new LoggingDriverConfigurationController (descriptor.ControllerId, controller, LogInfoCore);
		loggingController.StatusChanged += HandleChildConfigurationControllerStatusChanged;
		LogInfo ($"Child configuration controller created for controllerId='{descriptor.ControllerId}', model='{descriptor.ModelName}'.");
		return loggingController;
		}

	private void HandleChildConfigurationControllerStatusChanged (object? sender, StatusChangedEventArgs args)
		{
		string controllerId = args?.ControllerId ?? string.Empty;
		DriverControllerStatus? status = args?.Status;
		LogInfo ($"HandleChildConfigurationControllerStatusChanged: controllerId='{controllerId}', status='{status}'.");

		if (string.IsNullOrWhiteSpace (controllerId)
			|| status != DriverControllerStatus.Running)
			{
			return;
			}

		ActivateChildController (controllerId, "child-controller-status-running");
		}

	private bool IsChildConfigurationControllerRunning (ConfigurableDriverEntity controller)
		{
		return _childConfigurationControllers.TryGetValue (controller.ControllerId, out LoggingDriverConfigurationController? loggingController)
			&& loggingController.PeekStatus () == DriverControllerStatus.Running;
		}

	private ConfigurationItemErrors? ApplyChildConfigurationItems (
		string controllerId,
		DataDrivenConfigurationController.ApplyConfigurationAction action,
		string stepId,
		IDictionary<string, DriverEntityValue?> values)
		{
		LogInfo ($"ApplyChildConfigurationItems: controllerId='{controllerId}', action='{action}', stepId='{stepId}', hasExistingLightEntity={_lightEntities.ContainsKey (controllerId)}, valueKeys=[{string.Join (", ", values.Keys.OrderBy (key => key, StringComparer.OrdinalIgnoreCase))}].");
		if (action == DataDrivenConfigurationController.ApplyConfigurationAction.ClearValues)
			{
			LogInfo ($"ApplyChildConfigurationItems: ClearValues for controllerId='{controllerId}' resets the specified child configuration values to their defaults; no install-state side effects are required for ActivationMarker.");
			return null;
			}

		bool isActivationApply = action == DataDrivenConfigurationController.ApplyConfigurationAction.ApplyStep
			|| (action == DataDrivenConfigurationController.ApplyConfigurationAction.ApplyAll && values.ContainsKey ("ActivationMarker"));

		if (!isActivationApply)
			{
			LogInfo ($"ApplyChildConfigurationItems: ignoring child configuration action for controllerId='{controllerId}', action='{action}', stepId='{stepId}'. Child activation requires ActivationMarker configuration.");
			return null;
			}


		try
			{
			ActivateChildControllerFromConfiguration (controllerId, $"child-config-callback:{action}");
			}
		catch (OperationCanceledException)
			{
			return new ConfigurationItemErrors (
				new Dictionary<string, string>
					{
					["ActivationMarker"] = "Activation was canceled."
					},
				string.Empty);
			}
		catch (Exception ex)
			{
			LogError ($"ApplyChildConfigurationItems: activation failed for controllerId='{controllerId}': {ex}");
			return new ConfigurationItemErrors (
				new Dictionary<string, string>
					{
					["ActivationMarker"] = ex.Message
					},
				string.Empty);
			}

		return null;
		}

	private void ActivateChildControllerFromConfiguration (string controllerId, string context)
		{
		bool addedConfigured = _configuredChildControllerIds.Add (controllerId);
		bool addedInUse = _inUseChildControllerIds.Add (controllerId);
		_pendingRemovalMissCounts.Remove (controllerId);
		MarkChildConfiguredInCache (controllerId);
		PublishManagedDeviceEntryUpdate (controllerId, context);

		LogInfo ($"ActivateChildControllerFromConfiguration: controllerId='{controllerId}', context='{context}', addedConfigured={addedConfigured}, addedInUse={addedInUse}.");
		if (_lightEntities.TryGetValue (controllerId, out IKasaManagedLightEntity? lightEntity))
			{
			lightEntity.SetConfigured (true, context);
			LogInfo ($"ActivateChildControllerFromConfiguration: configured controllerId='{controllerId}', context='{context}'; physical device initialization will continue in the background.");
			}
		else
			{
			LogInfo ($"ActivateChildControllerFromConfiguration: controllerId='{controllerId}' has no materialized light entity yet; activation will occur after discovery/materialization.");
			}
		}

	private async Task ActivateChildControllerAsync (string controllerId, string context, CancellationToken cancellationToken)
		{
		bool addedConfigured = _configuredChildControllerIds.Add (controllerId);
		bool addedInUse = _inUseChildControllerIds.Add (controllerId);
		_pendingRemovalMissCounts.Remove (controllerId);
		MarkChildConfiguredInCache (controllerId);
		PublishManagedDeviceEntryUpdate (controllerId, context);

		LogInfo ($"ActivateChildControllerAsync: controllerId='{controllerId}', context='{context}', addedConfigured={addedConfigured}, addedInUse={addedInUse}.");
		if (_lightEntities.TryGetValue (controllerId, out IKasaManagedLightEntity? lightEntity))
			{
			await lightEntity.SetConfiguredAsync (true, context, cancellationToken).ConfigureAwait (false);
			LogInfo ($"ActivateChildControllerAsync: activated controllerId='{controllerId}', context='{context}'; initial definition and state were prepared during child configuration.");
			}
		else
			{
			LogInfo ($"ActivateChildControllerAsync: controllerId='{controllerId}' has no materialized light entity yet; activation will occur after discovery/materialization.");
			}
		}

	private void ActivateChildController (string controllerId, string context)
		{
		bool addedConfigured = _configuredChildControllerIds.Add (controllerId);
		bool addedInUse = _inUseChildControllerIds.Add (controllerId);
		_pendingRemovalMissCounts.Remove (controllerId);
		MarkChildConfiguredInCache (controllerId);
		PublishManagedDeviceEntryUpdate (controllerId, context);

		LogInfo ($"ActivateChildController: controllerId='{controllerId}', context='{context}', addedConfigured={addedConfigured}, addedInUse={addedInUse}.");
		if (_lightEntities.TryGetValue (controllerId, out IKasaManagedLightEntity? lightEntity))
			{
			lightEntity.SetConfigured (true, context);
			lightEntity.NotifyChildRunning (context);
			LogInfo ($"ActivateChildController: activated controllerId='{controllerId}', context='{context}'; current state snapshot was republished after child reached Running.");
			}
		else
			{
			LogInfo ($"ActivateChildController: controllerId='{controllerId}' has no materialized light entity yet; activation will occur after discovery/materialization.");
			}
		}

	private void MarkChildConfiguredInCache (string controllerId)
		{
		if (!_managedDeviceCacheMetadata.TryGetValue (controllerId, out ManagedDeviceCacheEntry? entry))
			{
			LogInfo ($"MarkChildConfiguredInCache: no managed-device cache metadata exists yet for controllerId='{controllerId}'.");
			return;
			}

		if (entry.IsConfigured)
			{
			return;
			}

		entry.IsConfigured = true;
		PersistManagedDeviceCache ();
		LogInfo ($"MarkChildConfiguredInCache: persisted configured child marker for controllerId='{controllerId}'.");
		}

	private void ClearChildRuntimeState (string controllerId, string context)
		{
		bool removedConfigured = _configuredChildControllerIds.Remove (controllerId);
		bool removedInUse = _inUseChildControllerIds.Remove (controllerId);
		bool removedPendingMiss = _pendingRemovalMissCounts.Remove (controllerId);
		bool removedController = _childControllers.Remove (controllerId);
		bool removedConfigurationController = _childConfigurationControllers.Remove (controllerId);
		bool removedLightEntity = _lightEntities.Remove (controllerId);
		bool removedMaterialization = _materializationInFlightControllerIds.Remove (controllerId);

		if (_managedDeviceCacheMetadata.TryGetValue (controllerId, out ManagedDeviceCacheEntry? entry) && entry.IsConfigured)
			{
			entry.IsConfigured = false;
			PersistManagedDeviceCache ();
			}

		LogInfo ($"ClearChildRuntimeState: controllerId='{controllerId}', context='{context}', removedConfigured={removedConfigured}, removedInUse={removedInUse}, removedPendingMiss={removedPendingMiss}, removedController={removedController}, removedConfigurationController={removedConfigurationController}, removedLightEntity={removedLightEntity}, removedMaterialization={removedMaterialization}; discovery and managed-device availability metadata preserved.");
		}

	private void HandleManagedLightDescriptorNameChanged (ManagedLightDescriptor descriptor)
		{
		LogInfo ($"HandleManagedLightDescriptorNameChanged: controllerId='{descriptor.ControllerId}', incomingName='{descriptor.Name}', incomingKind={descriptor.Kind}, hasExistingEntry={_managedDevices.ContainsKey (descriptor.ControllerId)}, currentCount={_managedDevices.Count}.");
		_connectedIdentityResolvedControllerIds.Add (descriptor.ControllerId);
		RememberResolvedDeviceName (descriptor.ControllerId, descriptor.Name, descriptor.DiscoveryDeviceId ?? descriptor.SerialNumber, descriptor.Host);

		if (!_managedDevices.ContainsKey (descriptor.ControllerId))
			{
			if (!TryPublishDeferredManagedDevice (descriptor))
				{
				LogInfo ($"HandleManagedLightDescriptorNameChanged: still deferring controllerId='{descriptor.ControllerId}' because no publishable identity is available yet.");
				return;
				}

			if (_childControllers.TryGetValue (descriptor.ControllerId, out ConfigurableDriverEntity? deferredController))
				{
							LogChildPublicationState ("Before UpdateSubControllers deferred publish", descriptor.ControllerId);
				UpdateSubControllers (new[] { deferredController }, null);
							LogChildPublicationState ("After UpdateSubControllers deferred publish", descriptor.ControllerId);
				if (_lightEntities.TryGetValue (descriptor.ControllerId, out IKasaManagedLightEntity? lightEntity))
					{
					lightEntity.NotifyChildPublished ();
					}
				}
			}

		if (!_managedDevices.TryGetValue (descriptor.ControllerId, out PlatformManagedDevice? existingEntry))
			{
			LogInfo ($"HandleManagedLightDescriptorNameChanged: no existing entry for controllerId='{descriptor.ControllerId}'.");
			return;
			}

		_managedDeviceCacheMetadata.TryGetValue (descriptor.ControllerId, out ManagedDeviceCacheEntry? existingMetadata);
		bool kindChanged = existingMetadata is null || existingMetadata.ManagedLightKind != descriptor.Kind;

		if (string.Equals (existingEntry.Name, descriptor.Name, StringComparison.Ordinal) && !kindChanged)
			{
			descriptor.AwaitingConnectedIdentity = false;
			LogInfo ($"HandleManagedLightDescriptorNameChanged: no effective managed-device identity change for controllerId='{descriptor.ControllerId}'.");
			return;
			}

		existingEntry.Name = descriptor.Name;
		descriptor.AwaitingConnectedIdentity = false;
		if (existingMetadata is not null)
			{
			existingMetadata.ManagedLightKind = descriptor.Kind;
			}
		PersistManagedDeviceCache ();

		try
			{
			PublishManagedDeviceEntryUpdate (descriptor.ControllerId, "managed-light-descriptor-name-changed");
			}
		catch (Exception ex)
			{
			LogInfo ($"HandleManagedLightDescriptorNameChanged: managed-device name publish failed for controllerId='{descriptor.ControllerId}': {ex.Message}");
			return;
			}

		LogInfo ($"HandleManagedLightDescriptorNameChanged: published updated managed-device identity for controllerId='{descriptor.ControllerId}', name='{descriptor.Name}', kind={descriptor.Kind}, kindChanged={kindChanged}.");
		LogManagedDeviceSnapshot ("HandleManagedLightDescriptorNameChanged snapshot", _managedDevices);
		}

	private bool TryPublishDeferredManagedDevice (ManagedLightDescriptor descriptor)
		{
		if (HasManagedDeviceEntry (descriptor.ControllerId))
			{
			return true;
			}

		string name = ResolvePublishableManagedDeviceName (descriptor);
		if (string.IsNullOrWhiteSpace (name))
			{
			return false;
			}

		return PublishManagedDeviceAddition (
			descriptor.ControllerId,
			name,
			descriptor.ModelName,
			descriptor.SerialNumber,
			_knownDescriptors);
		}

	private string ResolvePublishableManagedDeviceName (ManagedLightDescriptor descriptor)
		{
		if (!descriptor.AwaitingConnectedIdentity)
			{
			return ResolveManagedDeviceName (descriptor.ControllerId, descriptor.Name, descriptor.DiscoveryDeviceId ?? descriptor.SerialNumber, descriptor.Host);
			}

		if (_resolvedDeviceNames.TryGetValue (descriptor.ControllerId, out string? rememberedName)
			&& !string.IsNullOrWhiteSpace (rememberedName)
			&& !string.Equals (rememberedName, descriptor.DiscoveryDeviceId, StringComparison.OrdinalIgnoreCase)
			&& !string.Equals (rememberedName, descriptor.Host, StringComparison.OrdinalIgnoreCase))
			{
			return rememberedName;
			}

		return string.Empty;
		}

	private IEnumerable<ManagedLightDescriptor> CreateManagedLightDescriptors (DiscoveryResult discoveryResult)
		{
		string controllerId = CreateControllerId (discoveryResult);
		string rootName = ResolveManagedDeviceName (controllerId, ResolveDiscoveryName (discoveryResult), discoveryResult.DeviceId, discoveryResult.Host);
		string rootModel = discoveryResult.Model ?? "Kasa/Tapo Device";
		string rootSerial = discoveryResult.DeviceId ?? discoveryResult.Host;
		ManagedLightKind cachedManagedLightKind = ResolveKnownManagedLightKind (controllerId, discoveryResult.DeviceType);

		switch (discoveryResult.DeviceType)
			{
			case KasaDeviceType.Bulb:
			case KasaDeviceType.LightStrip:
			case KasaDeviceType.Dimmer:
				yield return new ManagedLightDescriptor (
					controllerId,
					discoveryResult.Host,
					discoveryResult.DeviceType,
					rootName,
					rootModel,
					rootSerial,
					cachedManagedLightKind == ManagedLightKind.Unknown ? ManagedLightKind.Unknown : cachedManagedLightKind,
						awaitingConnectedIdentity: IsTapoDiscoveryResult (discoveryResult) && string.IsNullOrWhiteSpace (discoveryResult.Alias),
					discoveryResult.DeviceId);
				yield break;

			case KasaDeviceType.Plug when _sharedConfiguration.TreatPlugsAsLights:
			case KasaDeviceType.Strip when _sharedConfiguration.TreatPlugsAsLights:
				yield return new ManagedLightDescriptor (
					controllerId,
					discoveryResult.Host,
					discoveryResult.DeviceType,
					rootName,
					rootModel,
					rootSerial,
					ManagedLightKind.OnOff,
						awaitingConnectedIdentity: IsTapoDiscoveryResult (discoveryResult) && string.IsNullOrWhiteSpace (discoveryResult.Alias),
					discoveryResult.DeviceId);
				yield break;

			case KasaDeviceType.Strip:
				yield return new ManagedLightDescriptor (
					controllerId,
					discoveryResult.Host,
					discoveryResult.DeviceType,
					rootName,
					rootModel,
					rootSerial,
					ManagedLightKind.OnOff,
						awaitingConnectedIdentity: IsTapoDiscoveryResult (discoveryResult) && string.IsNullOrWhiteSpace (discoveryResult.Alias),
					discoveryResult.DeviceId);
				yield break;
			}
		}

	private ManagedLightKind ResolveKnownManagedLightKind (string controllerId, KasaDeviceType deviceType)
		{
		if (_managedDeviceCacheMetadata.TryGetValue (controllerId, out ManagedDeviceCacheEntry? metadata))
			{
			ManagedLightKind cachedLightKind = ResolveCachedLightKind (metadata, deviceType);
			if (cachedLightKind != ManagedLightKind.Unknown)
				{
				return cachedLightKind;
				}
			}

		return deviceType == KasaDeviceType.Plug || deviceType == KasaDeviceType.Strip
			? ManagedLightKind.OnOff
			: ManagedLightKind.Unknown;
		}

	private static DeviceConfiguration CreateDeviceConfiguration (DiscoveryResult discoveryResult, DeviceCredentials? credentials, TimeSpan timeout)
		{
		return Discover.CreateConfiguration (discoveryResult, credentials, timeout);
		}

	private Task StartAliasEnrichmentAsync (DiscoveryResult discoveryResult, DeviceConfiguration configuration)
		{
		string controllerId = CreateControllerId (discoveryResult);
		if (!_aliasResolutionInFlightControllerIds.TryAdd (controllerId, 0))
			{
			LogInfo ($"EnrichDiscoveryResultAliasAsync: skipped duplicate in-flight alias fetch for controllerId='{controllerId}', host='{discoveryResult.Host}'.");
			return Task.CompletedTask;
			}

		return EnrichDiscoveryAliasCacheAsync (controllerId, discoveryResult, configuration, _runtimeCancellationSource.Token);
		}

	private async Task EnrichDiscoveryAliasCacheAsync (string controllerId, DiscoveryResult discoveryResult, DeviceConfiguration configuration, CancellationToken cancellationToken)
		{
		LogInfo ($"EnrichDiscoveryResultAliasAsync: attempting one-shot alias fetch for host='{discoveryResult.Host}', deviceId='{discoveryResult.DeviceId ?? "<null>"}', model='{discoveryResult.Model ?? "<null>"}'.");

		try
			{
			SemaphoreSlim connectionGate = GetConnectionGate (controllerId);
				using var aliasTimeoutSource = CancellationTokenSource.CreateLinkedTokenSource (cancellationToken);
				TimeSpan aliasTimeout = configuration.Timeout > TimeSpan.Zero
					? configuration.Timeout + TimeSpan.FromSeconds (2)
					: DefaultDiscoveryTimeout + TimeSpan.FromSeconds (2);
				LogInfo ($"EnrichDiscoveryResultAliasAsync: controllerId='{controllerId}', host='{discoveryResult.Host}', port={configuration.Port}, timeoutMs={aliasTimeout.TotalMilliseconds:0}, transport={configuration.ConnectionOptions.TransportKind}, appPath='{configuration.ConnectionOptions.ApplicationPath ?? string.Empty}'.");
				aliasTimeoutSource.CancelAfter (aliasTimeout);

			LogInfo ($"EnrichDiscoveryResultAliasAsync: controllerId='{controllerId}' waiting on connection gate.");
			Stopwatch aliasGateWaitStopwatch = Stopwatch.StartNew ();
			await connectionGate.WaitAsync (aliasTimeoutSource.Token).ConfigureAwait (false);
			aliasGateWaitStopwatch.Stop ();
			LogInfo ($"EnrichDiscoveryResultAliasAsync: controllerId='{controllerId}' acquired connection gate after {aliasGateWaitStopwatch.Elapsed.TotalMilliseconds:0} ms.");
			try
				{
				Stopwatch aliasConnectStopwatch = Stopwatch.StartNew ();
				KasaDevice? device = await Discover.ConnectAsync (configuration, updateState: true, cancellationToken: aliasTimeoutSource.Token).ConfigureAwait (false);
				aliasConnectStopwatch.Stop ();
				LogInfo ($"EnrichDiscoveryResultAliasAsync: controllerId='{controllerId}' Discover.ConnectAsync completed after {aliasConnectStopwatch.Elapsed.TotalMilliseconds:0} ms.");
				bool deviceAdopted = false;
				try
					{
				string? resolvedAlias = !string.IsNullOrWhiteSpace (device.Alias)
					? device.Alias
					: device.SystemInfo?.Alias;

				if (string.IsNullOrWhiteSpace (resolvedAlias))
					{
					LogInfo ($"EnrichDiscoveryResultAliasAsync: one-shot alias fetch returned no alias for host='{discoveryResult.Host}'.");
					return;
					}

				string resolvedAliasValue = resolvedAlias!;
				LogInfo ($"EnrichDiscoveryResultAliasAsync: resolved alias='{resolvedAliasValue}' for host='{discoveryResult.Host}'.");
				RememberResolvedDeviceName (controllerId, resolvedAliasValue, discoveryResult.DeviceId, discoveryResult.Host);
				LogInfo ($"EnrichDiscoveryResultAliasAsync: remembered alias for controllerId='{controllerId}', descriptorPresent={_knownDescriptors.ContainsKey (controllerId)}.");

				if (_knownDescriptors.TryGetValue (controllerId, out ManagedLightDescriptor? descriptor))
					{
					LogInfo ($"EnrichDiscoveryResultAliasAsync: applying resolved alias to descriptor/controllerId='{controllerId}', previousName='{descriptor.Name}', awaitingConnectedIdentity={descriptor.AwaitingConnectedIdentity}.");
					descriptor.Name = resolvedAliasValue;
					descriptor.AwaitingConnectedIdentity = false;
					HandleManagedLightDescriptorNameChanged (descriptor);
					LogInfo ($"EnrichDiscoveryResultAliasAsync: HandleManagedLightDescriptorNameChanged completed for controllerId='{controllerId}'.");
					}
				else
					{
					LogInfo ($"EnrichDiscoveryResultAliasAsync: resolved alias for controllerId='{controllerId}' but no descriptor was present to update.");
					}

				if (_lightEntities.TryGetValue (controllerId, out IKasaManagedLightEntity? lightEntity))
					{
					deviceAdopted = lightEntity.TryAttachConnectedDevice (device, "alias-enrichment");
					LogInfo ($"EnrichDiscoveryResultAliasAsync: connected device adoption for controllerId='{controllerId}' adopted={deviceAdopted}.");
					}
				}
			finally
				{
				if (!deviceAdopted)
					{
					device.Dispose ();
					}
				}
			}
			finally
				{
				_ = connectionGate.Release ();
				}
			}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
			{
			LogInfo ($"EnrichDiscoveryResultAliasAsync: timed out for controllerId='{controllerId}', host='{discoveryResult.Host}'.");
			}
		catch (OperationCanceledException)
			{
			LogInfo ($"EnrichDiscoveryResultAliasAsync: canceled for host='{discoveryResult.Host}'.");
			}
		catch (Exception ex) when (!(ex is OperationCanceledException))
			{
			LogInfo ($"EnrichDiscoveryResultAliasAsync: one-shot alias fetch failed for host='{discoveryResult.Host}': {ex.Message}");
			}
		finally
			{
			_aliasResolutionInFlightControllerIds.TryRemove (controllerId, out _);
				LogInfo ($"EnrichDiscoveryResultAliasAsync: finished for controllerId='{controllerId}', inFlightCleared=True.");
			}
		}

	private void RefreshExistingDeviceConfigurations (PlatformSharedConfigurationSnapshot configuration)
		{
		DeviceCredentials? credentials = string.IsNullOrWhiteSpace (configuration.UserName) || string.IsNullOrWhiteSpace (configuration.Password)
			? null
			: new DeviceCredentials (configuration.UserName, configuration.Password);

		foreach (KeyValuePair<string, IKasaManagedLightEntity> entry in _lightEntities)
			{
			if (!_discoveryResults.TryGetValue (entry.Key, out DiscoveryResult? discoveryResult))
				{
				continue;
				}

			DeviceConfiguration deviceConfiguration = CreateDeviceConfiguration (discoveryResult, credentials, configuration.DiscoveryTimeout);
			_deviceConfigurations[entry.Key] = deviceConfiguration;
			entry.Value.UpdateConfiguration (deviceConfiguration);
			}
		}

	private static bool HasFeature (IReadOnlyList<DeviceFeature> features, string featureId)
		{
		return features.Any (feature => string.Equals (feature.Id, featureId, StringComparison.Ordinal));
		}

	private static string ResolveDiscoveryName (DiscoveryResult discoveryResult)
		{
		return discoveryResult.Alias
			?? discoveryResult.DeviceId
			?? discoveryResult.Host;
		}

	private static bool HasDiscoveryIdentity (DiscoveryResult discoveryResult)
		{
		return !string.IsNullOrWhiteSpace (discoveryResult.Alias);
		}

	private static string CreateControllerId (DiscoveryResult discoveryResult)
		{
		string source = discoveryResult.DeviceId
			?? discoveryResult.Alias
			?? discoveryResult.Host;

		return CreateControllerIdFromSource (source);
		}

	private static string CreateControllerId (DiscoveryResult discoveryResult, string childId)
		{
		string source = $"{discoveryResult.DeviceId ?? discoveryResult.Host}_{childId}";

		return CreateControllerIdFromSource (source);
		}

	private static string CreateControllerIdFromSource (string source)
		{
		source ??= string.Empty;

		var cleaned = new string (source
			.Select (character => char.IsLetterOrDigit (character) ? char.ToLowerInvariant (character) : '_')
			.ToArray ())
			.Trim ('_');

		return string.IsNullOrWhiteSpace (cleaned)
			? $"device_{Math.Abs (source.GetHashCode ())}"
			: $"device_{cleaned}";
		}

	private static string CreateDiscoveryMergeKey (DiscoveryResult discoveryResult)
		{
		return !string.IsNullOrWhiteSpace (discoveryResult.DeviceId)
			? $"id:{discoveryResult.DeviceId}"
			: $"host:{discoveryResult.Host}";
		}

	private static DiscoveryResult MergeDiscoveryResult (DiscoveryResult current, DiscoveryResult incoming)
		{
		if (string.IsNullOrWhiteSpace (current.Alias) && !string.IsNullOrWhiteSpace (incoming.Alias))
			{
			return incoming;
			}

		if (string.IsNullOrWhiteSpace (current.Model) && !string.IsNullOrWhiteSpace (incoming.Model))
			{
			return incoming;
			}

		if (string.IsNullOrWhiteSpace (current.DeviceId) && !string.IsNullOrWhiteSpace (incoming.DeviceId))
			{
			return incoming;
			}

		return current;
		}

	private async Task<IReadOnlyList<DiscoveryResult>> DiscoverDevicesAsync (TimeSpan timeout, bool isInitialLoad, CancellationToken cancellationToken)
		{
		var discoveredDevices = new Dictionary<string, DiscoveryResult> (StringComparer.OrdinalIgnoreCase);

		// UDP broadcast discovery is inherently lossy; always run a second pass and merge its
		// results in, rather than only retrying when the initial load found zero devices. This
		// significantly reduces single-pass misses (e.g. bulbs intermittently not responding
		// within the timeout window) that previously led to transient removals from the room.
		// The two passes are independent broadcast listeners, so run them CONCURRENTLY rather
		// than sequentially: back-to-back sequential passes each waiting the full timeout (e.g.
		// 10s + 10s = 20s) doubled the time before any device could appear after a reload, which
		// regressed the previously-observed ~10-second startup appearance.
		var passStopwatch = Stopwatch.StartNew ();
		Task<IReadOnlyList<DiscoveryResult>> firstPassTask = Discover.DiscoverAsync (timeout, cancellationToken: cancellationToken);
		Task<IReadOnlyList<DiscoveryResult>> secondPassTask = Discover.DiscoverAsync (timeout, cancellationToken: cancellationToken);
		await Task.WhenAll (firstPassTask, secondPassTask).ConfigureAwait (false);
		passStopwatch.Stop ();

		IReadOnlyList<DiscoveryResult> passResults = firstPassTask.Result;
		IReadOnlyList<DiscoveryResult> secondPassResults = secondPassTask.Result;

		LogDiscoveryPassResults (passResults, passNumber: 1, totalPasses: 2, timeout, passStopwatch.Elapsed, isInitialLoad, retryAfterZero: false);
		LogDiscoveryPassResults (secondPassResults, passNumber: 2, totalPasses: 2, timeout, passStopwatch.Elapsed, isInitialLoad, retryAfterZero: passResults.Count == 0);

		foreach (DiscoveryResult discoveryResult in passResults)
			{
			string key = CreateDiscoveryMergeKey (discoveryResult);

			if (discoveredDevices.TryGetValue (key, out DiscoveryResult? current))
				{
				discoveredDevices[key] = MergeDiscoveryResult (current, discoveryResult);
				}
			else
				{
				discoveredDevices[key] = discoveryResult;
				}
			}

		foreach (DiscoveryResult discoveryResult in secondPassResults)
			{
			string key = CreateDiscoveryMergeKey (discoveryResult);

			if (discoveredDevices.TryGetValue (key, out DiscoveryResult? current))
				{
				discoveredDevices[key] = MergeDiscoveryResult (current, discoveryResult);
				}
			else
				{
				discoveredDevices[key] = discoveryResult;
				}
			}

		LogInfo ($"DiscoverDevicesAsync: mergedResultCount={discoveredDevices.Count}, rawResultCount={passResults.Count + secondPassResults.Count}, mergeStrategy=deviceIdOrHost, isInitialLoad={isInitialLoad}.");

		return discoveredDevices.Values.ToArray ();
		}

	private void LogDiscoveryPassResults (IReadOnlyList<DiscoveryResult> passResults, int passNumber, int totalPasses, TimeSpan timeout, TimeSpan elapsed, bool isInitialLoad, bool retryAfterZero)
		{
		int missingAliasCount = passResults.Count (result => string.IsNullOrWhiteSpace (result.Alias));
		int missingDeviceIdCount = passResults.Count (result => string.IsNullOrWhiteSpace (result.DeviceId));
		int missingModelCount = passResults.Count (result => string.IsNullOrWhiteSpace (result.Model));
		int bulbs = passResults.Count (result => result.DeviceType == KasaDeviceType.Bulb);
		int plugs = passResults.Count (result => result.DeviceType == KasaDeviceType.Plug);
		int strips = passResults.Count (result => result.DeviceType == KasaDeviceType.Strip);
		int hubs = passResults.Count (result => result.DeviceType == KasaDeviceType.Hub);
		int others = passResults.Count - bulbs - plugs - strips - hubs;

		LogInfo ($"DiscoverDevicesAsync: pass={passNumber}/{totalPasses}, resultCount={passResults.Count}, elapsedMs={elapsed.TotalMilliseconds:0}, timeoutMs={timeout.TotalMilliseconds:0}, isInitialLoad={isInitialLoad}, retryAfterZero={retryAfterZero}, typeCounts={{Bulb:{bulbs}, Plug:{plugs}, Strip:{strips}, Hub:{hubs}, Other:{others}}}, missingFields={{Alias:{missingAliasCount}, DeviceId:{missingDeviceIdCount}, Model:{missingModelCount}}}.");
		}

	private bool HasTapoCredentials ()
		{
		return !string.IsNullOrWhiteSpace (_userName) && !string.IsNullOrWhiteSpace (_password);
		}

	private DeviceCredentials? CreateCredentials ()
		{
		return !HasTapoCredentials ()
			? null
			: new DeviceCredentials (_userName, _password);
		}

	private static bool IsTapoDiscoveryResult (DiscoveryResult discoveryResult)
		{
		return discoveryResult.TpapMetadata is not null
			|| discoveryResult.TpapPreferred == true;
		}

	private static bool TryParseDiscoveryTimeout (string timeoutValue, out TimeSpan timeout, out string error)
		{
		if (!double.TryParse (timeoutValue, NumberStyles.Number, CultureInfo.InvariantCulture, out double parsedSeconds)
			|| parsedSeconds <= 0)
			{
			timeout = DefaultDiscoveryTimeout;
			error = "Discovery timeout must be a number greater than zero.";
			return false;
			}

		timeout = TimeSpan.FromSeconds (parsedSeconds);
		error = string.Empty;
		return true;
		}

	private static TimeSpan ParseDiscoveryTimeout (string timeoutValue)
		{
		return TryParseDiscoveryTimeout (timeoutValue, out TimeSpan timeout, out _)
			? timeout
			: DefaultDiscoveryTimeout;
		}

	private static bool TryParsePollInterval (string intervalValue, TimeSpan defaultInterval, out TimeSpan interval, out string error)
		{
		if (!double.TryParse (intervalValue, NumberStyles.Number, CultureInfo.InvariantCulture, out double parsedSeconds)
			|| parsedSeconds < 1)
			{
			interval = defaultInterval;
			error = "Polling interval must be a number greater than or equal to 1 second.";
			return false;
			}

		interval = TimeSpan.FromSeconds (parsedSeconds);
		error = string.Empty;
		return true;
		}

	private void RestartDiscoveryRefreshLoop (TimeSpan interval)
		{
		if (_disposed)
			{
			return;
			}

		LogInfo ("RestartDiscoveryRefreshLoop: resetting discovery refresh loop.");
		int generation = Interlocked.Increment (ref _discoveryRefreshGeneration);

		_discoveryRefreshTask = RunDiscoveryRefreshCycleAsync (generation, interval);

		LogInfo ($"RestartDiscoveryRefreshLoop: lightEntityCount={_lightEntities.Count}, nextIntervalMs={interval.TotalMilliseconds:0}.");
		}

	private async Task RunDiscoveryRefreshCycleAsync (int generation, TimeSpan interval)
		{
		LogInfo ($"RunDiscoveryRefreshCycleAsync: started with intervalMinutes={interval.TotalMinutes:0.###}.");

		try
			{
			LogInfo ($"RunDiscoveryRefreshCycleAsync: waiting intervalMs={interval.TotalMilliseconds:0}.");
			await Task.Delay (interval, _runtimeCancellationSource.Token).ConfigureAwait (false);

			if (_disposed || generation != Volatile.Read (ref _discoveryRefreshGeneration))
				{
				return;
				}

			await RefreshPlatformSafelyAsync (_runtimeCancellationSource.Token).ConfigureAwait (false);

			if (_disposed || generation != Volatile.Read (ref _discoveryRefreshGeneration))
				{
				return;
				}

			_discoveryRefreshTask = RunDiscoveryRefreshCycleAsync (generation, DefaultDiscoveryRefreshInterval);
			}
		catch (OperationCanceledException)
			{
			LogInfo ("RunDiscoveryRefreshCycleAsync: canceled.");
			}
		catch (Exception ex)
			{
			LogError ($"Discovery refresh cycle failed: {ex}");
			}
		}

	private static TimeSpan ParsePollIntervalOrDefault (string intervalValue, TimeSpan defaultInterval)
		{
		return TryParsePollInterval (intervalValue, defaultInterval, out TimeSpan interval, out _)
			? interval
			: defaultInterval;
		}

	private void HandleRefreshFailure (Exception ex, string status)
		{
		SetOnline (false);
		SetReady (false);
		LogError ($"{status} {ex}");
		}

	private void SetOnline (bool online) => OnlineIndicatorIsOnline = online;

	private void SetReady (bool ready) => ReadyIndicatorIsReady = ready;

	private void SetAndNotify (string propertyId, string value, ref string field)
		{
		if (string.Equals (field, value, StringComparison.Ordinal))
			{
			return;
			}

		field = value;
		NotifyPropertyChanged (propertyId, new DriverEntityValue (value));
		}

	private void SetAndNotify (string propertyId, bool value, ref bool field)
		{
		if (field == value)
			{
			return;
			}

		field = value;
		NotifyPropertyChanged (propertyId, new DriverEntityValue (value));
		}

	private bool PublishManagedDeviceAddition (
		string controllerId,
		string name,
		string modelName,
		string serialNumber,
		IDictionary<string, ManagedLightDescriptor> descriptorsByControllerId)
		{
		descriptorsByControllerId.TryGetValue (controllerId, out ManagedLightDescriptor? descriptor);
		if (string.IsNullOrWhiteSpace (name) && descriptor is not null)
			{
			name = ResolveManagedDeviceName (controllerId, descriptor.Name, descriptor.DiscoveryDeviceId ?? descriptor.SerialNumber, descriptor.Host);
			}

		name = ResolveManagedDeviceName (controllerId, name, serialNumber, null);

		if (string.IsNullOrWhiteSpace (name))
			{
			LogInfo ($"Skipping managed-device publish for '{controllerId}' because the resolved device name is blank.");
			return false;
			}

		PlatformManagedDevice entry = CreateManagedDeviceEntry (controllerId, name, modelName, serialNumber);
		if (!_managedDevices.TryAdd (controllerId, entry))
			{
			LogInfo ($"Skipping managed-device add for controllerId='{controllerId}' because an entry already exists.");
			return false;
			}

		if (descriptor is not null)
			{
			_managedDeviceCacheMetadata[controllerId] = CreateManagedDeviceCacheEntry (descriptor, name);
			RememberResolvedDeviceName (controllerId, name, descriptor.DiscoveryDeviceId ?? descriptor.SerialNumber, descriptor.Host);
			}
		else
			{
			_managedDevices.TryRemove (controllerId, out _);
			LogError ($"Skipping managed-device add for controllerId='{controllerId}' because discovery metadata is unavailable; cache metadata cannot be created safely.");
			return false;
			}
		PersistManagedDeviceCache ();

		NotifyManagedDevicesSnapshotChanged ();
		return true;
		}

	private void LoadManagedDeviceCacheIntoMemory ()
		{
		string cachePath = GetManagedDeviceCachePath ();
		if (string.IsNullOrWhiteSpace (cachePath) || !File.Exists (cachePath))
			{
			LogInfo ("Managed-device cache load skipped because no cache file exists.");
			return;
			}

		try
			{
				using var stream = File.OpenRead (cachePath);
				var serializer = new DataContractJsonSerializer (typeof (ManagedDeviceCacheDocument));
				if (serializer.ReadObject (stream) is not ManagedDeviceCacheDocument document)
					{
					LogInfo ("Managed-device cache load returned no document.");
					return;
					}

				if (document.Version != MANAGED_DEVICE_CACHE_VERSION)
					{
					LogInfo ($"Managed-device cache load skipped because version '{document.Version}' is not current; expected version '{MANAGED_DEVICE_CACHE_VERSION}'. The cache will be deleted and rebuilt from discovery.");
					DeleteInvalidManagedDeviceCache (cachePath);
					return;
					}

				bool skippedInvalidEntries = false;
				int cachedConfiguredEntryCount = 0;
				List<ManagedDeviceCacheEntry> devices = document.Devices ?? new List<ManagedDeviceCacheEntry> ();

				foreach (ManagedDeviceCacheEntry? entry in devices)
					{
					if (!IsValidManagedDeviceCacheEntryShape (entry))
						{
					LogInfo ("Managed-device cache contains an invalid current-schema entry; deleting cache so discovery can rebuild it.");
					DeleteInvalidManagedDeviceCache (cachePath);
					return;
						}

						if (!IsCompleteManagedDeviceCacheIdentity (entry)
							|| string.IsNullOrWhiteSpace (entry.Name))
							{
						LogInfo ($"Managed-device cache contains incomplete identity metadata for controllerId='{entry.ControllerId ?? "<null>"}'; deleting cache so discovery can rebuild it.");
						DeleteInvalidManagedDeviceCache (cachePath);
						return;
							}

						string cacheSerialNumber = entry.SerialNumber!;
						bool hasReconnectMetadata = !string.IsNullOrWhiteSpace (entry.Host)
							&& entry.Port > 0;

						if (!IsResolvedFriendlyDeviceName (entry.ControllerId, entry.Name, cacheSerialNumber, entry.Host)
							&& (!entry.AwaitingConnectedIdentity || !hasReconnectMetadata))
							{
							LogInfo ($"Managed-device cache dropped stale fallback name for controllerId='{entry.ControllerId}', cachedName='{entry.Name}', serial='{entry.SerialNumber ?? "<null>"}'.");
							skippedInvalidEntries = true;
							continue;
							}

						_managedDevices[entry.ControllerId] = new PlatformManagedDevice (
							entry.UxCategory,
							entry.Name,
							entry.Manufacturer,
							entry.Model,
							cacheSerialNumber);
					_managedDeviceCacheMetadata[entry.ControllerId] = entry;
					LogChildPublicationState ("Managed-device cache seeded state", entry.ControllerId);
					if (entry.IsConfigured)
						{
						cachedConfiguredEntryCount++;
						}

					RememberResolvedDeviceName (entry.ControllerId, entry.Name, cacheSerialNumber, entry.Host);
					}

				LogInfo ($"Managed-device cache seeded {_managedDevices.Count} device entries from '{cachePath}', cachedConfiguredChildCount={cachedConfiguredEntryCount}; current session child configuration state will be established only by child configuration callbacks.");
				if (skippedInvalidEntries)
					{
					PersistManagedDeviceCache ();
					LogInfo ("Managed-device cache was rewritten after dropping invalid current-schema entries.");
					}
			}
		catch (Exception ex)
			{
			LogInfo ($"Managed-device cache load failed from '{cachePath}': {ex.Message}");
			DeleteInvalidManagedDeviceCache (cachePath);
			}
		}

	private static bool IsValidManagedDeviceCacheEntryShape (ManagedDeviceCacheEntry? entry)
		{
		return entry is not null
			&& entry.Immutable is not null
			&& entry.Mutable is not null;
		}

	private static bool IsCompleteManagedDeviceCacheIdentity (ManagedDeviceCacheEntry? entry)
		{
		if (!IsValidManagedDeviceCacheEntryShape (entry) || entry is null)
			{
			return false;
			}

		return !string.IsNullOrWhiteSpace (entry.ControllerId)
			&& !string.IsNullOrWhiteSpace (entry.Manufacturer)
			&& !string.IsNullOrWhiteSpace (entry.Model)
			&& !string.IsNullOrWhiteSpace (entry.SerialNumber);
		}

	private void DeleteInvalidManagedDeviceCache (string cachePath)
		{
		try
			{
			if (!string.IsNullOrWhiteSpace (cachePath) && File.Exists (cachePath))
				{
				File.Delete (cachePath);
				LogInfo ($"Managed-device cache deleted after load failure; discovery will rebuild it using the current schema: '{cachePath}'.");
				}
			}
		catch (Exception ex)
			{
			LogInfo ($"Managed-device cache delete failed for '{cachePath}': {ex.Message}");
			}
		}

	private string GetManagedDeviceCachePath ()
		{
		return Path.Combine (PERSISTENT_STORAGE_ROOT, MANAGED_DEVICE_CACHE_FILE_NAME);
		}

	private string ResolveManagedDeviceName (string controllerId, string? candidateName, string? deviceId, string? host)
		{
		string? normalizedCandidateName = string.IsNullOrWhiteSpace (candidateName)
			? null
			: candidateName;

		if (normalizedCandidateName is not null
			&& !string.Equals (normalizedCandidateName, deviceId, StringComparison.OrdinalIgnoreCase)
			&& !string.Equals (normalizedCandidateName, host, StringComparison.OrdinalIgnoreCase))
			{
			return normalizedCandidateName;
			}

		if (_resolvedDeviceNames.TryGetValue (controllerId, out string? rememberedName)
			&& !string.IsNullOrWhiteSpace (rememberedName))
			{
			return rememberedName;
			}

		return normalizedCandidateName
			?? deviceId
			?? host
			?? controllerId;
		}

	private static bool IsResolvedFriendlyDeviceName (string controllerId, string? name, string? deviceId, string? host)
		{
		if (string.IsNullOrWhiteSpace (name))
			{
			return false;
			}

		return !string.Equals (name, deviceId, StringComparison.OrdinalIgnoreCase)
			&& !string.Equals (name, host, StringComparison.OrdinalIgnoreCase)
			&& !string.Equals (name, controllerId, StringComparison.OrdinalIgnoreCase);
		}

	private void RememberResolvedDeviceName (string controllerId, string? name, string? deviceId, string? host)
		{
		if (!IsResolvedFriendlyDeviceName (controllerId, name, deviceId, host))
			{
			return;
			}

		_resolvedDeviceNames[controllerId] = name!;
		}

	private Task StartCachedIdentityResolutionsAsync (PlatformSharedConfigurationSnapshot configuration)
		{
		return Task.Run (async () =>
			{
				DeviceCredentials? credentials = CreateCredentials ();
				foreach (ManagedDeviceCacheEntry entry in _managedDeviceCacheMetadata.Values.ToArray ())
					{
						if (!entry.AwaitingConnectedIdentity
							|| string.IsNullOrWhiteSpace (entry.Host)
							|| entry.Port <= 0
							|| !HasTapoCredentials ())
							{
							continue;
							}

						try
							{
							LogInfo ($"Cached identity resolution: attempting reconnect for controllerId='{entry.ControllerId}', host='{entry.Host}', serial='{entry.SerialNumber}'.");
							DeviceConfiguration cachedConfiguration = new DeviceConfiguration (
								entry.Host,
								entry.Port,
								credentials,
								new DeviceConnectionOptions (
									entry.TransportKind,
									new DeviceConnectionParameters (entry.DeviceFamily, entry.EncryptionKind, entry.LoginVersion, entry.UseHttps, entry.HttpPort),
									entry.UseSsl,
									entry.UseDefaultCredentials,
									entry.DefaultCredentialProfile,
									entry.ApplicationPath ?? string.Empty,
									entry.UseSecurePassthrough,
									entry.TpapKeepAliveIntervalMs.HasValue ? TimeSpan.FromMilliseconds (entry.TpapKeepAliveIntervalMs.Value) : null),
								configuration.DiscoveryTimeout);

							using KasaDevice device = await Discover.ConnectAsync (cachedConfiguration, updateState: true, _runtimeCancellationSource.Token).ConfigureAwait (false);
							string? resolvedAlias = !string.IsNullOrWhiteSpace (device.Alias)
								? device.Alias
								: device.SystemInfo?.Alias;

							if (string.IsNullOrWhiteSpace (resolvedAlias))
								{
								continue;
								}

							if (_managedDevices.TryGetValue (entry.ControllerId, out PlatformManagedDevice? managedDevice))
								{
								managedDevice.Name = resolvedAlias!;
								entry.Name = resolvedAlias!;
								entry.AwaitingConnectedIdentity = false;
								RememberResolvedDeviceName (entry.ControllerId, resolvedAlias!, entry.SerialNumber, entry.Host);
								PersistManagedDeviceCache ();
							PublishManagedDeviceEntryUpdate (entry.ControllerId, "cached-identity-resolution");
								LogInfo ($"Cached identity resolution: resolved alias='{resolvedAlias}' for controllerId='{entry.ControllerId}'.");
								}
							}
						catch (Exception ex) when (!(ex is OperationCanceledException))
							{
							LogInfo ($"Cached identity resolution failed for controllerId='{entry.ControllerId}', host='{entry.Host}': {ex.Message}");
							}
					}
			}, _runtimeCancellationSource.Token);
		}

	private bool PublishManagedDeviceRemoval (string controllerId)
		{
		if (!_managedDevices.TryRemove (controllerId, out _))
			{
			return false;
			}

		PersistManagedDeviceCache ();

		NotifyManagedDevicesSnapshotChanged ();
		return true;
		}

	private void PersistManagedDeviceCache ()
		{
		string cachePath = GetManagedDeviceCachePath ();
		if (string.IsNullOrWhiteSpace (cachePath))
			{
			return;
			}

		try
			{
				_managedDeviceCacheWriteGate.Wait ();
				string? cacheDirectory = Path.GetDirectoryName (cachePath);
				if (!string.IsNullOrWhiteSpace (cacheDirectory))
					{
					Directory.CreateDirectory (cacheDirectory);
					}

				var cacheableDevices = _managedDevices
					.OrderBy (entry => entry.Key, StringComparer.OrdinalIgnoreCase)
					.ToList ();

				foreach (KeyValuePair<string, PlatformManagedDevice> entry in cacheableDevices)
					{
					if (!_managedDeviceCacheMetadata.TryGetValue (entry.Key, out ManagedDeviceCacheEntry? metadata)
						|| !IsCompleteManagedDeviceCacheIdentity (metadata))
						{
						LogError ($"Managed-device cache write skipped because controllerId='{entry.Key}' has no complete immutable cache metadata. This indicates a discovery publication bug; discovery will rebuild metadata on the next refresh.");
						return;
						}
					}

				var document = new ManagedDeviceCacheDocument
					{
					Version = MANAGED_DEVICE_CACHE_VERSION,
					Devices = cacheableDevices
						.Select (entry =>
							{
							ManagedDeviceCacheEntry metadata = _managedDeviceCacheMetadata[entry.Key];
							return new ManagedDeviceCacheEntry
							{
						Immutable = new ManagedDeviceImmutableCacheFields
							{
							ControllerId = entry.Key,
							UxCategory = metadata.UxCategory,
							Manufacturer = metadata.Manufacturer,
							Model = metadata.Model,
							DiscoveredDeviceType = metadata.DiscoveredDeviceType,
							ManagedLightKind = metadata.ManagedLightKind,
							SerialNumber = metadata.SerialNumber
							},
						Mutable = new ManagedDeviceMutableCacheFields
							{
							Name = entry.Value.Name,
							Host = metadata.Host,
							AwaitingConnectedIdentity = metadata.AwaitingConnectedIdentity,
							IsConfigured = _configuredChildControllerIds.Contains (entry.Key) || metadata.IsConfigured,
							Port = metadata.Port,
							TransportKind = metadata.TransportKind,
							DeviceFamily = metadata.DeviceFamily,
							EncryptionKind = metadata.EncryptionKind,
							LoginVersion = metadata.LoginVersion,
							UseHttps = metadata.UseHttps,
							HttpPort = metadata.HttpPort,
							UseSsl = metadata.UseSsl,
							UseDefaultCredentials = metadata.UseDefaultCredentials,
							DefaultCredentialProfile = metadata.DefaultCredentialProfile,
							ApplicationPath = metadata.ApplicationPath,
							UseSecurePassthrough = metadata.UseSecurePassthrough,
							TpapKeepAliveIntervalMs = metadata.TpapKeepAliveIntervalMs
							}
							};
							})
						.ToList ()
					};

				using var stream = File.Create (cachePath);
				var serializer = new DataContractJsonSerializer (typeof (ManagedDeviceCacheDocument));
				serializer.WriteObject (stream, document);
			}
		catch (Exception ex)
			{
			LogInfo ($"Managed-device cache save failed to '{cachePath}': {ex.Message}");
			}
		finally
			{
			if (_managedDeviceCacheWriteGate.CurrentCount == 0)
				{
				_managedDeviceCacheWriteGate.Release ();
				}
			}
		}

	[Conditional ("DEBUG")]
	private void LogInfo (string message)
		{
		LogInfoCore (message);
		}

	private void LogInfoCore (string message)
		{
		_logger?.Log (_driverLogId, LogEntryLevel.Info, message);
		}

	private static bool IsDebugLoggingEnabled ()
		{
#if DEBUG
		return true;
#else
		return false;
#endif
		}

	private void LogError (string message)
		{
		_logger?.Log (_driverLogId, LogEntryLevel.Error, message);
		}
	}
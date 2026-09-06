// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE file in the project root for full license information.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Serialization;
using System.Text;

using Crestron.DeviceDrivers.EntityModel;
using Crestron.DeviceDrivers.EntityModel.Data;
using Crestron.DeviceDrivers.EntityModel.Logging;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel;
using Crestron.DeviceDrivers.SDK.EntityModel.Attributes;

namespace KasaTapoCrestronDriver;

public sealed partial class PlatformDriver : ReflectedAttributeDriverEntity, IDisposable
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

		// Strip child outlets/lights must remember which physical child on the strip they are, or a
		// managed-device cache reload (TryCreateCachedDescriptorAndConfiguration) recreates the
		// descriptor with ChildId=null, breaking UpdateDescriptorFromConnectedDevice's per-child alias
		// resolution (device.GetChild(ChildId)) and causing the child to display the wrong name.
		[DataMember (Name = "childId", EmitDefaultValue = false)]
		public string? ChildId
			{
			get;
			set;
			}
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

		// Persisted so that on reload, an unassigned plug/strip child's managed-device UxCategory
		// can be recomputed from the user's actual "Treat As Light" preference (via
		// ResolveManagedChildKind) instead of blindly trusting the cached UxCategory snapshot -
		// which previously went stale whenever the child was removed from configuration (its kind
		// is re-resolved in memory via RemoveChildFromConfiguration, but _childTreatAsLight/the
		// on-disk cache never reflected that reset), causing it to reappear with the wrong
		// category (e.g. still "Light") after a driver reload.
		[DataMember (Name = "treatAsLight", EmitDefaultValue = false)]
		public bool TreatAsLight
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

		public string? ChildId
			{
			get => Immutable.ChildId;
			set => Immutable.ChildId = value;
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

		public bool TreatAsLight
			{
			get => Mutable.TreatAsLight;
			set => Mutable.TreatAsLight = value;
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
	// Bumped to 7: earlier builds could persist UxCategory=Light for strip/plug outlets because
	// CreateManagedDeviceEntry read _managedDeviceCacheMetadata before it was populated for a
	// brand-new controllerId. That stale UxCategory was then loaded verbatim by
	// LoadManagedDeviceCacheIntoMemory on every subsequent startup, so bump the version to force
	// existing caches to be discarded and rebuilt with the corrected classification.
	private const int MANAGED_DEVICE_CACHE_VERSION = 8;

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
	private readonly Dictionary<string, IKasaManagedChildEntity> _lightEntities = new (StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, bool> _childTreatAsLight = new (StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, int> _pendingRemovalMissCounts = new (StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<string> _configuredChildControllerIds = new (StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<string> _inUseChildControllerIds = new (StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<string> _connectedIdentityResolvedControllerIds = new (StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<string, byte> _aliasResolutionInFlightControllerIds = new (StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, List<ManagedLightDescriptor>> _resolvedStripChildDescriptors = new (StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<string, byte> _stripChildResolutionInFlightControllerIds = new (StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<string> _materializationInFlightControllerIds = new (StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<string> _previousDiscoveredControllerIds = new (StringComparer.OrdinalIgnoreCase);
	private ConcurrentDictionary<string, PlatformManagedDevice> _managedDevices = new (StringComparer.OrdinalIgnoreCase);
	private readonly PlatformSharedConfiguration _sharedConfiguration = new ();
	private readonly ProcessorBaselineCoordinator _processorBaselineCoordinator;
	private readonly SemaphoreSlim _refreshGate = new (1, 1);
	private readonly SemaphoreSlim _scheduledRefreshGate = new (1, 1);
	private readonly SemaphoreSlim _managedDeviceCacheWriteGate = new (1, 1);
	private readonly DataDrivenConfigurationControllerArgs _configurationArgs;
	private DataDrivenConfigurationController _dataDrivenConfigurationController = null!;
	private readonly Func<string, ICondition>? _conditionLookup;
	private readonly Func<string, ITransformation>? _transformationLookup;
	private readonly IComponentLogger? _componentLogger;
	private string _userName = string.Empty;
	private string _password = string.Empty;
	private string _discoveryTimeoutSeconds = ((int)DefaultDiscoveryTimeout.TotalSeconds).ToString (CultureInfo.InvariantCulture);
	private string _lightPollIntervalSeconds = ((int)DefaultLightPollInterval.TotalSeconds).ToString (CultureInfo.InvariantCulture);
	private string _sensorPollIntervalSeconds = ((int)DefaultSensorPollInterval.TotalSeconds).ToString (CultureInfo.InvariantCulture);
	private bool _enableLightPolling;
	private bool _enableProcessorBaselineWorkaround;
	private string _processorSshHost = string.Empty;
	private string _processorSshUserName = string.Empty;
	private string _processorSshPassword = string.Empty;
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

	internal IDriverConfigurationController ConfigurationController
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
		_processorBaselineCoordinator = new ProcessorBaselineCoordinator (_sharedConfiguration, message => LogInfoCore (message), message => LogError (message));

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
		_dataDrivenConfigurationController = new DelegateDataDrivenConfigurationController (_configurationArgs, ApplyConfigurationItems, null, null);
		ConfigurationController = new LoggingDriverConfigurationController (ControllerId, _dataDrivenConfigurationController, message => LogInfoCore (message));
		}

	public void Stop ()
		{
		if (_disposed)
			{
			return;
			}

		LogInfo ($"PlatformDriver.Stop: childControllerCount={_childControllers.Count}, lightEntityCount={_lightEntities.Count}.");

		CancelRuntimeRefreshes ();

		foreach (IKasaManagedChildEntity lightEntity in _lightEntities.Values.ToArray ())
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

		foreach (IKasaManagedChildEntity lightEntity in _lightEntities.Values.ToArray ())
			{
			lightEntity.Dispose ();
			}

		_processorBaselineCoordinator.Dispose ();

		_scheduledRefreshGate.Dispose ();
		_managedDeviceCacheWriteGate.Dispose ();
		_refreshGate.Dispose ();

		// NOTE: Do NOT delete the managed-device cache file here. Dispose() is called by the
		// Crestron Home host on every driver reload/restart, not only when the driver instance is
		// actually removed - the SDK provides no reliable signal to distinguish the two. Deleting
		// the cache unconditionally on every Dispose wiped out persisted managed-device state
		// (friendly names, identity, UxCategory, etc.) on every routine reload, forcing a full
		// rediscovery and leaving Configure Pro showing every device as offline/not-configured
		// until identities re-resolved. See CHANGELOG/commit history for the removal-cleanup intent
		// that originally motivated this call; that intent still needs a real removal signal before
		// it can be reinstated safely.

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

		bool hasProcessorSshUserName = !string.IsNullOrWhiteSpace (_processorSshUserName);
		bool hasProcessorSshPassword = !string.IsNullOrWhiteSpace (_processorSshPassword);
		if (_enableProcessorBaselineWorkaround && (!hasProcessorSshUserName || !hasProcessorSshPassword))
			{
			const string processorSshCredentialError = "Processor SSH user name and password are required when the processor baseline workaround is enabled.";
			return new ConfigurationItemErrors (
				new Dictionary<string, string>
					{
					["ProcessorSshUserName"] = processorSshCredentialError,
					["ProcessorSshPassword"] = processorSshCredentialError
					},
				processorSshCredentialError);
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
			_enableProcessorBaselineWorkaround,
			_processorSshHost,
			_processorSshUserName,
			_processorSshPassword);

		PlatformSharedConfigurationSnapshot currentConfiguration = _sharedConfiguration.Snapshot ();
		LogInfo ($"ApplyConfigurationItems resolved configuration: mode={applyMode}, timeoutSeconds={currentConfiguration.DiscoveryTimeout.TotalSeconds:0.###}, enableLightPolling={currentConfiguration.EnableLightPolling}, lightPollIntervalSeconds={currentConfiguration.LightPollInterval.TotalSeconds:0.###}, sensorPollIntervalSeconds={currentConfiguration.SensorPollInterval.TotalSeconds:0.###}, hasTapoCredentials={HasTapoCredentials ()}.");

		switch (applyMode)
			{
			case ConfigurationApplyMode.InitialConfiguration:
			case ConfigurationApplyMode.SavedConfiguration:
				_managedDevices = new ConcurrentDictionary<string, PlatformManagedDevice> (StringComparer.OrdinalIgnoreCase);
					_managedDeviceCacheMetadata.Clear ();
					_resolvedDeviceNames.Clear ();
					LoadManagedDeviceCacheIntoMemory ();
				PublishCachedChildControllers (currentConfiguration);

				// PublishCachedChildControllers only materializes/configures controllers that are
				// not already registered in _childControllers - it silently skips (via `continue`)
				// any controller already published from an earlier apply in this same driver
				// session. If that earlier publish happened before credentials were entered/saved
				// (e.g. the device was discovered and materialized while the Tapo UserName/Password
				// fields were still blank), those already-published entities' DeviceConfiguration
				// would otherwise never be refreshed with the credentials just applied here, leaving
				// them retrying the TPAP handshake forever with no credentials. Explicitly refresh
				// every already-materialized entity's configuration here too, exactly like the
				// RealtimeChange branch below does, so credentials are always retrieved fresh on
				// every apply rather than staying cached in a stale configuration.
				RefreshExistingDeviceConfigurations (currentConfiguration);
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
				bool discoveryInputsChanged = connectionInputsChanged;

				if (connectionInputsChanged)
					{
					RefreshExistingDeviceConfigurations (currentConfiguration);
					}

				foreach (IKasaManagedChildEntity lightEntity in _lightEntities.Values)
					{
					lightEntity.ApplyRuntimeConfiguration (previousConfiguration, currentConfiguration);
					}

				if (discoveryInputsChanged)
					{
					_ = ScheduleRefreshAsync ();
					}

				// A RealtimeChange apply from Crestron Home's configuration UI is not guaranteed to
				// include every configuration item - e.g. editing just the Password field can submit
				// only {Password}, without resubmitting the UserName the user already committed on a
				// prior keystroke/apply. DataDrivenConfigurationController tracks each item's
				// "CurrentValue" independently and only updates the ones present in a given apply's
				// values dictionary. If a later apply never resubmits UserName, the controller's own
				// CurrentValue for UserName stays stale even though this driver's authoritative
				// in-memory _userName is already correct - and that stale controller-side value is
				// what gets shown back to the user (and persisted to the .dat file) on the next
				// reload. Explicitly resync the controller's CurrentValue for every credential item
				// after every successful apply so it always matches this driver's authoritative state.
				NotifyCredentialValuesChanged ();
				break;
			}

		return null;
		}

	}

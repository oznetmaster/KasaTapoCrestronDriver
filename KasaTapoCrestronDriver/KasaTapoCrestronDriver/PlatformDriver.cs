using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Diagnostics;
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
	private sealed class ManagedDeviceCacheEntry
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

		[DataMember (Name = "name")]
		public string Name
			{
			get;
			set;
			} = string.Empty;

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

		[DataMember (Name = "serialNumber")]
		public string SerialNumber
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

	private const string TP_LINK_MANUFACTURER = "TP-Link";
	private const string PERSISTENT_STORAGE_ROOT = "/user/Data/ThirdParty/NeilColvin/KasaTapoCrestronDriver";
	private const string MANAGED_DEVICE_CACHE_FILE_NAME = "managed-devices-cache.json";
	private const int MANAGED_DEVICE_CACHE_VERSION = 3;

	private static readonly TimeSpan InitialDiscoveryRefreshInterval = TimeSpan.FromSeconds (5);
	private static readonly TimeSpan DefaultDiscoveryTimeout = TimeSpan.FromSeconds (8);
	private static readonly TimeSpan DefaultDiscoveryRefreshInterval = TimeSpan.FromMinutes (10);
	private static readonly TimeSpan DefaultLightPollInterval = TimeSpan.FromSeconds (15);
	private static readonly TimeSpan DefaultSensorPollInterval = TimeSpan.FromSeconds (3);

	private readonly DriverControllerCreationArgs _args;
	private readonly DriverImplementationResources _resources;
	private readonly DriverControllerLogger _logger;
	private readonly string _driverLogId;
	private readonly Dictionary<string, ConfigurableDriverEntity> _childControllers = new (StringComparer.OrdinalIgnoreCase);
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

		LogInfo ($"Platform driver instance starting: driverId='{_driverLogId}', dataDirectory='{_args.DriverDataDirectoryPath}'.");

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

		switch (applyMode)
			{
			case ConfigurationApplyMode.InitialConfiguration:
			case ConfigurationApplyMode.SavedConfiguration:
				_managedDevices.Clear ();
					_managedDeviceCacheMetadata.Clear ();
					_resolvedDeviceNames.Clear ();
					LoadManagedDeviceCacheIntoMemory ();
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
						existingLightEntity.SetConfigured (_configuredChildControllerIds.Contains (descriptor.ControllerId), "rediscovery-existing-child");
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
			NotifyPropertyChanged ("platform:managedDevices", CreateValueForEntries (ManagedDevices));
			LogInfo ($"Managed-device publish: count={_managedDevices.Count}, controllerIds=[{string.Join (", ", _managedDevices.Keys.OrderBy (key => key, StringComparer.OrdinalIgnoreCase))}].");
			_initialDiscoveryLoadPending = false;
			}

		SetOnline (true);
		SetReady (true);

		var previousDiscoveredControllerIds = new HashSet<string> (_previousDiscoveredControllerIds, StringComparer.OrdinalIgnoreCase);
		int previousDiscoveredCount = previousDiscoveredControllerIds.Count;
		bool hasAddedDiscoveryChange = discoveredControllerIds.Except (previousDiscoveredControllerIds, StringComparer.OrdinalIgnoreCase).Any ();
		bool hasRemovedDiscoveryChange = previousDiscoveredControllerIds.Except (discoveredControllerIds, StringComparer.OrdinalIgnoreCase).Any ();
		bool ignoreRemovalSignalsThisPass = isFastRefreshPhase && hasRemovedDiscoveryChange;

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
			LogInfo ($"RefreshPlatformAsync: ignoring shrinking fast-refresh pass from {previousDiscoveredCount} discovered devices to {discoveredControllerIds.Count}; removals remain deferred until the 10-minute two-strike cycle.");
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

				if (missedCount < 3)
					{
					LogInfo ($"Managed-device removal deferred for controllerId='{existingControllerId}' after miss {missedCount}/3 on the normal 10-minute refresh cycle.");
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

				_pendingRemovalMissCounts.Remove (existingControllerId);
				_childControllers.Remove (existingControllerId);
				_lightEntities.Remove (existingControllerId);
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
							var controller = new ConfigurableDriverEntity (controllerId, (ReflectedAttributeDriverEntity)lightEntity, CreateChildConfigurationController (pendingMaterialization.Descriptor));
							_lightEntities[controllerId] = lightEntity;
							_childControllers[controllerId] = controller;
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

					if (managedDevicesChanged)
						{
						NotifyPropertyChanged ("platform:managedDevices", CreateValueForEntries (ManagedDevices));
						}

					if ((controllersToAdd?.Count ?? 0) > 0)
						{
						List<ConfigurableDriverEntity> controllersToPublish = controllersToAdd!;
						UpdateSubControllers (controllersToPublish, null);

						foreach (ConfigurableDriverEntity controller in controllersToPublish)
							{
							if (_lightEntities.TryGetValue (controller.ControllerId, out IKasaManagedLightEntity? lightEntity))
								{
								lightEntity.PublishStateSnapshot ();
								}
							}
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
			ControllerId = descriptor.ControllerId,
			UxCategory = DeviceUxCategory.Light,
			Name = name,
			Manufacturer = TP_LINK_MANUFACTURER,
			Model = descriptor.ModelName,
			SerialNumber = descriptor.SerialNumber,
			Host = configuration.Host,
			AwaitingConnectedIdentity = descriptor.AwaitingConnectedIdentity,
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
			};
		}

	private bool HasManagedDeviceEntry (string controllerId)
		{
		return _managedDevices.ContainsKey (controllerId);
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

	private IKasaManagedLightEntity CreateManagedLightEntity (ManagedLightDescriptor descriptor, DeviceConfiguration configuration)
		{
		return new KasaLightEntity (
			descriptor.ControllerId,
			descriptor,
			configuration,
			HandleManagedLightDescriptorNameChanged,
			_sharedConfiguration,
			_resources,
			_logger,
			_driverLogId);
		}

	private IDriverConfigurationController CreateChildConfigurationController (ManagedLightDescriptor descriptor)
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

		LogInfo ($"Child configuration controller created for controllerId='{descriptor.ControllerId}', model='{descriptor.ModelName}'.");
		return controller;
		}

	private ConfigurationItemErrors? ApplyChildConfigurationItems (
		string controllerId,
		DataDrivenConfigurationController.ApplyConfigurationAction action,
		string stepId,
		IDictionary<string, DriverEntityValue?> values)
		{
		LogInfo ($"ApplyChildConfigurationItems: controllerId='{controllerId}', action='{action}', stepId='{stepId}', hasExistingLightEntity={_lightEntities.ContainsKey (controllerId)}, valueKeys=[{string.Join (", ", values.Keys.OrderBy (key => key, StringComparer.OrdinalIgnoreCase))}].");
		_configuredChildControllerIds.Add (controllerId);
		_inUseChildControllerIds.Add (controllerId);
		_pendingRemovalMissCounts.Remove (controllerId);

		if (_lightEntities.TryGetValue (controllerId, out IKasaManagedLightEntity? lightEntity))
			{
			lightEntity.SetConfigured (true, $"child-config-callback:{action}");
						lightEntity.PublishStateSnapshot ();
						LogInfo ($"ApplyChildConfigurationItems: published initial state snapshot for configured child controllerId='{controllerId}'.");
			}
		else
			{
			LogInfo ($"ApplyChildConfigurationItems: controllerId='{controllerId}' has no materialized light entity yet; activation will occur after discovery/materialization.");
			}

		return null;
		}

	private void HandleManagedLightDescriptorNameChanged (ManagedLightDescriptor descriptor)
		{
		LogInfo ($"HandleManagedLightDescriptorNameChanged: controllerId='{descriptor.ControllerId}', incomingName='{descriptor.Name}', hasExistingEntry={_managedDevices.ContainsKey (descriptor.ControllerId)}, currentCount={_managedDevices.Count}.");
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
				UpdateSubControllers (new[] { deferredController }, null);
				if (_lightEntities.TryGetValue (descriptor.ControllerId, out IKasaManagedLightEntity? lightEntity))
					{
					lightEntity.PublishStateSnapshot ();
					}
				}
			}

		if (!_managedDevices.TryGetValue (descriptor.ControllerId, out PlatformManagedDevice? existingEntry))
			{
			LogInfo ($"HandleManagedLightDescriptorNameChanged: no existing entry for controllerId='{descriptor.ControllerId}'.");
			return;
			}

		if (string.Equals (existingEntry.Name, descriptor.Name, StringComparison.Ordinal))
			{
			descriptor.AwaitingConnectedIdentity = false;
			LogInfo ($"HandleManagedLightDescriptorNameChanged: no effective managed-device name change for controllerId='{descriptor.ControllerId}'.");
			return;
			}

		existingEntry.Name = descriptor.Name;
		descriptor.AwaitingConnectedIdentity = false;
		PersistManagedDeviceCache ();

		try
			{
			NotifyPropertyChanged (
				"platform:managedDevices",
				DriverEntityValueUpdate.Create (
					DriverEntityValueUpdate.Create (
						descriptor.ControllerId,
						DriverEntityValueUpdate.Create ("name", new DriverEntityValue (descriptor.Name)))));
			}
		catch (Exception ex)
			{
			LogInfo ($"HandleManagedLightDescriptorNameChanged: managed-device name publish failed for controllerId='{descriptor.ControllerId}': {ex.Message}");
			return;
			}

		LogInfo ($"HandleManagedLightDescriptorNameChanged: published updated managed-device name for controllerId='{descriptor.ControllerId}', name='{descriptor.Name}'.");
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
					ManagedLightKind.Dimmable,
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
				using var aliasTimeoutSource = CancellationTokenSource.CreateLinkedTokenSource (cancellationToken);
				TimeSpan aliasTimeout = configuration.Timeout > TimeSpan.Zero
					? configuration.Timeout + TimeSpan.FromSeconds (2)
					: DefaultDiscoveryTimeout + TimeSpan.FromSeconds (2);
				LogInfo ($"EnrichDiscoveryResultAliasAsync: controllerId='{controllerId}', host='{discoveryResult.Host}', port={configuration.Port}, timeoutMs={aliasTimeout.TotalMilliseconds:0}, transport={configuration.ConnectionOptions.TransportKind}, appPath='{configuration.ConnectionOptions.ApplicationPath ?? string.Empty}'.");
				aliasTimeoutSource.CancelAfter (aliasTimeout);

				using KasaDevice device = await Discover.ConnectAsync (configuration, updateState: true, cancellationToken: aliasTimeoutSource.Token).ConfigureAwait (false);
				string? resolvedAlias = !string.IsNullOrWhiteSpace (device.Alias)
					? device.Alias
					: device.SystemInfo?.Alias;

				if (string.IsNullOrWhiteSpace (resolvedAlias))
					{
					LogInfo ($"EnrichDiscoveryResultAliasAsync: one-shot alias fetch returned no alias for host='{discoveryResult.Host}'.");
					return;
					}

				string resolvedAliasValue = resolvedAlias!;
				LogInfo ($"EnrichDiscoveryResultAliasAsync: resolved alias='{resolvedAliasValue}' for host='{discoveryResult.Host}'; disposing one-shot connection.");
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
		for (int index = 0; index < features.Count; index++)
			{
			if (string.Equals (features[index].Id, featureId, StringComparison.Ordinal))
				{
				return true;
				}
			}

		return false;
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

		var passStopwatch = Stopwatch.StartNew ();
		IReadOnlyList<DiscoveryResult> passResults = await Discover.DiscoverAsync (timeout, cancellationToken: cancellationToken).ConfigureAwait (false);
		passStopwatch.Stop ();
		LogDiscoveryPassResults (passResults, passNumber: 1, totalPasses: 1, timeout, passStopwatch.Elapsed, isInitialLoad, retryAfterZero: false);

		if (isInitialLoad && passResults.Count == 0)
			{
			cancellationToken.ThrowIfCancellationRequested ();
			LogInfo ("DiscoverDevicesAsync: initial load found 0 devices; retrying one immediate discovery pass.");

			passStopwatch.Restart ();
			passResults = await Discover.DiscoverAsync (timeout, cancellationToken: cancellationToken).ConfigureAwait (false);
			passStopwatch.Stop ();
			LogDiscoveryPassResults (passResults, passNumber: 2, totalPasses: 2, timeout, passStopwatch.Elapsed, isInitialLoad, retryAfterZero: true);
			}

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

		LogInfo ($"DiscoverDevicesAsync: mergedResultCount={discoveredDevices.Count}, rawResultCount={passResults.Count}, mergeStrategy=deviceIdOrHost, isInitialLoad={isInitialLoad}.");

		return discoveredDevices.Values.ToArray ();
		}

	private void LogDiscoveryPassResults (IReadOnlyList<DiscoveryResult> passResults, int passNumber, int totalPasses, TimeSpan timeout, TimeSpan elapsed, bool isInitialLoad, bool retryAfterZero)
		{
		int missingAliasCount = 0;
		int missingDeviceIdCount = 0;
		int missingModelCount = 0;
		int bulbs = 0;
		int plugs = 0;
		int strips = 0;
		int hubs = 0;
		int others = 0;

		for (int index = 0; index < passResults.Count; index++)
			{
			DiscoveryResult result = passResults[index];

			if (string.IsNullOrWhiteSpace (result.Alias))
				{
				missingAliasCount++;
				}

			if (string.IsNullOrWhiteSpace (result.DeviceId))
				{
				missingDeviceIdCount++;
				}

			if (string.IsNullOrWhiteSpace (result.Model))
				{
				missingModelCount++;
				}

			switch (result.DeviceType)
				{
				case KasaDeviceType.Bulb:
					bulbs++;
					break;

				case KasaDeviceType.Plug:
					plugs++;
					break;

				case KasaDeviceType.Strip:
					strips++;
					break;

				case KasaDeviceType.Hub:
					hubs++;
					break;

				default:
					others++;
					break;
				}
			}

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
		ManagedLightDescriptor? descriptor = null;
		if (string.IsNullOrWhiteSpace (name)
			&& descriptorsByControllerId.TryGetValue (controllerId, out descriptor))
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
			RememberResolvedDeviceName (controllerId, name, serialNumber, null);
			}
		PersistManagedDeviceCache ();

		DriverEntityValueUpdate managedDevicesChange = DriverEntityValueUpdate.Create (
			DriverEntityValueUpdate.Create (controllerId, CreateValueForObject (entry)));

		NotifyPropertyChanged ("platform:managedDevices", managedDevicesChange);
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

				if (document.Version <= 0 || document.Version > MANAGED_DEVICE_CACHE_VERSION)
					{
					LogInfo ($"Managed-device cache load skipped because version '{document.Version}' is unsupported; expected version '{MANAGED_DEVICE_CACHE_VERSION}'.");
					return;
					}

				bool requiresUpgradeRewrite = document.Version < MANAGED_DEVICE_CACHE_VERSION;

				bool skippedInvalidEntries = false;

				foreach (ManagedDeviceCacheEntry entry in document.Devices)
					{
						if (string.IsNullOrWhiteSpace (entry.ControllerId)
							|| string.IsNullOrWhiteSpace (entry.SerialNumber)
							|| string.IsNullOrWhiteSpace (entry.Name))
							{
							skippedInvalidEntries = true;
							continue;
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
					RememberResolvedDeviceName (entry.ControllerId, entry.Name, cacheSerialNumber, entry.Host);
					}

				LogInfo ($"Managed-device cache seeded {_managedDevices.Count} device entries from '{cachePath}'.");
				if (skippedInvalidEntries || requiresUpgradeRewrite)
					{
					PersistManagedDeviceCache ();
					LogInfo (requiresUpgradeRewrite
						? $"Managed-device cache was upgraded from version '{document.Version}' to '{MANAGED_DEVICE_CACHE_VERSION}'."
						: "Managed-device cache was rewritten after dropping invalid cached names.");
					}
			}
		catch (Exception ex)
			{
			LogInfo ($"Managed-device cache load failed from '{cachePath}': {ex.Message}");
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
								NotifyPropertyChanged (
									"platform:managedDevices",
									DriverEntityValueUpdate.Create (
										DriverEntityValueUpdate.Create (
											entry.ControllerId,
											DriverEntityValueUpdate.Create ("name", new DriverEntityValue (resolvedAlias!)))));
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

		DriverEntityValueUpdate managedDevicesChange = DriverEntityValueUpdate.Create (
			DriverEntityValueUpdate.CreateDeletion (controllerId));
		PersistManagedDeviceCache ();

		NotifyPropertyChanged ("platform:managedDevices", managedDevicesChange);
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

				var document = new ManagedDeviceCacheDocument
					{
					Version = MANAGED_DEVICE_CACHE_VERSION,
					Devices = cacheableDevices
						.Select (entry => new ManagedDeviceCacheEntry
							{
							ControllerId = entry.Key,
							UxCategory = entry.Value.UxCategory,
							Name = entry.Value.Name,
							Manufacturer = entry.Value.Manufacturer,
							Model = entry.Value.Model,
							SerialNumber = entry.Value.SerialNumber,
							Host = _managedDeviceCacheMetadata.TryGetValue (entry.Key, out ManagedDeviceCacheEntry? metadata) ? metadata.Host : string.Empty,
							AwaitingConnectedIdentity = _managedDeviceCacheMetadata.TryGetValue (entry.Key, out metadata) && metadata.AwaitingConnectedIdentity
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

	private void LogInfo (string message)
		{
		_logger?.Log (_driverLogId, LogEntryLevel.Info, message);
		}

	private void LogError (string message)
		{
		_logger?.Log (_driverLogId, LogEntryLevel.Error, message);
		}
	}
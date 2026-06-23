using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Crestron.DeviceDrivers.EntityModel;
using Crestron.DeviceDrivers.EntityModel.Data;
using Crestron.DeviceDrivers.EntityModel.Logging;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel;
using Crestron.DeviceDrivers.SDK.EntityModel.Attributes;

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
		public PendingMaterialization (DiscoveryResult discoveryResult, ManagedLightDescriptor descriptor, DeviceConfiguration configuration)
			{
			DiscoveryResult = discoveryResult;
			Descriptor = descriptor;
			Configuration = configuration;
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
		}

	private const string TP_LINK_MANUFACTURER = "TP-Link";

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
	private readonly Dictionary<string, IKasaManagedLightEntity> _lightEntities = new (StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<string> _awaitingRemovalControllerIds = new (StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<string> _connectedIdentityResolvedControllerIds = new (StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<string> _materializationInFlightControllerIds = new (StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<string, PlatformManagedDevice> _managedDevices = new (StringComparer.OrdinalIgnoreCase);
	private readonly PlatformSharedConfiguration _sharedConfiguration = new ();
	private readonly SemaphoreSlim _refreshGate = new (1, 1);
	private readonly SemaphoreSlim _scheduledRefreshGate = new (1, 1);
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

		var configurationArgs = DataDrivenConfigurationControllerArgs.FromResources (args, resources, ControllerId);
		ConfigurationController = new DelegateDataDrivenConfigurationController (configurationArgs, ApplyConfigurationItems, null, null);
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
				NotifyPropertyChanged ("platform:managedDevices", CreateValueForEntries (ManagedDevices));
				SetOnline (false);
				SetReady (false);
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
		bool allowRemovals = !_initialMaterializationStageActive && _normalRemovalRefreshPhaseReached;
		_runtimeDiscoveryStarted = true;
		var timeout = _sharedConfiguration.DiscoveryTimeout;
		var credentials = CreateCredentials ();
		bool hasTapoCredentials = HasTapoCredentials ();
		SetReady (false);

		IReadOnlyList<DiscoveryResult> discoveredDevices = await DiscoverDevicesAsync (timeout, isInitialLoad, cancellationToken).ConfigureAwait (false);
		LogInfo ($"RefreshPlatformAsync: timeoutSeconds={timeout.TotalSeconds:0.###}, discoveredDeviceCount={discoveredDevices.Count}, hasTapoCredentials={hasTapoCredentials}.");
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
		bool hasIncompleteMaterializations = false;
		bool hasPendingConnectedIdentity = false;

		foreach (DiscoveryResult discoveryResult in discoveredDevices.OrderBy (ResolveDiscoveryName, StringComparer.OrdinalIgnoreCase))
			{
			_discoveryResults[CreateControllerId (discoveryResult)] = discoveryResult;

			DeviceConfiguration configuration = CreateDeviceConfiguration (discoveryResult, credentials, timeout);
			_deviceConfigurations[CreateControllerId (discoveryResult)] = configuration;

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
				foreach (ManagedLightDescriptor descriptor in CreateManagedLightDescriptors (discoveryResult))
					{
					activeControllerIds.Add (descriptor.ControllerId);
					_awaitingRemovalControllerIds.Remove (descriptor.ControllerId);
					descriptorsByControllerId[descriptor.ControllerId] = descriptor;
					hasPendingConnectedIdentity |= descriptor.AwaitingConnectedIdentity;

					if (HasSuccessfulMaterialization (descriptor.ControllerId))
						{
						continue;
						}

					hasIncompleteMaterializations = true;

					if (IsMaterializationInFlight (descriptor.ControllerId))
						{
						LogInfo ($"Skipping materialization queue for controllerId='{descriptor.ControllerId}' because materialization is already queued or in progress.");
						continue;
						}

					if (isInitialLoad)
						{
						AddInitialManagedDeviceEntry (
							descriptor.ControllerId,
							descriptor.Name,
							descriptor.ModelName,
							descriptor.SerialNumber);

						TryMarkMaterializationInFlight (descriptor.ControllerId);
						pendingMaterializations.Add (new PendingMaterialization (
							discoveryResult,
							descriptor,
							configuration));
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
						configuration));
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

		TimeSpan nextRefreshInterval = DefaultDiscoveryRefreshInterval;
		if (_initialDiscoveryLoadPending)
			{
			nextRefreshInterval = InitialDiscoveryRefreshInterval;
			}
		else if (hasPendingConnectedIdentity)
			{
			nextRefreshInterval = InitialDiscoveryRefreshInterval;
			}
		else if (hasIncompleteMaterializations)
			{
			nextRefreshInterval = InitialDiscoveryRefreshInterval;
			}
		else if (_initialShortRefreshPending)
			{
			nextRefreshInterval = InitialDiscoveryRefreshInterval;
			_initialShortRefreshPending = false;
			}
		else
			{
			_normalRemovalRefreshPhaseReached = true;
			}

		RestartDiscoveryRefreshLoop (nextRefreshInterval);

		if (pendingMaterializations.Count > 0)
			{
			CompletePendingMaterializationsAsync (
				pendingMaterializations,
				discoveryErrors,
				cancellationToken);
			}

		if (allowRemovals)
			{
			foreach (string existingControllerId in _childControllers.Keys.Except (activeControllerIds, StringComparer.OrdinalIgnoreCase).ToArray ())
				{
				if (_awaitingRemovalControllerIds.Add (existingControllerId))
					{
					LogInfo ($"Managed-device removal deferred for controllerId='{existingControllerId}' after first missed discovery.");
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

				_awaitingRemovalControllerIds.Remove (existingControllerId);
				_childControllers.Remove (existingControllerId);
				_lightEntities.Remove (existingControllerId);
				controllersToRemove ??= new List<string> ();
				controllersToRemove.Add (existingControllerId);
				}
			}
		else
			{
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
		CancellationToken cancellationToken)
		{
		_ = Task.Run (async () =>
			{
				List<ConfigurableDriverEntity>? controllersToAdd = null;
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
								LogInfo ($"Discarding async materialization for controllerId='{controllerId}' because no managed-device entry exists before materialization completes.");
								continue;
								}

							IKasaManagedLightEntity lightEntity = await materialization.Task.ConfigureAwait (false);
							LogInfo ($"Async materialization completed for controllerId='{controllerId}', deviceName='{lightEntity.DeviceName}', model='{lightEntity.ModelName}', serial='{lightEntity.SerialNumber}'.");

							if (!HasManagedDeviceEntry (controllerId))
								{
								LogInfo ($"Discarding async materialized controller for controllerId='{controllerId}' because the managed-device entry was removed while materialization was in progress.");
								lightEntity.Stop ();
								continue;
								}

							var controller = new ConfigurableDriverEntity (controllerId, (ReflectedAttributeDriverEntity)lightEntity, null);
							_lightEntities[controllerId] = lightEntity;
							_childControllers[controllerId] = controller;
							controllersToAdd ??= new List<ConfigurableDriverEntity> ();
							controllersToAdd.Add (controller);
							LogInfo ($"Async materialization accepted for controllerId='{controllerId}' and controller queued for publication.");
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
					if (pendingMaterializations.Count == 0 && _initialMaterializationStageActive)
						{
						_initialMaterializationStageActive = false;
						LogInfo ("CompletePendingMaterializationsAsync: no pending materializations remained; future discovery refreshes may remove missing devices.");
						}
					}
			}, cancellationToken);
		}

	private bool AddInitialManagedDeviceEntry (string controllerId, string name, string modelName, string serialNumber)
		{
		if (string.IsNullOrWhiteSpace (name))
			{
			LogInfo ($"Skipping managed-device publish for '{controllerId}' because the resolved device name is blank.");
			return false;
			}

		_managedDevices[controllerId] = CreateManagedDeviceEntry (controllerId, name, modelName, serialNumber);
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

	private bool HasManagedDeviceEntry (string controllerId)
		{
		return _managedDevices.ContainsKey (controllerId);
		}

	private bool HasSuccessfulMaterialization (string controllerId)
		{
		return _lightEntities.ContainsKey (controllerId)
			&& _childControllers.ContainsKey (controllerId);
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

	private void HandleManagedLightDescriptorNameChanged (ManagedLightDescriptor descriptor)
		{
		LogInfo ($"HandleManagedLightDescriptorNameChanged: controllerId='{descriptor.ControllerId}', incomingName='{descriptor.Name}', hasExistingEntry={_managedDevices.ContainsKey (descriptor.ControllerId)}, currentCount={_managedDevices.Count}.");
		_connectedIdentityResolvedControllerIds.Add (descriptor.ControllerId);

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

	private IEnumerable<ManagedLightDescriptor> CreateManagedLightDescriptors (DiscoveryResult discoveryResult)
		{
		string rootName = ResolveDiscoveryName (discoveryResult);
		string rootModel = discoveryResult.Model ?? "Kasa/Tapo Device";
		string rootSerial = discoveryResult.DeviceId ?? discoveryResult.Host;

		switch (discoveryResult.DeviceType)
			{
			case KasaDeviceType.Bulb:
			case KasaDeviceType.LightStrip:
			case KasaDeviceType.Dimmer:
				yield return new ManagedLightDescriptor (
					CreateControllerId (discoveryResult),
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
					CreateControllerId (discoveryResult),
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
					CreateControllerId (discoveryResult),
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

		IReadOnlyList<DiscoveryResult> passResults = await Discover.DiscoverAsync (timeout, cancellationToken: cancellationToken).ConfigureAwait (false);
		LogInfo ($"DiscoverDevicesAsync: pass=1/1, resultCount={passResults.Count}, isInitialLoad={isInitialLoad}.");

		if (isInitialLoad && passResults.Count == 0)
			{
			cancellationToken.ThrowIfCancellationRequested ();
			LogInfo ("DiscoverDevicesAsync: initial load found 0 devices; retrying one immediate discovery pass.");

			passResults = await Discover.DiscoverAsync (timeout, cancellationToken: cancellationToken).ConfigureAwait (false);
			LogInfo ($"DiscoverDevicesAsync: pass=2/2, resultCount={passResults.Count}, retryAfterZero=true.");
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

		return discoveredDevices.Values.ToArray ();
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
		if (string.IsNullOrWhiteSpace (name)
			&& descriptorsByControllerId.TryGetValue (controllerId, out ManagedLightDescriptor? descriptor))
			{
			name = !string.IsNullOrWhiteSpace (descriptor.Name)
				? descriptor.Name
				: !string.IsNullOrWhiteSpace (descriptor.DiscoveryDeviceId)
					? descriptor.DiscoveryDeviceId!
					: !string.IsNullOrWhiteSpace (descriptor.SerialNumber)
						? descriptor.SerialNumber
						: !string.IsNullOrWhiteSpace (descriptor.Host)
							? descriptor.Host
							: controllerId;
			}

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

		DriverEntityValueUpdate managedDevicesChange = DriverEntityValueUpdate.Create (
			DriverEntityValueUpdate.Create (controllerId, CreateValueForObject (entry)));

		NotifyPropertyChanged ("platform:managedDevices", managedDevicesChange);
		return true;
		}

	private bool PublishManagedDeviceRemoval (string controllerId)
		{
		if (!_managedDevices.TryRemove (controllerId, out _))
			{
			return false;
			}

		DriverEntityValueUpdate managedDevicesChange = DriverEntityValueUpdate.Create (
			DriverEntityValueUpdate.CreateDeletion (controllerId));

		NotifyPropertyChanged ("platform:managedDevices", managedDevicesChange);
		return true;
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
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Crestron.DeviceDrivers.EntityModel.Data;
using Crestron.DeviceDrivers.SDK.EntityModel;

using KasaTapoClient;

using KasaDeviceType = KasaTapoClient.DeviceType;


namespace KasaTapoCrestronDriver;

public sealed partial class PlatformDriver
	{
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

		ConcurrentDictionary<string, PlatformManagedDevice> initialEntryCopy = new (_managedDevices, StringComparer.OrdinalIgnoreCase);
		initialEntryCopy[controllerId] = CreateManagedDeviceEntry (controllerId, name, modelName, serialNumber);
		_managedDevices = initialEntryCopy;
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

		ConcurrentDictionary<string, PlatformManagedDevice> copy = new (_managedDevices, StringComparer.OrdinalIgnoreCase);
		copy[controllerId] = updatedEntry;
		_managedDevices = copy;

		DriverEntityValueUpdate nameChange = DriverEntityValueUpdate.Create ("name", new DriverEntityValue (updatedEntry.Name));
		DriverEntityValueUpdate managedDevicesChange = DriverEntityValueUpdate.Create (
			DriverEntityValueUpdate.Create (controllerId, nameChange));
		NotifyPropertyChanged ("platform:managedDevices", managedDevicesChange);

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
				ActivatePublishedChildIfRunning (controller, "cached-publication-status-reconciliation");
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

	}

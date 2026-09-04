// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE file in the project root for full license information.

using System.Collections.Concurrent;

using Crestron.DeviceDrivers.EntityModel.Data;
using Crestron.DeviceDrivers.SDK.EntityModel;

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

		// CreateManagedDeviceEntry resolves UxCategory (Light vs Outlet) from _managedDeviceCacheMetadata,
		// so the cache metadata must be populated first - CreateManagedDeviceEntry now throws if it isn't.
		_managedDeviceCacheMetadata[controllerId] = CreateManagedDeviceCacheEntry (descriptor, name);
		ConcurrentDictionary<string, PlatformManagedDevice> initialEntryCopy = new (_managedDevices, StringComparer.OrdinalIgnoreCase);
		initialEntryCopy[controllerId] = CreateManagedDeviceEntry (controllerId, name, modelName, serialNumber);
		_managedDevices = initialEntryCopy;
		RememberResolvedDeviceName (controllerId, name, descriptor.DiscoveryDeviceId ?? descriptor.SerialNumber, descriptor.Host);
		PersistManagedDeviceCache ();
		LogInfo ($"Managed-device entry added: controllerId='{controllerId}', name='{name}', model='{modelName}', serial='{serialNumber}'.");
		LogChildPublicationState ("Managed-device entry added state", controllerId);
		return true;
		}

	private PlatformManagedDevice CreateManagedDeviceEntry (string controllerId, string name, string modelName, string serialNumber)
		{
		// Every caller must seed _managedDeviceCacheMetadata (via CreateManagedDeviceCacheEntry) before
		// calling this method. A missing entry means a caller was added/changed without doing that, and
		// silently guessing DeviceUxCategory.Light here previously masked that bug - an outlet child
		// would materialize correctly but its managed-device entry would be miscategorized as a Light
		// and never render an outlet room tile. Fail loudly instead so the real defect is caught.
		if (!_managedDeviceCacheMetadata.TryGetValue (controllerId, out ManagedDeviceCacheEntry? metadata))
			{
			throw new InvalidOperationException ($"Cannot create managed-device entry for controllerId='{controllerId}' because no cache metadata has been seeded; CreateManagedDeviceCacheEntry must be called first.");
			}

		// Configure Pro renders a managed device's secondary info line as the serial number
		// whenever one is supplied, and falls back to manufacturer/model only when it is
		// null. Every other entity v2 driver (e.g. the Overkiz driver) always passes null
		// here for exactly this reason, so pass null too instead of the real serial number -
		// the real serial number is still tracked separately in _managedDeviceCacheMetadata
		// for reconnect/identity purposes and is unaffected by this.
		return new PlatformManagedDevice (
			metadata.UxCategory,
			name,
			TP_LINK_MANUFACTURER,
			modelName,
			null!);
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
				UxCategory = descriptor.ChildKind switch
					{
					ManagedChildKind.Outlet => DeviceUxCategory.Outlet,
					ManagedChildKind.Sensor => DeviceUxCategory.Sensor,
					ManagedChildKind.Button => DeviceUxCategory.Switch,
					ManagedChildKind.Thermostat => DeviceUxCategory.Thermostat,
					ManagedChildKind.Light => DeviceUxCategory.Light,
					_ => throw new InvalidOperationException ($"Cannot resolve DeviceUxCategory for controllerId='{descriptor.ControllerId}' because ManagedChildKind '{descriptor.ChildKind}' is unrecognized."),
					},
				Manufacturer = TP_LINK_MANUFACTURER,
				Model = descriptor.ModelName,
				DiscoveredDeviceType = descriptor.DiscoveredDeviceType,
				ManagedLightKind = descriptor.Kind,
				SerialNumber = descriptor.SerialNumber,
				ChildId = descriptor.ChildId
				},
			Mutable = new ManagedDeviceMutableCacheFields
				{
				Name = name,
				Host = configuration.Host,
				AwaitingConnectedIdentity = descriptor.AwaitingConnectedIdentity,
				IsConfigured = _configuredChildControllerIds.Contains (descriptor.ControllerId),
				TreatAsLight = _childTreatAsLight.TryGetValue (descriptor.ControllerId, out bool treatAsLight) && treatAsLight,
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

		// Notify every field on the entry, not just "name" - Crestron Home only ever applied the
		// UxCategory/Manufacturer/Model/SerialNumber values from the very first (pre-room, pre-"Treat
		// As Light" resolution) snapshot notification. Because that snapshot always defaults new
		// controllerIds to DeviceUxCategory.Light (see AddInitialManagedDeviceEntry), a subsequent
		// name-only delta here left the UxCategory stuck at Light even after the child correctly
		// resolved to Outlet, so the outlet room tile never rendered.
		DriverEntityValueUpdate nameChange = DriverEntityValueUpdate.Create ("name", new DriverEntityValue (updatedEntry.Name));
		DriverEntityValueUpdate uxCategoryChange = DriverEntityValueUpdate.Create ("uxCategory", new DriverEntityValue (updatedEntry.UxCategory.ToString ()));
		DriverEntityValueUpdate manufacturerChange = DriverEntityValueUpdate.Create ("manufacturer", new DriverEntityValue (updatedEntry.Manufacturer));
		DriverEntityValueUpdate modelChange = DriverEntityValueUpdate.Create ("model", new DriverEntityValue (updatedEntry.Model));
		DriverEntityValueUpdate serialNumberChange = DriverEntityValueUpdate.Create ("serialNumber", new DriverEntityValue (updatedEntry.SerialNumber));
		DriverEntityValueUpdate managedDevicesChange = DriverEntityValueUpdate.Create (
			DriverEntityValueUpdate.Create (controllerId, nameChange, uxCategoryChange, manufacturerChange, modelChange, serialNumberChange));
		NotifyPropertyChanged ("platform:managedDevices", managedDevicesChange);

		LogInfo ($"PublishManagedDeviceEntryUpdate: published managed-device entry update for controllerId='{controllerId}', name='{updatedEntry.Name}', uxCategory='{updatedEntry.UxCategory}', model='{updatedEntry.Model}', serial='{updatedEntry.SerialNumber}', configured={_configuredChildControllerIds.Contains (controllerId)}, context='{context}'.");
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
			bool wasConfigured = _configuredChildControllerIds.Contains (descriptor.ControllerId);
			IKasaManagedChildEntity lightEntity = CreateManagedLightEntity (descriptor, deviceConfiguration);
			lightEntity.SetConfigured (wasConfigured, "cached-child-controller-publication");

			LoggingDriverConfigurationController childConfigurationController = CreateChildConfigurationController (descriptor);
			var controller = new ConfigurableDriverEntity (descriptor.ControllerId, (ReflectedAttributeDriverEntity)lightEntity, childConfigurationController);
			_lightEntities[descriptor.ControllerId] = lightEntity;
			_childControllers[descriptor.ControllerId] = controller;
			_childConfigurationControllers[descriptor.ControllerId] = childConfigurationController;

			if (wasConfigured)
				{
				// The freshly-constructed configuration controller has no CurrentValue for
				// ActivationMarker (or TreatAsLight) yet, so it starts life as NotConfigured -
				// that only becomes Configured/Running when Configure Pro's UI submits an
				// ApplyConfiguration for it. On a driver reload this child controller is
				// recreated programmatically from the on-disk cache, so Configure Pro is never
				// asked to resubmit anything for it. Left alone, the child stays permanently
				// NotConfigured after every reload, which is why Crestron Home drops it out of
				// its room even though the managed-device list still shows its last-known
				// UxCategory (e.g. Light). Since the cache says this child was configured
				// before the reload, replay the same values Configure Pro would have sent so
				// the new controller transitions itself straight back to Configured/Running.
				bool replayTreatAsLight = entry.TreatAsLight;
				var replayValues = new Dictionary<string, string> { ["ActivationMarker"] = "true" };
				if (IsTreatAsLightChoiceEligible (descriptor))
					{
					replayValues["TreatAsLight"] = replayTreatAsLight ? "true" : "false";
					}

				try
					{
					childConfigurationController.ApplyConfiguration (replayValues);
					LogInfo ($"PublishCachedChildControllers: controllerId='{descriptor.ControllerId}' replayed prior configuration values into the recreated configuration controller to avoid it getting stuck NotConfigured after reload.");
					}
				catch (Exception ex)
					{
					LogError ($"PublishCachedChildControllers: controllerId='{descriptor.ControllerId}' failed to replay configuration values into the recreated configuration controller: {ex}");
					}
				}

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
				if (_lightEntities.TryGetValue (controller.ControllerId, out IKasaManagedChildEntity? lightEntity))
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
		ManagedChildKind childKind = entry.UxCategory switch
			{
			DeviceUxCategory.Outlet => ManagedChildKind.Outlet,
			DeviceUxCategory.Sensor => ManagedChildKind.Sensor,
			DeviceUxCategory.Switch => ManagedChildKind.Button,
			DeviceUxCategory.Thermostat => ManagedChildKind.Thermostat,
			DeviceUxCategory.Light => ManagedChildKind.Light,
			_ => throw new InvalidOperationException ($"Cannot resolve ManagedChildKind for controllerId='{entry.ControllerId}' because cached UxCategory '{entry.UxCategory}' is unrecognized."),
			};

		descriptor = new ManagedLightDescriptor (
			entry.ControllerId,
			entry.Host,
			deviceType,
			entry.Name,
			entry.Model,
			serialNumber,
			lightKind,
			entry.AwaitingConnectedIdentity,
			serialNumber,
			childId: entry.ChildId,
			childKind: childKind);

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
		bool hasLightEntity = _lightEntities.TryGetValue (controllerId, out IKasaManagedChildEntity? lightEntity);
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

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

		// Notify every field on the entry, not just "name". A name-only delta left the UxCategory
		// stuck at whatever the very first (pre-room, pre-"Treat As Light" resolution) snapshot
		// published, and because that snapshot always defaults new controllerIds to
		// DeviceUxCategory.Light (see AddInitialManagedDeviceEntry), the child stayed Light even
		// after it correctly resolved to Outlet, so the outlet room tile never rendered. The host
		// does apply uxCategory from this delta once it is actually included - both transition
		// directions rely on that - so no full-snapshot follow-up is needed here.
		DriverEntityValueUpdate nameChange = DriverEntityValueUpdate.Create ("name", new DriverEntityValue (updatedEntry.Name));
		DriverEntityValueUpdate uxCategoryChange = DriverEntityValueUpdate.Create ("uxCategory", new DriverEntityValue (updatedEntry.UxCategory.ToString ()));
		DriverEntityValueUpdate manufacturerChange = DriverEntityValueUpdate.Create ("manufacturer", new DriverEntityValue (updatedEntry.Manufacturer));
		DriverEntityValueUpdate modelChange = DriverEntityValueUpdate.Create ("model", new DriverEntityValue (updatedEntry.Model));
		DriverEntityValueUpdate serialNumberChange = DriverEntityValueUpdate.Create ("serialNumber", new DriverEntityValue (updatedEntry.SerialNumber));
		DriverEntityValueUpdate managedDevicesChange = DriverEntityValueUpdate.Create (
			DriverEntityValueUpdate.Create (controllerId, nameChange, uxCategoryChange, manufacturerChange, modelChange, serialNumberChange));

		NotifyPropertyChanged ("platform:managedDevices", managedDevicesChange);
		LogInfo ($"PublishManagedDeviceEntryUpdate: controllerId='{controllerId}', context='{context}', publishedUxCategory={updatedEntry.UxCategory}, name='{updatedEntry.Name}'.");
		}

	private void NotifyManagedDevicesSnapshotChanged ()
		{
		NotifyManagedDevicesSnapshotChanged ("unspecified");
		}

	private void NotifyManagedDevicesSnapshotChanged (string context)
		{
		var snapshot = ManagedDevices;
		LogInfo ($"NotifyManagedDevicesSnapshotChanged: context='{context}', entryCount={snapshot.Count}, categories=[{string.Join (", ", snapshot.Select (pair => $"{pair.Key}={pair.Value.UxCategory}"))}].");
		NotifyPropertyChanged ("platform:managedDevices", CreateValueForEntries (snapshot));
		}

	private bool HasManagedDeviceEntry (string controllerId)
		{
		return _managedDevices.ContainsKey (controllerId);
		}

	private void PublishCachedChildControllers (PlatformSharedConfigurationSnapshot configuration)
		{
		List<ConfigurableDriverEntity>? controllersToAdd = null;
		List<(ConfigurableDriverEntity Controller, LoggingDriverConfigurationController ConfigurationController, ManagedDeviceCacheEntry Entry, ManagedLightDescriptor Descriptor)>? controllersNeedingReplay = null;
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
				//
				// This replay must happen AFTER the controller is registered with the host via
				// UpdateSubControllers below - not before, as previously done here. Replaying
				// the configuration before the host even knows about the controller means the
				// NotConfigured->Configured/Running transition happens "invisibly" from the
				// host's perspective, which is why Configure Pro's Setup/Configure device list
				// (built from host-observed configuration-controller state changes) never
				// picked the child back up after a reload, even though its runtime state -
				// and therefore the Room UI, which reads platform:managedDevices directly -
				// was otherwise perfectly correct.
				controllersNeedingReplay ??= new List<(ConfigurableDriverEntity, LoggingDriverConfigurationController, ManagedDeviceCacheEntry, ManagedLightDescriptor)> ();
				controllersNeedingReplay.Add ((controller, childConfigurationController, entry, descriptor));
				}

			controllersToAdd ??= new List<ConfigurableDriverEntity> ();
			controllersToAdd.Add (controller);
			LogInfo ($"Cached child controller published early for controllerId='{descriptor.ControllerId}', name='{descriptor.Name}', model='{descriptor.ModelName}', host='{descriptor.Host}'.");
			}

		if ((controllersToAdd?.Count ?? 0) > 0)
			{
			List<ConfigurableDriverEntity> controllersToPublish = controllersToAdd!;
			UpdateSubControllers (controllersToPublish, null);
			foreach (ConfigurableDriverEntity controller in controllersToPublish)
				{
				if (_lightEntities.TryGetValue (controller.ControllerId, out IKasaManagedChildEntity? lightEntity))
					{
					lightEntity.NotifyChildPublished ();
					}
				}

			if (controllersNeedingReplay is not null)
				{
				bool isFirstReplay = true;
				foreach (var (_, childConfigurationController, entry, descriptor) in controllersNeedingReplay)
					{
					// Firing every cached child's configuration-controller status transition and
					// platform:managedDevices publish back-to-back in the same tick could race with
					// the host's own asynchronous notification processing (observed on a different
					// CSTP worker thread than the one driving this loop), causing the host to lose
					// track of one child per reload when several were replayed together - a different
					// child each time, ruling out a defect tied to any specific device. Staggering the
					// replay gives the host time to fully process each child's transition before the
					// next one arrives; verified stable across multiple consecutive reloads with all
					// children remaining online after this change.
					if (!isFirstReplay)
						{
						System.Threading.Thread.Sleep (500);
						}
					isFirstReplay = false;

					bool replayTreatAsLight = entry.TreatAsLight;
					var replayValues = new Dictionary<string, string> { ["ActivationMarker"] = "true" };
					if (IsTreatAsLightChoiceEligible (descriptor))
						{
						replayValues["TreatAsLight"] = replayTreatAsLight ? "true" : "false";
						}

					// NOTE: ApplyConfigurationStep below always comes back with
					// errorKeys=DriverDataStore, because DriverDataStore is a host-owned
					// configuration item (it is not declared in
					// CreateChildConfigurationController's item list) that only Crestron Home can
					// populate - the host logs it as "Remaining configuration item to be set was
					// not known yet: DriverDataStore; Device configuration required." The driver
					// has no legitimate value to supply for it, so this error is expected here and
					// must NOT be papered over by injecting a placeholder into replayValues. Every
					// cached child reports it, including ones that go on to reach Running normally.

					try
						{
						// Replay via the same step-based GetFirstConfigurationStep/ApplyConfigurationStep
						// sequence that Configure Pro itself drives, instead of only calling
						// ApplyConfiguration(dict) directly, so the host observes the same
						// NotConfigured->Configured transition it would see from a real Configure Pro
						// submission.
						//
						// NOTE: the calls below go through the same LoggingDriverConfigurationController
						// wrapper used for host-initiated calls, so "GetFirstConfigurationStep called"/
						// "ApplyConfigurationStep called" log lines cannot be distinguished from an actual
						// Configure Pro/Setup Program call by message text alone - the explicit
						// "PublishCachedChildControllers: about to replay..." line immediately below marks
						// which calls originate from this internal replay rather than from the host.
						LogInfo ($"PublishCachedChildControllers: about to replay configuration for controllerId='{descriptor.ControllerId}' via internal GetFirstConfigurationStep/ApplyConfigurationStep call (not a host/Configure Pro request).");
						Crestron.DeviceDrivers.EntityModel.Data.DeviceConfiguration.ConfigurationStep firstStep = childConfigurationController.GetFirstConfigurationStep ();
						if (firstStep is not null && !string.IsNullOrWhiteSpace (firstStep.Id))
							{
							childConfigurationController.ApplyConfigurationStep (firstStep.Id, replayValues);
							LogInfo ($"PublishCachedChildControllers: controllerId='{descriptor.ControllerId}' replayed prior configuration values via step-based ApplyConfigurationStep(stepId='{firstStep.Id}') into the recreated configuration controller (after host registration).");
							}
						else
							{
							childConfigurationController.ApplyConfiguration (replayValues);
							LogInfo ($"PublishCachedChildControllers: controllerId='{descriptor.ControllerId}' had no first configuration step available; fell back to replaying prior configuration values via ApplyConfiguration into the recreated configuration controller (after host registration).");
							}
						}
					catch (Exception ex)
						{
						LogError ($"PublishCachedChildControllers: controllerId='{descriptor.ControllerId}' failed to replay configuration values into the recreated configuration controller: {ex}");
						}
					}
				}

			foreach (ConfigurableDriverEntity controller in controllersToPublish)
				{
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

		// Cache files written before the ChildId-persistence fix (see PersistManagedDeviceCache)
		// already have ChildId=null baked in for every strip child outlet - the fix only stops
		// *future* rewrites from dropping it, it cannot repair entries that were already
		// corrupted on disk. Without recovery here, those children permanently resolve their
		// alias/identity to the parent strip (see UpdateDescriptorFromConnectedDevice) even
		// after the fix is deployed, because the cache is never regenerated from scratch.
		// ControllerId for a strip child is always built by CreateControllerId(discoveryResult,
		// child.Id) as "device_{parentDeviceId}_{childId}" (see PlatformDriver.Discovery.cs);
		// since both halves are already alnum, sanitization does not touch the delimiter, so the
		// original childId can be losslessly recovered by splitting on the single remaining '_'.
		string? recoveredChildId = entry.ChildId;
		if (string.IsNullOrWhiteSpace (recoveredChildId))
			{
			recoveredChildId = TryRecoverChildIdFromControllerId (entry.ControllerId);
			if (!string.IsNullOrWhiteSpace (recoveredChildId))
				{
				entry.ChildId = recoveredChildId;
				LogInfo ($"Cached child controller for controllerId='{entry.ControllerId}' had a null ChildId; recovered '{recoveredChildId}' from the controllerId pattern and will persist it.");
				PersistManagedDeviceCache ();
				}
			}

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
			childId: recoveredChildId,
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

	// A strip child's controllerId is always built by CreateControllerId(discoveryResult, child.Id)
	// as CreateControllerIdFromSource($"{deviceId}_{childId}"), then prefixed with "device_". A
	// root (non-child) controllerId can also legitimately contain '_' though - e.g. an IP-based
	// fallback host like "192.168.1.5" is sanitized to "192_168_1_5" - so naively splitting on the
	// last '_' is not safe on its own. Kasa's own child-id scheme happens to always derive a
	// child's id by appending a short numeric suffix (e.g. "00", "01", "02") to its parent device's
	// id (see the "…d709_…d70900" / "…d70901" pattern), so the recovered child-id candidate must
	// itself start with the recovered parent-id candidate before it is trusted; this rejects
	// unrelated '_'-joined fallback ids (like sanitized hosts) that don't share that prefix
	// relationship, while still recovering genuine strip-child controllerIds.
	private static string? TryRecoverChildIdFromControllerId (string controllerId)
		{
		const string prefix = "device_";
		if (string.IsNullOrWhiteSpace (controllerId)
			|| !controllerId.StartsWith (prefix, StringComparison.OrdinalIgnoreCase))
			{
			return null;
			}

		string remainder = controllerId.Substring (prefix.Length);
		int separatorIndex = remainder.LastIndexOf ('_');
		if (separatorIndex < 0 || separatorIndex == remainder.Length - 1)
			{
			return null;
			}

		string parentIdCandidate = remainder.Substring (0, separatorIndex);
		string childIdCandidate = remainder.Substring (separatorIndex + 1);
		return childIdCandidate.StartsWith (parentIdCandidate, StringComparison.OrdinalIgnoreCase)
			? childIdCandidate
			: null;
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

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

		// Only lights participate in the processor-baseline color/white tuning workaround, and
		// this check is only meaningful when EnableProcessorBaselineWorkaround is on -
		// ValidateLoadNameFireAndForget itself also gates on this, but check here too to avoid
		// spawning the background task at all for outlets/sensors/buttons.
		if (descriptor.ChildKind == ManagedChildKind.Light && _sharedConfiguration.EnableProcessorBaselineWorkaround)
			{
			_processorBaselineCoordinator.ValidateLoadNameFireAndForget (name, _runtimeCancellationSource.Token);
			}

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
					// Switch is used only as a distinct, round-trippable marker for Button kind in
					// the persisted cache/host UxCategory - it must stay different from Sensor so
					// TryCreateCachedDescriptorAndConfiguration can resolve ManagedChildKind.Button
					// back correctly on reload instead of collapsing S200B into KasaSensorEntity.
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
				ChildId = descriptor.ChildId,
				HubChildCategory = descriptor.HubChildCategory
				},
			Mutable = new ManagedDeviceMutableCacheFields
				{
				Name = name,
				Host = configuration.Host,
				AwaitingConnectedIdentity = descriptor.AwaitingConnectedIdentity,
				IsConfigured = _configuredChildControllerIds.ContainsKey (descriptor.ControllerId),
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
		// Use the SDK's serialized enum identifier, matching what full snapshots send (for example, "sensor")
		// instead of the C# enum name (for example, "Sensor"); ToString () was silently sending the wrong
		// casing on incremental updates, hiding a real host-facing wire mismatch behind identical-looking logs.
		DriverEntityValueUpdate uxCategoryChange = DriverEntityValueUpdate.Create ("uxCategory",
			CreateValueForObject (updatedEntry).GetValue<DriverEntityValueDictionary> ()["uxCategory"]);
		DriverEntityValueUpdate manufacturerChange = DriverEntityValueUpdate.Create ("manufacturer", new DriverEntityValue (updatedEntry.Manufacturer));
		DriverEntityValueUpdate modelChange = DriverEntityValueUpdate.Create ("model", new DriverEntityValue (updatedEntry.Model));
		DriverEntityValueUpdate serialNumberChange = DriverEntityValueUpdate.Create ("serialNumber", new DriverEntityValue (updatedEntry.SerialNumber));
		DriverEntityValueUpdate managedDevicesChange = DriverEntityValueUpdate.Create (
			DriverEntityValueUpdate.Create (controllerId, nameChange, uxCategoryChange, manufacturerChange, modelChange, serialNumberChange));

		NotifyPropertyChanged ("platform:managedDevices", managedDevicesChange);
		LogInfo ($"PublishManagedDeviceEntryUpdate: controllerId='{controllerId}', context='{context}', publishedUxCategory={updatedEntry.UxCategory} (wire='{uxCategoryChange.Value}'), name='{updatedEntry.Name}'.");
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
		LogManagedDeviceSnapshotPayloadDiagnostics (context, snapshot);
		}

	// DIAGNOSTIC (temporary): the reboot capture (2026-09-08, 15:13-15:14) shows every child hits
	// "Driver Registration failed" at 15:14:00, after which the host RE-INITIALIZES only the
	// Outlet/Switch children (S200B/KP115/KP303 get "Initialize driver" -> "Create driver
	// instance" -> GetStatus/ApplyConfiguration -> online by 15:14:03). The three Sensor children
	// get the identical registration failure and then NOTHING - no retry, no GetStatus, no
	// ApplyAll - even though the driver keeps polling them successfully every ~16s for the rest
	// of the log. So the divergence is in what the HOST re-reads per entry during its post-failure
	// retry pass, not in publication (identical), teardown (symmetric), category (Sensor works on
	// initial add), cache/manifest (both verified correct), or package version.
	//
	// Existing logging only prints Key=UxCategory, which is identical in shape for adopted and
	// skipped children and therefore cannot discriminate. This dumps the FULL per-entry payload
	// actually handed to the host in the managedDevices property, so the adopted and skipped
	// cohorts can be diffed field-by-field at the exact moment the host makes its retry decision.
	private void LogManagedDeviceSnapshotPayloadDiagnostics (
		string context,
		IDictionary<string, PlatformManagedDevice> snapshot)
		{
		foreach (var pair in snapshot)
			{
			PlatformManagedDevice published = pair.Value;
			bool hasDescriptor = _knownDescriptors.TryGetValue (pair.Key, out ManagedLightDescriptor? descriptor);
			bool hasCacheEntry = _managedDeviceCacheMetadata.TryGetValue (pair.Key, out ManagedDeviceCacheEntry? entry);

			LogInfo ($"SNAPSHOT-PAYLOAD-DIAG: context='{context}', controllerId='{pair.Key}', publishedUxCategory={published.UxCategory}, publishedName='{published.Name}', publishedManufacturer='{published.Manufacturer}', publishedModel='{published.Model}', publishedSerial='{published.SerialNumber}', hasCacheEntry={hasCacheEntry}, cachedUxCategory={(hasCacheEntry ? entry!.UxCategory.ToString () : "<none>")}, cachedIsConfigured={(hasCacheEntry ? entry!.IsConfigured.ToString () : "<none>")}, hasKnownDescriptor={hasDescriptor}, descriptorChildKind={(hasDescriptor ? descriptor!.ChildKind.ToString () : "<none>")}, hasLightEntity={_lightEntities.ContainsKey (pair.Key)}, hasChildController={_childControllers.ContainsKey (pair.Key)}, isConfiguredChild={_configuredChildControllerIds.ContainsKey (pair.Key)}, isInUseChild={_inUseChildControllerIds.ContainsKey (pair.Key)}.");
			}
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
			bool wasConfigured = _configuredChildControllerIds.ContainsKey (descriptor.ControllerId);
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
				// Replay each cached child once. Do NOT add pacing, waiting or retrying here -
				// see the MEASURED FINDING note on ReplayCachedChildConfiguration before changing
				// anything in this loop.
				foreach (var (_, childConfigurationController, entry, descriptor) in controllersNeedingReplay)
					{
					ReplayCachedChildConfiguration (childConfigurationController, entry, descriptor, "initial");
					}
				}

			foreach (ConfigurableDriverEntity controller in controllersToPublish)
				{
				ActivatePublishedChildIfRunning (controller, "cached-publication-status-reconciliation");
				}

			LogCachedChildCohortDiagnostics (controllersNeedingReplay);
			}
		}

	// DIAGNOSTIC (temporary): emit one CACHED-CHILD-DIAG line per replayed cached child at the
	// end of the cached-publish pass, so the cohort the host is about to run ApplyAll over can be
	// compared field-by-field between the children it adopts and the ones it silently skips.
	//
	// Why this and not poller/replay-step instrumentation: the replay call sequence is already
	// fully logged by LoggingDriverConfigurationController, and captures show every child replays
	// IDENTICALLY (stepId='Activation', itemCount=2, keys=[ActivationMarker], errorKeys=
	// DriverDataStore) whether or not it is later adopted. Re-logging the replay steps therefore
	// cannot discriminate. What has never been dumped is the per-child IDENTITY/state tuple at the
	// moment the cohort is handed to the host - and identity is the only surviving correlation
	// (T310/T315 skipped, T100/S200B/KP303 adopted). One line per child, one place, greppable.
	private void LogCachedChildCohortDiagnostics (
		List<(ConfigurableDriverEntity Controller, LoggingDriverConfigurationController ConfigurationController, ManagedDeviceCacheEntry Entry, ManagedLightDescriptor Descriptor)>? replayedChildren)
		{
		if (replayedChildren is null)
			{
			return;
			}

		foreach (var (_, childConfigurationController, entry, descriptor) in replayedChildren)
			{
			string status;
			try
				{
				status = childConfigurationController.PeekStatus ().ToString ();
				}
			catch (Exception ex)
				{
				status = $"peek-failed:{ex.GetType ().Name}";
				}

			LogInfo ($"CACHED-CHILD-DIAG: controllerId='{descriptor.ControllerId}', name='{descriptor.Name}', model='{descriptor.ModelName}', serial='{descriptor.SerialNumber}', uxCategory={entry.UxCategory}, childKind={descriptor.ChildKind}, hubChildCategory={descriptor.HubChildCategory}, kind={descriptor.Kind}, childId='{descriptor.ChildId ?? "<null>"}', host='{descriptor.Host}', discoveryDeviceId='{descriptor.DiscoveryDeviceId ?? "<null>"}', awaitingConnectedIdentity={descriptor.AwaitingConnectedIdentity}, cachedIsConfigured={entry.IsConfigured}, treatAsLight={entry.TreatAsLight}, statusAfterReplay='{status}', hasLightEntity={_lightEntities.ContainsKey (descriptor.ControllerId)}, hasManagedDeviceEntry={HasManagedDeviceEntry (descriptor.ControllerId)}.");
			}
		}

	// MEASURED FINDING (live console capture of a reload, 3 cached children) - read this before
	// adding any pacing, waiting, or retrying to the cached-child replay path.
	//
	// A cached child does NOT reach Running as a result of our replay. Verified directly:
	// after replaying all 3 children, PeekStatus() reported "not Running" for ALL 3 on three
	// successive checks at +5s, +10s and +15s. Then, at +26s, the host ran its own ApplyAll
	// pass and only THEN did children transition Running -> online. That includes children
	// that come up perfectly (the KP303 outlet was flagged "not Running" all three times and
	// still came online normally moments later).
	//
	// Consequences, all confirmed against the capture:
	//  * "child is not Running shortly after replay" is the NORMAL state for every child. It is
	//    not a straggler signal, so retrying on it re-replays healthy children pointlessly. A
	//    reconciliation loop built on that check was tried and removed - it retried all 3
	//    children 3 times, changed nothing, and added 15s to every reload.
	//  * The per-child 10s "confirmation" wait that used to live here could never have worked
	//    either: the StatusChanged it waited for is not emitted during replay at all, so every
	//    child always burned the full timeout (N * 10s, minutes on a 20-30 child system).
	//  * Earlier reload logs that appeared to show "the last-replayed child fails" were
	//    misread. Every child times out in replay; the children that came up Running did so
	//    from the host's later ApplyAll, not from replay succeeding.
	//
	// So replay pacing is NOT the variable that decides whether a child comes online.
	//
	// WHERE THE REMAINING T310/T315 SENSOR BUG ACTUALLY LIVES (live capture, control run):
	//
	// NOT the Sensor UxCategory. Two brand-new T3xx sensors added by hand in the same session
	// were adopted by the host immediately (action='ApplyStep') and came online normally, so
	// the host has no problem with UxCategory=Sensor. Do not go looking there.
	//
	// What actually happens on reload:
	//  1. PublishCachedChildControllers creates the child entity and registers it in
	//     _lightEntities, then replays its configuration.
	//  2. The refresh pass that follows sees _lightEntities already contains the controllerId
	//     and takes the "existing child" early-continue branch in PlatformDriver.Refresh.cs -
	//     so the cached child is NEVER queued for materialization on that pass.
	//  3. At the host's ApplyAll pass, the Outlet and Button children are adopted, but the
	//     Sensor child is not, and it stays NotConfigured/offline.
	//  4. Minutes later a subsequent refresh reaches the HasManagedDeviceEntry branch, logs
	//     "Discovered existing managed-device entry ... without successful materialization;
	//     requeueing materialization attempt", materializes properly, and the host then adopts
	//     the sensor immediately. The device SELF-HEALS - it is delayed adoption, not a
	//     permanent failure. (Observed: reload 12:17:01, recovery 12:23:06.)
	//
	// IMPORTANT UNRESOLVED DETAIL: the Button child follows the byte-for-byte identical path
	// (same cached publish, same replayed keys=[ActivationMarker], same PeekStatus
	// 'NotConfigured' at ApplyAll time, also never materialized) and the host DOES adopt it.
	// So the driver-side state of Button vs Sensor at ApplyAll time looks the same in the logs;
	// what differs is only whether the host includes the child in its ApplyAll pass. Whatever
	// the trigger is, it is not something this replay code is doing differently per child kind.
	//
	// SHARPENED BY THE 12:38 RELOAD CAPTURE (five hub children, three of them sensors):
	// The host adopted S200B (Button), KP303 (Outlet) and T100 (SENSOR) at ApplyAll, but not
	// T310/T315. Two things follow:
	//  a) Child KIND is definitively not the discriminator - a Sensor was adopted on the very
	//     same reload that skipped two other Sensors.
	//  b) After replay the host called GetStatus ONLY for the three children it adopted. It
	//     never polled T310/T315 again, so it is selecting its adopted set BEFORE re-reading any
	//     driver-side status - nothing the driver returns at replay time is being consulted.
	// All five children replayed identically (stepId='Activation', itemCount=2,
	// keys=[ActivationMarker]). The 'errorKeys=DriverDataStore' on the replay result is benign
	// noise: it appears on the adopted KP303/S200B too, so it is not the rejection reason.
	//
	// CONFIRMED ACROSS FOUR CONSECUTIVE RELOADS (14:14, 14:33, 14:39, 14:45 capture), by
	// grepping ApplyChildConfigurationItems for action='ApplyAll' - i.e. exactly which children
	// the host chose to reconcile on each reload:
	//     14:14  KP115, KP303, T100
	//     14:33  S200B, KP115, KP303
	//     14:39  S200B, KP115, KP303
	//     14:45  S200B, KP115, KP303, T100
	// Read that table before forming any theory here, because it falsifies the obvious ones:
	//  * Child KIND is not the discriminator (point (a) above is CORRECT and was re-verified).
	//    T100 is a Sensor and appears in the host's ApplyAll set on 2 of 4 reloads. A
	//    "sensors are declined, outlets/buttons are adopted" reading of a single reload is
	//    wrong - it was tried during this investigation and falsified by the table above.
	//  * The set is not even stable per child: S200B (Button) is ABSENT at 14:14 and present
	//    on the other three. So membership varies run to run for a child that always works.
	//  * T310/T315 appear in NO reload's ApplyAll set. They are never selected, rather than
	//    being selected and rejected.
	// Since the host never asks the driver about T310/T315 at all on a reload, no driver-side
	// value the replay produces can be the cause. The decision is made from the host's own
	// persisted record before the driver is consulted.
	//
	// Consistent with that: the driver author reports that REMOVING and RE-ADDING T310/T315
	// makes them work permanently. A re-add is a host-initiated ApplyStep that runs
	// ActivateChildControllerFromConfiguration and rewrites the host-side record; the cached
	// reload path calls the same MarkChildConfiguredInCache/SetConfigured with the same values
	// and does not. That asymmetry - host record rewritten vs not - is the open question, and
	// it lives on the host side of the boundary, not in this file.
	//
	// Driver-side fields RULED OUT by direct measurement (CACHED-CHILD-DIAG / SENSOR-CAP-DIAG,
	// one line per cached child at publish time; both diagnostics are still in this codebase):
	// uxCategory, childKind, hubChildCategory (nothing reads it), kind, childId, serial,
	// discoveryDeviceId, host, awaitingConnectedIdentity, cachedIsConfigured, treatAsLight,
	// statusAfterReplay, hasLightEntity, hasManagedDeviceEntry, UiDefinition load result
	// (loaded=True for every child), capability-resolution timing, and hasConnectedDevice
	// (False for ALL hub children including the always-adopted S200B - it is by design for the
	// push architecture, not a sensor defect). Every one of these is identical between adopted
	// and skipped children.
	//
	// Regression context from the driver author: all of these children worked before the
	// push-based ManagedParentDevicePoller rearchitecture (commit c95c7e1), which is when hub
	// children began acquiring a shared poller during entity construction on the cached-publish
	// path. That commit is the most likely place the ordering above was introduced.
	//
	// A timed "stuck cached child" watchdog used to tear down children that had not reached
	// Running some interval after this replay. It has been removed - see the DELIBERATELY NO
	// STUCK-CACHED-CHILD WATCHDOG note below for the measurements showing it was what actually
	// took T310/T315 offline and permanently cleared their cached IsConfigured flag.
	private void ReplayCachedChildConfiguration (
		LoggingDriverConfigurationController childConfigurationController,
		ManagedDeviceCacheEntry entry,
		ManagedLightDescriptor descriptor,
		string context)
		{
		var replayValues = new Dictionary<string, string> { ["ActivationMarker"] = "true" };
		if (IsTreatAsLightChoiceEligible (descriptor))
			{
			replayValues["TreatAsLight"] = entry.TreatAsLight ? "true" : "false";
			}

		// NOTE: ApplyConfigurationStep below always comes back with errorKeys=DriverDataStore,
		// because DriverDataStore is a host-owned configuration item (it is not declared in
		// CreateChildConfigurationController's item list) that only Crestron Home can populate -
		// the host logs it as "Remaining configuration item to be set was not known yet:
		// DriverDataStore; Device configuration required." The driver has no legitimate value to
		// supply for it, so this error is expected here and must NOT be papered over by injecting
		// a placeholder into replayValues. Every cached child reports it, including ones that go
		// on to reach Running normally.
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
			// Configure Pro/Setup Program call by message text alone - the explicit "about to
			// replay..." line immediately below marks which calls originate from this internal
			// replay rather than from the host.
			LogInfo ($"PublishCachedChildControllers: about to replay configuration for controllerId='{descriptor.ControllerId}' (context='{context}') via internal GetFirstConfigurationStep/ApplyConfigurationStep call (not a host/Configure Pro request).");
			Crestron.DeviceDrivers.EntityModel.Data.DeviceConfiguration.ConfigurationStep firstStep = childConfigurationController.GetFirstConfigurationStep ();
			if (firstStep is not null && !string.IsNullOrWhiteSpace (firstStep.Id))
				{
				childConfigurationController.ApplyConfigurationStep (firstStep.Id, replayValues);
				LogInfo ($"PublishCachedChildControllers: controllerId='{descriptor.ControllerId}' (context='{context}') replayed prior configuration values via step-based ApplyConfigurationStep(stepId='{firstStep.Id}') into the recreated configuration controller (after host registration).");
				}
			else
				{
				childConfigurationController.ApplyConfiguration (replayValues);
				LogInfo ($"PublishCachedChildControllers: controllerId='{descriptor.ControllerId}' (context='{context}') had no first configuration step available; fell back to replaying prior configuration values via ApplyConfiguration into the recreated configuration controller (after host registration).");
				}
			}
		catch (Exception ex)
			{
			LogError ($"PublishCachedChildControllers: controllerId='{descriptor.ControllerId}' (context='{context}') failed to replay configuration values into the recreated configuration controller: {ex}");
			}
		}

	// DELIBERATELY NO STUCK-CACHED-CHILD WATCHDOG.
	//
	// A destructive watchdog used to live here: if a replayed cached child had not reached
	// Running within a bounded interval, it called RemoveChildFromConfiguration to tear the
	// child down and republish it as available. Successive measurements proved that this was
	// not a backstop but the actual cause of the "sensor offline after reload" reports, and
	// that its damage was permanent rather than transient:
	//
	//   12:52:11  five cached children published and replayed identically
	//   12:52:23  host ApplyAll adopted only S200B (Button) and KP303 (Outlet)
	//   12:55:11  the watchdog fired at its deadline for T315/T100 and tore them down
	//   12:58:15  Refresh.cs requeued them - reachable ONLY because the teardown had removed
	//             their _lightEntities entries - and they came back
	//
	// The teardown path runs ClearChildRuntimeState, which sets entry.IsConfigured=false and
	// flushes the managed-device cache to disk. That is the permanent part: on the NEXT
	// reload LoadManagedDeviceCacheIntoMemory seeds _configuredChildControllerIds only from
	// entries whose cached IsConfigured is still true, so a child the watchdog had previously
	// torn down is no longer replay-eligible in PublishCachedChildControllers at all. Measured
	// on the 13:39:42 reload: "Managed-device cache seeded 12 device entries ...
	// cachedConfiguredChildCount=2" - only S200B and KP303 replayed, and only those two came
	// online. T310/T315/T100 were published but never replayed, so the host was never asked to
	// adopt them and they sat at NotConfigured forever.
	//
	// In other words the watchdog converted a child that the host had merely been slow to
	// adopt into one that could never be adopted again without a manual re-add in Configure
	// Pro. Nothing here should destroy a child to provoke recovery: an unadopted cached child
	// is not evidence of a stuck child, because replay is indistinguishable between children
	// the host goes on to adopt and children it ignores (every one of them, including the two
	// that succeed, returns errorKeys=DriverDataStore).
	//
	// If a genuinely stuck cached child is ever observed again, diagnose why the host is not
	// completing the NotConfigured->Running handshake; do not reintroduce a timer that erases
	// persisted configuration state.

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
			// Legacy cache entries persisted Switch for button children before this was fixed to
			// use Sensor; keep accepting it on read so old caches still resolve correctly.
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
			childKind: childKind,
			hubChildCategory: entry.HubChildCategory);

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
		return _materializationInFlightControllerIds.ContainsKey (controllerId);
		}

	private bool TryMarkMaterializationInFlight (string controllerId)
		{
		return _materializationInFlightControllerIds.TryAdd (controllerId, 0);
		}

	private void ClearMaterializationInFlight (string controllerId)
		{
		_ = _materializationInFlightControllerIds.TryRemove (controllerId, out _);
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

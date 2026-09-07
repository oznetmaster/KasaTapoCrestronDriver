// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE file in the project root for full license information.

using System.Diagnostics;

using Crestron.DeviceDrivers.EntityModel;
using Crestron.DeviceDrivers.EntityModel.Data;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel;
using Crestron.DeviceDrivers.SDK.EntityModel.Data;

namespace KasaTapoCrestronDriver;

public sealed partial class PlatformDriver
	{
	// Model prefixes for plugs that are dimmable hardware despite classifying as
	// KasaDeviceType.Plug (DetermineSmartDeviceType matches the "PLUG" branch before the dimmer
	// branch is reached). A dimmable plug can only ever be used to dim a light - there is nothing
	// else a dim level could control - so it must always be treated as a light regardless of
	// TreatPlugsAsLights, the same way WallSwitch/KS240 always is below. On current evidence P135
	// is the only such device; add further model prefixes here if more are identified.
	private static readonly string[] _dimmablePlugModelPrefixes = { "P135" };

	internal static bool IsDimmablePlugModel (string? model)
		{
		if (string.IsNullOrWhiteSpace (model))
			{
			return false;
			}

		foreach (string prefix in _dimmablePlugModelPrefixes)
			{
			if (model!.StartsWith (prefix, StringComparison.OrdinalIgnoreCase))
				{
				return true;
				}
			}

		return false;
		}

	internal static bool IsSupportedLightDeviceType (KasaDeviceType deviceType, string? model = null)
		{
		// Wall switches (dimmer or on/off) are always genuine lighting loads. KasaTapoClient 1.3.0
		// keys brightness support on the negotiated SMART component rather than DeviceType, so
		// dimmable switch hardware such as KS240 (dimmer+fan, reports child_device and classifies
		// as WallSwitch rather than Dimmer) must be included here or it is silently dropped. Actual
		// dimmable-vs-on/off classification for a WallSwitch is resolved later, from the connected
		// device's real capability (see InferManagedLightKind), not assumed here.
		if (deviceType is KasaDeviceType.Bulb or KasaDeviceType.LightStrip or KasaDeviceType.Dimmer or KasaDeviceType.WallSwitch)
			{
			return true;
			}

		// Bare plugs and power strips are always discoverable and selectable for installation now
		// - each one individually decides (via its per-child "Treat As Light" configuration item)
		// whether it is materialized as a Light child or an Outlet child. See
		// ResolveManagedChildKind for how that per-child decision is resolved.
		// Hub roots (e.g. Tapo H100) are also always discoverable/itemizable, the same way Strip
		// roots are - see ResolveHubChildDescriptorsAsync, which itemizes each hub child
		// (T310/T315/T100/S200B, etc.) into its own Sensor/Button managed child descriptor.
		return deviceType == KasaDeviceType.Plug || deviceType == KasaDeviceType.Strip || deviceType == KasaDeviceType.Hub;
		}

	/// <summary>
	/// Resolves whether a plug/strip-outlet-capable controllerId should be materialized as a Light
	/// or an Outlet child. Bulbs/light strips/dimmers/wall switches are always Light and never call
	/// this. A known dimmable-plug model (e.g. P135) is always Light, since a dim level can only
	/// ever control a light. Otherwise the per-child "TreatAsLight" configuration item decides,
	/// defaulting to Outlet when not yet configured.
	/// </summary>
	private ManagedChildKind ResolveManagedChildKind (string controllerId, KasaDeviceType deviceType, string? model)
		{
		if (deviceType != KasaDeviceType.Plug && deviceType != KasaDeviceType.Strip)
			{
			return ManagedChildKind.Light;
			}

		if (deviceType == KasaDeviceType.Plug && IsDimmablePlugModel (model))
			{
			return ManagedChildKind.Light;
			}

		return _childTreatAsLight.TryGetValue (controllerId, out bool treatAsLight) && treatAsLight
			? ManagedChildKind.Light
			: ManagedChildKind.Outlet;
		}

	/// <summary>
	/// Returns the shared <see cref="ManagedParentDevicePoller"/> for the physical hub at
	/// <paramref name="configuration"/>'s host/port, creating it on first use. All hub children
	/// (Sensor/Button) materialized for the same physical hub share one poller instance, keyed by
	/// host+port since that is how the hub is physically identified - <see cref="ManagedLightDescriptor"/>
	/// itself carries no separate "parent hub" controllerId.
	/// </summary>
	private ManagedParentDevicePoller GetOrCreateHubPoller (DeviceConfiguration configuration)
		{
		string hostKey = $"{configuration.Host}:{configuration.Port}";
		lock (_hubPollersGate)
			{
			if (_hubPollers.TryGetValue (hostKey, out ManagedParentDevicePoller? existingPoller))
				{
				existingPoller.UpdateConfiguration (configuration);
				return existingPoller;
				}

			var poller = new ManagedParentDevicePoller (hostKey, configuration, _sharedConfiguration, _logger, _driverLogId);
			_hubPollers[hostKey] = poller;
			return poller;
			}
		}

	private IKasaManagedChildEntity CreateManagedLightEntity (ManagedLightDescriptor descriptor, DeviceConfiguration configuration)
		{
		if (descriptor.ChildKind == ManagedChildKind.Outlet)
			{
			return new KasaOutletEntity (
				descriptor.ControllerId,
				descriptor,
				configuration,
				HandleManagedLightDescriptorNameChanged,
				_sharedConfiguration,
				_resources,
				_logger,
				_driverLogId,
				_args.DriverDataDirectoryPath);
			}

		if (descriptor.ChildKind == ManagedChildKind.Sensor)
			{
			return new KasaSensorEntity (
				descriptor.ControllerId,
				descriptor,
				configuration,
				HandleManagedLightDescriptorNameChanged,
				_sharedConfiguration,
				_resources,
				_logger,
				_driverLogId,
				_args.DriverDataDirectoryPath,
				GetOrCreateHubPoller (configuration));
			}

		if (descriptor.ChildKind == ManagedChildKind.Button)
			{
			return new KasaButtonEntity (
				descriptor.ControllerId,
				descriptor,
				configuration,
				HandleManagedLightDescriptorNameChanged,
				_sharedConfiguration,
				_resources,
				_logger,
				_driverLogId,
				_args.DriverDataDirectoryPath);
			}

		return new KasaLightEntity (
			descriptor.ControllerId,
			descriptor,
			configuration,
			HandleManagedLightDescriptorNameChanged,
			_sharedConfiguration,
			SynchronizeProcessorBaselineAsync,
			_resources,
			_logger,
			_driverLogId);
		}

	private Task SynchronizeProcessorBaselineAsync (string loadName, ProcessorLightTuningMode mode, double level, double hue, double saturation, long colorTemperature, CancellationToken cancellationToken)
		{
		return _processorBaselineCoordinator.SynchronizeAsync (loadName, mode, level, hue, saturation, colorTemperature, cancellationToken);
		}

	/// <summary>
	/// When the per-child "Treat As Light" configuration item changes, the child must move
	/// between the <see cref="KasaLightEntity"/> and <see cref="KasaOutletEntity"/> concrete
	/// types. Those are different classes, so this cannot be done by mutating the existing
	/// entity in place - instead the existing entity/controller is stopped and disposed and a
	/// brand-new one is materialized and published, exactly as happens when a child is first
	/// added after discovery.
	///
	/// This intentionally does NOT remove and re-add the controllerId's platform:managedDevices
	/// entry. An earlier revision did that (PublishManagedDeviceRemoval followed by
	/// AddInitialManagedDeviceEntry) to try to force Crestron Home to detach a stale Light/Outlet
	/// room-tile facet, but that remove/re-add of an already room-bound managed-device entry is
	/// what caused the processor to log "Could not find associated registry object for object
	/// who's definition changed" and leave the device Offline/Driver Not Loaded in Configure Pro.
	/// The original (working) approach - swap the entity/ConfigurableDriverEntity in place and
	/// then publish an in-place UxCategory update via PublishManagedDeviceEntryUpdate - is used
	/// here instead, since that was proven to work correctly (aside from an unrelated cache
	/// restore-on-restart issue).
	///
	/// NOT A DRIVER DEFECT (Light -&gt; Outlet, investigated 2026-09-06): after switching a
	/// room-bound child back from Light to Outlet, Configure Pro can no longer enumerate it for
	/// the remainder of that session - it appears in neither the room list nor the
	/// available-device list, while the room count still includes it. The device stays fully
	/// functional on the touchpanel and correct in the setup UI throughout.
	///
	/// RESOLVED: after a driver reload the child is present in its room as an outlet and is
	/// fully configurable. Nothing in the driver's persisted or published model is malformed, so
	/// do not write driver code against this. Two earlier attempts to "fix" it from the driver (a
	/// full managedDevices snapshot after the delta, and reordering the outlet's property
	/// publication) were both based on incorrect theories and neither changed the outcome.
	///
	/// Note this is NOT a general "Configure Pro cannot refresh mid-session" limitation, which
	/// would be an overstatement: the Outlet -&gt; Light direction refreshes perfectly in the same
	/// session, picking up the new category with the correct light icon and light dialog. Only
	/// Light -&gt; Outlet fails to re-enumerate, and the reason appears to be which data source
	/// each UI reads (see below), not any inability to refresh.
	///
	/// The room membership is never lost. Four independent observations establish this: the
	/// touchpanel renders the device as part of its room, the setup UI shows it correctly,
	/// Configure Pro's own room count still includes it, and a reload restores the correct view.
	/// The device was also absent from the room list AND the available-device list simultaneously
	/// while still being counted - a device cannot be genuinely unbound and also absent from the
	/// available pool. The count is telling the truth; only Configure Pro's two lists go stale.
	///
	/// The 2026-09-06 15:43:01 capture localised the trigger. The driver side is correct and
	/// fully ordered: uxCategory=Outlet is published three times (TreatAsLight, ApplyAll,
	/// child-controller-status-running) and extension:uiDefinition is sent ahead of every value
	/// and indicator property. The teardown happens afterwards, driven by the host itself:
	///
	///   15:43:01.602  Status changed: Running
	///   15:43:02.057  Validate driver definition
	///   15:43:02.067  Remove Load adapter ID 52756 ... from Location "Office"
	///   15:43:02.070  Unloaded programming data for: Load adapter ID 52756
	///   15:43:02.092  DeviceIsOnline deviceId: 52754 status: 0
	///
	/// Removing the Load adapter is correct, not a fault: a Load adapter is a lighting construct
	/// and the child is no longer a light. It is not the room binding, which stays intact (see
	/// above). The evidence points at Configure Pro and the setup UI reading different data for
	/// their device lists:
	///
	/// - The "Unassigned:UNIVERSAL" flip logged at 15:43:02 is a Lights|Load|RemoveDevice event.
	///   What is unassigned is the load adapter, not device 52754; the removal line itself reads
	///   "Remove Load adapter ID 52756 for Demo KP115 ID 52754 from Location Office".
	/// - Locations.json is saved immediately afterwards and a reload reads back a correct
	///   outlet-in-Office, so the persisted location model was never wrong.
	/// - Critically, NO "Add Load adapter" occurs for this device after the transition, including
	///   across the reload at 15:48:24 ("Reload driver for Device Kasa/Tapo Controller ID
	///   #52677", with the log running to 15:55). The last was 52756 at 15:28:29, removed at
	///   15:43:02. Configure Pro nonetheless shows the child correctly in its room as a fully
	///   configurable outlet after that reload.
	///
	/// Note "Load adapter" here means specifically a LIGHTING load: every add/remove is tagged
	/// Lights|Load|AddDevice / Lights|Load|RemoveDevice. It is what backs the child while it is
	/// a light. While it is an outlet the extension device backs it instead - which is why the
	/// touchpanel kept working after the load adapter was destroyed at 15:43:02, with
	/// outletToggle/outletOn executing successfully at 15:43:18. So the absence of a load
	/// adapter after a Light -&gt; Outlet change is expected and correct, not a missing binding.
	///
	/// The host maintains a different representation for each state - a Load adapter backs the
	/// lighting view, an extension device backs the outlet view - and each is required for the
	/// touchpanel to render the child while it is in that state. They are alternatives, not
	/// concurrent: the child is a light or an outlet, never both. The driver mirrors this
	/// correctly. ManagedChildKind maps one-to-one onto DeviceUxCategory, and each child has
	/// exactly one entity in _lightEntities and one ConfigurableDriverEntity in
	/// _childControllers, replaced (not supplemented) when the kind changes.
	///
	/// That last point rules out the earlier guess that Configure Pro's lists are driven off
	/// Load adapter creation. The consistent reading is that Configure Pro holds an in-memory
	/// projection seeded from the lighting registry: when the load adapter is destroyed it drops
	/// the row and does not re-derive the device as an extension-type room member, until a
	/// reload rebuilds that projection from Locations.json. The setup UI and the touchpanel read
	/// the live location/device model, which is correct throughout - which is why they never
	/// show the problem and why this is not a "Configure Pro cannot refresh" limitation: the
	/// Outlet -&gt; Light direction refreshes fine because it ADDS a row to the source it watches.
	///
	/// Practical remediation for an installer who hits this: reload the driver (or restart the
	/// processor). No configuration is lost and no re-add is required.
	///
	/// Ruled out by experiment:
	/// - The host ignoring uxCategory on a per-entry delta. It does not ignore it; if it did,
	///   neither direction would apply the category at all, and the log shows Outlet published
	///   and honoured (the device functions as a real outlet, outletToggle/outletOn succeed).
	/// - A full managedDevices snapshot following the delta. Tried and reverted; the category
	///   was never the problem.
	/// - Entity property publication ordering. KasaOutletEntity.PublishStateSnapshot now sends
	///   the UI definition first and the online/ready indicators last, and the Load adapter is
	///   still removed 455ms after Running by "Validate driver definition".
	/// - Driver-level publication ordering. The entity is registered (UpdateSubControllers/
	///   NotifyChildPublished) before SetConfigured publishes any state.
	/// - Where the change is initiated. Configure Pro and the setup UI behave identically.
	/// - Remove/re-add of the managed-device entry (registry-object error, device left
	///   Offline/Driver Not Loaded).
	/// - Loss of room membership. Disproven by the touchpanel, the setup UI, the room count, and
	///   a reload restoring the correct outlet view with configuration intact.
	/// - Corruption of the persisted managed-device cache. Disproven by the same reload: the
	///   child comes back as a fully configurable outlet in its room, so IsConfigured and
	///   UxCategory are being persisted and restored correctly.
	///
	/// The captured sequence for a Light -&gt; Outlet change carries no ClearValues at all - only
	/// ApplyConfiguration[TreatAsLight] followed by ApplyConfiguration[ActivationMarker,
	/// DriverDataStore, TreatAsLight] - so this always takes the in-place recreate path below.
	/// </summary>
	private void ReconcileChildKindAfterTreatAsLightChange (string controllerId, string context)
		{
		if (!_knownDescriptors.TryGetValue (controllerId, out ManagedLightDescriptor? descriptor))
			{
			LogInfo ($"ReconcileChildKindAfterTreatAsLightChange: no known descriptor for controllerId='{controllerId}', context='{context}'; kind reconciliation deferred until (re)materialization.");
			return;
			}

		ManagedChildKind resolvedKind = ResolveManagedChildKind (controllerId, descriptor.DiscoveredDeviceType, descriptor.ModelName);
		if (resolvedKind == descriptor.ChildKind)
			{
			LogInfo ($"ReconcileChildKindAfterTreatAsLightChange: controllerId='{controllerId}' resolved kind {resolvedKind} unchanged; no recreation required.");
			return;
			}

		if (!_deviceConfigurations.TryGetValue (controllerId, out DeviceConfiguration? deviceConfiguration))
			{
			LogInfo ($"ReconcileChildKindAfterTreatAsLightChange: controllerId='{controllerId}' has no device configuration available yet; kind change to {resolvedKind} deferred until next materialization.");
			return;
			}

		LogInfo ($"ReconcileChildKindAfterTreatAsLightChange: controllerId='{controllerId}' changing kind from {descriptor.ChildKind} to {resolvedKind}, context='{context}'; disposing existing entity and recreating in place.");

		bool hadManagedDeviceEntry = _managedDevices.ContainsKey (controllerId);

		if (_childControllers.ContainsKey (controllerId))
			{
			UpdateSubControllers (null, new[] { controllerId });
			}

		if (_lightEntities.TryGetValue (controllerId, out IKasaManagedChildEntity? existingEntity))
			{
			existingEntity.Stop ();
			existingEntity.Dispose ();
			}

		bool wasConfigured = _configuredChildControllerIds.Contains (controllerId);
		ClearChildRuntimeState (controllerId, context);

		descriptor.ChildKind = resolvedKind;

		if (_managedDeviceCacheMetadata.TryGetValue (controllerId, out ManagedDeviceCacheEntry? cacheEntry))
			{
			cacheEntry.UxCategory = resolvedKind switch
				{
				ManagedChildKind.Outlet => DeviceUxCategory.Outlet,
				ManagedChildKind.Sensor => DeviceUxCategory.Sensor,
				ManagedChildKind.Button => DeviceUxCategory.Switch,
				ManagedChildKind.Thermostat => DeviceUxCategory.Thermostat,
				ManagedChildKind.Light => DeviceUxCategory.Light,
				_ => throw new InvalidOperationException ($"Cannot resolve DeviceUxCategory for controllerId='{controllerId}' because ManagedChildKind '{resolvedKind}' is unrecognized."),
				};

			// This UxCategory change must be flushed to the on-disk managed-device cache
			// immediately, not left in memory only. LoadManagedDeviceCacheIntoMemory (run on the
			// next driver reload) explicitly trusts the persisted UxCategory - rather than
			// re-resolving it - for any cache entry that is still IsConfigured (i.e. still sitting
			// in a room), specifically so an intentionally-published Light/Outlet choice survives a
			// reload. Without persisting here, a TreatAsLight revert while still configured in a
			// room updates the in-memory category correctly but leaves the on-disk cache pointing
			// at the old (pre-revert) category, so the next reload reconstructs the child with the
			// stale kind instead of the one the user actually chose.
			//
			// IsConfigured must be restored here as well. ClearChildRuntimeState (called above to
			// tear down the outgoing entity) unconditionally sets IsConfigured=false and flushes
			// that to disk, because for its normal callers the child really is leaving
			// configuration. Here it is not - the child stays installed in its room and only its
			// concrete kind changes - so leaving the flag false silently corrupts the cache: the
			// in-memory _configuredChildControllerIds entry is restored below, but the on-disk
			// entry keeps IsConfigured=false. On the next reload LoadManagedDeviceCacheIntoMemory
			// then (a) refuses to trust the persisted UxCategory and re-resolves the kind, and
			// (b) skips restoring _configuredChildControllerIds, so PublishCachedChildControllers
			// never replays ActivationMarker/TreatAsLight and the child comes back permanently
			// NotConfigured/Offline in Configure Pro. That is why toggling a plug to Light and
			// back to Outlet appears to work in the live session but breaks after a reload.
			cacheEntry.IsConfigured = wasConfigured;
			PersistManagedDeviceCache ();
			}

		IKasaManagedChildEntity newEntity = CreateManagedLightEntity (descriptor, deviceConfiguration);

		LoggingDriverConfigurationController childConfigurationController = CreateChildConfigurationController (descriptor);
		var controller = new ConfigurableDriverEntity (controllerId, (ReflectedAttributeDriverEntity)newEntity, childConfigurationController);
		_lightEntities[controllerId] = newEntity;
		_childControllers[controllerId] = controller;
		_childConfigurationControllers[controllerId] = childConfigurationController;

		if (wasConfigured)
			{
			_configuredChildControllerIds.Add (controllerId);

			// The freshly-constructed configuration controller has no CurrentValue for
			// ActivationMarker (or TreatAsLight) yet, so it starts life as NotConfigured -
			// normally that only becomes Configured/Running when Configure Pro's UI submits an
			// ApplyConfiguration for it. But this recreation happens programmatically in the
			// background, so Configure Pro is never asked to resubmit anything for this new
			// controller instance. Left alone, the child stays permanently NotConfigured and
			// Configure Pro reports that the driver needs reconfiguring. Since this child was
			// already configured before the kind swap, replay the same values Configure Pro
			// would have sent so the new controller transitions itself straight back to
			// Configured/Running.
			bool replayTreatAsLight = _childTreatAsLight.TryGetValue (controllerId, out bool replayValue) && replayValue;
			var replayValues = new Dictionary<string, string> { ["ActivationMarker"] = "true" };
			if (IsTreatAsLightChoiceEligible (descriptor))
				{
				replayValues["TreatAsLight"] = replayTreatAsLight ? "true" : "false";
				}

			try
				{
				childConfigurationController.ApplyConfiguration (replayValues);
				LogInfo ($"ReconcileChildKindAfterTreatAsLightChange: controllerId='{controllerId}' replayed prior configuration values into the recreated configuration controller to avoid it getting stuck NotConfigured.");
				}
			catch (Exception ex)
				{
				LogError ($"ReconcileChildKindAfterTreatAsLightChange: controllerId='{controllerId}' failed to replay configuration values into the recreated configuration controller: {ex}");
				}
			}

		if (hadManagedDeviceEntry)
			{
			// In-place UxCategory update only - do not remove/re-add the managed-device entry.
			// See the method summary for why a remove/re-add regressed the KP115 TreatAsLight
			// switch (processor registry-object error, device ending up Offline/Driver Not
			// Loaded).
			PublishManagedDeviceEntryUpdate (controllerId, context);
			}
		else
			{
			AddInitialManagedDeviceEntry (descriptor);
			}

		UpdateSubControllers (new[] { controller }, null);
		newEntity.NotifyChildPublished ();

		// SetConfigured must not run before the entity above has actually been registered with
		// the host. It synchronously starts the connect/initialize work that flips
		// onlineIndicator/readyIndicator and publishes the first state snapshot; raised against
		// an entity the host has not yet seen, those notifications are simply dropped. The
		// recreated child then reaches Running while the processor still believes it is offline
		// (SystemManager_DeviceStatusChanged status: 0), which is what made a Light->Outlet
		// switch disappear from Configure Pro even though the room UI and touchscreen were fine.
		// The reverse direction never showed this because KasaLightEntity defers and replays its
		// startup snapshot (TryPublishDeferredStartupSnapshot) whereas KasaOutletEntity does not.
		newEntity.SetConfigured (wasConfigured, context);

		ActivatePublishedChildIfRunning (controller, context);

		LogInfo ($"ReconcileChildKindAfterTreatAsLightChange: controllerId='{controllerId}' recreated as {resolvedKind}, wasConfigured={wasConfigured}, context='{context}'.");
		}

	private LoggingDriverConfigurationController CreateChildConfigurationController (ManagedLightDescriptor descriptor)
		{
		bool supportsTreatAsLightChoice = IsTreatAsLightChoiceEligible (descriptor);
		bool currentTreatAsLight = _childTreatAsLight.TryGetValue (descriptor.ControllerId, out bool treatAsLight) && treatAsLight;
		LogInfo ($"CreateChildConfigurationController: controllerId='{descriptor.ControllerId}', supportsTreatAsLightChoice={supportsTreatAsLightChoice}, currentTreatAsLight={currentTreatAsLight} (used as TreatAsLight DefaultValue), childKind={descriptor.ChildKind}.");

		var items = new List<ConfigurationItemDefinition>
			{
			new ()
				{
				Id = "ActivationMarker",
				Title = "Ready",
				Description = "Confirms the device configuration has been applied.",
				Availability = Crestron.DeviceDrivers.EntityModel.Data.DeviceConfiguration.ConfigurationItemAvailability.Always,
				ValueType = Crestron.DeviceDrivers.EntityModel.Data.DeviceConfiguration.ConfigurationItemValueType.Boolean,
				UsageContext = ConfigurationItemContext.Generic.Prompt,
				DefaultValue = "true",
				Required = true,
				Persistent = true,
				},
			};

		var stepItems = new List<string> { "ActivationMarker" };

		if (supportsTreatAsLightChoice)
			{
			items.Add (new ()
				{
				Id = "TreatAsLight",
				Title = "Treat As Light",
				Description = "Expose this plug/outlet as a light entity when it controls a lamp or other lighting load.",
				Availability = Crestron.DeviceDrivers.EntityModel.Data.DeviceConfiguration.ConfigurationItemAvailability.Always,
				ValueType = Crestron.DeviceDrivers.EntityModel.Data.DeviceConfiguration.ConfigurationItemValueType.Boolean,
				UsageContext = ConfigurationItemContext.Generic.Prompt,
				DefaultValue = currentTreatAsLight ? "true" : "false",
				Required = true,
				Persistent = true,
				});
			stepItems.Add ("TreatAsLight");
			}

		var definition = new ConfigurationStepsDefinition
			{
			Items = items,
			Steps = new List<ConfigurationStepDefinition>
				{
				new ()
					{
					StepId = "Activation",
					Items = stepItems,
					},
				},
			FirstStep = "Activation",
			// Both default to false when left unset. IsNotOfflineConfigurable=false is required so
			// Configure Pro treats this child as configurable while the device is not actively
			// mid-handshake with the host (matches the root driver's own configuration args).
			// IncludePersistentValueData=true is the actual fix for the KP303-vs-KP115 Installer
			// Settings discrepancy: without it, Configure Pro only recognizes a child controller as
			// "configured" after ITS OWN live ApplyConfiguration round-trip through the host - our
			// internal replay in PublishCachedChildControllers (which calls
			// GetFirstConfigurationStep/ApplyConfigurationStep programmatically, not through a real
			// Configure Pro session) is invisible to that tracking. Setting this true tells Configure
			// Pro to honor the controller's persisted configuration values directly, so a
			// recreated-from-cache child is recognized as already configured (and shows Installer
			// Settings) without requiring the user to have manually reconfigured it since the reload.
			IncludePersistentValueData = true,
			IsNotOfflineConfigurable = false,
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

	private static bool IsTreatAsLightChoiceEligible (ManagedLightDescriptor descriptor)
		{
		if (descriptor.DiscoveredDeviceType != KasaDeviceType.Plug && descriptor.DiscoveredDeviceType != KasaDeviceType.Strip)
			{
			return false;
			}

		// A known dimmable plug model (e.g. P135) is always forced to Light - there is no
		// meaningful outlet-only interpretation of a dim level - so it never shows the choice.
		return descriptor.DiscoveredDeviceType != KasaDeviceType.Plug || !IsDimmablePlugModel (descriptor.ModelName);
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

	private void ActivatePublishedChildIfRunning (ConfigurableDriverEntity controller, string context)
		{
		if (!IsChildConfigurationControllerRunning (controller))
			{
			return;
			}

		LogInfo ($"ActivatePublishedChildIfRunning: controllerId='{controller.ControllerId}' is already Running after publication; reconciling activation to avoid a missed status-change event.");
		ActivateChildController (controller.ControllerId, context);
		}

	private ConfigurationItemErrors? ApplyChildConfigurationItems (
		string controllerId,
		DataDrivenConfigurationController.ApplyConfigurationAction action,
		string stepId,
		IDictionary<string, DriverEntityValue?> values)
		{
		LogInfo ($"ApplyChildConfigurationItems: controllerId='{controllerId}', action='{action}', stepId='{stepId}', hasExistingLightEntity={_lightEntities.ContainsKey (controllerId)}, valueKeys=[{string.Join (", ", values.Keys.OrderBy (key => key, StringComparer.OrdinalIgnoreCase))}].");
		if (values.TryGetValue ("TreatAsLight", out var treatAsLightValue) && treatAsLightValue.HasValue)
			{
			bool incomingTreatAsLight = treatAsLightValue.Value.GetValue<bool> ();
			bool hasPreviousTreatAsLight = _childTreatAsLight.TryGetValue (controllerId, out bool previousTreatAsLight);
			LogInfo ($"ApplyChildConfigurationItems: controllerId='{controllerId}' received TreatAsLight={incomingTreatAsLight} (previous stored value={(hasPreviousTreatAsLight ? previousTreatAsLight.ToString () : "<none>")}).");

			// An absent _childTreatAsLight entry means "no known preference", NOT "Outlet".
			// RemoveChildFromConfiguration (and the refresh removal path) erase this entry, so a
			// re-add after a removal arrives here with no baseline. Defaulting the missing value
			// to false made every re-add carrying TreatAsLight=true look like a genuine
			// Outlet->Light toggle, which tore the child down and recreated it as a Light - that
			// is what caused the host to mint an uncommissioned lighting-load record for a plug
			// that the user had never re-classified. Compare against the descriptor's actual
			// current kind instead, so a re-add only counts as a change when it truly disagrees
			// with what the child already is.
			bool currentlyLight = hasPreviousTreatAsLight
				? previousTreatAsLight
				: _knownDescriptors.TryGetValue (controllerId, out ManagedLightDescriptor? existingDescriptor)
					&& existingDescriptor.ChildKind == ManagedChildKind.Light;

			bool kindActuallyChanging = incomingTreatAsLight != currentlyLight;
			if (!hasPreviousTreatAsLight)
				{
				LogInfo ($"ApplyChildConfigurationItems: controllerId='{controllerId}' had no stored TreatAsLight preference; resolved current kind as {(currentlyLight ? "Light" : "Outlet")} from the descriptor, kindActuallyChanging={kindActuallyChanging}.");
				}

			_childTreatAsLight[controllerId] = incomingTreatAsLight;

			// Disposing/recreating the child's entity and configuration controller synchronously
			// here would tear down the very DelegateDataDrivenConfigurationController/transport
			// that is currently in the middle of running this ApplyConfiguration call, which the
			// SDK observes as a failed apply ("Received response Error") and reacts to by
			// re-pushing its last-known-good (i.e. stale, pre-toggle) configuration value - which
			// looks exactly like the toggle "changing back by itself". Deferring the actual
			// dispose/recreate until after this callback has returned avoids tearing down the
			// in-flight call.
			//
			// A bare Task.Run is not sufficient: it is scheduled on the thread pool immediately
			// and can start running (and complete the dispose/recreate) before this
			// ApplyConfigurationItems call has actually returned control back up to the SDK's
			// ApplyConfiguration dispatch, which is exactly the same "tear down the in-flight
			// call" race the comment above describes, just moved from synchronous-in-this-call to
			// synchronous-in-the-async-continuation. A short delay before the reconcile body runs
			// gives ApplyConfiguration/ApplyConfigurationItems time to unwind and return the
			// ConfigurationItemErrors? result to the SDK first, so the dispose/recreate never races
			// the in-flight apply.
			if (kindActuallyChanging)
				{
				_ = Task.Run (async () =>
					{
					await Task.Delay (TimeSpan.FromMilliseconds (250)).ConfigureAwait (false);
					ReconcileChildKindAfterTreatAsLightChange (controllerId, "child-config-callback:TreatAsLight");
					});
				}
			}

		if (action == DataDrivenConfigurationController.ApplyConfigurationAction.ClearValues)
			{
			// ClearValues is what Crestron Home sends when the device is deleted from the
			// controller's "Add a device" configuration list. Previously this was treated as a
			// no-op, which left the child's internal configured/in-use state (and its
			// platform:managedDevices entry) intact - so on the next discovery refresh or driver
			// reload the deleted device would be resurrected into the managed-device list
			// instead of staying removed. Tear the child down the same way discovery-driven
			// removal does so the deletion actually sticks.
			//
			// NOTE: deletion is not the only trigger. Processor logs show Crestron Home also
			// sending ClearValues around a per-child "Treat As Light" transition, in both
			// directions, with the same valueKeys as the deletion case. The values themselves
			// arrive null on ClearValues (the TreatAsLight block above does not run, because
			// HasValue is false), so this path is safe to treat uniformly as a teardown. What
			// must NOT be assumed is that the erased _childTreatAsLight entry can be
			// re-defaulted to false on the way back in - see the descriptor-based fallback in
			// the TreatAsLight block above.
			//
			// RemoveChildFromConfiguration disposes/tears down the very entity and
			// configuration controller that is currently in the middle of running this
			// ApplyConfiguration call if done synchronously here - the SDK observes that as a
			// failed apply and reacts by re-pushing its last-known-good (i.e. stale,
			// pre-removal) configuration/state, which looks exactly like the managed-device
			// entry "staying Light" immediately after removal even though the on-disk cache was
			// already correctly updated to Outlet. This is the same race described in
			// ReconcileChildKindAfterTreatAsLightChange's summary above; deferring the actual
			// dispose/removal until after this callback has returned avoids it here too.
			LogInfo ($"ApplyChildConfigurationItems: ClearValues for controllerId='{controllerId}'; treating as child removal from configuration.");
			_ = Task.Run (async () =>
				{
				await Task.Delay (TimeSpan.FromMilliseconds (250)).ConfigureAwait (false);
				RemoveChildFromConfiguration (controllerId, "child-config-callback:ClearValues");
				});
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
		if (_lightEntities.TryGetValue (controllerId, out IKasaManagedChildEntity? lightEntity))
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
		if (_lightEntities.TryGetValue (controllerId, out IKasaManagedChildEntity? lightEntity))
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
		if (_lightEntities.TryGetValue (controllerId, out IKasaManagedChildEntity? lightEntity))
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

	private void RemoveChildFromConfiguration (string controllerId, string context)
		{
		// Capture the descriptor before ClearChildRuntimeState runs - it doesn't remove
		// _knownDescriptors/_deviceConfigurations/_managedDeviceCacheMetadata (those are
		// discovery-owned state, deliberately preserved so the physical device keeps its
		// identity), so we can use it below to immediately republish the device as an
		// available (unconfigured) entry instead of waiting for the next discovery pass.
		_knownDescriptors.TryGetValue (controllerId, out ManagedLightDescriptor? descriptor);

		_inUseChildControllerIds.Remove (controllerId);

		bool removedManagedDevice = PublishManagedDeviceRemoval (controllerId);

		if (_lightEntities.TryGetValue (controllerId, out IKasaManagedChildEntity? removedEntity))
			{
			removedEntity.Stop ();
			removedEntity.Dispose ();
			}

		if (_childControllers.ContainsKey (controllerId))
			{
			UpdateSubControllers (null, new List<string> { controllerId });
			}

		ClearChildRuntimeState (controllerId, context);

		// ClearValues deletes the child's entire configuration, including any per-child
		// "Treat As Light" override - so that override must be forgotten here too. Otherwise
		// the stale _childTreatAsLight entry (and the descriptor's now-stale ChildKind) would
		// cause the device to be republished still classified as whatever kind it was before
		// deletion (e.g. Light for a plug the user had switched to Light), instead of
		// re-resolving to its correct default kind.
		_childTreatAsLight.Remove (controllerId);

		if (descriptor is not null)
			{
			// ResolveManagedChildKind only understands the Plug/Strip "Treat As Light" toggle;
			// for every other device type (including Hub children, which are always Sensor/Button
			// telemetry with no such toggle) it unconditionally returns ManagedChildKind.Light.
			// Only re-resolve for Plug/Strip descriptors so a hub child's Sensor/Button kind is
			// never clobbered back to Light here.
			if (descriptor.DiscoveredDeviceType == KasaDeviceType.Plug || descriptor.DiscoveredDeviceType == KasaDeviceType.Strip)
				{
				descriptor.ChildKind = ResolveManagedChildKind (controllerId, descriptor.DiscoveredDeviceType, descriptor.ModelName);
				}
			}

		// The device is still physically discoverable and its identity is still known, so
		// re-publish it immediately as an unconfigured managed-device entry - this is what
		// makes it reappear in the "Add a device" list right away instead of only after the
		// next discovery refresh.
		bool republishedAsAvailable = false;
		if (descriptor is not null && _deviceConfigurations.ContainsKey (controllerId))
			{
			republishedAsAvailable = AddInitialManagedDeviceEntry (descriptor);
			}

		LogInfo ($"RemoveChildFromConfiguration: controllerId='{controllerId}', context='{context}', removedManagedDevice={removedManagedDevice}, republishedAsAvailable={republishedAsAvailable}, resolvedChildKind={descriptor?.ChildKind.ToString() ?? "<none>"}.");
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
				UpdateSubControllers (new[] { deferredController }, null);
				if (_lightEntities.TryGetValue (descriptor.ControllerId, out IKasaManagedChildEntity? lightEntity))
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

		// A rediscovery pass can leave the device's name and raw ManagedLightKind unchanged
		// while its resolved ChildKind (Outlet vs Light) has actually changed - e.g. after
		// RemoveChildFromConfiguration re-resolves ChildKind back to Outlet in memory once a
		// plug that had been switched to "Treat As Light" is removed from the room. If that
		// UxCategory mismatch is ignored here, PublishManagedDeviceEntryUpdate never runs and
		// the stale platform:managedDevices entry keeps reporting the old category (e.g. Light)
		// even though the device has already reverted to Outlet internally.
		DeviceUxCategory resolvedUxCategory = descriptor.ChildKind switch
			{
			ManagedChildKind.Outlet => DeviceUxCategory.Outlet,
			ManagedChildKind.Sensor => DeviceUxCategory.Sensor,
			ManagedChildKind.Button => DeviceUxCategory.Switch,
			ManagedChildKind.Thermostat => DeviceUxCategory.Thermostat,
			ManagedChildKind.Light => DeviceUxCategory.Light,
			_ => existingEntry.UxCategory,
			};
		bool uxCategoryChanged = existingEntry.UxCategory != resolvedUxCategory;

		if (string.Equals (existingEntry.Name, descriptor.Name, StringComparison.Ordinal) && !kindChanged && !uxCategoryChanged)
			{
			descriptor.AwaitingConnectedIdentity = false;
			LogInfo ($"HandleManagedLightDescriptorNameChanged: no effective managed-device identity change for controllerId='{descriptor.ControllerId}'.");
			return;
			}

		if (uxCategoryChanged)
			{
			LogInfo ($"HandleManagedLightDescriptorNameChanged: controllerId='{descriptor.ControllerId}' UxCategory mismatch detected (existing={existingEntry.UxCategory}, resolved={resolvedUxCategory}); republishing managed-device entry.");
			if (existingMetadata is not null)
				{
				existingMetadata.UxCategory = resolvedUxCategory;
				}
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

	private async Task<IReadOnlyList<ManagedLightDescriptor>> ResolveManagedLightDescriptorsAsync (
		DiscoveryResult discoveryResult,
		DeviceConfiguration configuration,
		CancellationToken cancellationToken)
		{
		if (discoveryResult.DeviceType == KasaDeviceType.Strip)
			{
			// Strips are the only device type whose managed devices (child outlets) cannot be
			// determined synchronously from DiscoveryResult; see ResolveStripChildDescriptorsAsync.
			// Every strip's outlets are always itemized now, each independently deciding light vs
			// outlet via its own per-child configuration.
			return await ResolveStripChildDescriptorsAsync (discoveryResult, configuration, cancellationToken).ConfigureAwait (false);
			}

		if (discoveryResult.DeviceType == KasaDeviceType.Hub)
			{
			// Hub children (Tapo H100 and similar) are likewise only known once connected; see
			// ResolveHubChildDescriptorsAsync.
			return await ResolveHubChildDescriptorsAsync (discoveryResult, configuration, cancellationToken).ConfigureAwait (false);
			}

		return CreateManagedLightDescriptors (discoveryResult).ToArray ();
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
			case KasaDeviceType.WallSwitch:
				yield return new ManagedLightDescriptor (
					controllerId,
					discoveryResult.Host,
					discoveryResult.DeviceType,
					rootName,
					rootModel,
					rootSerial,
					cachedManagedLightKind == ManagedLightKind.Unknown ? ManagedLightKind.Unknown : cachedManagedLightKind,
						awaitingConnectedIdentity: IsTapoDiscoveryResult (discoveryResult) && string.IsNullOrWhiteSpace (discoveryResult.Alias),
					discoveryResult.DeviceId,
					childKind: ManagedChildKind.Light);
				yield break;

			case KasaDeviceType.Plug:
				// P135 and similar dimmable plugs advertise brightness/dimmer_calibration but still
				// classify as DeviceType.Plug (the "PLUG" branch in DetermineSmartDeviceType matches
				// before the dimmer branch is reached). A known dimmable plug model is always
				// materialized as a Light, since a dim level can only ever control a light. All
				// other plugs are always discoverable too, and individually resolve Light vs Outlet
				// via ResolveManagedChildKind (per-child "Treat As Light" configuration). Defer to
				// the cached/Unknown kind here too - same as the unconditional group above - so
				// InferManagedLightKind can resolve Dimmable vs OnOff from the connected device's
				// actual negotiated capability instead of assuming every plug is on/off-only.
				yield return new ManagedLightDescriptor (
					controllerId,
					discoveryResult.Host,
					discoveryResult.DeviceType,
					rootName,
					rootModel,
					rootSerial,
					cachedManagedLightKind == ManagedLightKind.Unknown ? ManagedLightKind.Unknown : cachedManagedLightKind,
						awaitingConnectedIdentity: IsTapoDiscoveryResult (discoveryResult) && string.IsNullOrWhiteSpace (discoveryResult.Alias),
					discoveryResult.DeviceId,
					childKind: ResolveManagedChildKind (controllerId, discoveryResult.DeviceType, discoveryResult.Model));
				yield break;

			// KasaDeviceType.Strip is itemized into one descriptor per child outlet by
			// ResolveStripChildDescriptorsAsync instead of being yielded here, because the strip's
			// child outlets are only known after connecting to the device (DiscoveryResult alone does
			// not expose them). See RefreshPlatformAsync's strip pre-resolution pass.
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

		// Strip child outlets are plain on/off (see ResolveStripChildDescriptorsAsync, which always
		// assigns ManagedLightKind.OnOff to itemized outlets directly and never calls this method for
		// them), but a bare Plug can be a dimmable device (e.g. P135) that only reveals its real
		// capability once connected. Leave Plug as Unknown here so InferManagedLightKind resolves it
		// from the negotiated brightness component instead of assuming on/off-only.
		return deviceType == KasaDeviceType.Strip
			? ManagedLightKind.OnOff
			: ManagedLightKind.Unknown;
		}

	private static DeviceConfiguration CreateDeviceConfiguration (DiscoveryResult discoveryResult, DeviceCredentials? credentials, TimeSpan timeout)
		{
		return Discover.CreateConfiguration (discoveryResult, credentials, timeout);
		}

	// Power strip child outlets (KasaDeviceType.Strip) are only known once connected -
	// DiscoveryResult alone does not expose them - so, unlike every other supported device type,
	// strips must be itemized into one ManagedLightDescriptor per child outlet via a one-shot
	// connect rather than synchronously in CreateManagedLightDescriptors. Each child is a simple
	// on/off outlet: no light-specific baseline/color/dimmer handling ever applies to it, because
	// ManagedLightKind.OnOff already disables all of that in KasaLightEntity.
	private async Task<IReadOnlyList<ManagedLightDescriptor>> ResolveStripChildDescriptorsAsync (
		DiscoveryResult discoveryResult,
		DeviceConfiguration configuration,
		CancellationToken cancellationToken)
		{
		string stripControllerId = CreateControllerId (discoveryResult);

		if (_resolvedStripChildDescriptors.TryGetValue (stripControllerId, out List<ManagedLightDescriptor>? cachedChildDescriptors))
			{
			// _deviceConfigurations/_discoveryResults are cleared and rebuilt every refresh pass
			// (see RefreshPlatformAsync), so cached child descriptors must be re-registered here
			// on every call, not just when they are first resolved, otherwise later materialization
			// lookups fail with "no device configuration is available" and the child is marked missing.
			foreach (ManagedLightDescriptor cachedChildDescriptor in cachedChildDescriptors)
				{
				_deviceConfigurations[cachedChildDescriptor.ControllerId] = configuration;
				_discoveryResults[cachedChildDescriptor.ControllerId] = discoveryResult;
				}

			return cachedChildDescriptors;
			}

		if (!_stripChildResolutionInFlightControllerIds.TryAdd (stripControllerId, 0))
			{
			LogInfo ($"ResolveStripChildDescriptorsAsync: skipped duplicate in-flight strip child resolution for controllerId='{stripControllerId}', host='{discoveryResult.Host}'.");
			return Array.Empty<ManagedLightDescriptor> ();
			}

		try
			{
			string rootName = ResolveManagedDeviceName (stripControllerId, ResolveDiscoveryName (discoveryResult), discoveryResult.DeviceId, discoveryResult.Host);
			string rootModel = discoveryResult.Model ?? "Kasa/Tapo Device";

			LogInfo ($"ResolveStripChildDescriptorsAsync: attempting one-shot child expansion connect for host='{discoveryResult.Host}', deviceId='{discoveryResult.DeviceId ?? "<null>"}', model='{rootModel}'.");

			using var expansionTimeoutSource = CancellationTokenSource.CreateLinkedTokenSource (cancellationToken);
			TimeSpan expansionTimeout = configuration.Timeout > TimeSpan.Zero
				? configuration.Timeout + TimeSpan.FromSeconds (2)
				: DefaultDiscoveryTimeout + TimeSpan.FromSeconds (2);
			expansionTimeoutSource.CancelAfter (expansionTimeout);

			KasaDevice? device = null;
			bool deviceAdopted = false;
			try
				{
				// KasaTapoClient 1.4.0+ has GetOrConnectSharedAsync refresh a cached shared instance's
				// state (Children/LightState/etc.) via UpdateAsync whenever updateState: true is passed,
				// even on a cache hit - previously it silently ignored updateState on cache hits and could
				// return a long-lived instance whose Children the strip's very first connect happened to
				// observe as empty, forever, until a full driver reload cleared the cache. That refresh
				// now happens inside GetOrConnectSharedAsync itself, so no extra re-fetch/dispose workaround
				// is needed here.
				device = await Discover.GetOrConnectSharedAsync (configuration, updateState: true, cancellationToken: expansionTimeoutSource.Token).ConfigureAwait (false);

				if (device.Children.Count == 0)
					{
					LogInfo ($"ResolveStripChildDescriptorsAsync: host='{discoveryResult.Host}' reported 0 child outlets after connecting; skipping itemization for this pass.");
					return Array.Empty<ManagedLightDescriptor> ();
					}

				var childDescriptors = new List<ManagedLightDescriptor> (device.Children.Count);
				int outletIndex = 0;
				foreach (ChildDeviceInfo child in device.Children)
					{
					outletIndex++;
					string childName = string.IsNullOrWhiteSpace (child.Alias)
						? $"{rootName} Outlet {outletIndex}"
						: child.Alias!;
					string childModel = child.Model ?? rootModel;
					string childSerial = child.Id;
					string childControllerId = CreateControllerId (discoveryResult, child.Id);

					// Each child outlet gets its own controllerId (distinct from the strip's root
					// controllerId), but shares the same physical device/connection configuration.
					// Materialization (CreateManagedDeviceCacheEntry) looks up _deviceConfigurations
					// and _discoveryResults by controllerId, so both must be registered here or
					// materialization fails with "no device configuration is available" and the
					// child gets marked missing.
					_deviceConfigurations[childControllerId] = configuration;
					_discoveryResults[childControllerId] = discoveryResult;

					childDescriptors.Add (new ManagedLightDescriptor (
						childControllerId,
						discoveryResult.Host,
						discoveryResult.DeviceType,
						childName,
						childModel,
						childSerial,
						ManagedLightKind.OnOff,
						awaitingConnectedIdentity: false,
						discoveryResult.DeviceId,
						child.Id,
						childKind: ResolveManagedChildKind (childControllerId, discoveryResult.DeviceType, childModel)));
					}

				_resolvedStripChildDescriptors[stripControllerId] = childDescriptors;
				LogInfo ($"ResolveStripChildDescriptorsAsync: resolved {childDescriptors.Count} child outlet(s) for host='{discoveryResult.Host}'.");

				// The shared connection is intentionally left for the child light entities'
				// own startup connects (Discover.GetOrConnectSharedAsync shares one instance
				// per Host:Port), exactly like EnrichDiscoveryAliasCacheAsync does for bulbs;
				// it is not adopted directly by any single entity here because there may be
				// multiple child entities for one physical strip.
				deviceAdopted = true;
				return childDescriptors;
				}
			finally
				{
				if (!deviceAdopted)
					{
					device?.Dispose ();
					}
				}
			}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
			{
			LogInfo ($"ResolveStripChildDescriptorsAsync: timed out for controllerId='{stripControllerId}', host='{discoveryResult.Host}'.");
			return Array.Empty<ManagedLightDescriptor> ();
			}
		catch (OperationCanceledException)
			{
			LogInfo ($"ResolveStripChildDescriptorsAsync: canceled for host='{discoveryResult.Host}'.");
			return Array.Empty<ManagedLightDescriptor> ();
			}
		catch (Exception ex) when (!(ex is OperationCanceledException))
			{
			LogInfo ($"ResolveStripChildDescriptorsAsync: child expansion failed for host='{discoveryResult.Host}': {ex.Message}");
			return Array.Empty<ManagedLightDescriptor> ();
			}
		finally
			{
			_stripChildResolutionInFlightControllerIds.TryRemove (stripControllerId, out _);
			}
		}

	// Classifies a hub child (Tapo H100 and similar) into a HubChildCategory/ManagedChildKind
	// pair from its reported Category/Model/Features, since DiscoveryResult alone never exposes
	// per-child capability. Every recognized hub accessory today (T310/T315 temperature+humidity,
	// T100 contact/motion, S200B button) reports enough via ChildDeviceInfo.Category or Features
	// to distinguish it; anything unrecognized falls back to a generic Sensor classification
	// rather than being silently dropped, so new/unknown hub accessories are still materialized
	// (with whatever telemetry KasaSensorEntity can read) instead of disappearing.
	private static (ManagedChildKind ChildKind, HubChildCategory Category) ResolveHubChildKind (ChildDeviceInfo child)
		{
		string category = child.Category ?? string.Empty;
		string model = child.Model ?? string.Empty;

		bool hasFeature (string featureId) =>
			child.Features.Any (feature => string.Equals (feature.Id, featureId, StringComparison.OrdinalIgnoreCase));

		bool isButtonModel = category.Contains ("switch", StringComparison.OrdinalIgnoreCase)
			|| category.Contains ("button", StringComparison.OrdinalIgnoreCase)
			|| model.StartsWith ("S200", StringComparison.OrdinalIgnoreCase)
			|| hasFeature ("double_click") || hasFeature ("trigger_log");

		if (isButtonModel)
			{
			return (ManagedChildKind.Button, HubChildCategory.Button);
			}

		bool hasTemperature = hasFeature ("temperature") || model.StartsWith ("T31", StringComparison.OrdinalIgnoreCase);
		bool hasHumidity = hasFeature ("humidity") || model.StartsWith ("T31", StringComparison.OrdinalIgnoreCase);
		bool hasContact = hasFeature ("open") || model.StartsWith ("T100", StringComparison.OrdinalIgnoreCase);
		bool hasMotion = hasFeature ("detected") || category.Contains ("motion", StringComparison.OrdinalIgnoreCase);
		bool hasWaterLeak = hasFeature ("water_leak") || category.Contains ("leak", StringComparison.OrdinalIgnoreCase);

		if (hasContact)
			{
			return (ManagedChildKind.Sensor, HubChildCategory.Contact);
			}

		if (hasMotion)
			{
			return (ManagedChildKind.Sensor, HubChildCategory.Motion);
			}

		if (hasWaterLeak)
			{
			return (ManagedChildKind.Sensor, HubChildCategory.WaterLeak);
			}

		if (hasTemperature && hasHumidity)
			{
			return (ManagedChildKind.Sensor, HubChildCategory.TemperatureHumidity);
			}

		if (hasTemperature)
			{
			return (ManagedChildKind.Sensor, HubChildCategory.Temperature);
			}

		if (hasHumidity)
			{
			return (ManagedChildKind.Sensor, HubChildCategory.Humidity);
			}

		return (ManagedChildKind.Sensor, HubChildCategory.None);
		}

	// Hub children (KasaDeviceType.Hub) are, like strip children, only known once connected -
	// DiscoveryResult alone does not expose them. Unlike strip children, hub children (Tapo H100
	// contact/motion/temperature/humidity sensors and S200B buttons) are read-only telemetry with
	// no relay control, so they are always itemized as Sensor/Button descriptors and never as
	// Light/Outlet.
	private async Task<IReadOnlyList<ManagedLightDescriptor>> ResolveHubChildDescriptorsAsync (
		DiscoveryResult discoveryResult,
		DeviceConfiguration configuration,
		CancellationToken cancellationToken)
		{
		string hubControllerId = CreateControllerId (discoveryResult);

		if (_resolvedHubChildDescriptors.TryGetValue (hubControllerId, out List<ManagedLightDescriptor>? cachedChildDescriptors))
			{
			// _deviceConfigurations/_discoveryResults are cleared and rebuilt every refresh pass
			// (see RefreshPlatformAsync), so cached child descriptors must be re-registered here
			// on every call, not just when they are first resolved, otherwise later materialization
			// lookups fail with "no device configuration is available" and the child is marked missing.
			foreach (ManagedLightDescriptor cachedChildDescriptor in cachedChildDescriptors)
				{
				_deviceConfigurations[cachedChildDescriptor.ControllerId] = configuration;
				_discoveryResults[cachedChildDescriptor.ControllerId] = discoveryResult;
				}

			return cachedChildDescriptors;
			}

		if (!_hubChildResolutionInFlightControllerIds.TryAdd (hubControllerId, 0))
			{
			LogInfo ($"ResolveHubChildDescriptorsAsync: skipped duplicate in-flight hub child resolution for controllerId='{hubControllerId}', host='{discoveryResult.Host}'.");
			return Array.Empty<ManagedLightDescriptor> ();
			}

		var expansionConnectStopwatch = System.Diagnostics.Stopwatch.StartNew ();
		try
			{
			string rootName = ResolveManagedDeviceName (hubControllerId, ResolveDiscoveryName (discoveryResult), discoveryResult.DeviceId, discoveryResult.Host);
			string rootModel = discoveryResult.Model ?? "Kasa/Tapo Hub";

			using var expansionTimeoutSource = CancellationTokenSource.CreateLinkedTokenSource (cancellationToken);
			TimeSpan expansionTimeout = configuration.Timeout > TimeSpan.Zero
				? configuration.Timeout + TimeSpan.FromSeconds (2)
				: DefaultDiscoveryTimeout + TimeSpan.FromSeconds (2);
			expansionTimeoutSource.CancelAfter (expansionTimeout);

			LogInfo ($"ResolveHubChildDescriptorsAsync: attempting one-shot child expansion connect for host='{discoveryResult.Host}', deviceId='{discoveryResult.DeviceId ?? "<null>"}', model='{rootModel}', timeout={expansionTimeout.TotalSeconds:0}s.");

			// Routed through the hub's shared ManagedParentDevicePoller (rather than calling
			// Discover.GetOrConnectSharedAsync directly) so this driver never opens two
			// independent KasaDevice sessions against the same physical hub. GetOrConnectSharedAsync
			// only reuses/serializes an existing shared instance on a cache hit; on a cache miss
			// (e.g. first connect, or right after a previous shared instance was disposed/replaced)
			// it falls through to a brand-new independent connection, and KasaDevice's internal
			// operation semaphore only serializes calls within one such instance - it does nothing
			// to prevent a second, separate instance/session from being created concurrently. Some
			// hubs only tolerate one active session at a time, so two independent connect attempts
			// against the same host was observed to wedge the hub entirely.
			ManagedParentDevicePoller hubPoller = GetOrCreateHubPoller (configuration);
			KasaDevice device = await hubPoller.ConnectSharedAsync (expansionTimeoutSource.Token).ConfigureAwait (false);
			LogInfo ($"ResolveHubChildDescriptorsAsync: host='{discoveryResult.Host}' child expansion connect completed after {expansionConnectStopwatch.ElapsedMilliseconds}ms; reported {device.Children.Count} child(ren).");

			if (device.Children.Count == 0)
				{
				LogInfo ($"ResolveHubChildDescriptorsAsync: host='{discoveryResult.Host}' reported 0 hub children after connecting; skipping itemization for this pass.");
				return Array.Empty<ManagedLightDescriptor> ();
				}

			var childDescriptors = new List<ManagedLightDescriptor> (device.Children.Count);
			int childIndex = 0;
			foreach (ChildDeviceInfo child in device.Children)
				{
				childIndex++;
				string childName = string.IsNullOrWhiteSpace (child.Alias)
					? $"{rootName} Sensor {childIndex}"
					: child.Alias!;
				string childModel = child.Model ?? rootModel;
				string childSerial = child.Id;
				string childControllerId = CreateControllerId (discoveryResult, child.Id);

				(ManagedChildKind childKind, HubChildCategory hubChildCategory) = ResolveHubChildKind (child);

				// Each hub child gets its own controllerId (distinct from the hub's root
				// controllerId), but shares the same physical device/connection configuration.
				// Materialization (CreateManagedDeviceCacheEntry) looks up _deviceConfigurations
				// and _discoveryResults by controllerId, so both must be registered here or
				// materialization fails with "no device configuration is available" and the
				// child gets marked missing.
				_deviceConfigurations[childControllerId] = configuration;
				_discoveryResults[childControllerId] = discoveryResult;

				childDescriptors.Add (new ManagedLightDescriptor (
					childControllerId,
					discoveryResult.Host,
					discoveryResult.DeviceType,
					childName,
					childModel,
					childSerial,
					ManagedLightKind.OnOff,
					awaitingConnectedIdentity: false,
					discoveryResult.DeviceId,
					child.Id,
					childKind: childKind,
					hubChildCategory: hubChildCategory));
				}

			_resolvedHubChildDescriptors[hubControllerId] = childDescriptors;
			LogInfo ($"ResolveHubChildDescriptorsAsync: resolved {childDescriptors.Count} hub child(ren) for host='{discoveryResult.Host}'.");

			// The shared connection is owned by the hub's ManagedParentDevicePoller (see
			// GetOrCreateHubPoller) and is never disposed here; it is not adopted directly by any
			// single entity here because there may be multiple child entities for one physical hub.
			return childDescriptors;
			}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
			{
			LogInfo ($"ResolveHubChildDescriptorsAsync: timed out for controllerId='{hubControllerId}', host='{discoveryResult.Host}' after {expansionConnectStopwatch.ElapsedMilliseconds}ms.");
			return Array.Empty<ManagedLightDescriptor> ();
			}
		catch (OperationCanceledException)
			{
			LogInfo ($"ResolveHubChildDescriptorsAsync: canceled for host='{discoveryResult.Host}'.");
			return Array.Empty<ManagedLightDescriptor> ();
			}
		catch (Exception ex) when (!(ex is OperationCanceledException))
			{
			LogInfo ($"ResolveHubChildDescriptorsAsync: child expansion failed for host='{discoveryResult.Host}': {ex.Message}");
			return Array.Empty<ManagedLightDescriptor> ();
			}
		finally
			{
			_hubChildResolutionInFlightControllerIds.TryRemove (hubControllerId, out _);
			}
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

			// This alias fetch may race with the light entity's own connect for the same host, so it
			// uses the explicit, opt-in shared-connection API (Discover.GetOrConnectSharedAsync) rather
			// than the plain ConnectAsync (which, as of KasaClient 1.2.3, always returns an instance
			// exclusively owned by the calling code and no longer shares across separate calls). Using
			// the shared API here lets this fetch reuse the entity's already-connected device instead
			// of opening a second, redundant TCP session to the same bulb.
			Stopwatch aliasConnectStopwatch = Stopwatch.StartNew ();
			KasaDevice? device = await Discover.GetOrConnectSharedAsync (configuration, updateState: true, cancellationToken: aliasTimeoutSource.Token).ConfigureAwait (false);
			aliasConnectStopwatch.Stop ();
			LogInfo ($"EnrichDiscoveryResultAliasAsync: controllerId='{controllerId}' Discover.GetOrConnectSharedAsync completed after {aliasConnectStopwatch.Elapsed.TotalMilliseconds:0} ms.");
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

				if (_lightEntities.TryGetValue (controllerId, out IKasaManagedChildEntity? lightEntity))
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

	}

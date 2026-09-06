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
		return deviceType == KasaDeviceType.Plug || deviceType == KasaDeviceType.Strip;
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
			PersistManagedDeviceCache ();
			}

		IKasaManagedChildEntity newEntity = CreateManagedLightEntity (descriptor, deviceConfiguration);
		newEntity.SetConfigured (wasConfigured, context);

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
			LogInfo ($"ApplyChildConfigurationItems: controllerId='{controllerId}' received TreatAsLight={incomingTreatAsLight} (previous stored value={( _childTreatAsLight.TryGetValue (controllerId, out bool previousTreatAsLight) ? previousTreatAsLight.ToString () : "<none>")}).");
			bool kindActuallyChanging = incomingTreatAsLight != previousTreatAsLight;
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
			ManagedChildKind resolvedKind = ResolveManagedChildKind (controllerId, descriptor.DiscoveredDeviceType, descriptor.ModelName);
			descriptor.ChildKind = resolvedKind;
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
							LogChildPublicationState ("Before UpdateSubControllers deferred publish", descriptor.ControllerId);
				UpdateSubControllers (new[] { deferredController }, null);
							LogChildPublicationState ("After UpdateSubControllers deferred publish", descriptor.ControllerId);
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

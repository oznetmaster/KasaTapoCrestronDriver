using System.Diagnostics;

using Crestron.DeviceDrivers.EntityModel;
using Crestron.DeviceDrivers.EntityModel.Data;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel;
using Crestron.DeviceDrivers.SDK.EntityModel.Data;

namespace KasaTapoCrestronDriver;

public sealed partial class PlatformDriver
	{
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
			SynchronizeProcessorBaselineAsync,
			_resources,
			_logger,
			_driverLogId);
		}

	private Task SynchronizeProcessorBaselineAsync (string loadName, ProcessorLightTuningMode mode, double level, double hue, double saturation, long colorTemperature, CancellationToken cancellationToken)
		{
		return _processorBaselineCoordinator.SynchronizeAsync (loadName, mode, level, hue, saturation, colorTemperature, cancellationToken);
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
					Title = "Ready",
					Description = "Confirms the device configuration has been applied.",
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

	private async Task<IReadOnlyList<ManagedLightDescriptor>> ResolveManagedLightDescriptorsAsync (
		DiscoveryResult discoveryResult,
		DeviceConfiguration configuration,
		CancellationToken cancellationToken)
		{
		if (discoveryResult.DeviceType == KasaDeviceType.Strip)
			{
			// Strips are the only device type whose managed devices (child outlets) cannot be
			// determined synchronously from DiscoveryResult; see ResolveStripChildDescriptorsAsync.
			return _sharedConfiguration.TreatPlugsAsLights
				? await ResolveStripChildDescriptorsAsync (discoveryResult, configuration, cancellationToken).ConfigureAwait (false)
				: Array.Empty<ManagedLightDescriptor> ();
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

		return deviceType == KasaDeviceType.Plug || deviceType == KasaDeviceType.Strip
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
						child.Id));
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

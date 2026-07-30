using Crestron.DeviceDrivers.SDK.EntityModel;

namespace KasaTapoCrestronDriver;

public sealed partial class PlatformDriver
	{
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

			if (discoveryResult.DeviceType == KasaDeviceType.Strip && _sharedConfiguration.TreatPlugsAsLights)
				{
				// Strips are always itemized into one managed device per child outlet
				// (ResolveStripChildDescriptorsAsync); the strip's own root controllerId is never a
				// valid managed device and must never be published/persisted. A stale root entry can
				// still exist in _managedDevices/_managedDeviceCacheMetadata from before itemization
				// was introduced (or from a session where TreatPlugsAsLights was previously false), so
				// proactively purge it here rather than waiting for the multi-cycle removal-miss
				// threshold, which would otherwise leave the parent strip visibly listed for a long time.
				PurgeStaleStripRootManagedDevice (controllerId);
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
					else if (_lightEntities.ContainsKey (controllerId))
						{
						// A materialized light entity already exists for this controller and will perform
						// its own startup/reconnect (KasaLightEntity.InitializeStartupAsync). KasaClient's
						// Discover now coalesces/reuses connects per Host:Port itself, so a redundant
						// background alias-enrichment connect started here resolves to the SAME shared
						// KasaDevice instance rather than competing for a separate gate/connection - it is
						// skipped here simply because the light entity's own startup connect will resolve
						// the alias itself via UpdateDescriptorFromConnectedDevice once connected, making a
						// second alias-only connect redundant.
						LogInfo ($"EnrichDiscoveryResultAliasAsync: skipped one-shot alias fetch for controllerId='{controllerId}', host='{discoveryResult.Host}' because a light entity already exists and will resolve its own alias via its startup connect.");
						}
					else
						{
						_ = StartAliasEnrichmentAsync (discoveryResult, configuration);
						}
					}

				foreach (ManagedLightDescriptor descriptor in await ResolveManagedLightDescriptorsAsync (discoveryResult, configuration, cancellationToken).ConfigureAwait (false))
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

						// Once a child has been configured/activated, do not re-derive its configured
						// state from a point-in-time PeekStatus() snapshot on every rediscovery pass.
						// Doing so could transiently observe a non-Running status (a race, not an actual
						// deactivation) and tear down an otherwise healthy connection, leaving the device
						// disconnected until an unrelated status-changed event happened to fire again.
						// Only newly-discovered/never-configured children need their configured state
						// derived from the child configuration controller here.
						if (!_configuredChildControllerIds.Contains (descriptor.ControllerId))
							{
							bool childConfigurationRunning = _childControllers.TryGetValue (descriptor.ControllerId, out ConfigurableDriverEntity? existingController)
								&& IsChildConfigurationControllerRunning (existingController);
							if (childConfigurationRunning)
								{
								_configuredChildControllerIds.Add (descriptor.ControllerId);
								_inUseChildControllerIds.Add (descriptor.ControllerId);
								}

							existingLightEntity.SetConfigured (childConfigurationRunning, "rediscovery-existing-child");
							}

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
			_initialDiscoveryLoadPending = false;
			}

		SetOnline (true);
		SetReady (true);

		var previousDiscoveredControllerIds = new HashSet<string> (_previousDiscoveredControllerIds, StringComparer.OrdinalIgnoreCase);
		int previousDiscoveredCount = previousDiscoveredControllerIds.Count;
		bool hasAddedDiscoveryChange = discoveredControllerIds.Except (previousDiscoveredControllerIds, StringComparer.OrdinalIgnoreCase).Any ();
		bool hasRemovedDiscoveryChange = previousDiscoveredControllerIds.Except (discoveredControllerIds, StringComparer.OrdinalIgnoreCase).Any ();
		bool ignoreRemovalSignalsThisPass = isFastRefreshPhase && hasRemovedDiscoveryChange;

		if (hasAddedDiscoveryChange)
			{
			_fastRefreshShrinkRetryCount = 0;
			}
		else if (ignoreRemovalSignalsThisPass)
			{
			_fastRefreshShrinkRetryCount++;
			if (_fastRefreshShrinkRetryCount > MAX_FAST_REFRESH_SHRINK_RETRIES)
				{
				ignoreRemovalSignalsThisPass = false;
				LogInfo ($"RefreshPlatformAsync: fast-refresh shrink retry limit ({MAX_FAST_REFRESH_SHRINK_RETRIES}) reached for this missing-device streak; allowing the normal removal-refresh phase to proceed instead of continuing indefinite fast retries.");
				}
			}
		else
			{
			_fastRefreshShrinkRetryCount = 0;
			}

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
			nextRefreshInterval = InitialDiscoveryRefreshInterval;
			LogInfo ($"RefreshPlatformAsync: ignoring shrinking fast-refresh pass from {previousDiscoveredCount} discovered devices to {discoveredControllerIds.Count}; removals remain deferred until the 5-minute two-strike cycle, and the fast-refresh interval is retained so still-missing devices keep being retried quickly.");
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
			if (isInitialLoad)
				{
				NotifyPropertyChanged ("platform:managedDevices", CreateValueForEntries (ManagedDevices));
				LogInfo ($"Managed-device publish after initial materialization skipped/no-op: count={_managedDevices.Count}, controllerIds=[{string.Join (", ", _managedDevices.Keys.OrderBy (key => key, StringComparer.OrdinalIgnoreCase))}].");
				}

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

				if (missedCount < MANAGED_DEVICE_REMOVAL_MISS_THRESHOLD)
					{
					LogInfo ($"Managed-device removal deferred for controllerId='{existingControllerId}' after miss {missedCount}/{MANAGED_DEVICE_REMOVAL_MISS_THRESHOLD} on the normal 5-minute refresh cycle.");
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

				ClearChildRuntimeState (existingControllerId, "managed-device-removal");
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
							LoggingDriverConfigurationController childConfigurationController = CreateChildConfigurationController (pendingMaterialization.Descriptor);
							var controller = new ConfigurableDriverEntity (controllerId, (ReflectedAttributeDriverEntity)lightEntity, childConfigurationController);
							_lightEntities[controllerId] = lightEntity;
							_childControllers[controllerId] = controller;
							_childConfigurationControllers[controllerId] = childConfigurationController;
							LogChildPublicationState ("Async materialization stored child publication state", controllerId);
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

					if ((controllersToAdd?.Count ?? 0) > 0)
						{
						List<ConfigurableDriverEntity> controllersToPublish = controllersToAdd!;
						foreach (ConfigurableDriverEntity controller in controllersToPublish)
							{
							LogChildPublicationState ("Before UpdateSubControllers async publish", controller.ControllerId);
							}
						UpdateSubControllers (controllersToPublish, null);

						foreach (ConfigurableDriverEntity controller in controllersToPublish)
							{
							LogChildPublicationState ("After UpdateSubControllers async publish", controller.ControllerId);
							if (_lightEntities.TryGetValue (controller.ControllerId, out IKasaManagedLightEntity? lightEntity))
								{
								lightEntity.NotifyChildPublished ();
								}
				ActivatePublishedChildIfRunning (controller, "async-publication-status-reconciliation");
							}
						}

					if (managedDevicesChanged || pendingMaterializations.Count > 0)
						{
						NotifyPropertyChanged ("platform:managedDevices", CreateValueForEntries (ManagedDevices));
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

	}

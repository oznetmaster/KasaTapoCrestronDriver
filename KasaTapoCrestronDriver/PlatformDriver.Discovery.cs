// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE file in the project root for full license information.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization.Json;

using Crestron.DeviceDrivers.EntityModel.Data;
using Crestron.DeviceDrivers.EntityModel.Logging;

namespace KasaTapoCrestronDriver;

public sealed partial class PlatformDriver
	{
	private void RefreshExistingDeviceConfigurations (PlatformSharedConfigurationSnapshot configuration)
		{
		DeviceCredentials? credentials = string.IsNullOrWhiteSpace (configuration.UserName) || string.IsNullOrWhiteSpace (configuration.Password)
			? null
			: new DeviceCredentials (configuration.UserName, configuration.Password);

		foreach (KeyValuePair<string, IKasaManagedChildEntity> entry in _lightEntities)
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
		return features.Any (feature => string.Equals (feature.Id, featureId, StringComparison.Ordinal));
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

		// UDP broadcast discovery is inherently lossy, and crucially the loss differs between two
		// independent listeners: live logs show two concurrent passes over the same window returning
		// DIFFERENT device subsets (e.g. one pass resultCount=4 while the other resultCount=9 from the
		// same ~9-11 received packets). A single pass therefore risks surfacing only a partial set,
		// dropping devices out of the room. Run two independent passes and merge their results so a
		// device missed by one pass is still recovered from the other. The passes are run CONCURRENTLY
		// rather than sequentially: back-to-back passes each waiting the full timeout (e.g. 10s + 10s =
		// 20s) doubled the time before any device could appear after a reload, which regressed the
		// previously-observed ~10-second startup appearance.
		var passStopwatch = Stopwatch.StartNew ();
		Task<IReadOnlyList<DiscoveryResult>> firstPassTask = Discover.DiscoverAsync (timeout, cancellationToken: cancellationToken);
		Task<IReadOnlyList<DiscoveryResult>> secondPassTask = Discover.DiscoverAsync (timeout, cancellationToken: cancellationToken);
		await Task.WhenAll (firstPassTask, secondPassTask).ConfigureAwait (false);
		passStopwatch.Stop ();

		IReadOnlyList<DiscoveryResult> passResults = firstPassTask.Result;
		IReadOnlyList<DiscoveryResult> secondPassResults = secondPassTask.Result;

		LogDiscoveryPassResults (passResults, passNumber: 1, totalPasses: 2, timeout, passStopwatch.Elapsed, isInitialLoad, retryAfterZero: false);
		LogDiscoveryPassResults (secondPassResults, passNumber: 2, totalPasses: 2, timeout, passStopwatch.Elapsed, isInitialLoad, retryAfterZero: passResults.Count == 0);

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

		foreach (DiscoveryResult discoveryResult in secondPassResults)
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

		LogInfo ($"DiscoverDevicesAsync: mergedResultCount={discoveredDevices.Count}, rawResultCount={passResults.Count + secondPassResults.Count}, mergeStrategy=deviceIdOrHost, isInitialLoad={isInitialLoad}.");

		return discoveredDevices.Values.ToArray ();
		}

	private void LogDiscoveryPassResults (IReadOnlyList<DiscoveryResult> passResults, int passNumber, int totalPasses, TimeSpan timeout, TimeSpan elapsed, bool isInitialLoad, bool retryAfterZero)
		{
		int missingAliasCount = passResults.Count (result => string.IsNullOrWhiteSpace (result.Alias));
		int missingDeviceIdCount = passResults.Count (result => string.IsNullOrWhiteSpace (result.DeviceId));
		int missingModelCount = passResults.Count (result => string.IsNullOrWhiteSpace (result.Model));
		int bulbs = passResults.Count (result => result.DeviceType == KasaDeviceType.Bulb);
		int plugs = passResults.Count (result => result.DeviceType == KasaDeviceType.Plug);
		int strips = passResults.Count (result => result.DeviceType == KasaDeviceType.Strip);
		int hubs = passResults.Count (result => result.DeviceType == KasaDeviceType.Hub);
		int others = passResults.Count - bulbs - plugs - strips - hubs;

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
		descriptorsByControllerId.TryGetValue (controllerId, out ManagedLightDescriptor? descriptor);
		if (string.IsNullOrWhiteSpace (name) && descriptor is not null)
			{
			name = ResolveManagedDeviceName (controllerId, descriptor.Name, descriptor.DiscoveryDeviceId ?? descriptor.SerialNumber, descriptor.Host);
			}

		name = ResolveManagedDeviceName (controllerId, name, serialNumber, null);

		if (string.IsNullOrWhiteSpace (name))
			{
			LogInfo ($"Skipping managed-device publish for '{controllerId}' because the resolved device name is blank.");
			return false;
			}

		if (descriptor is null)
			{
			LogError ($"Skipping managed-device add for controllerId='{controllerId}' because discovery metadata is unavailable; cache metadata cannot be created safely.");
			return false;
			}

		// CreateManagedDeviceEntry resolves UxCategory (Light vs Outlet) from _managedDeviceCacheMetadata,
		// so the cache metadata must be populated first - CreateManagedDeviceEntry now throws if it isn't.
		_managedDeviceCacheMetadata[controllerId] = CreateManagedDeviceCacheEntry (descriptor, name);

		PlatformManagedDevice entry = CreateManagedDeviceEntry (controllerId, name, modelName, serialNumber);
		ConcurrentDictionary<string, PlatformManagedDevice> additionCopy = new (_managedDevices, StringComparer.OrdinalIgnoreCase);
		if (!additionCopy.TryAdd (controllerId, entry))
			{
			LogInfo ($"Skipping managed-device add for controllerId='{controllerId}' because an entry already exists.");
			return false;
			}
		_managedDevices = additionCopy;

		RememberResolvedDeviceName (controllerId, name, descriptor.DiscoveryDeviceId ?? descriptor.SerialNumber, descriptor.Host);
		PersistManagedDeviceCache ();

		NotifyManagedDevicesSnapshotChanged ();
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

				if (document.Version != MANAGED_DEVICE_CACHE_VERSION)
					{
					LogInfo ($"Managed-device cache load skipped because version '{document.Version}' is not current; expected version '{MANAGED_DEVICE_CACHE_VERSION}'. The cache will be deleted and rebuilt from discovery.");
					DeleteInvalidManagedDeviceCache (cachePath);
					return;
					}

				bool skippedInvalidEntries = false;
				int cachedConfiguredEntryCount = 0;
				List<ManagedDeviceCacheEntry> devices = document.Devices ?? new List<ManagedDeviceCacheEntry> ();
				ConcurrentDictionary<string, PlatformManagedDevice> cacheSeedCopy = new (_managedDevices, StringComparer.OrdinalIgnoreCase);

				foreach (ManagedDeviceCacheEntry? entry in devices)
					{
					if (!IsValidManagedDeviceCacheEntryShape (entry))
						{
					LogInfo ("Managed-device cache contains an invalid current-schema entry; deleting cache so discovery can rebuild it.");
					DeleteInvalidManagedDeviceCache (cachePath);
					return;
						}

						if (!IsCompleteManagedDeviceCacheIdentity (entry)
							|| string.IsNullOrWhiteSpace (entry.Name))
							{
						LogInfo ($"Managed-device cache contains incomplete identity metadata for controllerId='{entry.ControllerId ?? "<null>"}'; deleting cache so discovery can rebuild it.");
						DeleteInvalidManagedDeviceCache (cachePath);
						return;
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

						// Restore the per-child "Treat As Light" preference before recomputing
						// UxCategory below - otherwise ResolveManagedChildKind would see an empty
						// _childTreatAsLight (this dictionary is never itself persisted directly)
						// and default every plug/strip child back to Outlet.
						_childTreatAsLight[entry.ControllerId] = entry.TreatAsLight;

						// Only trust the cached UxCategory for entries that are still configured
						// (in a room) - an unconfigured entry's UxCategory can go stale (e.g. it
						// was switched to Light, then removed from configuration, which resolves
						// the in-memory ChildKind back to Outlet without updating this on-disk
						// cache entry). Recomputing here from the restored TreatAsLight preference
						// ensures an unassigned device reappears with its correct current kind
						// instead of the last-published one.
						//
						// ResolveManagedChildKind only understands the Plug/Strip "Treat As Light"
						// toggle - for every other discovered device type (including Hub children:
						// Sensor/Button telemetry with no such toggle) it unconditionally returns
						// ManagedChildKind.Light. Calling it unconditionally here previously forced
						// every unconfigured hub child (T310/T315/T100/S200B, etc.) back to
						// DeviceUxCategory.Light on every driver reload, even though the cached
						// UxCategory correctly recorded Sensor/Switch. Only recompute for the
						// device types ResolveManagedChildKind actually knows how to classify;
						// trust the cached UxCategory for everything else.
						DeviceUxCategory resolvedUxCategory = entry.IsConfigured
							|| (entry.DiscoveredDeviceType != KasaDeviceType.Plug && entry.DiscoveredDeviceType != KasaDeviceType.Strip)
							? entry.UxCategory
							: ResolveManagedChildKind (entry.ControllerId, entry.DiscoveredDeviceType, entry.Model) switch
								{
								ManagedChildKind.Outlet => DeviceUxCategory.Outlet,
								ManagedChildKind.Sensor => DeviceUxCategory.Sensor,
								// Switch is used only as a distinct, round-trippable marker for Button
								// kind - see matching comment in PlatformDriver.ManagedDevices.cs.
								ManagedChildKind.Button => DeviceUxCategory.Switch,
								ManagedChildKind.Thermostat => DeviceUxCategory.Thermostat,
								ManagedChildKind.Light => DeviceUxCategory.Light,
								_ => entry.UxCategory,
								};
						entry.UxCategory = resolvedUxCategory;

						// Configure Pro renders a managed device's secondary info line as the serial
						// number whenever one is supplied, and falls back to manufacturer/model only
						// when it is null (see CreateManagedDeviceCacheEntry for the same rule). Pass
						// null here too instead of cacheSerialNumber, otherwise every device restored
						// from the on-disk cache on driver startup reverts to showing its serial
						// number again. The real serial number is still tracked separately in
						// _managedDeviceCacheMetadata for reconnect/identity purposes.
						cacheSeedCopy[entry.ControllerId] = new PlatformManagedDevice (
							resolvedUxCategory,
							entry.Name,
							entry.Manufacturer,
							entry.Model,
							null!);
						_managedDeviceCacheMetadata[entry.ControllerId] = entry;
						if (entry.IsConfigured)
							{
							cachedConfiguredEntryCount++;
							// _configuredChildControllerIds is in-memory only and would otherwise start
							// empty on every process restart, even though the cache on disk still says
							// this child was configured/installed. Without restoring it here,
							// PublishCachedChildControllers later reads wasConfigured=false for every
							// cached child and skips replaying ActivationMarker/TreatAsLight into the
							// recreated configuration controller, leaving it stuck NotConfigured (and the
							// device permanently Offline in Configure Pro) no matter how many times
							// discovery re-runs.
							_ = _configuredChildControllerIds.TryAdd (entry.ControllerId, 0);
							}

					RememberResolvedDeviceName (entry.ControllerId, entry.Name, cacheSerialNumber, entry.Host);
					}


				_managedDevices = cacheSeedCopy;
				LogInfo ($"Managed-device cache seeded {_managedDevices.Count} device entries from '{cachePath}', cachedConfiguredChildCount={cachedConfiguredEntryCount}; current session child configuration state will be established only by child configuration callbacks.");
				if (skippedInvalidEntries)
					{
					PersistManagedDeviceCache ();
					LogInfo ("Managed-device cache was rewritten after dropping invalid current-schema entries.");
					}
			}
		catch (Exception ex)
			{
			LogInfo ($"Managed-device cache load failed from '{cachePath}': {ex.Message}");
			DeleteInvalidManagedDeviceCache (cachePath);
			}
		}

	private static bool IsValidManagedDeviceCacheEntryShape (ManagedDeviceCacheEntry? entry)
		{
		return entry is not null
			&& entry.Immutable is not null
			&& entry.Mutable is not null;
		}

	private static bool IsCompleteManagedDeviceCacheIdentity (ManagedDeviceCacheEntry? entry)
		{
		if (!IsValidManagedDeviceCacheEntryShape (entry) || entry is null)
			{
			return false;
			}

		return !string.IsNullOrWhiteSpace (entry.ControllerId)
			&& !string.IsNullOrWhiteSpace (entry.Manufacturer)
			&& !string.IsNullOrWhiteSpace (entry.Model)
			&& !string.IsNullOrWhiteSpace (entry.SerialNumber);
		}

	private void DeleteInvalidManagedDeviceCache (string cachePath)
		{
		try
			{
			if (!string.IsNullOrWhiteSpace (cachePath) && File.Exists (cachePath))
				{
				File.Delete (cachePath);
				LogInfo ($"Managed-device cache deleted after load failure; discovery will rebuild it using the current schema: '{cachePath}'.");
				}
			}
		catch (Exception ex)
			{
			LogInfo ($"Managed-device cache delete failed for '{cachePath}': {ex.Message}");
			}
		}

	private string GetManagedDeviceCachePath ()
		{
		return _managedDeviceCachePath;
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
							PublishManagedDeviceEntryUpdate (entry.ControllerId, "cached-identity-resolution");
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
		ConcurrentDictionary<string, PlatformManagedDevice> removalCopy = new (_managedDevices, StringComparer.OrdinalIgnoreCase);
		if (!removalCopy.TryRemove (controllerId, out _))
			{
			return false;
			}
		_managedDevices = removalCopy;

		PersistManagedDeviceCache ();

		NotifyManagedDevicesSnapshotChanged ();
		return true;
		}

	private void PurgeStaleStripRootManagedDevice (string stripRootControllerId)
		{
		bool hadManagedDeviceEntry = _managedDevices.ContainsKey (stripRootControllerId);
		bool hadCacheMetadata = _managedDeviceCacheMetadata.ContainsKey (stripRootControllerId);

		if (!hadManagedDeviceEntry && !hadCacheMetadata)
			{
			return;
			}

		LogInfo ($"PurgeStaleStripRootManagedDevice: removing stale strip-root managed-device entry for controllerId='{stripRootControllerId}' because strips are itemized into per-child managed devices and the root is never itself a managed device.");

		if (hadManagedDeviceEntry)
			{
			ConcurrentDictionary<string, PlatformManagedDevice> removalCopy = new (_managedDevices, StringComparer.OrdinalIgnoreCase);
			removalCopy.TryRemove (stripRootControllerId, out _);
			_managedDevices = removalCopy;
			}

		_ = _managedDeviceCacheMetadata.TryRemove (stripRootControllerId, out _);

		if (_lightEntities.TryGetValue (stripRootControllerId, out IKasaManagedChildEntity? staleRootEntity))
			{
			staleRootEntity.Stop ();
			staleRootEntity.Dispose ();
			}

		if (_childControllers.ContainsKey (stripRootControllerId))
			{
			UpdateSubControllers (null, new List<string> { stripRootControllerId });
			}

		ClearChildRuntimeState (stripRootControllerId, "stale-strip-root-purge");

		if (hadManagedDeviceEntry)
			{
			PersistManagedDeviceCache ();
			NotifyManagedDevicesSnapshotChanged ();
			}
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

				var cacheEntries = new List<ManagedDeviceCacheEntry> ();

				foreach (KeyValuePair<string, PlatformManagedDevice> entry in cacheableDevices)
					{
					if (!_managedDeviceCacheMetadata.TryGetValue (entry.Key, out ManagedDeviceCacheEntry? metadata)
						|| !IsCompleteManagedDeviceCacheIdentity (metadata))
						{
						LogError ($"Managed-device cache entry skipped for controllerId='{entry.Key}' because it has no complete immutable cache metadata. This indicates a discovery publication bug; discovery will rebuild metadata on the next refresh.");
						continue;
						}

					cacheEntries.Add (new ManagedDeviceCacheEntry
						{
						Immutable = new ManagedDeviceImmutableCacheFields
							{
							ControllerId = entry.Key,
							UxCategory = metadata.UxCategory,
							Manufacturer = metadata.Manufacturer,
							Model = metadata.Model,
							DiscoveredDeviceType = metadata.DiscoveredDeviceType,
							ManagedLightKind = metadata.ManagedLightKind,
							SerialNumber = metadata.SerialNumber,
							// ChildId identifies which physical child on a strip this controllerId
							// is (see PlatformDriver.cs's ManagedDeviceImmutableCacheFields.ChildId
							// comment). Omitting it here silently drops it from every persisted
							// cache rewrite, so a subsequent reload recreates the descriptor with
							// ChildId=null and UpdateDescriptorFromConnectedDevice falls back to
							// the strip root's own alias/identity instead of this specific child's.
							ChildId = metadata.ChildId,
							// Same rewrite hazard as ChildId above: omitting HubChildCategory here
							// would drop it from every persisted rewrite, so a reload recreates the
							// descriptor with HubChildCategory=None and the sensor cannot trim its
							// declared surface before publication (see KasaSensorEntity's
							// ResolveCapabilitiesFromHubChildCategory).
							HubChildCategory = metadata.HubChildCategory
							},
						Mutable = new ManagedDeviceMutableCacheFields
							{
							Name = entry.Value.Name,
							Host = metadata.Host,
							AwaitingConnectedIdentity = metadata.AwaitingConnectedIdentity,
							IsConfigured = _configuredChildControllerIds.ContainsKey (entry.Key) || metadata.IsConfigured,
							// TreatAsLight was previously omitted here, so every cache rewrite
							// silently reset it to false regardless of the actual in-memory
							// preference - discarding the user's "Treat As Light" choice even
							// though the device was never assigned to a room. Restore it from
							// _childTreatAsLight (falling back to the existing metadata value)
							// so a driver reload/rewrite doesn't lose the preference.
							TreatAsLight = _childTreatAsLight.TryGetValue (entry.Key, out bool treatAsLight) ? treatAsLight : metadata.TreatAsLight,
							Port = metadata.Port,
							TransportKind = metadata.TransportKind,
							DeviceFamily = metadata.DeviceFamily,
							EncryptionKind = metadata.EncryptionKind,
							LoginVersion = metadata.LoginVersion,
							UseHttps = metadata.UseHttps,
							HttpPort = metadata.HttpPort,
							UseSsl = metadata.UseSsl,
							UseDefaultCredentials = metadata.UseDefaultCredentials,
							DefaultCredentialProfile = metadata.DefaultCredentialProfile,
							ApplicationPath = metadata.ApplicationPath,
							UseSecurePassthrough = metadata.UseSecurePassthrough,
							TpapKeepAliveIntervalMs = metadata.TpapKeepAliveIntervalMs
							}
						});
					}

				var document = new ManagedDeviceCacheDocument
					{
					Version = MANAGED_DEVICE_CACHE_VERSION,
					Devices = cacheEntries
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

	[Conditional ("DEBUG")]
	private void LogInfo (string message)
		{
		LogInfoCore (message);
		}

	private void LogInfoCore (string message)
		{
		_logger?.Log (_driverLogId, LogEntryLevel.Info, message);
		}

	private static bool IsDebugLoggingEnabled ()
		{
#if DEBUG
		return true;
#else
		return false;
#endif
		}

	private void LogError (string message)
		{
		_logger?.Log (_driverLogId, LogEntryLevel.Error, message);
		}
	}
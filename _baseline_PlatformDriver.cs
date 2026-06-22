using System;
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

public sealed class PlatformDriver : ReflectedAttributeDriverEntity
   {
   private static readonly TimeSpan DefaultDiscoveryTimeout = TimeSpan.FromSeconds (5);
   private static readonly TimeSpan DefaultLightPollInterval = TimeSpan.FromSeconds (15);
   private static readonly TimeSpan DefaultSensorPollInterval = TimeSpan.FromSeconds (3);

   private readonly DriverControllerCreationArgs _args;
   private readonly DriverImplementationResources _resources;
   private readonly DriverControllerLogger _logger;
   private readonly string _driverLogId;
   private readonly Dictionary<string, ConfigurableDriverEntity> _childControllers = new (StringComparer.OrdinalIgnoreCase);
   private readonly Dictionary<string, IKasaManagedLightEntity> _lightEntities = new (StringComparer.OrdinalIgnoreCase);
   private readonly HashSet<string> _lightsWithRefreshInProgress = new (StringComparer.OrdinalIgnoreCase);
   private readonly object _lightRefreshGate = new ();
   private readonly UiDefinitionProperty _uiDefinition;

   private string _userName = string.Empty;
   private string _password = string.Empty;
   private string _discoveryTimeoutSeconds = ((int) DefaultDiscoveryTimeout.TotalSeconds).ToString (CultureInfo.InvariantCulture);
   private string _lightPollIntervalSeconds = ((int) DefaultLightPollInterval.TotalSeconds).ToString (CultureInfo.InvariantCulture);
   private string _sensorPollIntervalSeconds = ((int) DefaultSensorPollInterval.TotalSeconds).ToString (CultureInfo.InvariantCulture);
   private bool _enableLightPolling;
   private bool _treatPlugsAsLights;
   private string _platformStatus = "Not configured";
   private string _platformLastError = string.Empty;
   private string _platformTileIcon = "icDeviceGeneric";
   private string _platformTileStatus = "Configure the driver to discover Kasa devices and optionally Tapo devices.";
   private string _primaryActionLabel = "Discover";
   private bool _onlineIndicatorIsOnline;
   private bool _readyIndicatorIsReady;
   private IDictionary<string, PlatformManagedDevice> _managedDevices = new Dictionary<string, PlatformManagedDevice> (StringComparer.OrdinalIgnoreCase);
   private CancellationTokenSource? _pollingCancellationSource;
   private Task? _lightPollingTask;

   internal DataDrivenConfigurationController ConfigurationController { get; }

   [EntityProperty (Id = "platformStatus", FriendlyName = "Platform Status", Type = DriverEntityValueType.String)]
   [EntityPropertyMetadata (ExtensionUiProperty = true)]
   public string PlatformStatus
      {
      get => _platformStatus;
      private set => SetAndNotify ("platformStatus", value, ref _platformStatus);
      }

   [EntityProperty (Id = "platformLastError", FriendlyName = "Platform Last Error", Type = DriverEntityValueType.String)]
   [EntityPropertyMetadata (ExtensionUiProperty = true)]
   public string PlatformLastError
      {
      get => _platformLastError;
      private set => SetAndNotify ("platformLastError", value, ref _platformLastError);
      }

   [EntityProperty (Id = "platform:managedDevices", Type = DriverEntityValueType.DeviceDictionary, ItemTypeRef = "platform:ManagedDevice", FriendlyName = "Managed Devices")]
   public IDictionary<string, PlatformManagedDevice> ManagedDevices
      {
      get => _managedDevices;
      private set => SetAndNotifyManagedDevices (value, ref _managedDevices);
      }

   [EntityProperty (Id = "onlineIndicator:isOnline", Type = DriverEntityValueType.Boolean)]
   public bool OnlineIndicatorIsOnline
      {
      get => _onlineIndicatorIsOnline;
      private set => SetAndNotify ("onlineIndicator:isOnline", value, ref _onlineIndicatorIsOnline);
      }

   [EntityProperty (Id = "readyIndicator:isReady", Type = DriverEntityValueType.Boolean)]
   public bool ReadyIndicatorIsReady
      {
      get => _readyIndicatorIsReady;
      private set => SetAndNotify ("readyIndicator:isReady", value, ref _readyIndicatorIsReady);
      }

   [EntityProperty (Id = "platformTileIcon", FriendlyName = "Platform Tile Icon", Type = DriverEntityValueType.String)]
   [EntityPropertyMetadata (ExtensionUiProperty = true)]
   public string PlatformTileIcon
      {
      get => _platformTileIcon;
      private set => SetAndNotify ("platformTileIcon", value, ref _platformTileIcon);
      }

   [EntityProperty (Id = "platformTileStatus", FriendlyName = "Platform Tile Status", Type = DriverEntityValueType.String)]
   [EntityPropertyMetadata (ExtensionUiProperty = true)]
   public string PlatformTileStatus
      {
      get => _platformTileStatus;
      private set => SetAndNotify ("platformTileStatus", value, ref _platformTileStatus);
      }

   [EntityProperty (Id = "primaryActionLabel", FriendlyName = "Primary Action Label", Type = DriverEntityValueType.String)]
   [EntityPropertyMetadata (ExtensionUiProperty = true)]
   public string PrimaryActionLabel
      {
      get => _primaryActionLabel;
      private set => SetAndNotify ("primaryActionLabel", value, ref _primaryActionLabel);
      }

   public PlatformDriver (DriverControllerCreationArgs args, DriverImplementationResources resources)
       : base (DriverController.RootControllerId)
      {
      _args = args;
      _resources = resources;
      _logger = args.Logger;
      _driverLogId = args.DriverId;

      _uiDefinition = UiDefinitionProperty.LoadFromDirectoryIfExists (_args.DriverDataDirectoryPath, resources.InitLogger, LogEntryLevel.Error)
          ?? throw new InvalidOperationException ($"UiDefinition was not found at '{_args.DriverDataDirectoryPath}'.");

      AddProperty (this, UiDefinitionProperty.Name, _uiDefinition);
      AddCommand (this, ExtensionDoCommandExecutor.CommandName, new ExtensionDoCommandExecutor (GetCommand, resources.Logger));
      AddCommand (this, ExtensionSetPropertyValueExecutor.CommandName, new ExtensionSetPropertyValueExecutor (GetCommand, resources.Logger));

      var configurationArgs = DataDrivenConfigurationControllerArgs.FromResources (args, resources, ControllerId);
      ConfigurationController = new DelegateDataDrivenConfigurationController (configurationArgs, ApplyConfigurationItems, null, null);
      }

   [EntityCommand (Id = "performPrimaryAction", FriendlyName = "Perform Primary Action")]
   public void PerformPrimaryAction ()
      {
      try
         {
         RefreshPlatformAsync (CancellationToken.None).GetAwaiter ().GetResult ();
         }
      catch (Exception ex)
         {
         HandleRefreshFailure (ex, "Manual refresh failed.");
         }
      }

   private ConfigurationItemErrors? ApplyConfigurationItems (
       DataDrivenConfigurationController.ApplyConfigurationAction action,
       string stepId,
       IDictionary<string, DriverEntityValue?> values)
      {
      ApplyValues (values);

      if (!TryParseDiscoveryTimeout (_discoveryTimeoutSeconds, out var timeout, out var timeoutError))
         {
         return new ConfigurationItemErrors (
             new Dictionary<string, string>
                {
                ["DiscoveryTimeoutSeconds"] = timeoutError
                },
             timeoutError);
         }

      _discoveryTimeoutSeconds = ((int) timeout.TotalSeconds).ToString (CultureInfo.InvariantCulture);

      try
         {
         RefreshPlatformAsync (CancellationToken.None).GetAwaiter ().GetResult ();
         return null;
         }
      catch (Exception ex)
         {
         HandleRefreshFailure (ex, "Discovery failed while applying configuration.");
         return new ConfigurationItemErrors (null, ex.Message);
         }
      }

   private void ApplyValues (IDictionary<string, DriverEntityValue?> values)
      {
      if (values.TryGetValue ("UserName", out var userNameValue) && userNameValue.HasValue)
         {
         _userName = userNameValue.Value.GetValue<string> ()?.Trim () ?? _userName;
         }

      if (values.TryGetValue ("Password", out var passwordValue) && passwordValue.HasValue)
         {
         string? configuredPassword = passwordValue.Value.GetValue<string> ();
         if (!string.IsNullOrEmpty (configuredPassword))
            {
            _password = configuredPassword;
            }
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

   private async Task RefreshPlatformAsync (CancellationToken cancellationToken)
      {
      var timeout = ParseDiscoveryTimeout (_discoveryTimeoutSeconds);
      var credentials = CreateCredentials ();
      bool hasTapoCredentials = HasTapoCredentials ();

      PlatformStatus = "Discovering devices";
      PlatformLastError = string.Empty;
      PlatformTileStatus = hasTapoCredentials
          ? "Discovering Kasa and Tapo devices..."
          : "Discovering Kasa devices...";
      PlatformTileIcon = "icDeviceGeneric";
      PrimaryActionLabel = "Refresh";
      SetReady (false);

      IReadOnlyList<DiscoveryResult> discoveredDevices = await Discover.DiscoverAsync (timeout, cancellationToken: cancellationToken).ConfigureAwait (false);
      List<ConfigurableDriverEntity>? controllersToAdd = null;
      List<string>? controllersToRemove = null;
      var connectedManagedDevices = new Dictionary<string, PlatformManagedDevice> (StringComparer.OrdinalIgnoreCase);
      var activeControllerIds = new HashSet<string> (StringComparer.OrdinalIgnoreCase);
      var discoveryErrors = new List<string> ();

      foreach (DiscoveryResult discoveryResult in discoveredDevices.OrderBy (ResolveDiscoveryName, StringComparer.OrdinalIgnoreCase))
         {
         if (!hasTapoCredentials && IsTapoDiscoveryResult (discoveryResult))
            {
            LogInfo ($"Skipping Tapo device '{ResolveDiscoveryName (discoveryResult)}' because Tapo credentials were not provided.");
            continue;
            }

         if (!IsSupportedLightDeviceType (discoveryResult.DeviceType, _treatPlugsAsLights))
            {
            LogInfo ($"Skipping unsupported discovered device '{ResolveDiscoveryName (discoveryResult)}' of type {discoveryResult.DeviceType}.");
            continue;
            }

         string controllerId = CreateControllerId (discoveryResult);

         try
            {
            KasaDevice kasaDevice = await Discover.ConnectAsync (
                discoveryResult,
                updateState: true,
                credentials: credentials,
                timeout: timeout,
                cancellationToken: cancellationToken).ConfigureAwait (false);

            foreach (ManagedLightDescriptor descriptor in CreateManagedLightDescriptors (discoveryResult, kasaDevice))
               {
               IKasaManagedLightEntity lightEntity;
               if (_lightEntities.TryGetValue (descriptor.ControllerId, out IKasaManagedLightEntity? existingEntity))
                  {
                  existingEntity.UpdateDevice (kasaDevice, descriptor);
                  lightEntity = existingEntity;
                  }
               else
                  {
                  lightEntity = CreateManagedLightEntity (descriptor, kasaDevice);
                  var controller = new ConfigurableDriverEntity (descriptor.ControllerId, (ReflectedAttributeDriverEntity) lightEntity, null);
                  _lightEntities[descriptor.ControllerId] = lightEntity;
                  _childControllers[descriptor.ControllerId] = controller;
                  controllersToAdd ??= new List<ConfigurableDriverEntity> ();
                  controllersToAdd.Add (controller);
                  }

               IKasaManagedLightEntity lightEntityForMetadata = _lightEntities[descriptor.ControllerId];
               activeControllerIds.Add (descriptor.ControllerId);
               connectedManagedDevices[descriptor.ControllerId] = new PlatformManagedDevice (
                   DeviceUxCategory.Light,
                   lightEntityForMetadata.DeviceName,
                   "TP-Link",
                   lightEntityForMetadata.ModelName,
                   lightEntityForMetadata.SerialNumber);
               }
            }
         catch (Exception ex)
            {
            discoveryErrors.Add ($"{ResolveDiscoveryName (discoveryResult)}: {ex.Message}");
            LogError ($"Failed to connect to '{ResolveDiscoveryName (discoveryResult)}': {ex}");
            }
         }

      foreach (string existingControllerId in _childControllers.Keys.Except (activeControllerIds, StringComparer.OrdinalIgnoreCase).ToArray ())
         {
         _childControllers.Remove (existingControllerId);
         _lightEntities.Remove (existingControllerId);
         controllersToRemove ??= new List<string> ();
         controllersToRemove.Add (existingControllerId);
         }

      if ((controllersToAdd?.Count ?? 0) > 0 || (controllersToRemove?.Count ?? 0) > 0)
         {
         UpdateSubControllers (controllersToAdd, controllersToRemove);
         }

      foreach (string controllerId in activeControllerIds)
         {
         if (_lightEntities.TryGetValue (controllerId, out IKasaManagedLightEntity? lightEntity))
            {
            StartLightRefresh (controllerId, lightEntity, cancellationToken);
            }
         }

      ManagedDevices = connectedManagedDevices;
      SetOnline (true);
      SetReady (true);
      RestartPolling ();

      if (connectedManagedDevices.Count > 0)
         {
         PlatformStatus = $"Discovered {connectedManagedDevices.Count} light device{(connectedManagedDevices.Count == 1 ? string.Empty : "s")}";
         PlatformTileStatus = discoveryErrors.Count == 0
             ? "Light devices are ready in Crestron Home."
             : $"{connectedManagedDevices.Count} ready, {discoveryErrors.Count} failed.";
         PlatformTileIcon = "icLightOn";
         PlatformLastError = discoveryErrors.Count == 0 ? string.Empty : string.Join (" | ", discoveryErrors.Take (3));
         return;
         }

      PlatformStatus = "No supported light devices found";
      PlatformTileStatus = hasTapoCredentials
          ? "Discovery completed, but no supported Kasa/Tapo light devices were available."
          : "Discovery completed, but no supported Kasa light devices were available.";
      PlatformTileIcon = "icDeviceGeneric";
      PlatformLastError = discoveryErrors.Count == 0 ? string.Empty : string.Join (" | ", discoveryErrors.Take (3));
      }

   private static bool IsSupportedLightDeviceType (KasaDeviceType deviceType, bool treatPlugsAsLights)
      {
      if (deviceType is KasaDeviceType.Bulb or KasaDeviceType.LightStrip or KasaDeviceType.Dimmer)
         {
         return true;
         }

      return treatPlugsAsLights && (deviceType == KasaDeviceType.Plug || deviceType == KasaDeviceType.Strip);
      }

   private IKasaManagedLightEntity CreateManagedLightEntity (ManagedLightDescriptor descriptor, KasaDevice device)
      {
      return descriptor.Kind switch
         {
         ManagedLightKind.ColorRoot => device.SupportsLightEffects
             ? new KasaEffectLightEntity (descriptor.ControllerId, descriptor, device, _resources, _logger, _driverLogId)
             : new KasaLightEntity (descriptor.ControllerId, descriptor, device, _resources, _logger, _driverLogId),
         ManagedLightKind.TunableWhiteRoot => new KasaLightEntity (descriptor.ControllerId, descriptor, device, _resources, _logger, _driverLogId),
         ManagedLightKind.DimmableRoot => new KasaLightEntity (descriptor.ControllerId, descriptor, device, _resources, _logger, _driverLogId),
         _ => new KasaOnOffLightEntity (descriptor.ControllerId, descriptor, device, _resources, _logger, _driverLogId),
         };
      }

   private IEnumerable<ManagedLightDescriptor> CreateManagedLightDescriptors (DiscoveryResult discoveryResult, KasaDevice device)
      {
      string rootName = device.SystemInfo?.Alias ?? ResolveDiscoveryName (discoveryResult);
      string rootModel = device.SystemInfo?.Model ?? discoveryResult.Model ?? "Kasa/Tapo Device";
      string rootSerial = device.SystemInfo?.DeviceId ?? discoveryResult.DeviceId ?? discoveryResult.Host;

      switch (discoveryResult.DeviceType)
         {
         case KasaDeviceType.Bulb:
         case KasaDeviceType.LightStrip:
         case KasaDeviceType.Dimmer:
            ManagedLightKind rootKind = DetermineRootLightKind (device, rootModel);
            yield return new ManagedLightDescriptor (
                CreateControllerId (discoveryResult),
                rootName,
                rootModel,
                rootSerial,
                rootKind);
            yield break;

         case KasaDeviceType.Plug when _treatPlugsAsLights:
            yield return new ManagedLightDescriptor (
                CreateControllerId (discoveryResult),
                rootName,
                rootModel,
                rootSerial,
                ManagedLightKind.OnOffRoot);
            yield break;

         case KasaDeviceType.Strip when _treatPlugsAsLights:
            int outletIndex = 0;
            foreach (ChildDeviceInfo child in device.Children)
               {
               outletIndex++;
               string childName = string.IsNullOrWhiteSpace (child.Alias)
                   ? $"{rootName} Outlet {outletIndex}"
                   : child.Alias!;
               string childModel = child.Model ?? rootModel;
               string childSerial = child.Id;
               yield return new ManagedLightDescriptor (
                   CreateControllerId (discoveryResult, child.Id),
                   childName,
                   childModel,
                   childSerial,
                   ManagedLightKind.OnOffChild,
                   child.Id);
               }
            yield break;
         }
      }

   private static ManagedLightKind DetermineRootLightKind (KasaDevice device, string? modelName)
      {
      bool hasColor = device.LightState?.Hue is not null
          || device.LightState?.Saturation is not null
          || device.LightState?.Hsv is not null
          || ModelSupportsFullColor (modelName);

      if (hasColor)
         {
         return ManagedLightKind.ColorRoot;
         }

      bool hasColorTemperature = device.LightState?.ColorTemperature is not null
          || ModelSupportsColorTemperature (modelName);

      return hasColorTemperature ? ManagedLightKind.TunableWhiteRoot : ManagedLightKind.DimmableRoot;
      }

   private static bool ModelSupportsFullColor (string? modelName)
      {
      string modelText = (modelName ?? string.Empty).Trim ().ToUpperInvariant ();
      return modelText.StartsWith ("LB130", StringComparison.Ordinal)
          || modelText.StartsWith ("LB230", StringComparison.Ordinal)
          || modelText.StartsWith ("KB130", StringComparison.Ordinal)
          || modelText.StartsWith ("KL130", StringComparison.Ordinal)
          || modelText.StartsWith ("KL135", StringComparison.Ordinal)
          || modelText.StartsWith ("KL430", StringComparison.Ordinal)
          || modelText.StartsWith ("L530", StringComparison.Ordinal)
          || modelText.StartsWith ("L900", StringComparison.Ordinal);
      }

   private static bool ModelSupportsColorTemperature (string? modelName)
      {
      string modelText = (modelName ?? string.Empty).Trim ().ToUpperInvariant ();
      return modelText.StartsWith ("LB130", StringComparison.Ordinal)
          || modelText.StartsWith ("LB230", StringComparison.Ordinal)
          || modelText.StartsWith ("KB130", StringComparison.Ordinal)
          || modelText.StartsWith ("KL130", StringComparison.Ordinal)
          || modelText.StartsWith ("KL135", StringComparison.Ordinal)
          || modelText.StartsWith ("KL430", StringComparison.Ordinal)
          || modelText.StartsWith ("L900", StringComparison.Ordinal)
          || modelText.StartsWith ("LB120", StringComparison.Ordinal)
          || modelText.StartsWith ("KL120(EU)", StringComparison.Ordinal)
          || modelText.StartsWith ("KL125", StringComparison.Ordinal)
          || modelText.StartsWith ("L530", StringComparison.Ordinal)
          || modelText.StartsWith ("KL120(US)", StringComparison.Ordinal);
      }

   private static string ResolveDiscoveryName (DiscoveryResult discoveryResult)
      {
      return discoveryResult.Alias
          ?? discoveryResult.DeviceId
          ?? discoveryResult.Host;
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
          || discoveryResult.TpapPreferred == true
          || discoveryResult.RawJson.IndexOf ("TAPO", StringComparison.OrdinalIgnoreCase) >= 0;
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

   private void RestartPolling ()
      {
      CancellationTokenSource? previousCancellation = _pollingCancellationSource;
      _pollingCancellationSource = new CancellationTokenSource ();
      previousCancellation?.Cancel ();
      previousCancellation?.Dispose ();

      _lightPollingTask = _enableLightPolling
          ? RunLightPollingLoopAsync (_pollingCancellationSource.Token)
          : null;
      }

   private async Task RunLightPollingLoopAsync (CancellationToken cancellationToken)
      {
      TimeSpan interval = ParsePollIntervalOrDefault (_lightPollIntervalSeconds, DefaultLightPollInterval);

      try
         {
         while (!cancellationToken.IsCancellationRequested)
            {
            await Task.Delay (interval, cancellationToken).ConfigureAwait (false);
            await PollLightsAsync (cancellationToken).ConfigureAwait (false);
            }
         }
      catch (OperationCanceledException)
         {
         }
      catch (Exception ex)
         {
         LogError ($"Light polling loop failed: {ex}");
         }
      }

   private async Task PollLightsAsync (CancellationToken cancellationToken)
      {
      foreach (var entry in _lightEntities.ToArray ())
         {
         cancellationToken.ThrowIfCancellationRequested ();
         StartLightRefresh (entry.Key, entry.Value, cancellationToken);
         }
      }

   private static TimeSpan ParsePollIntervalOrDefault (string intervalValue, TimeSpan defaultInterval)
      {
      return TryParsePollInterval (intervalValue, defaultInterval, out TimeSpan interval, out _)
          ? interval
          : defaultInterval;
      }

   private void StartLightRefresh (string controllerId, IKasaManagedLightEntity lightEntity, CancellationToken cancellationToken)
      {
      lock (_lightRefreshGate)
         {
         if (!_lightsWithRefreshInProgress.Add (controllerId))
            {
            return;
            }
         }

      _ = ObserveLightRefreshAsync (controllerId, lightEntity, cancellationToken);
      }

   private async Task ObserveLightRefreshAsync (string controllerId, IKasaManagedLightEntity lightEntity, CancellationToken cancellationToken)
      {
      try
         {
         await lightEntity.RefreshAsync (cancellationToken).ConfigureAwait (false);
         }
      catch (OperationCanceledException)
         {
         }
      catch (Exception ex)
         {
         LogError ($"Light refresh task failed for '{lightEntity.DeviceName}': {ex}");
         }
      finally
         {
         lock (_lightRefreshGate)
            {
            _lightsWithRefreshInProgress.Remove (controllerId);
            }
         }
      }

   private void HandleRefreshFailure (Exception ex, string status)
      {
      SetOnline (false);
      SetReady (false);
      PlatformStatus = status;
      PlatformLastError = ex.Message;
      PlatformTileStatus = ex.Message;
      PlatformTileIcon = "icDeviceGeneric";
      PrimaryActionLabel = "Retry";
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

   private void SetAndNotifyManagedDevices (IDictionary<string, PlatformManagedDevice> value, ref IDictionary<string, PlatformManagedDevice> field)
      {
      value ??= new Dictionary<string, PlatformManagedDevice> (StringComparer.OrdinalIgnoreCase);

      if (ReferenceEquals (field, value))
         {
         return;
         }

      field = value;
      NotifyPropertyChanged ("platform:managedDevices", CreateValueForEntries (field));
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

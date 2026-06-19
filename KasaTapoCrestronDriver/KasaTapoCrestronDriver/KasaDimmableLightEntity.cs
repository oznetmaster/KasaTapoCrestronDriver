using System;
using System.Threading;
using System.Threading.Tasks;

using Crestron.DeviceDrivers.EntityModel.Data;
using Crestron.DeviceDrivers.EntityModel.Logging;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel;
using Crestron.DeviceDrivers.SDK.EntityModel.Attributes;

using KasaTapoClient;

namespace KasaTapoCrestronDriver;

internal sealed class KasaDimmableLightEntity : ReflectedAttributeDriverEntity, IKasaManagedLightEntity
   {
   private readonly WorkQueue<KasaDevice> _deviceQueue = new ();
   private readonly DriverControllerLogger _logger;
   private readonly string _driverLogId;

   private string _deviceName;
   private string _modelName;
   private string _serialNumber;
   private int _stateVersion;
   private bool _lightIsOn;
   private double _lightDimmerLevel;
   private bool _onlineIndicatorIsOnline;
   private bool _readyIndicatorIsReady;

   public KasaDimmableLightEntity (string controllerId, ManagedLightDescriptor descriptor, KasaDevice device, DriverImplementationResources resources, DriverControllerLogger logger, string driverLogId)
       : base (controllerId)
      {
      _logger = logger;
      _driverLogId = driverLogId;
      _deviceName = descriptor.Name;
      _modelName = descriptor.ModelName;
      _serialNumber = descriptor.SerialNumber;

      AddCommand (this, ExtensionDoCommandExecutor.CommandName, new ExtensionDoCommandExecutor (GetCommand, resources.Logger));
      AddCommand (this, ExtensionSetPropertyValueExecutor.CommandName, new ExtensionSetPropertyValueExecutor (GetCommand, resources.Logger));

      UpdateDevice (device, descriptor);
      }

   public string DeviceName => _deviceName;
   public string ModelName => _modelName;
   public string SerialNumber => _serialNumber;

   [EntityProperty (Id = "light:isOn")]
   public bool LightIsOn { get => _lightIsOn; private set => SetAndNotify ("light:isOn", value, ref _lightIsOn); }

   [EntityCommand (Id = "light:on")]
   public void LightOn ()
      {
      LightIsOn = true;
      ExecuteDeviceCommandAsync (device => device.TurnLightOnAsync ()).GetAwaiter ().GetResult ();
      }

   [EntityCommand (Id = "light:off")]
   public void LightOff ()
      {
      LightIsOn = false;
      ExecuteDeviceCommandAsync (device => device.TurnLightOffAsync ()).GetAwaiter ().GetResult ();
      }

   [EntityProperty (Id = "lightDimmer:level", RelativeRangeProperty = "lightDimmer:levelRange")]
   public double LightDimmerLevel { get => _lightDimmerLevel; private set => SetAndNotify ("lightDimmer:level", value, ref _lightDimmerLevel); }

   [EntityProperty (Id = "lightDimmer:levelRange")]
   public DriverEntityValueRelativeRange LightDimmerLevelRange { get; } = new (0.01);

   [EntityCommand (Id = "lightDimmer:setLevel")]
   public void LightDimmerSetLevel (
       [EntityParameter (RelativeRangeProperty = "lightDimmer:levelRange")] double level,
       [EntityParameter (OptionalFeature = true, OptionalParameter = true, DefaultValue = 0, Units = "Milliseconds")] long transitionTime)
      {
      double relativeLevel = Clamp01 (level);
      LightIsOn = relativeLevel > 0d;
      LightDimmerLevel = relativeLevel;
      ExecuteBrightnessCommandAsync (relativeLevel).GetAwaiter ().GetResult ();
      }

   [EntityProperty (Id = "onlineIndicator:isOnline")]
   public bool OnlineIndicatorIsOnline { get => _onlineIndicatorIsOnline; private set => SetAndNotify ("onlineIndicator:isOnline", value, ref _onlineIndicatorIsOnline); }

   [EntityProperty (Id = "readyIndicator:isReady")]
   public bool ReadyIndicatorIsReady { get => _readyIndicatorIsReady; private set => SetAndNotify ("readyIndicator:isReady", value, ref _readyIndicatorIsReady); }

   public void UpdateDevice (KasaDevice device, ManagedLightDescriptor descriptor)
      {
      _deviceQueue.SetClient (device);
      _deviceName = descriptor.Name;
      _modelName = descriptor.ModelName;
      _serialNumber = descriptor.SerialNumber;
      ApplyState (device);
      OnlineIndicatorIsOnline = true;
      ReadyIndicatorIsReady = true;
      }

   public async Task RefreshAsync (CancellationToken cancellationToken)
      {
      try
         {
         await _deviceQueue.EnqueueAsync (async device =>
            {
            cancellationToken.ThrowIfCancellationRequested ();
            await device.UpdateAsync (cancellationToken).ConfigureAwait (false);
            ApplyState (device);
            }).ConfigureAwait (false);
         OnlineIndicatorIsOnline = true;
         ReadyIndicatorIsReady = true;
         }
      catch (OperationCanceledException)
         {
         throw;
         }
      catch (Exception ex)
         {
         OnlineIndicatorIsOnline = false;
         ReadyIndicatorIsReady = false;
         _logger?.Log (_driverLogId, LogEntryLevel.Error, $"Light entity '{ControllerId}' refresh failed: {ex}");
         }
      }

   public void PublishStateSnapshot ()
      {
      NotifyPropertyChanged ("lightDimmer:level", new DriverEntityValue (_lightDimmerLevel));
      NotifyPropertyChanged ("light:isOn", new DriverEntityValue (_lightIsOn));
      }

   private async Task ExecuteBrightnessCommandAsync (double relativeLevel)
      {
      int requestedStateVersion = NextStateVersion ();
      try
         {
         await _deviceQueue.EnqueueAsync (async device =>
            {
            await SetBrightnessAsync (device, relativeLevel).ConfigureAwait (false);
            await ApplyFreshStateAsync (device, requestedStateVersion).ConfigureAwait (false);
            }).ConfigureAwait (false);
         OnlineIndicatorIsOnline = true;
         ReadyIndicatorIsReady = true;
         }
      catch (Exception ex)
         {
         OnlineIndicatorIsOnline = false;
         ReadyIndicatorIsReady = false;
         _logger?.Log (_driverLogId, LogEntryLevel.Error, $"Light entity '{ControllerId}' brightness command failed: {ex}");
         throw;
         }
      }

   private async Task ExecuteDeviceCommandAsync (Func<KasaDevice, Task> action)
      {
      int requestedStateVersion = NextStateVersion ();
      try
         {
         await _deviceQueue.EnqueueAsync (async device =>
            {
            await action (device).ConfigureAwait (false);
            await ApplyFreshStateAsync (device, requestedStateVersion).ConfigureAwait (false);
            }).ConfigureAwait (false);
         OnlineIndicatorIsOnline = true;
         ReadyIndicatorIsReady = true;
         }
      catch (Exception ex)
         {
         OnlineIndicatorIsOnline = false;
         ReadyIndicatorIsReady = false;
         _logger?.Log (_driverLogId, LogEntryLevel.Error, $"Light entity '{ControllerId}' command failed: {ex}");
         throw;
         }
      }

   private static Task SetBrightnessAsync (KasaDevice device, double relativeLevel)
      {
      int brightness = (int) Math.Round (relativeLevel * 100d, MidpointRounding.AwayFromZero);
      return brightness <= 0 ? device.TurnLightOffAsync () : device.SetBrightnessAsync (brightness);
      }

   private void ApplyState (KasaDevice device)
      {
      bool isOn = device.LightState?.IsOn ?? device.IsOn ?? false;
      int? brightness = device.LightState?.Brightness;
      LightIsOn = isOn;
      if (brightness.HasValue)
         {
         LightDimmerLevel = Clamp01 (brightness.Value / 100d);
         }
      }

   private async Task ApplyFreshStateAsync (KasaDevice device, int requestedStateVersion)
      {
      await Task.Delay (350).ConfigureAwait (false);
      await device.UpdateAsync ().ConfigureAwait (false);
      if (requestedStateVersion != _stateVersion)
         {
         return;
         }

      ApplyState (device);
      }

   private int NextStateVersion () => System.Threading.Interlocked.Increment (ref _stateVersion);

   private static double Clamp01 (double value) => value < 0 ? 0 : value > 1 ? 1 : value;

   private void SetAndNotify (string propertyId, bool value, ref bool field)
      {
      if (field == value) return;
      field = value;
      NotifyPropertyChanged (propertyId, new DriverEntityValue (value));
      }

   private void SetAndNotify (string propertyId, double value, ref double field)
      {
      if (Math.Abs (field - value) < 0.0001d) return;
      field = value;
      NotifyPropertyChanged (propertyId, new DriverEntityValue (value));
      }
   }
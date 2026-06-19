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

internal sealed class KasaOnOffLightEntity : ReflectedAttributeDriverEntity, IKasaManagedLightEntity
   {
   private readonly WorkQueue<KasaDevice> _deviceQueue = new ();
   private readonly DriverControllerLogger _logger;
   private readonly string _driverLogId;

   private string _deviceName;
   private string _modelName;
   private string _serialNumber;
   private string? _childId;
   private bool _lightIsOn;
   private bool _onlineIndicatorIsOnline;
   private bool _readyIndicatorIsReady;

   public KasaOnOffLightEntity (
       string controllerId,
       ManagedLightDescriptor descriptor,
       KasaDevice device,
       DriverImplementationResources resources,
       DriverControllerLogger logger,
       string driverLogId)
       : base (controllerId)
      {
      _logger = logger;
      _driverLogId = driverLogId;
      _deviceName = descriptor.Name;
      _modelName = descriptor.ModelName;
      _serialNumber = descriptor.SerialNumber;
      _childId = descriptor.ChildId;

      AddCommand (this, ExtensionDoCommandExecutor.CommandName, new ExtensionDoCommandExecutor (GetCommand, resources.Logger));
      AddCommand (this, ExtensionSetPropertyValueExecutor.CommandName, new ExtensionSetPropertyValueExecutor (GetCommand, resources.Logger));

      UpdateDevice (device, descriptor);
      }

   public string DeviceName => _deviceName;

   public string ModelName => _modelName;

   public string SerialNumber => _serialNumber;

   [EntityProperty (Id = "light:isOn")]
   public bool LightIsOn
      {
      get => _lightIsOn;
      private set => SetAndNotify ("light:isOn", value, ref _lightIsOn);
      }

   [EntityCommand (Id = "light:on")]
   public void LightOn ()
      {
      LightIsOn = true;
      ExecuteDeviceCommandAsync (device => ExecutePowerAsync (device, true)).GetAwaiter ().GetResult ();
      }

   [EntityCommand (Id = "light:off")]
   public void LightOff ()
      {
      LightIsOn = false;
      ExecuteDeviceCommandAsync (device => ExecutePowerAsync (device, false)).GetAwaiter ().GetResult ();
      }

   [EntityProperty (Id = "onlineIndicator:isOnline")]
   public bool OnlineIndicatorIsOnline
      {
      get => _onlineIndicatorIsOnline;
      private set => SetAndNotify ("onlineIndicator:isOnline", value, ref _onlineIndicatorIsOnline);
      }

   [EntityProperty (Id = "readyIndicator:isReady")]
   public bool ReadyIndicatorIsReady
      {
      get => _readyIndicatorIsReady;
      private set => SetAndNotify ("readyIndicator:isReady", value, ref _readyIndicatorIsReady);
      }

   public void UpdateDevice (KasaDevice device, ManagedLightDescriptor descriptor)
      {
      _deviceQueue.SetClient (device);
      _deviceName = descriptor.Name;
      _modelName = descriptor.ModelName;
      _serialNumber = descriptor.SerialNumber;
      _childId = descriptor.ChildId;
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
         _logger?.Log (_driverLogId, LogEntryLevel.Error, $"On/off light entity '{ControllerId}' refresh failed: {ex}");
         }
      }

   public void PublishStateSnapshot ()
      {
      NotifyPropertyChanged ("light:isOn", new DriverEntityValue (_lightIsOn));
      }

   private async Task ExecuteDeviceCommandAsync (Func<KasaDevice, Task> action)
      {
      try
         {
         await _deviceQueue.EnqueueAsync (async device =>
            {
            await action (device).ConfigureAwait (false);
            ApplyState (device);
            }).ConfigureAwait (false);

         OnlineIndicatorIsOnline = true;
         ReadyIndicatorIsReady = true;
         }
      catch (Exception ex)
         {
         OnlineIndicatorIsOnline = false;
         ReadyIndicatorIsReady = false;
         _logger?.Log (_driverLogId, LogEntryLevel.Error, $"On/off light entity '{ControllerId}' command failed: {ex}");
         throw;
         }
      }

   private Task ExecutePowerAsync (KasaDevice device, bool on)
      {
      string? childId = _childId;
      if (string.IsNullOrWhiteSpace (childId))
         {
         return on ? device.TurnOnAsync () : device.TurnOffAsync ();
         }

      return on ? device.TurnChildOnAsync (childId!) : device.TurnChildOffAsync (childId!);
      }

   private void ApplyState (KasaDevice device)
      {
      string? childId = _childId;
      bool isOn = string.IsNullOrWhiteSpace (childId)
          ? device.IsOn ?? false
          : device.GetChild (childId!)?.IsOn ?? false;

      LightIsOn = isOn;
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
   }
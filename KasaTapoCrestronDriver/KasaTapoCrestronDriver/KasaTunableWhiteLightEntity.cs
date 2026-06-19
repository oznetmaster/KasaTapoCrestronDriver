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

internal sealed class KasaTunableWhiteLightEntity : ReflectedAttributeDriverEntity, IKasaManagedLightEntity
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
   private long _lightColorTemperatureLevel;
   private bool _onlineIndicatorIsOnline;
   private bool _readyIndicatorIsReady;

   public KasaTunableWhiteLightEntity (string controllerId, ManagedLightDescriptor descriptor, KasaDevice device, DriverImplementationResources resources, DriverControllerLogger logger, string driverLogId)
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
   [EntityProperty (Id = "light:isOn")] public bool LightIsOn { get => _lightIsOn; private set => SetAndNotify ("light:isOn", value, ref _lightIsOn); }
   [EntityProperty (Id = "lightDimmer:level", RelativeRangeProperty = "lightDimmer:levelRange")] public double LightDimmerLevel { get => _lightDimmerLevel; private set => SetAndNotify ("lightDimmer:level", value, ref _lightDimmerLevel); }
   [EntityProperty (Id = "lightDimmer:levelRange")] public DriverEntityValueRelativeRange LightDimmerLevelRange { get; } = new (0.01);
   [EntityProperty (Id = "lightColorTemperature:level", RangeProperty = "lightColorTemperature:range", Units = "Kelvin")] public long LightColorTemperatureLevel { get => _lightColorTemperatureLevel; private set => SetAndNotify ("lightColorTemperature:level", value, ref _lightColorTemperatureLevel); }
   [EntityProperty (Id = "lightColorTemperature:range", Units = "Kelvin")] public DriverEntityValueRange LightColorTemperatureRange => KasaLightEntity.CreateSharedColorTemperatureRange (_modelName);
   [EntityProperty (Id = "onlineIndicator:isOnline")] public bool OnlineIndicatorIsOnline { get => _onlineIndicatorIsOnline; private set => SetAndNotify ("onlineIndicator:isOnline", value, ref _onlineIndicatorIsOnline); }
   [EntityProperty (Id = "readyIndicator:isReady")] public bool ReadyIndicatorIsReady { get => _readyIndicatorIsReady; private set => SetAndNotify ("readyIndicator:isReady", value, ref _readyIndicatorIsReady); }

   [EntityCommand (Id = "light:on")]
   public void LightOn () { LightIsOn = true; ExecuteDeviceCommandAsync (d => d.TurnLightOnAsync ()).GetAwaiter ().GetResult (); }
   [EntityCommand (Id = "light:off")]
   public void LightOff () { LightIsOn = false; ExecuteDeviceCommandAsync (d => d.TurnLightOffAsync ()).GetAwaiter ().GetResult (); }
   [EntityCommand (Id = "lightDimmer:setLevel")]
   public void LightDimmerSetLevel ([EntityParameter (RelativeRangeProperty = "lightDimmer:levelRange")] double level, [EntityParameter (OptionalFeature = true, OptionalParameter = true, DefaultValue = 0, Units = "Milliseconds")] long transitionTime)
      {
      double relativeLevel = Clamp01 (level); LightIsOn = relativeLevel > 0d; LightDimmerLevel = relativeLevel; ExecuteBrightnessCommandAsync (relativeLevel).GetAwaiter ().GetResult (); }
   [EntityCommand (Id = "lightColorTemperature:setLevel")]
   public void LightColorTemperatureSetLevel ([EntityParameter (RangeProperty = "lightColorTemperature:range", Units = "Kelvin")] long level, [EntityParameter (OptionalFeature = true, OptionalParameter = true, DefaultValue = 0, Units = "Milliseconds")] long transitionTime)
      {
      long temperatureLevel = Math.Max (1L, level); LightColorTemperatureLevel = temperatureLevel; if (!LightIsOn) { LightIsOn = true; } ExecuteColorTemperatureCommandAsync (temperatureLevel).GetAwaiter ().GetResult (); }

   public void UpdateDevice (KasaDevice device, ManagedLightDescriptor descriptor)
      { _deviceQueue.SetClient (device); _deviceName = descriptor.Name; _modelName = descriptor.ModelName; _serialNumber = descriptor.SerialNumber; ApplyState (device); OnlineIndicatorIsOnline = true; ReadyIndicatorIsReady = true; }

   public async Task RefreshAsync (CancellationToken cancellationToken)
      {
      try
         {
         await _deviceQueue.EnqueueAsync (async d =>
            {
            cancellationToken.ThrowIfCancellationRequested ();
            await d.UpdateAsync (cancellationToken).ConfigureAwait (false);
            ApplyState (d);
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
      NotifyPropertyChanged ("lightColorTemperature:level", new DriverEntityValue (_lightColorTemperatureLevel));
      NotifyPropertyChanged ("light:isOn", new DriverEntityValue (_lightIsOn));
      }

   private async Task ExecuteBrightnessCommandAsync (double relativeLevel) { int requestedStateVersion = NextStateVersion (); try { await _deviceQueue.EnqueueAsync (async d => { await SetBrightnessAsync (d, relativeLevel).ConfigureAwait (false); await ApplyFreshStateAsync (d, requestedStateVersion).ConfigureAwait (false); }).ConfigureAwait (false); OnlineIndicatorIsOnline = true; ReadyIndicatorIsReady = true; } catch (Exception ex) { OnlineIndicatorIsOnline = false; ReadyIndicatorIsReady = false; _logger?.Log (_driverLogId, LogEntryLevel.Error, $"Light entity '{ControllerId}' brightness command failed: {ex}"); throw; } }
   private async Task ExecuteColorTemperatureCommandAsync (long colorTemperature) { int requestedStateVersion = NextStateVersion (); try { await _deviceQueue.EnqueueAsync (async d => { await d.SetColorTemperatureAsync ((int) colorTemperature).ConfigureAwait (false); await ApplyFreshStateAsync (d, requestedStateVersion).ConfigureAwait (false); }).ConfigureAwait (false); OnlineIndicatorIsOnline = true; ReadyIndicatorIsReady = true; } catch (Exception ex) { OnlineIndicatorIsOnline = false; ReadyIndicatorIsReady = false; _logger?.Log (_driverLogId, LogEntryLevel.Error, $"Light entity '{ControllerId}' color temperature command failed: {ex}"); throw; } }
   private async Task ExecuteDeviceCommandAsync (Func<KasaDevice, Task> action) { int requestedStateVersion = NextStateVersion (); try { await _deviceQueue.EnqueueAsync (async d => { await action (d).ConfigureAwait (false); await ApplyFreshStateAsync (d, requestedStateVersion).ConfigureAwait (false); }).ConfigureAwait (false); OnlineIndicatorIsOnline = true; ReadyIndicatorIsReady = true; } catch (Exception ex) { OnlineIndicatorIsOnline = false; ReadyIndicatorIsReady = false; _logger?.Log (_driverLogId, LogEntryLevel.Error, $"Light entity '{ControllerId}' command failed: {ex}"); throw; } }
   private static Task SetBrightnessAsync (KasaDevice d, double relativeLevel) { int b = (int) Math.Round (relativeLevel * 100d, MidpointRounding.AwayFromZero); return b <= 0 ? d.TurnLightOffAsync () : d.SetBrightnessAsync (b); }
   private void ApplyState (KasaDevice d) { bool isOn = d.LightState?.IsOn ?? d.IsOn ?? false; int? brightness = d.LightState?.Brightness; int? colorTemperature = d.LightState?.ColorTemperature; LightIsOn = isOn; LightColorTemperatureLevel = colorTemperature.HasValue && colorTemperature.Value > 0 ? colorTemperature.Value : 0L; if (brightness.HasValue) { LightDimmerLevel = Clamp01 (brightness.Value / 100d); } }
   private async Task ApplyFreshStateAsync (KasaDevice device, int requestedStateVersion) { await Task.Delay (350).ConfigureAwait (false); await device.UpdateAsync ().ConfigureAwait (false); if (requestedStateVersion != _stateVersion) return; ApplyState (device); }
   private int NextStateVersion () => System.Threading.Interlocked.Increment (ref _stateVersion);
   private static double Clamp01 (double value) => value < 0 ? 0 : value > 1 ? 1 : value;
   private void SetAndNotify (string propertyId, bool value, ref bool field) { if (field == value) return; field = value; NotifyPropertyChanged (propertyId, new DriverEntityValue (value)); }
   private void SetAndNotify (string propertyId, double value, ref double field) { if (Math.Abs (field - value) < 0.0001d) return; field = value; NotifyPropertyChanged (propertyId, new DriverEntityValue (value)); }
   private void SetAndNotify (string propertyId, long value, ref long field) { if (field == value) return; field = value; NotifyPropertyChanged (propertyId, new DriverEntityValue (value)); }
   }
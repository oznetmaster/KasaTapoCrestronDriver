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

internal class KasaLightEntity : ReflectedAttributeDriverEntity, IKasaManagedLightEntity
   {
   private enum DesiredLightMode
      {
      Off,
      On,
      Brightness,
      ColorTemperature,
      Hsv
      }

   private readonly struct DesiredLightCommand
      {
      public DesiredLightCommand (DesiredLightMode mode, double level, double hue, double saturation, long colorTemperature)
         {
         Mode = mode;
         Level = level;
         Hue = hue;
         Saturation = saturation;
         ColorTemperature = colorTemperature;
         }

      public DesiredLightMode Mode { get; }
      public double Level { get; }
      public double Hue { get; }
      public double Saturation { get; }
      public long ColorTemperature { get; }
      }

   private static readonly TimeSpan SliderDebounceInterval = TimeSpan.FromMilliseconds (250);

   private readonly object _sliderGate = new ();
   protected readonly WorkQueue<KasaDevice> DeviceQueue = new ();
   private readonly DriverControllerLogger _logger;
   private readonly string _driverLogId;

   private string _deviceName;
   private string _modelName;
   private string _serialNumber;
   private DesiredLightCommand? _pendingSliderCommand;
   private CancellationTokenSource? _sliderCommandCancellationSource;
   private bool _sliderInteractionActive;
   private int _stateVersion;
   private bool _lightIsOn;
   private double _lightDimmerLevel;
   private double _lightColorHue;
   private double _lightColorSaturation;
   private long _lightColorTemperatureLevel;
   private bool _onlineIndicatorIsOnline;
   private bool _readyIndicatorIsReady;

   public KasaLightEntity (
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
      ExecuteDeviceCommandAsync (device => device.TurnLightOnAsync ()).GetAwaiter ().GetResult ();
      }

   [EntityCommand (Id = "light:off")]
   public void LightOff ()
      {
      LightIsOn = false;
      CancelSliderInteraction ();
      ExecuteDeviceCommandAsync (device => device.TurnLightOffAsync ()).GetAwaiter ().GetResult ();
      }

   [EntityProperty (Id = "lightDimmer:level", RelativeRangeProperty = "lightDimmer:levelRange")]
   public double LightDimmerLevel
      {
      get => _lightDimmerLevel;
      private set => SetAndNotify ("lightDimmer:level", value, ref _lightDimmerLevel);
      }

   [EntityProperty (Id = "lightDimmer:levelRange")]
   public DriverEntityValueRelativeRange LightDimmerLevelRange { get; } = new (0.01);

   [EntityProperty (Id = "lightColor:hue", RelativeRangeProperty = "lightColor:hueRange")]
   public double LightColorHue
      {
      get => _lightColorHue;
      private set => SetAndNotify ("lightColor:hue", value, ref _lightColorHue);
      }

   [EntityProperty (Id = "lightColor:hueRange")]
   public DriverEntityValueRelativeRange LightColorHueRange { get; } = new (1d / 360d);

   [EntityProperty (Id = "lightColor:saturation", RelativeRangeProperty = "lightColor:saturationRange")]
   public double LightColorSaturation
      {
      get => _lightColorSaturation;
      private set => SetAndNotify ("lightColor:saturation", value, ref _lightColorSaturation);
      }

   [EntityProperty (Id = "lightColor:saturationRange")]
   public DriverEntityValueRelativeRange LightColorSaturationRange { get; } = new (0.01);

   [EntityProperty (Id = "lightColorTemperature:level", RangeProperty = "lightColorTemperature:range", Units = "Kelvin")]
   public long LightColorTemperatureLevel
      {
      get => _lightColorTemperatureLevel;
      private set => SetAndNotify ("lightColorTemperature:level", value, ref _lightColorTemperatureLevel);
      }

   [EntityProperty (Id = "lightColorTemperature:range", Units = "Kelvin")]
   public DriverEntityValueRange LightColorTemperatureRange => CreateSharedColorTemperatureRange (_modelName);

   [EntityCommand (Id = "lightDimmer:setLevel")]
   public void LightDimmerSetLevel (
       [EntityParameter (RelativeRangeProperty = "lightDimmer:levelRange")] double level,
       [EntityParameter (OptionalFeature = true, OptionalParameter = true, DefaultValue = 0, Units = "Milliseconds")] long transitionTime)
      {
      double relativeLevel = Clamp01 (level);
      LightIsOn = relativeLevel > 0d;
      LightDimmerLevel = relativeLevel;

       QueueSliderCommand (new DesiredLightCommand (DesiredLightMode.Brightness, relativeLevel, 0d, 0d, 0L));
      }

   [EntityCommand (Id = "lightColor:setHue")]
   public void LightColorSetHue (
       [EntityParameter (RelativeRangeProperty = "lightColor:hueRange")] double level,
       [EntityParameter (OptionalFeature = true, OptionalParameter = true, DefaultValue = 0, Units = "Milliseconds")] long transitionTime)
      {
      double hueLevel = Clamp01 (level);
       LightColorTemperatureLevel = 0L;
       LightColorHue = hueLevel;
      if (!LightIsOn)
         {
         LightIsOn = true;
         }

       QueueSliderCommand (new DesiredLightCommand (DesiredLightMode.Hsv, GetEffectiveOnLevel (), hueLevel, LightColorSaturation, 0L));
      }

   [EntityCommand (Id = "lightColor:setSaturation")]
   public void LightColorSetSaturation (
       [EntityParameter (RelativeRangeProperty = "lightColor:saturationRange")] double level,
       [EntityParameter (OptionalFeature = true, OptionalParameter = true, DefaultValue = 0, Units = "Milliseconds")] long transitionTime)
      {
      double saturationLevel = Clamp01 (level);
       LightColorTemperatureLevel = 0L;
       LightColorSaturation = saturationLevel;
      if (!LightIsOn)
         {
         LightIsOn = true;
         }

       QueueSliderCommand (new DesiredLightCommand (DesiredLightMode.Hsv, GetEffectiveOnLevel (), LightColorHue, saturationLevel, 0L));
      }

   [EntityCommand (Id = "lightColorTemperature:setLevel")]
   public void LightColorTemperatureSetLevel (
       [EntityParameter (RangeProperty = "lightColorTemperature:range", Units = "Kelvin")] long level,
       [EntityParameter (OptionalFeature = true, OptionalParameter = true, DefaultValue = 0, Units = "Milliseconds")] long transitionTime)
      {
      long temperatureLevel = Math.Max (1L, level);
       LightColorHue = 0d;
       LightColorSaturation = 0d;
       LightColorTemperatureLevel = temperatureLevel;
      if (!LightIsOn)
         {
         LightIsOn = true;
         }

       QueueSliderCommand (new DesiredLightCommand (DesiredLightMode.ColorTemperature, GetEffectiveOnLevel (), 0d, 0d, temperatureLevel));
      }

   [EntityProperty (Id = "onlineIndicator:isOnline")]
   public bool OnlineIndicatorIsOnline
      {
      get => _onlineIndicatorIsOnline;
      private set => SetAndNotify ("onlineIndicator:isOnline", value, ref _onlineIndicatorIsOnline);
      }

   private void QueueSliderCommand (DesiredLightCommand command)
      {
      CancellationTokenSource cancellationSource;
      lock (_sliderGate)
         {
         _pendingSliderCommand = command;
         _sliderInteractionActive = true;
         _sliderCommandCancellationSource?.Cancel ();
         _sliderCommandCancellationSource?.Dispose ();
         _sliderCommandCancellationSource = new CancellationTokenSource ();
         cancellationSource = _sliderCommandCancellationSource;
         }

      _ = ProcessSliderCommandAsync (cancellationSource.Token);
      }

   private async Task ProcessSliderCommandAsync (CancellationToken cancellationToken)
      {
      try
         {
         await Task.Delay (SliderDebounceInterval, cancellationToken).ConfigureAwait (false);

         DesiredLightCommand? command;
         lock (_sliderGate)
            {
            if (cancellationToken.IsCancellationRequested || _sliderCommandCancellationSource is null || _sliderCommandCancellationSource.Token != cancellationToken)
               {
               return;
               }

            command = _pendingSliderCommand;
            _pendingSliderCommand = null;
            }

         if (!command.HasValue)
            {
            return;
            }

         await DeviceQueue.EnqueueAsync (async device =>
            {
            cancellationToken.ThrowIfCancellationRequested ();
            await ApplyDesiredLightCommandAsync (device, command.Value).ConfigureAwait (false);
            }).ConfigureAwait (false);

         OnlineIndicatorIsOnline = true;
         ReadyIndicatorIsReady = true;
         }
      catch (OperationCanceledException)
         {
         }
      catch (Exception ex)
         {
         OnlineIndicatorIsOnline = false;
         ReadyIndicatorIsReady = false;
         _logger?.Log (_driverLogId, LogEntryLevel.Error, $"Light entity '{ControllerId}' slider command failed: {ex}");
         }
      finally
         {
         lock (_sliderGate)
            {
            if (_sliderCommandCancellationSource is not null && _sliderCommandCancellationSource.Token == cancellationToken)
               {
               _sliderInteractionActive = false;
               _sliderCommandCancellationSource.Dispose ();
               _sliderCommandCancellationSource = null;
               }
            }
         }
      }

   private bool IsSliderInteractionActive ()
      {
      lock (_sliderGate)
         {
         return _sliderInteractionActive;
         }
      }

   private void CancelSliderInteraction ()
      {
      lock (_sliderGate)
         {
         _sliderInteractionActive = false;
         _pendingSliderCommand = null;
         _sliderCommandCancellationSource?.Cancel ();
         _sliderCommandCancellationSource?.Dispose ();
         _sliderCommandCancellationSource = null;
         }
      }

   private async Task ApplyDesiredLightCommandAsync (KasaDevice device, DesiredLightCommand command)
      {
      switch (command.Mode)
         {
         case DesiredLightMode.Off:
            await device.TurnLightOffAsync ().ConfigureAwait (false);
            return;

         case DesiredLightMode.On:
            await device.TurnLightOnAsync ().ConfigureAwait (false);
            return;

         case DesiredLightMode.Hsv:
            await device.SetHsvAsync (
                (int) Math.Round (Clamp01 (command.Hue) * 360d, MidpointRounding.AwayFromZero),
                (int) Math.Round (Clamp01 (command.Saturation) * 100d, MidpointRounding.AwayFromZero),
                ToBrightnessPercent (command.Level)).ConfigureAwait (false);
            return;

         case DesiredLightMode.ColorTemperature:
            await EnsurePoweredForLevelAsync (device, command.Level).ConfigureAwait (false);
            await device.SetBrightnessAsync (ToBrightnessPercent (command.Level)).ConfigureAwait (false);
            await device.SetColorTemperatureAsync ((int) Math.Max (1L, command.ColorTemperature)).ConfigureAwait (false);
            return;

         case DesiredLightMode.Brightness:
            await ApplyBrightnessCommandAsync (device, command.Level).ConfigureAwait (false);
            return;
         }
      }

   [EntityProperty (Id = "readyIndicator:isReady")]
   public bool ReadyIndicatorIsReady
      {
      get => _readyIndicatorIsReady;
      private set => SetAndNotify ("readyIndicator:isReady", value, ref _readyIndicatorIsReady);
      }

   public virtual void UpdateDevice (KasaDevice device, ManagedLightDescriptor descriptor)
      {
      DeviceQueue.SetClient (device);
      _deviceName = descriptor.Name;
      _modelName = descriptor.ModelName;
      _serialNumber = descriptor.SerialNumber;
      LogReportedState ("UpdateDevice", device);
      ApplyState (device);
      OnlineIndicatorIsOnline = true;
      ReadyIndicatorIsReady = true;
      }

   public async Task RefreshAsync (CancellationToken cancellationToken)
      {
      try
         {
         await DeviceQueue.EnqueueAsync (async device =>
            {
            cancellationToken.ThrowIfCancellationRequested ();
            await device.UpdateAsync (cancellationToken).ConfigureAwait (false);
            LogReportedState ("RefreshAsync.AfterUpdate", device);
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
      LogPublishedState ();
      NotifyPropertyChanged ("lightDimmer:level", new DriverEntityValue (_lightDimmerLevel));

      if (_lightColorSaturation > 0.001d)
         {
         NotifyPropertyChanged ("lightColorTemperature:level", new DriverEntityValue (_lightColorTemperatureLevel));
         NotifyPropertyChanged ("lightColor:hue", new DriverEntityValue (_lightColorHue));
         NotifyPropertyChanged ("lightColor:saturation", new DriverEntityValue (_lightColorSaturation));
         }
      else
         {
         NotifyPropertyChanged ("lightColor:hue", new DriverEntityValue (_lightColorHue));
         NotifyPropertyChanged ("lightColor:saturation", new DriverEntityValue (_lightColorSaturation));
         NotifyPropertyChanged ("lightColorTemperature:level", new DriverEntityValue (_lightColorTemperatureLevel));
         }

      NotifyPropertyChanged ("light:isOn", new DriverEntityValue (_lightIsOn));
      }

   protected async Task ExecuteDeviceCommandAsync (Func<KasaDevice, Task> action)
      {
      try
         {
         await DeviceQueue.EnqueueAsync (async device =>
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
         _logger?.Log (_driverLogId, LogEntryLevel.Error, $"Light entity '{ControllerId}' command failed: {ex}");
         throw;
         }
      }

   private static Task SetBrightnessAsync (KasaDevice device, double relativeLevel)
      {
      int brightness = (int) Math.Round (relativeLevel * 100d, MidpointRounding.AwayFromZero);
      return brightness <= 0
          ? device.TurnLightOffAsync ()
          : device.SetBrightnessAsync (brightness);
      }

   private void ApplyState (KasaDevice device)
      {
      bool sliderInteractionActive = IsSliderInteractionActive ();
      bool isOn = device.LightState?.IsOn ?? device.IsOn ?? false;
      int? brightness = device.LightState?.Brightness;
      int? hue = device.LightState?.Hue ?? device.LightState?.Hsv?.Hue;
      int? saturation = device.LightState?.Saturation ?? device.LightState?.Hsv?.Saturation;
      int? colorTemperature = device.LightState?.ColorTemperature;
      bool hasWhiteFeedback = colorTemperature.HasValue && colorTemperature.Value > 0;
      bool hasSaturationFeedback = saturation.HasValue;
      bool hasActiveColorFeedback = saturation is int saturationValue && saturationValue > 0;

      LightIsOn = isOn;

      if (brightness.HasValue && !sliderInteractionActive)
         {
         double reportedLevel = Clamp01 (brightness.Value / 100d);
         }

      if (!sliderInteractionActive && hasActiveColorFeedback && saturation.HasValue)
         {
         LightColorHue = hue.HasValue ? Clamp01 (hue.Value / 360d) : LightColorHue;
         LightColorSaturation = Clamp01 (saturation.Value / 100d);
         LightColorTemperatureLevel = 0L;
         }
      else if (!sliderInteractionActive && hasWhiteFeedback)
         {
         LightColorTemperatureLevel = colorTemperature!.Value;
         LightColorSaturation = 0d;
         LightColorHue = 0d;
         }
      else if (!sliderInteractionActive && hasSaturationFeedback)
         {
         LightColorSaturation = Clamp01 (saturation!.Value / 100d);
         if (LightColorSaturation <= 0.001d)
            {
            LightColorHue = 0d;
            }
         }

      if (!sliderInteractionActive && brightness.HasValue)
         {
         LightDimmerLevel = Clamp01 (brightness.Value / 100d);
         }
      else if (!sliderInteractionActive && isOn)
         {
         LightDimmerLevel = 1d;
         }

      OnStateApplied (device);
      }

   protected async Task ApplyFreshStateAsync (KasaDevice device, int requestedStateVersion)
      {
      await Task.Delay (350).ConfigureAwait (false);
      await device.UpdateAsync ().ConfigureAwait (false);
      if (requestedStateVersion != _stateVersion)
         {
         return;
         }

      ApplyState (device);
      }

   private DesiredLightCommand CreateBrightnessCommand (double relativeLevel)
      {
      return new DesiredLightCommand (DesiredLightMode.Brightness, relativeLevel, 0d, 0d, 0L);
      }

   private async Task ApplyBrightnessCommandAsync (KasaDevice device, double relativeLevel)
      {
      if (relativeLevel <= 0d)
         {
         await device.TurnLightOffAsync ().ConfigureAwait (false);
         return;
         }

      await EnsurePoweredForLevelAsync (device, relativeLevel).ConfigureAwait (false);
      await device.SetBrightnessAsync (ToBrightnessPercent (relativeLevel)).ConfigureAwait (false);
      }

   private static async Task EnsurePoweredForLevelAsync (KasaDevice device, double relativeLevel)
      {
      if (relativeLevel <= 0d || device.LightState?.IsOn == true || device.IsOn == true)
         {
         return;
         }

      await device.TurnLightOnAsync ().ConfigureAwait (false);
      }

   private double GetEffectiveOnLevel () => LightDimmerLevel > 0d ? LightDimmerLevel : 1d;

   private static int ToBrightnessPercent (double relativeLevel) => Math.Max (1, (int) Math.Round (Clamp01 (relativeLevel) * 100d, MidpointRounding.AwayFromZero));

   protected int NextStateVersion () => System.Threading.Interlocked.Increment (ref _stateVersion);

   private void LogReportedState (string stage, KasaDevice device)
      {
      int? brightness = device.LightState?.Brightness;
      int? hue = device.LightState?.Hue ?? device.LightState?.Hsv?.Hue;
      int? saturation = device.LightState?.Saturation ?? device.LightState?.Hsv?.Saturation;
      int? colorTemperature = device.LightState?.ColorTemperature;
      bool? isOn = device.LightState?.IsOn ?? device.IsOn;

      _logger?.Log (
          _driverLogId,
          LogEntryLevel.Info,
          $"Light entity '{ControllerId}' {stage} raw state: isOn={FormatNullable (isOn)}, brightness={FormatNullable (brightness)}, hue={FormatNullable (hue)}, saturation={FormatNullable (saturation)}, colorTemperature={FormatNullable (colorTemperature)}.");
      }

   private void LogPublishedState ()
      {
      _logger?.Log (
          _driverLogId,
          LogEntryLevel.Info,
          $"Light entity '{ControllerId}' PublishStateSnapshot values: isOn={_lightIsOn}, dimmer={_lightDimmerLevel:0.####}, hue={_lightColorHue:0.####}, saturation={_lightColorSaturation:0.####}, colorTemperature={_lightColorTemperatureLevel}.");
      }

   private static string FormatNullable<T> (T? value)
      where T : struct
      {
      return value.HasValue ? value.Value.ToString ()! : "null";
      }

   protected virtual void OnStateApplied (KasaDevice device)
      {
      }

   internal static DriverEntityValueRange CreateSharedColorTemperatureRange (string? modelName)
      {
      (long minimum, long maximum) = GetColorTemperatureBounds (modelName);
      return new DriverEntityValueRange (minimum, maximum, 1);
      }

   private static (long Minimum, long Maximum) GetColorTemperatureBounds (string? modelName)
      {
      string modelText = (modelName ?? string.Empty).Trim ().ToUpperInvariant ();
      if (modelText.StartsWith ("LB130", StringComparison.Ordinal)
          || modelText.StartsWith ("LB230", StringComparison.Ordinal)
          || modelText.StartsWith ("KB130", StringComparison.Ordinal)
          || modelText.StartsWith ("KL130", StringComparison.Ordinal)
          || modelText.StartsWith ("KL135", StringComparison.Ordinal)
          || modelText.StartsWith ("KL430", StringComparison.Ordinal)
          || modelText.StartsWith ("L900", StringComparison.Ordinal))
         {
         return (2500L, 9000L);
         }

      if (modelText.StartsWith ("LB120", StringComparison.Ordinal)
          || modelText.StartsWith ("KL120(EU)", StringComparison.Ordinal)
          || modelText.StartsWith ("KL125", StringComparison.Ordinal)
          || modelText.StartsWith ("L530", StringComparison.Ordinal))
         {
         return (2500L, 6500L);
         }

      if (modelText.StartsWith ("KL120(US)", StringComparison.Ordinal))
         {
         return (2700L, 5000L);
         }

      return (1500L, 9000L);
      }

   private static double Clamp01 (double value)
      {
      if (value < 0)
         {
         return 0;
         }

      if (value > 1)
         {
         return 1;
         }

      return value;
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

   private void SetAndNotify (string propertyId, double value, ref double field)
      {
      if (Math.Abs (field - value) < 0.0001d)
         {
         return;
         }

      field = value;
      NotifyPropertyChanged (propertyId, new DriverEntityValue (value));
      }

   private void SetAndNotify (string propertyId, long value, ref long field)
      {
      if (field == value)
         {
         return;
         }

      field = value;
      NotifyPropertyChanged (propertyId, new DriverEntityValue (value));
      }
   }
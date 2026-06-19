using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Crestron.DeviceDrivers.EntityModel.Data;
using Crestron.DeviceDrivers.EntityModel.Logging;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel.Attributes;

using KasaTapoClient;

namespace KasaTapoCrestronDriver;

internal sealed class KasaEffectLightEntity : KasaLightEntity
   {
   private static readonly ICollection<SceneControllerSceneType> LightSceneTypes = new[] { SceneControllerSceneType.Light };
   private const string NoEffectSceneId = "no_effect";

   private readonly DriverControllerLogger _logger;
   private readonly string _driverLogId;
   private IDictionary<string, SceneControllerScene> _sceneControllerScenes = new Dictionary<string, SceneControllerScene> (StringComparer.OrdinalIgnoreCase);

   public KasaEffectLightEntity (string controllerId, ManagedLightDescriptor descriptor, KasaDevice device, DriverImplementationResources resources, DriverControllerLogger logger, string driverLogId)
       : base (controllerId, descriptor, device, resources, logger, driverLogId)
      {
      _logger = logger;
      _driverLogId = driverLogId;
      }

   [EntityProperty (Id = "sceneController:scenes", ItemTypeRef = "sceneController:Scene")]
   public IDictionary<string, SceneControllerScene> SceneControllerScenes
      {
      get => _sceneControllerScenes;
      private set => SetAndNotifyScenes (value, ref _sceneControllerScenes);
      }

   [EntityCommand (Id = "sceneController:recallScene")]
   public void SceneControllerRecallScene (string sceneId)
      {
      if (string.IsNullOrWhiteSpace (sceneId))
         {
         throw new ArgumentException ("Scene ID is required.", nameof (sceneId));
         }

      ExecuteEffectSceneAsync (sceneId.Trim ()).GetAwaiter ().GetResult ();
      }

   public override void UpdateDevice (KasaDevice device, ManagedLightDescriptor descriptor)
      {
      base.UpdateDevice (device, descriptor);
      UpdateScenes (device);
      }

   protected override void OnStateApplied (KasaDevice device)
      {
      base.OnStateApplied (device);
      UpdateScenes (device);
      }

   private async Task ExecuteEffectSceneAsync (string sceneId)
      {
      int requestedStateVersion = NextStateVersion ();
      try
         {
         await DeviceQueue.EnqueueAsync (async device =>
            {
            if (string.Equals (sceneId, NoEffectSceneId, StringComparison.OrdinalIgnoreCase)
                || string.Equals (GetActiveSceneId (device), sceneId, StringComparison.OrdinalIgnoreCase))
               {
               await device.ClearLightEffectAsync ().ConfigureAwait (false);
               }
            else
               {
               await device.SetLightEffectAsync (sceneId).ConfigureAwait (false);
               }

            await ApplyFreshStateAsync (device, requestedStateVersion).ConfigureAwait (false);
            }).ConfigureAwait (false);
         }
      catch (Exception ex)
         {
         _logger?.Log (_driverLogId, LogEntryLevel.Error, $"Light entity '{ControllerId}' effect scene command failed: {ex}");
         throw;
         }
      }

   private void UpdateScenes (KasaDevice device)
      {
      if (!device.SupportsLightEffects)
         {
         SceneControllerScenes = new Dictionary<string, SceneControllerScene> (StringComparer.OrdinalIgnoreCase);
         return;
         }

      string? activeSceneId = GetActiveSceneId (device);
      var scenes = new Dictionary<string, SceneControllerScene> (StringComparer.OrdinalIgnoreCase);
      scenes[NoEffectSceneId] = new SceneControllerScene ("No Effect", supportsIsActive: true, isActive: string.IsNullOrWhiteSpace (activeSceneId), isToggle: false, applicableSceneTypes: LightSceneTypes);
      foreach (LightEffectDefinition effect in device.AvailableLightEffects)
         {
         if (string.IsNullOrWhiteSpace (effect.Identifier))
            {
            continue;
            }

         string sceneId = effect.Identifier;
         string sceneName = string.IsNullOrWhiteSpace (effect.Name) ? effect.Identifier : effect.Name!;
         bool isActive = string.Equals (sceneId, activeSceneId, StringComparison.OrdinalIgnoreCase);
         scenes[sceneId] = new SceneControllerScene (sceneName, supportsIsActive: true, isActive: isActive, isToggle: true, applicableSceneTypes: LightSceneTypes);
         }

      SceneControllerScenes = scenes;
      }

   private static string? GetActiveSceneId (KasaDevice device)
      {
      LightEffectState? effect = device.LightEffect;
      if (effect?.IsEnabled != true)
         {
         return null;
         }

      return !string.IsNullOrWhiteSpace (effect.Identifier)
          ? effect.Identifier
          : effect.AvailableEffects.FirstOrDefault (candidate => string.Equals (candidate.Name, effect.Name, StringComparison.OrdinalIgnoreCase))?.Identifier;
      }

   private void SetAndNotifyScenes (IDictionary<string, SceneControllerScene> value, ref IDictionary<string, SceneControllerScene> field)
      {
      field = value;
      NotifyPropertyChanged ("sceneController:scenes", CreateValueForEntries (field));
      }
   }
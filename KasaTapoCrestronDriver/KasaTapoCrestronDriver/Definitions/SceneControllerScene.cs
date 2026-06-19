using System.Collections.Generic;

using Crestron.DeviceDrivers.SDK.EntityModel.Attributes;

namespace KasaTapoCrestronDriver;

[EntityDataType (Id = "sceneController:Scene")]
public sealed class SceneControllerScene
   {
   public SceneControllerScene (string name, bool supportsIsActive, bool isActive, bool isToggle, ICollection<SceneControllerSceneType> applicableSceneTypes)
      {
      Name = name;
      SupportsIsActive = supportsIsActive;
      IsActive = isActive;
      IsToggle = isToggle;
      ApplicableSceneTypes = applicableSceneTypes;
      }

   [EntityProperty]
   public string Name { get; }

   [EntityProperty]
   public bool SupportsIsActive { get; }

   [EntityProperty]
   public bool IsActive { get; }

   [EntityProperty]
   public bool IsToggle { get; }

   [EntityProperty]
   public ICollection<SceneControllerSceneType> ApplicableSceneTypes { get; }
   }
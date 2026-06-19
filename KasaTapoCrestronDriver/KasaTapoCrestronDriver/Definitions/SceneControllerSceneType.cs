using Crestron.DeviceDrivers.SDK.EntityModel.Attributes;

namespace KasaTapoCrestronDriver;

[EntityDataType (Id = "sceneController:SceneType")]
public enum SceneControllerSceneType
   {
   Unknown,
   Other,
   Light,
   Camera,
   Audio,
   Video
   }
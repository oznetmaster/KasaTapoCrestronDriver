// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE file in the project root for full license information.

using Crestron.DeviceDrivers.SDK.EntityModel.Attributes;

namespace KasaTapoCrestronDriver;

[EntityDataType (Id = "crestron:DeviceUxCategory")]
/// <summary>
/// Defines common Crestron Home UX categories that can be assigned to scaffold managed devices.
/// </summary>
public enum DeviceUxCategory
   {
   /// <summary>Represents a generic device category.</summary>
   Other,
   /// <summary>Represents an amplifier device.</summary>
   Amplifier,
   /// <summary>Represents an appliance device.</summary>
   Appliance,
   /// <summary>Represents an area device.</summary>
   Area,
   /// <summary>Represents an audio mixer device.</summary>
   AudioMixer,
   /// <summary>Represents an audio processor device.</summary>
   AudioProcessor,
   /// <summary>Represents an AV receiver device.</summary>
   AvReceiver,
   /// <summary>Represents an AV switcher device.</summary>
   AvSwitcher,
   /// <summary>Represents an audio switcher device.</summary>
   AudioSwitcher,
   /// <summary>Represents a Blu-ray player device.</summary>
   BlurayPlayer,
   /// <summary>Represents a cable box device.</summary>
   CableBox,
   /// <summary>Represents a camera device.</summary>
   Camera,
   /// <summary>Represents a display device.</summary>
   Display,
   /// <summary>Represents a fan device.</summary>
   Fan,
   /// <summary>Represents a fireplace device.</summary>
   Fireplace,
   /// <summary>Represents a game console device.</summary>
   GameConsole,
   /// <summary>Represents a garage door device.</summary>
   GarageDoor,
   /// <summary>Represents a grouped device or logical collection.</summary>
   Group,
   /// <summary>Represents an HVAC device.</summary>
   Hvac,
   /// <summary>Represents an intercom device.</summary>
   Intercom,
   /// <summary>Represents an irrigation system device.</summary>
   IrrigationSystem,
   /// <summary>Represents a lighting device.</summary>
   Light,
   /// <summary>Represents a lock device.</summary>
   Lock,
   /// <summary>Represents a network router device.</summary>
   NetworkRouter,
   /// <summary>Represents a network switch device.</summary>
   NetworkSwitch,
   /// <summary>Represents a controllable outlet device.</summary>
   Outlet,
   /// <summary>Represents a platform or gateway device.</summary>
   Platform,
   /// <summary>Represents a pool device.</summary>
   Pool,
   /// <summary>Represents a pool controller device.</summary>
   PoolController,
   /// <summary>Represents a power controller device.</summary>
   PowerController,
   /// <summary>Represents a printer device.</summary>
   Printer,
   /// <summary>Represents a projector device.</summary>
   Projector,
   /// <summary>Represents a projector lift device.</summary>
   ProjectorLift,
   /// <summary>Represents a projector screen device.</summary>
   ProjectorScreen,
   /// <summary>Represents a room device.</summary>
   Room,
   /// <summary>Represents a scene device.</summary>
   Scene,
   /// <summary>Represents a security system device.</summary>
   SecuritySystem,
   /// <summary>Represents a sensor device.</summary>
   Sensor,
   /// <summary>Represents a shade device.</summary>
   Shade,
   /// <summary>Represents a signage player device.</summary>
   SignagePlayer,
   /// <summary>Represents a spa device.</summary>
   Spa,
   /// <summary>Represents a streaming media device.</summary>
   Streamer,
   /// <summary>Represents a switch device.</summary>
   Switch,
   /// <summary>Represents a thermostat device.</summary>
   Thermostat,
   /// <summary>Represents a tuner device.</summary>
   Tuner,
   /// <summary>Represents a television device.</summary>
   Tv,
   /// <summary>Represents a ventilation device.</summary>
   Ventilation,
   /// <summary>Represents a video player device.</summary>
   VideoPlayer,
   /// <summary>Represents a video processor device.</summary>
   VideoProcessor,
   /// <summary>Represents a video wall processor device.</summary>
   VideoWallProcessor,
   /// <summary>Represents a video wall screen device.</summary>
   VideoWallScreen,
   /// <summary>Represents a virtual device.</summary>
   VirtualDevice,
   /// <summary>Represents a voice assistant device.</summary>
   VoiceAssistant,
   /// <summary>Represents a volume control device.</summary>
   VolumeControl,
   /// <summary>Represents a weather device.</summary>
   Weather,
   /// <summary>Represents a whiteboard device.</summary>
   Whiteboard,
   /// <summary>Represents a window device.</summary>
   Window,
   /// <summary>Represents a wireless access point device.</summary>
   WirelessAccessPoint
   }

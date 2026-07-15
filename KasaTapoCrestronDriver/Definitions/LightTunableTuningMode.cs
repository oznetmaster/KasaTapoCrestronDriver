using Crestron.DeviceDrivers.SDK.EntityModel.Attributes;

namespace KasaTapoCrestronDriver;

/// <summary>
/// Mirrors the Crestron Home Lights API <c>lightTunable:TuningMode</c> data type. The platform binds
/// this capability by the well-known Id/member-name strings on the wire (exactly like the other light
/// capability magic strings the driver already emits, such as <c>lightColor:hue</c>); the driver SDK
/// does not ship this type, so it is declared locally with the exact contract Id and member names.
/// These tuning modes are mutually exclusive - the device may be in only one at a time.
/// </summary>
[EntityDataType (Id = "lightTunable:TuningMode")]
public enum LightTunableTuningMode
	{
	/// <summary>The device is in color mode, as controlled by hue and/or saturation.</summary>
	Color,

	/// <summary>The device is in white tuning mode, as controlled by (emulated) color temperature.</summary>
	White,
	}

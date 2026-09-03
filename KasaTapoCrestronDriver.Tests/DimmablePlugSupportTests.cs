// Copyright © 2026 Neil Colvin.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// Regression coverage for KasaTapoClient 1.3.0/1.3.1's capability-based brightness support: brightness
// is keyed off the negotiated SMART component rather than DeviceType, which means some hardware dims
// despite classifying as a non-Dimmer DeviceType (P135 classifies as Plug, KS240 classifies as
// WallSwitch). These tests exercise PlatformDriver's discovery-time supportability gate
// (IsSupportedLightDeviceType / IsDimmablePlugModel) and KasaLightEntity's post-connect capability
// classification (InferManagedLightKind) against that misfit set.

extern alias driverassembly;

using System.Threading.Tasks;

using KasaTapoClient;

using DriverKind = driverassembly::KasaTapoCrestronDriver.ManagedLightKind;
using DriverLightEntity = driverassembly::KasaTapoCrestronDriver.KasaLightEntity;
using DriverPlatform = driverassembly::KasaTapoCrestronDriver.PlatformDriver;

namespace KasaTapoCrestronDriver.Tests;

[TestClass]
public sealed class DimmablePlugSupportTests
	{
	private const string DIMMER_COMPONENTS =
		"""{ "id": "device", "ver_code": 2 }, { "id": "brightness", "ver_code": 1 }, { "id": "dimmer_calibration", "ver_code": 1 }""";

	private const string DIMMER_WITH_CHILDREN_COMPONENTS =
		"""{ "id": "device", "ver_code": 2 }, { "id": "brightness", "ver_code": 1 }, { "id": "dimmer_calibration", "ver_code": 1 }, { "id": "child_device", "ver_code": 1 }""";

	private const string PLAIN_PLUG_COMPONENTS =
		"""{ "id": "device", "ver_code": 2 }""";

	private const string PLAIN_SWITCH_COMPONENTS =
		"""{ "id": "device", "ver_code": 2 }, { "id": "child_device", "ver_code": 1 }""";

	// IsSupportedLightDeviceType / IsDimmablePlugModel - discovery-time gating.

	[TestMethod]
	public void IsSupportedLightDeviceType_WithWallSwitch_IsAlwaysSupported ()
		{
		Assert.IsTrue (DriverPlatform.IsSupportedLightDeviceType (DeviceType.WallSwitch, model: "KS240"), "A WallSwitch must always be supported.");
		}

	[TestMethod]
	public void IsSupportedLightDeviceType_WithPlainPlug_IsAlwaysSupported ()
		{
		Assert.IsTrue (DriverPlatform.IsSupportedLightDeviceType (DeviceType.Plug, model: "HS100"), "A plain on/off plug must always be discoverable/selectable, regardless of its per-child light/outlet choice.");
		}

	[TestMethod]
	public void IsSupportedLightDeviceType_WithStrip_IsAlwaysSupported ()
		{
		Assert.IsTrue (DriverPlatform.IsSupportedLightDeviceType (DeviceType.Strip, model: "HS300"), "A power strip must always be discoverable/selectable, regardless of its per-child light/outlet choice.");
		}

	[TestMethod]
	public void IsSupportedLightDeviceType_WithP135_IsAlwaysSupported ()
		{
		Assert.IsTrue (DriverPlatform.IsSupportedLightDeviceType (DeviceType.Plug, model: "P135"), "P135 is a dimmable plug and can only ever be used to dim a light, so it must always be supported.");
		}

	[TestMethod]
	public void IsSupportedLightDeviceType_WithP135ModelVariantAndLowercase_IsAlwaysSupported ()
		{
		Assert.IsTrue (DriverPlatform.IsSupportedLightDeviceType (DeviceType.Plug, model: "p135(us)"), "Model matching must be case-insensitive and tolerate region-suffixed model strings.");
		}

	[TestMethod]
	public void IsDimmablePlugModel_WithNullOrBlankModel_ReturnsFalse ()
		{
		Assert.IsFalse (DriverPlatform.IsDimmablePlugModel (null), "A null model must not be treated as a known dimmable plug.");
		Assert.IsFalse (DriverPlatform.IsDimmablePlugModel ("   "), "A blank model must not be treated as a known dimmable plug.");
		}

	[TestMethod]
	public void IsDimmablePlugModel_WithUnrelatedModel_ReturnsFalse ()
		{
		Assert.IsFalse (DriverPlatform.IsDimmablePlugModel ("HS100"), "An unrelated plug model must not be treated as a known dimmable plug.");
		}

	// InferManagedLightKind - post-connect capability classification.

	[TestMethod]
	public async Task InferManagedLightKind_WithP135StyleSmartPlug_ResolvesToDimmable ()
		{
		KasaDevice device = CreateSmartDevice ("P135", "SMART.TAPOPLUG", DIMMER_COMPONENTS);
		await device.UpdateAsync ().ConfigureAwait (false);

		DriverKind kind = DriverLightEntity.InferManagedLightKind (device, DeviceType.Plug);

		Assert.AreEqual (DriverKind.Dimmable, kind, "A Plug-classified device that negotiates the brightness component must resolve to Dimmable, not OnOff.");
		}

	[TestMethod]
	public async Task InferManagedLightKind_WithPlainSmartPlug_ResolvesToOnOff ()
		{
		KasaDevice device = CreateSmartDevice ("HS103", "SMART.TAPOPLUG", PLAIN_PLUG_COMPONENTS);
		await device.UpdateAsync ().ConfigureAwait (false);

		DriverKind kind = DriverLightEntity.InferManagedLightKind (device, DeviceType.Plug);

		Assert.AreEqual (DriverKind.OnOff, kind, "A plain plug with no LightState and no brightness component must resolve to OnOff.");
		}

	[TestMethod]
	public async Task InferManagedLightKind_WithKs240StyleSmartWallSwitch_ResolvesToDimmable ()
		{
		KasaDevice device = CreateSmartDevice ("KS240", "SMART.KASASWITCH", DIMMER_WITH_CHILDREN_COMPONENTS);
		await device.UpdateAsync ().ConfigureAwait (false);

		DriverKind kind = DriverLightEntity.InferManagedLightKind (device, DeviceType.WallSwitch);

		Assert.AreEqual (DriverKind.Dimmable, kind, "A WallSwitch-classified device that negotiates the brightness component (e.g. KS240) must resolve to Dimmable.");
		}

	[TestMethod]
	public async Task InferManagedLightKind_WithKs205StyleSmartWallSwitch_ResolvesToOnOff ()
		{
		KasaDevice device = CreateSmartDevice ("KS205", "SMART.KASASWITCH", PLAIN_SWITCH_COMPONENTS);
		await device.UpdateAsync ().ConfigureAwait (false);

		DriverKind kind = DriverLightEntity.InferManagedLightKind (device, DeviceType.WallSwitch);

		Assert.AreEqual (DriverKind.OnOff, kind, "A WallSwitch with no brightness component (e.g. KS205) must resolve to OnOff.");
		}

	/// <summary>
	/// One envelope answers every SMART SendAsync, mirroring the upstream KasaClient dimmer-support
	/// test fixtures (KasaClient.Tests/DimmerTestSupport.cs).
	/// </summary>
	private static string SmartEnvelope (string model, string type, string components) => $$"""
		{
		  "result": {
			 "responses": [
				{
				  "method": "get_device_info",
				  "result": {
					 "model": "{{model}}",
					 "type": "{{type}}",
					 "device_id": "device-1",
					 "nickname": "VGVzdCBEZXZpY2U=",
					 "device_on": true,
					 "brightness": 25,
					 "fw_ver": "1.0.0",
					 "hw_ver": "1.0"
				  }
				},
				{
				  "method": "component_nego",
				  "result": { "component_list": [{{components}}] }
				},
				{ "method": "get_child_device_list", "result": { "child_device_list": [] } },
				{ "method": "set_device_info", "result": { } }
			 ]
		  }
		}
		""";

	private static KasaDevice CreateSmartDevice (string model, string type, string components)
		{
		string envelope = SmartEnvelope (model, type, components);
		var transport = new FakeDeviceTransport (sendHandler: (_, _) => Task.FromResult (envelope));
		DeviceConfiguration configuration = new (
			"127.0.0.1",
			connectionOptions: new DeviceConnectionOptions (
				connectionParameters: new DeviceConnectionParameters (
					type.Contains ("PLUG") ? DeviceFamilyKind.SmartTapoPlug : DeviceFamilyKind.SmartKasaSwitch,
					DeviceEncryptionKind.Aes)));
		return new KasaDevice (configuration, transport);
		}
	}

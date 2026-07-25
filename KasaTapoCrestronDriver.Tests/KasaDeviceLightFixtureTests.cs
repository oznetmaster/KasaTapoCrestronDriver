// Copyright © 2026 Neil Colvin.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// Regression coverage for the DTO shapes consumed by KasaLightEntity's color-temperature / full-color
// capability detection (TryCreateColorTemperatureRange, HasCurrentFullColorState), using the upstream
// KasaClient fake-transport pattern to drive a real KasaDevice against canned KL130-style (legacy IOT
// protocol) and L530-style (SMART/TAPO protocol) fixtures.

using System.Threading.Tasks;

using KasaTapoClient;

namespace KasaTapoCrestronDriver.Tests;

[TestClass]
public sealed class KasaDeviceLightFixtureTests
	{
	[TestMethod]
	public async Task UpdateAsync_WithKl130WhiteModeFixture_ReportsColorTemperatureAndNoFullColorState ()
		{
		// Real KL130 firmware omits the hue/saturation fields entirely while operating in white
		// (color-temperature) mode, rather than reporting them as zero.
		var transport = new FakeDeviceTransport (
			sendResponses:
			[
				"{" +
				"\"system\":{\"get_sysinfo\":{\"alias\":\"Bulb\",\"model\":\"KL130\",\"deviceId\":\"bulb-1\"," +
				"\"light_state\":{\"on_off\":1,\"brightness\":60,\"color_temp\":3000,\"mode\":\"normal\"}}}}"
			],
			sendManyResponses:
			[
				"{\"emeter\":{\"err_code\":-1},\"time\":{\"get_time\":{\"year\":2025,\"month\":1,\"mday\":2,\"hour\":3,\"min\":4,\"sec\":5}}," +
				"\"cnCloud\":{\"get_info\":{\"binded\":1,\"cld_connection\":1}},\"count_down\":{\"get_rules\":{\"rule_list\":[]}}," +
				"\"schedule\":{\"get_rules\":{\"rule_list\":[]}},\"anti_theft\":{\"get_rules\":{\"rule_list\":[]}}}"
			]);
		DeviceConfiguration configuration = new ("127.0.0.1");
		var device = new KasaDevice (configuration, transport);

		await device.UpdateAsync ().ConfigureAwait (false);

		Assert.IsNotNull (device.LightState);
		Assert.AreEqual (3000, device.LightState.ColorTemperature);
		Assert.IsFalse (KasaLightEntityFixtureAssertions.HasCurrentFullColorState (device.LightState), "A white-mode KL130 fixture (hue=0, saturation=0, color_temp set) must not be reported as having an active full-color state.");
		Assert.IsTrue (KasaLightEntityFixtureAssertions.HasColorTemperatureFeature (device.Features), "A KL130 fixture must expose the color_temperature feature so the driver can build its tunable-white range.");
		}

	[TestMethod]
	public async Task UpdateAsync_WithKl130ColorModeFixture_ReportsFullColorStateAndColorTemperatureUnset ()
		{
		var transport = new FakeDeviceTransport (
			sendResponses:
			[
				"{" +
				"\"system\":{\"get_sysinfo\":{\"alias\":\"Bulb\",\"model\":\"KL130\",\"deviceId\":\"bulb-1\"," +
				"\"light_state\":{\"on_off\":1,\"brightness\":60,\"color_temp\":0,\"hue\":210,\"saturation\":80,\"mode\":\"hsv\"}}}}"
			],
			sendManyResponses:
			[
				"{\"emeter\":{\"err_code\":-1},\"time\":{\"get_time\":{\"year\":2025,\"month\":1,\"mday\":2,\"hour\":3,\"min\":4,\"sec\":5}}," +
				"\"cnCloud\":{\"get_info\":{\"binded\":1,\"cld_connection\":1}},\"count_down\":{\"get_rules\":{\"rule_list\":[]}}," +
				"\"schedule\":{\"get_rules\":{\"rule_list\":[]}},\"anti_theft\":{\"get_rules\":{\"rule_list\":[]}}}"
			]);
		DeviceConfiguration configuration = new ("127.0.0.1");
		var device = new KasaDevice (configuration, transport);

		await device.UpdateAsync ().ConfigureAwait (false);

		Assert.IsNotNull (device.LightState);
		Assert.AreEqual (0, device.LightState.ColorTemperature);
		Assert.AreEqual (210, device.LightState.Hue);
		Assert.AreEqual (80, device.LightState.Saturation);
		Assert.IsTrue (KasaLightEntityFixtureAssertions.HasCurrentFullColorState (device.LightState), "A color-mode KL130 fixture (nonzero hue/saturation) must be reported as having an active full-color state.");
		}

	[TestMethod]
	public async Task UpdateAsync_WithL530SmartWhiteModeFixture_ReportsColorTemperatureAndNoFullColorState ()
		{
		// SMART/TAPO-protocol bulbs (e.g. L530) report the same hue/saturation/color_temp triple as legacy
		// devices, but via get_device_info inside a multipleRequest envelope rather than a flat get_sysinfo.
		// As with the legacy KL130 fixture, real firmware omits the hue/saturation fields entirely while
		// operating in white (color-temperature) mode, rather than reporting them as zero.
		var transport = new FakeDeviceTransport (
			sendResponses:
			[
				"""
				{
				  "result": {
					 "responses": [
						{
						  "method": "get_device_info",
						  "result": {
							 "model": "L530",
							 "type": "SMART.TAPOBULB",
							 "device_id": "bulb-1",
							 "nickname": "QnVsYg==",
							 "device_on": true,
							 "brightness": 60,
							 "color_temp": 3000
						  }
						},
						{
						  "method": "component_nego",
						  "result": {
							 "component_list": [
								{ "id": "brightness", "ver_code": 1 },
								{ "id": "color", "ver_code": 1 },
								{ "id": "color_temperature", "ver_code": 1 }
							 ]
						  }
						}
					 ]
				  }
				}
				"""
			]);
		DeviceConfiguration configuration = new (
			"127.0.0.1",
			connectionOptions: new DeviceConnectionOptions (
				connectionParameters: new DeviceConnectionParameters (DeviceFamilyKind.SmartTapoBulb, DeviceEncryptionKind.Aes)));
		var device = new KasaDevice (configuration, transport);

		await device.UpdateAsync ().ConfigureAwait (false);

		Assert.IsNotNull (device.LightState);
		Assert.AreEqual (3000, device.LightState.ColorTemperature);
		Assert.IsFalse (KasaLightEntityFixtureAssertions.HasCurrentFullColorState (device.LightState), "A white-mode L530 SMART fixture (hue=0, saturation=0, color_temp set) must not be reported as having an active full-color state.");
		Assert.IsTrue (KasaLightEntityFixtureAssertions.HasColorTemperatureFeature (device.Features), "An L530 SMART fixture must expose the color_temperature feature so the driver can build its tunable-white range.");
		}

	[TestMethod]
	public async Task UpdateAsync_WithL530SmartColorModeFixture_ReportsFullColorStateAndColorTemperatureUnset ()
		{
		var transport = new FakeDeviceTransport (
			sendResponses:
			[
				"""
				{
				  "result": {
					 "responses": [
						{
						  "method": "get_device_info",
						  "result": {
							 "model": "L530",
							 "type": "SMART.TAPOBULB",
							 "device_id": "bulb-1",
							 "nickname": "QnVsYg==",
							 "device_on": true,
							 "brightness": 60,
							 "hue": 210,
							 "saturation": 80,
							 "color_temp": 0
						  }
						},
						{
						  "method": "component_nego",
						  "result": {
							 "component_list": [
								{ "id": "brightness", "ver_code": 1 },
								{ "id": "color", "ver_code": 1 },
								{ "id": "color_temperature", "ver_code": 1 }
							 ]
						  }
						}
					 ]
				  }
				}
				"""
			]);
		DeviceConfiguration configuration = new (
			"127.0.0.1",
			connectionOptions: new DeviceConnectionOptions (
				connectionParameters: new DeviceConnectionParameters (DeviceFamilyKind.SmartTapoBulb, DeviceEncryptionKind.Aes)));
		var device = new KasaDevice (configuration, transport);

		await device.UpdateAsync ().ConfigureAwait (false);

		Assert.IsNotNull (device.LightState);
		Assert.AreEqual (0, device.LightState.ColorTemperature);
		Assert.AreEqual (210, device.LightState.Hue);
		Assert.AreEqual (80, device.LightState.Saturation);
		Assert.IsTrue (KasaLightEntityFixtureAssertions.HasCurrentFullColorState (device.LightState), "A color-mode L530 SMART fixture (nonzero hue/saturation) must be reported as having an active full-color state.");
		}
	}

/// <summary>
/// Reimplements the pure, private capability-detection predicates from
/// <see cref="global::KasaTapoCrestronDriver.KasaLightEntity" /> (HasCurrentFullColorState / color-temperature
/// feature lookup) so fixture-based device tests can assert on the same rules without requiring the full
/// Crestron entity plumbing (IPlatformSharedConfiguration, DriverImplementationResources, etc.).
/// </summary>
internal static class KasaLightEntityFixtureAssertions
	{
	internal static bool HasCurrentFullColorState (global::KasaTapoClient.LightState? lightState)
		{
		return lightState?.Hsv is not null
			|| lightState?.Hue is not null
			|| lightState?.Saturation is not null;
		}

	internal static bool HasColorTemperatureFeature (System.Collections.Generic.IReadOnlyList<global::KasaTapoClient.DeviceFeature> features)
		{
		foreach (global::KasaTapoClient.DeviceFeature feature in features)
			{
			if (string.Equals (feature.Id, "color_temperature", System.StringComparison.Ordinal))
				{
				return true;
				}
			}

		return false;
		}
	}

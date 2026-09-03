// Copyright © 2026 Neil Colvin.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// Regression coverage for the energy telemetry KasaOutletEntity.ApplyState consumes via
// KasaDevice.Energy (device.Energy.IsAvailable / CurrentPowerWatts / TodayKilowattHours), using the
// upstream KasaClient fake-transport pattern to drive a real KasaDevice against canned legacy
// (HS110-style emeter) and SMART (P110-style energy_monitoring component) fixtures, plus a plain
// on/off plug fixture with no energy support at all.

using System.Threading.Tasks;

using KasaTapoClient;

namespace KasaTapoCrestronDriver.Tests;

[TestClass]
public sealed class KasaOutletEnergyFixtureTests
	{
	[TestMethod]
	public async Task UpdateAsync_WithLegacyEmeterCapablePlugFixture_ReportsEnergyAvailableWithPowerAndTodayUsage ()
		{
		// HS110-style legacy plug: emeter.get_realtime reports power_mw/voltage_mv/current_ma/total_wh.
		var transport = new FakeDeviceTransport (
			sendResponses:
			[
				"{" +
				"\"system\":{\"get_sysinfo\":{\"alias\":\"Plug\",\"model\":\"HS110\",\"deviceId\":\"plug-1\"," +
				"\"relay_state\":1}}}"
			],
			sendManyResponses:
			[
				"{\"emeter\":{\"get_realtime\":{\"power_mw\":36500,\"voltage_mv\":230000,\"current_ma\":159,\"total_wh\":1250}}," +
				"\"time\":{\"get_time\":{\"year\":2025,\"month\":1,\"mday\":2,\"hour\":3,\"min\":4,\"sec\":5}}," +
				"\"cnCloud\":{\"get_info\":{\"binded\":1,\"cld_connection\":1}},\"count_down\":{\"get_rules\":{\"rule_list\":[]}}," +
				"\"schedule\":{\"get_rules\":{\"rule_list\":[]}},\"anti_theft\":{\"get_rules\":{\"rule_list\":[]}}}"
			]);
		DeviceConfiguration configuration = new ("127.0.0.1");
		var device = new KasaDevice (configuration, transport);

		await device.UpdateAsync ().ConfigureAwait (false);

		Assert.IsTrue (device.Energy.IsAvailable, "A legacy plug fixture reporting emeter.get_realtime data must expose energy usage as available.");
		Assert.AreEqual (36.5d, device.Energy.CurrentPowerWatts, "power_mw must be scaled down to watts (power_mw / 1000).");
		Assert.AreEqual (1.25d, device.Energy.TotalKilowattHours, "total_wh must be scaled down to kilowatt-hours (total_wh / 1000).");
		}

	[TestMethod]
	public async Task UpdateAsync_WithSmartEnergyMonitoringPlugFixture_ReportsEnergyAvailableWithCurrentPower ()
		{
		// P110-style SMART plug: negotiates the energy_monitoring component and reports get_energy_usage /
		// get_current_power module results.
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
						  "model": "P110",
						  "type": "SMART.TAPOPLUG",
						  "device_id": "plug-1",
						  "nickname": "UGx1Zw==",
						  "device_on": true
						}
					  },
					  {
						"method": "component_nego",
						"result": {
						  "component_list": [
							{ "id": "device", "ver_code": 2 },
							{ "id": "energy_monitoring", "ver_code": 2 }
						  ]
						}
					  },
					  { "method": "get_child_device_list", "result": { "child_device_list": [] } },
					  {
						"method": "get_energy_usage",
						"result": {
						  "current_power": 42123,
						  "today_energy": 567
						}
					  },
					  {
						"method": "get_current_power",
						"result": {
						  "current_power": 42.123
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
				connectionParameters: new DeviceConnectionParameters (DeviceFamilyKind.SmartTapoPlug, DeviceEncryptionKind.Aes)));
		var device = new KasaDevice (configuration, transport);

		await device.UpdateAsync ().ConfigureAwait (false);

		Assert.IsTrue (device.Energy.IsAvailable, "A SMART plug fixture negotiating the energy_monitoring component must expose energy usage as available.");
		Assert.AreEqual (42.123d, device.Energy.CurrentPowerWatts, "current_power (get_energy_usage, in mW) must be scaled down to watts.");
		}

	[TestMethod]
	public async Task UpdateAsync_WithPlainOnOffPlugFixture_ReportsEnergyNotAvailable ()
		{
		// A plain on/off plug (e.g. HS100) reports no emeter data at all.
		var transport = new FakeDeviceTransport (
			sendResponses:
			[
				"{" +
				"\"system\":{\"get_sysinfo\":{\"alias\":\"Plug\",\"model\":\"HS100\",\"deviceId\":\"plug-1\"," +
				"\"relay_state\":1}}}"
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

		Assert.IsFalse (device.Energy.IsAvailable, "A plain on/off plug fixture with no emeter data must not report energy usage as available.");
		}
	}

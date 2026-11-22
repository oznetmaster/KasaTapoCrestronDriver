// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

extern alias driverassembly;

using KasaTapoClient;

using DriverPlatform = driverassembly::KasaTapoCrestronDriver.PlatformDriver;

namespace KasaTapoCrestronDriver.Tests;

[TestFixture]
public sealed class SmartStripDiscoveryTimeoutTests
	{
	[Test]
	public async Task DiscoveryDeadline_AllowsTimedOutChildBatchToFallBackAndReadBothPages ()
		{
		const string initialBatch = """
			{"error_code":0,"result":{"responses":[
				{"method":"get_device_info","error_code":0,"result":{"model":"P300","type":"SMART.TAPOPLUG","device_id":"strip"}},
				{"method":"component_nego","error_code":0,"result":{"component_list":[{"id":"child_device","ver_code":1}]}}
			]}}
			""";
		var individualReplies = new Queue<string> (
			[
			"""{"error_code":0,"result":{"sum":2,"start_index":0,"child_device_list":[{"device_id":"outlet-1","category":"plug.powerstrip.sub-plug"}]}}""",
			"""{"error_code":0,"result":{"child_component_list":[]}}""",
			"""{"error_code":0,"result":{"sum":2,"start_index":1,"child_device_list":[{"device_id":"outlet-2","category":"plug.powerstrip.sub-plug"}]}}""",
			]);
		DeviceConfiguration configuration = new ("127.0.0.1", timeout: TimeSpan.FromSeconds (4),
			connectionOptions: new DeviceConnectionOptions (DeviceTransportKind.HttpToken,
				new DeviceConnectionParameters (DeviceFamilyKind.SmartTapoPlug, DeviceEncryptionKind.Aes)));
		var transport = new FakeDeviceTransport (sendHandler: async (request, token) =>
			{
			if (request.Contains ("\"multipleRequest\""))
				{
				if (request.Contains ("\"get_child_device_component_list\""))
					await Task.Delay (Timeout.Infinite, token).ConfigureAwait (false);
				return initialBatch;
				}

			// Together, the individual reads exceed the previous two-second margin.
			await Task.Delay (TimeSpan.FromMilliseconds (850), token).ConfigureAwait (false);
			return individualReplies.Dequeue ();
			});
		using var device = new KasaDevice (configuration, transport);
		using var deadline = new CancellationTokenSource (DriverPlatform.GetStripDiscoveryTimeout (configuration));

		await device.UpdateAsync (deadline.Token).ConfigureAwait (false);

		Assert.That (device.Children.Select (child => child.Id), Is.EquivalentTo (new[] { "outlet-1", "outlet-2" }));
		Assert.That (transport.SentCommands, Has.Count.EqualTo (5));
		}

	[Test]
	public void DiscoveryDeadline_RetainsLegacyStripTimeout ()
		{
		DeviceConfiguration configuration = new ("127.0.0.1", timeout: TimeSpan.FromSeconds (4),
			connectionOptions: new DeviceConnectionOptions (DeviceTransportKind.LegacyXor));

		Assert.That (DriverPlatform.GetStripDiscoveryTimeout (configuration), Is.EqualTo (TimeSpan.FromSeconds (6)));
		}
	}

// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using KasaTapoClient;
using KasaTapoClient.Internal;

using KasaTapoCrestronDriver;

using Newtonsoft.Json.Linq;

using Crestron.DeviceDrivers.SDK;

using System.Reflection;

using DeviceType = KasaTapoClient.DeviceType;

namespace KasaTapoCrestronDriver.Tests;

#if NETFRAMEWORK
[Category ("Processor" )]
#endif
[TestFixture]
public sealed class ParentDeviceTests
	{
	private sealed class Transport : IDeviceTransport
		{
		public readonly List<string> Commands = new ();
		public bool A;
		public bool B = true;
		public int Updates;
		public Task<string> SendAsync (string json, CancellationToken token)
			{
			token.ThrowIfCancellationRequested ();
			Commands.Add (json);
			var command = JObject.Parse (json);
			if (command["system"]?["set_relay_state"] is JObject relay)
				{
				bool on = relay["state"]!.Value<int> () != 0;
				string? id = command["context"]?["child_ids"]?[0]?.Value<string> ();
				if (id == "a")
					A = on;
				else if (id == "b")
					B = on;
				else
					throw new AssertionException ("A strip command omitted the socket ID.");
				return Task.FromResult ("{\"system\":{\"set_relay_state\":{\"err_code\":0}}}");
				}
			Interlocked.Increment (ref Updates);
			return Task.FromResult ("{\"system\":{\"get_sysinfo\":{\"alias\":\"Strip\",\"model\":\"KP303\",\"deviceId\":\"strip\",\"relay_state\":1,\"children\":[{\"id\":\"a\",\"alias\":\"A\",\"state\":" + (A ? 1 : 0) + "},{\"id\":\"b\",\"alias\":\"B\",\"state\":" + (B ? 1 : 0) + "}]}}}");
			}
		public Task<string> SendManyAsync (IReadOnlyList<string> commands, CancellationToken token) =>
			 Task.FromResult ("{\"emeter\":{\"err_code\":-1}}");
		}

	private class Child (string id) : IParentDeviceChild
		{
		public string ChildId => id;
		public int Pushes;
		public int Offline;
		public bool? IsOn;
		public void ApplyPushedState (ChildDevice? child, KasaDevice parent)
			{
			Interlocked.Increment (ref Pushes);
			IsOn = parent.GetChild (id)?.IsOn;
			}
		public void ApplyConnectionState (bool online)
			{
			if (!online)
				Interlocked.Increment (ref Offline);
			}
		}

	// Isolate subscription policy from the SDK, which subscribes to entity events itself.
	private sealed class HubChild (string id) : Child (id), IKasaHubChildEntity
		{
		public bool HasEventSubscribers
			{
			get; private set;
			}
		public event Action? EventSubscribersChanged;
		public void Subscribe (bool value)
			{
			HasEventSubscribers = value;
			EventSubscribersChanged?.Invoke ();
			}
		public string DeviceName => ChildId;
		public string ModelName => "T310";
		public string SerialNumber => ChildId;
		public void UpdateDescriptor (ManagedLightDescriptor descriptor, DeviceConfiguration configuration)
			{
			}
		public void UpdateConfiguration (DeviceConfiguration configuration)
			{
			}
		public bool TryAttachConnectedDevice (KasaDevice device, string context) => false;
		public void SetConfigured (bool configured, string context)
			{
			}
		public Task SetConfiguredAsync (bool configured, string context, CancellationToken token) => Task.CompletedTask;
		public void ApplyRuntimeConfiguration (PlatformSharedConfigurationSnapshot previous, PlatformSharedConfigurationSnapshot current)
			{
			}
		public void Stop ()
			{
			}
		public Task RefreshAsync (CancellationToken token) => Task.CompletedTask;
		public void NotifyChildPublished ()
			{
			}
		public void NotifyChildRunning (string context)
			{
			}
		public void PublishStateSnapshot ()
			{
			}
		public void Dispose ()
			{
			}
		}

	private sealed class HubTransport : IDeviceTransport
		{
		public int IntervalA = 16;
		public int IntervalB = 32;
		public Task<string> SendAsync (string json, CancellationToken token) => Task.FromResult (
			 new JObject
				 {
				 ["result"] = new JObject
					 {
					 ["responses"] = new JArray (
				  new JObject { ["method"] = "get_device_info", ["result"] = new JObject { ["model"] = "H100", ["type"] = "SMART.TAPOHUB", ["device_id"] = "hub" } },
				  new JObject { ["method"] = "component_nego", ["result"] = new JObject { ["component_list"] = new JArray (new JObject { ["id"] = "child_device", ["ver_code"] = 1 }) } },
				  new JObject
					  {
					  ["method"] = "get_child_device_list",
					  ["result"] = new JObject
						  {
						  ["child_device_list"] = new JArray (
						new JObject { ["device_id"] = "a", ["model"] = "T310", ["category"] = "subg.trigger.temp-hmdt", ["report_interval"] = IntervalA, ["at_low_battery"] = false },
						new JObject { ["device_id"] = "b", ["model"] = "T315", ["category"] = "subg.trigger.temp-hmdt", ["report_interval"] = IntervalB, ["at_low_battery"] = false })
						  }
					  })
					 }
				 }.ToString ());
		public Task<string> SendManyAsync (IReadOnlyList<string> commands, CancellationToken token) => SendAsync ("", token);
		}

	[Test]
	public async Task HubPollsAtHalfShortestReportIntervalAndUsesSubscriberDrivenPolling ()
		{
		var transport = new HubTransport ();
		var config = new DeviceConfiguration ("127.0.0.1", connectionOptions: new DeviceConnectionOptions (
			 connectionParameters: new DeviceConnectionParameters (DeviceFamilyKind.SmartTapoHub, DeviceEncryptionKind.Aes)));
		using var device = new KasaDevice (config, transport);
		var settings = Settings ();
		int updates = 0;
		var sleeping = new TaskCompletionSource<bool> (TaskCreationOptions.RunContinuationsAsynchronously);
		CancellationToken delayToken = default;
		using var poller = new ManagedParentDevicePoller ("hub", config, settings, null, "test", false,
			 (_, _) => Task.FromResult (device), async (d, t) => { updates++; await d.UpdateAsync (t); },
			 (_, t) => { delayToken = t; sleeping.TrySetResult (true); return Task.Delay (Timeout.Infinite, t); });
		using var sensor = new HubChild ("a");
		poller.RegisterChild (sensor);
		poller.RegisterChild (new Child ("b"));
		Assert.That (poller.ComputeNextDelay (), Is.EqualTo (TimeSpan.FromSeconds (30)));
		await poller.RefreshAsync (default);
		Assert.That (updates, Is.EqualTo (1), "No automatic loop without event subscribers.");
		Assert.That (poller.ComputeNextDelay (), Is.EqualTo (TimeSpan.FromSeconds (8)));
		transport.IntervalB = 8;
		await poller.RefreshAsync (default);
		Assert.That (poller.ComputeNextDelay (), Is.EqualTo (TimeSpan.FromSeconds (4)));
		transport.IntervalB = 16;
		sensor.Subscribe (true);
		await sleeping.Task.WaitForTestAsync (TimeSpan.FromSeconds (5));
		Assert.That (updates, Is.EqualTo (3));
		Assert.That (poller.ComputeNextDelay (), Is.EqualTo (TimeSpan.FromSeconds (8)));
		sensor.Subscribe (false);
		Assert.That (delayToken.IsCancellationRequested, Is.True);
		}

	[Test]
	public async Task RuntimeSettingsRestartOneLoop_AndDisposeCancelsPendingWork ()
		{
		var transport = new Transport ();
		using var device = new KasaDevice (new DeviceConfiguration ("127.0.0.1"), transport);
		var settings = Settings ();
		var firstSleep = new TaskCompletionSource<bool> (TaskCreationOptions.RunContinuationsAsynchronously);
		var secondSleep = new TaskCompletionSource<bool> (TaskCreationOptions.RunContinuationsAsynchronously);
		CancellationToken firstToken = default;
		int delays = 0;
		using var poller = new ManagedParentDevicePoller ("strip", device.Configuration, settings, null, "test", true,
			 (_, _) => Task.FromResult (device), (d, t) => d.UpdateAsync (t),
			 (_, t) => { if (++delays == 1) { firstToken = t; firstSleep.TrySetResult (true); } else secondSleep.TrySetResult (true); return Task.Delay (Timeout.Infinite, t); });
		var a = new Child ("a");
		poller.RegisterChild (a);
		await firstSleep.Task.WaitForTestAsync (TimeSpan.FromSeconds (5));
		var before = settings.Snapshot ();
		settings.Update ("", "", TimeSpan.FromSeconds (8), true, TimeSpan.FromSeconds (12), TimeSpan.FromSeconds (30), false, "", "", "");
		poller.ApplyRuntimeConfiguration (before, settings.Snapshot ());
		await secondSleep.Task.WaitForTestAsync (TimeSpan.FromSeconds (5));
		Assert.That (firstToken.IsCancellationRequested, Is.True);
		Assert.That (poller.ComputeNextDelay (), Is.EqualTo (TimeSpan.FromSeconds (12)));
		Assert.That (transport.Updates, Is.EqualTo (2));
		var started = new TaskCompletionSource<bool> (TaskCreationOptions.RunContinuationsAsynchronously);
		var pending = poller.ExecuteCommandAsync (async (_, t) => { started.TrySetResult (true); await Task.Delay (Timeout.Infinite, t); }, default);
		await started.Task.WaitForTestAsync (TimeSpan.FromSeconds (5));
		poller.Dispose ();
		Assert.CatchAsync<OperationCanceledException> (() => pending);
		Assert.That (a.Pushes, Is.EqualTo (2));
		Assert.That (device.IsDisposed, Is.False);
		}

	private static async Task Until (Func<bool> condition)
		{
		using var timeout = new CancellationTokenSource (TimeSpan.FromSeconds (5));
		while (!condition ())
			await Task.Delay (5, timeout.Token);
		}

	private static PlatformSharedConfiguration Settings (bool enabled = true, int seconds = 5)
		{
		var settings = new PlatformSharedConfiguration ();
		settings.Update ("", "", TimeSpan.FromSeconds (8), enabled, TimeSpan.FromSeconds (seconds),
			 TimeSpan.FromSeconds (30), false, "", "", "");
		return settings;
		}

	[Test]
	public async Task ActualOutletAndLight_ShareStateAndSurviveSiblingRemoval ()
		{
		var transport = new Transport ();
		using var device = new KasaDevice (new DeviceConfiguration ("127.0.0.1"), transport);
		await device.UpdateAsync ();
		var settings = Settings ();
		using var poller = new ManagedParentDevicePoller ("test", device.Configuration, settings, null, "test", true,
			 (_, _) => Task.FromResult (device), (d, t) => d.UpdateAsync (t),
			 (_, t) => Task.Delay (Timeout.Infinite, t));
		using var logger = new DriverLogger ("strip-test");
		var resources = new DriverImplementationResources { Logger = logger, InitLogger = logger.GetComponentLogger ("test", "strip") };
		var outletDescriptor = new ManagedLightDescriptor ("outlet-a", "127.0.0.1", DeviceType.Strip,
			 "A", "KP303", "strip", ManagedLightKind.OnOff, childId: "a", childKind: ManagedChildKind.Outlet);
		var lightDescriptor = new ManagedLightDescriptor ("light-b", "127.0.0.1", DeviceType.Strip,
			 "B", "KP303", "strip", ManagedLightKind.OnOff, childId: "b", childKind: ManagedChildKind.Light);
		using var outlet = new KasaOutletEntity ("outlet-a", outletDescriptor, device.Configuration, null,
			 settings, resources, null!, "test", DriverTestPaths.DataDirectory, poller);
		using var light = new KasaLightEntity ("light-b", lightDescriptor, device.Configuration, null,
			 settings, null, resources, null!, "test", poller);
		bool LightIsOn () => light.GetState ().PropertyValues["light:isOn"].GetValue<bool> ();
		await outlet.SetConfiguredAsync (true, "test", default);
		await light.SetConfiguredAsync (true, "test", default);
		await poller.RefreshAsync (default);
		Assert.That (outlet.OutletIsOn, Is.False);
		Assert.That (LightIsOn (), Is.True);
		Assert.That (light.GetState ().Definition.Properties.ContainsKey ("light:isOn"), Is.True);
		outlet.OutletOn ();
		await Until (() => outlet.OutletIsOn && transport.A);
		typeof (KasaLightEntity).GetMethod ("LightOff", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke (light, null);
		await Until (() => !LightIsOn () && !transport.B);
		Assert.That (outlet.OutletIsOn, Is.True);
		outlet.Dispose ();
		Assert.That (device.IsDisposed, Is.False);
		transport.B = true;
		await poller.RefreshAsync (default);
		Assert.That (LightIsOn (), Is.True);
		Assert.That (light.OnlineIndicatorIsOnline, Is.True);
		}

	[Test]
	public async Task Failure_NotifiesSiblingsAndRecoveryResetsBackoff ()
		{
		var transport = new Transport ();
		using var device = new KasaDevice (new DeviceConfiguration ("127.0.0.1"), transport);
		bool fail = true;
		int connects = 0;
		var sleeping = new TaskCompletionSource<bool> (TaskCreationOptions.RunContinuationsAsynchronously);
		using var poller = new ManagedParentDevicePoller ("test", device.Configuration, Settings (), null, "test", true,
			 (_, _) => { connects++; return Task.FromResult (device); },
			 (d, t) => fail ? Task.FromException (new IOException ("simulated disconnect")) : d.UpdateAsync (t),
			 (_, t) => { sleeping.TrySetResult (true); return Task.Delay (Timeout.Infinite, t); });
		var a = new Child ("a");
		var b = new Child ("b");
		poller.RegisterChild (a);
		poller.RegisterChild (b);
		await sleeping.Task.WaitForTestAsync (TimeSpan.FromSeconds (5));
		await poller.RefreshAsync (default);
		Assert.That (a.Offline > 0 && b.Offline > 0, Is.True);
		Assert.That (poller.ComputeNextDelay (), Is.EqualTo (TimeSpan.FromSeconds (20)));
		fail = false;
		await poller.RefreshAsync (default);
		Assert.That (connects, Is.EqualTo (3));
		Assert.That (poller.ComputeNextDelay (), Is.EqualTo (TimeSpan.FromSeconds (5)));
		Assert.That (a.IsOn, Is.False);
		Assert.That (b.IsOn, Is.True);
		Assert.That (device.IsDisposed, Is.False);
		}

	[Test]
	public async Task CommandsAndRefresh_SerializeAndQueuedCancellationDoesNotRun ()
		{
		var transport = new Transport ();
		using var device = new KasaDevice (new DeviceConfiguration ("127.0.0.1"), transport);
		var sleeping = new TaskCompletionSource<bool> (TaskCreationOptions.RunContinuationsAsynchronously);
		using var poller = new ManagedParentDevicePoller ("test", device.Configuration, Settings (), null, "test", true,
			 (_, _) => Task.FromResult (device), (d, t) => d.UpdateAsync (t),
			 (_, t) => { sleeping.TrySetResult (true); return Task.Delay (Timeout.Infinite, t); });
		poller.RegisterChild (new Child ("a"));
		await sleeping.Task.WaitForTestAsync (TimeSpan.FromSeconds (5));
		var started = new TaskCompletionSource<bool> (TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource<bool> (TaskCreationOptions.RunContinuationsAsynchronously);
		var first = poller.ExecuteCommandAsync (async (d, t) =>
		{
			started.SetResult (true);
			await release.Task.WaitForTestAsync (t);
			await StripOutletControl.SetIsOnAsync (d, "a", true, t);
		}, default);
		await started.Task.WaitForTestAsync (TimeSpan.FromSeconds (5));
		int updatesBefore = transport.Updates;
		var refresh = poller.RefreshAsync (default);
		bool ran = false;
		using var cancellation = new CancellationTokenSource ();
		var canceled = poller.ExecuteCommandAsync ((_, _) => { ran = true; return Task.CompletedTask; }, cancellation.Token);
		cancellation.Cancel ();
		Assert.CatchAsync<OperationCanceledException> (() => canceled);
		Assert.That (ran, Is.False);
		Assert.That (refresh.IsCompleted, Is.False);
		Assert.That (transport.Updates, Is.EqualTo (updatesBefore));
		release.SetResult (true);
		await Task.WhenAll (first, refresh).WaitForTestAsync (TimeSpan.FromSeconds (5));
		Assert.That (transport.Updates, Is.EqualTo (updatesBefore + 2));
		}

	[Test]
	public async Task RemovingChildDuringRefresh_PreventsLateStateDelivery ()
		{
		var transport = new Transport ();
		using var device = new KasaDevice (new DeviceConfiguration ("127.0.0.1"), transport);
		var started = new TaskCompletionSource<bool> (TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource<bool> (TaskCreationOptions.RunContinuationsAsynchronously);
		var sleeping = new TaskCompletionSource<bool> (TaskCreationOptions.RunContinuationsAsynchronously);
		using var poller = new ManagedParentDevicePoller ("test", device.Configuration, Settings (), null, "test", true,
			 (_, _) => Task.FromResult (device), async (d, t) => { started.TrySetResult (true); await release.Task.WaitForTestAsync (t); await d.UpdateAsync (t); },
			 (_, t) => { sleeping.TrySetResult (true); return Task.Delay (Timeout.Infinite, t); });
		var a = new Child ("a");
		var b = new Child ("b");
		poller.RegisterChild (a);
		poller.RegisterChild (b);
		await started.Task.WaitForTestAsync (TimeSpan.FromSeconds (5));
		poller.UnregisterChild (a);
		release.SetResult (true);
		await sleeping.Task.WaitForTestAsync (TimeSpan.FromSeconds (5));
		Assert.That (a.Pushes, Is.EqualTo (0));
		Assert.That (b.Pushes, Is.EqualTo (1));
		}

	[Test]
	public async Task DisabledPolling_InitializesOnceAndCommandsStillRefresh ()
		{
		var transport = new Transport ();
		using var device = new KasaDevice (new DeviceConfiguration ("127.0.0.1"), transport);
		int delays = 0;
		var sleeping = new TaskCompletionSource<bool> (TaskCreationOptions.RunContinuationsAsynchronously);
		using var poller = new ManagedParentDevicePoller ("test", device.Configuration, Settings (false), null, "test", true,
			 (_, _) => Task.FromResult (device), (d, t) => d.UpdateAsync (t),
			 (_, t) => { if (++delays < 3) return Task.CompletedTask; sleeping.TrySetResult (true); return Task.Delay (Timeout.Infinite, t); });
		var a = new Child ("a");
		poller.RegisterChild (a);
		await sleeping.Task.WaitForTestAsync (TimeSpan.FromSeconds (5));
		Assert.That (transport.Updates, Is.EqualTo (1));
		await poller.ExecuteCommandAsync ((d, t) => StripOutletControl.SetIsOnAsync (d, "a", true, t), default);
		Assert.That (a.IsOn, Is.True);
		Assert.That (transport.Updates, Is.EqualTo (2));
		}

	[Test]
	public async Task StripCommandsAndState_AreSocketSpecific ()
		{
		var transport = new Transport ();
		using var device = new KasaDevice (new DeviceConfiguration ("127.0.0.1"), transport);
		await device.UpdateAsync ();
		Assert.That (StripOutletControl.ReadIsOn (device, "a"), Is.False);
		Assert.That (StripOutletControl.ReadIsOn (device, "b"), Is.True);
		await StripOutletControl.SetIsOnAsync (device, "a", true, default);
		Assert.That (transport.A, Is.True);
		Assert.That (transport.B, Is.True);
		await StripOutletControl.SetIsOnAsync (device, "b", false, default);
		Assert.That (transport.A, Is.True);
		Assert.That (transport.B, Is.False);
		}

	[Test]
	public async Task OneLoop_FansOutAndOldRegistrationCannotRemoveReplacement ()
		{
		var transport = new Transport ();
		using var device = new KasaDevice (new DeviceConfiguration ("127.0.0.1"), transport);
		var releaseUpdate = new TaskCompletionSource<bool> (TaskCreationOptions.RunContinuationsAsynchronously);
		var sleeping = new TaskCompletionSource<bool> (TaskCreationOptions.RunContinuationsAsynchronously);
		int connects = 0;
		using var poller = new ManagedParentDevicePoller ("test", device.Configuration, new PlatformSharedConfiguration (), null, "test", true,
			 (_, _) => { Interlocked.Increment (ref connects); return Task.FromResult (device); },
			 async (d, t) => { await releaseUpdate.Task.WaitForTestAsync (t); await d.UpdateAsync (t); },
			 (_, t) => { sleeping.TrySetResult (true); return Task.Delay (Timeout.Infinite, t); });
		var oldA = new Child ("a");
		var newA = new Child ("a");
		var b = new Child ("b");
		poller.RegisterChild (oldA);
		poller.RegisterChild (b);
		poller.RegisterChild (newA);
		poller.UnregisterChild (oldA);
		releaseUpdate.SetResult (true);
		await sleeping.Task.WaitForTestAsync (TimeSpan.FromSeconds (5));
		Assert.That (transport.Updates, Is.EqualTo (1));
		Assert.That (connects, Is.EqualTo (1));
		Assert.That (oldA.Pushes, Is.EqualTo (0));
		Assert.That (newA.Pushes, Is.EqualTo (1));
		Assert.That (b.Pushes, Is.EqualTo (1));
		await poller.ExecuteCommandAsync ((d, t) => StripOutletControl.SetIsOnAsync (d, "a", true, t), default);
		Assert.That (transport.Updates, Is.EqualTo (2), "The command's built-in refresh must not be followed by a duplicate coordinator refresh.");
		Assert.That (newA.IsOn, Is.True);
		Assert.That (b.IsOn, Is.True);
		poller.UnregisterChild (newA);
		poller.UnregisterChild (b);
		await poller.RefreshAsync (default);
		Assert.That (transport.Updates, Is.EqualTo (2));
		Assert.That (device.IsDisposed, Is.False, "A projection/poller must not dispose the shared device.");
		}
	private sealed class CallbackChild (string id, Action onState, Action onOffline) : IParentDeviceChild
		{
		public string ChildId => id;
		public void ApplyPushedState (ChildDevice? child, KasaDevice parent) => onState ();
		public void ApplyConnectionState (bool online)
			{
			if (!online)
				onOffline ();
			}
		}
	[Test]
	public async Task FailedCommand_DoesNotPoisonFollowingWorkOrSkipHealthySibling ()
		{
		var transport = new Transport ();
		using var device = new KasaDevice (new DeviceConfiguration ("127.0.0.1"), transport);
		using var poller = new ManagedParentDevicePoller ("test", device.Configuration, Settings (), null, "test", false,
			(_, _) => Task.FromResult (device), (d, t) => d.UpdateAsync (t), (_, t) => Task.Delay (Timeout.Infinite, t));
		poller.RegisterChild (new CallbackChild ("a", () => throw new InvalidOperationException ("subscriber"), () => throw new InvalidOperationException ("subscriber")));
		var healthy = new Child ("b");
		poller.RegisterChild (healthy);
		Assert.ThrowsAsync<InvalidOperationException> (() => poller.ExecuteCommandAsync ((_, _) => Task.FromException (new InvalidOperationException ("command")), default));
		Assert.That (healthy.Offline, Is.EqualTo (1));
		await poller.RefreshAsync (default).WaitForTestAsync (TimeSpan.FromSeconds (5));
		Assert.That (healthy.Pushes, Is.EqualTo (1));
		Assert.That (healthy.IsOn, Is.True);
		}
	[Test]
	public async Task RemovingSiblingDuringFailureNotification_PreventsLateOfflineDelivery ()
		{
		var transport = new Transport ();
		using var device = new KasaDevice (new DeviceConfiguration ("127.0.0.1"), transport);
		using var poller = new ManagedParentDevicePoller ("test", device.Configuration, Settings (), null, "test", false,
			(_, _) => Task.FromResult (device), (d, t) => d.UpdateAsync (t), (_, t) => Task.Delay (Timeout.Infinite, t));
		var removed = new Child ("b");
		poller.RegisterChild (new CallbackChild ("a", () => { }, () => poller.UnregisterChild (removed)));
		poller.RegisterChild (removed);
		Assert.ThrowsAsync<InvalidOperationException> (() => poller.ExecuteCommandAsync ((_, _) => Task.FromException (new InvalidOperationException ("command")), default));
		Assert.That (removed.Offline, Is.Zero, "A removed entity must not receive a later callback from a captured subscriber list.");
		await poller.RefreshAsync (default);
		Assert.That (removed.Pushes, Is.Zero);
		}
	[Test]
	public async Task DisposingDuringUncooperativeRefresh_DiscardsItsLateResult ()
		{
		var transport = new Transport ();
		using var device = new KasaDevice (new DeviceConfiguration ("127.0.0.1"), transport);
		var started = new TaskCompletionSource<bool> (TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource<bool> (TaskCreationOptions.RunContinuationsAsynchronously);
		using var poller = new ManagedParentDevicePoller ("test", device.Configuration, Settings (), null, "test", false,
			(_, _) => Task.FromResult (device), async (d, _) => { started.TrySetResult (true); await release.Task; await d.UpdateAsync (); },
			(_, t) => Task.Delay (Timeout.Infinite, t));
		var child = new Child ("a");
		poller.RegisterChild (child);
		Task refresh = poller.RefreshAsync (default);
		try
			{
			await started.Task.WaitForTestAsync (TimeSpan.FromSeconds (5));
			poller.Dispose ();
			}
		finally { release.TrySetResult (true); }
		Assert.CatchAsync<OperationCanceledException> (() => refresh.WaitForTestAsync (TimeSpan.FromSeconds (5)));
		Assert.That (child.Pushes, Is.Zero);
		Assert.That (child.Offline, Is.Zero);
		Assert.That (device.IsDisposed, Is.False);
		}

	}
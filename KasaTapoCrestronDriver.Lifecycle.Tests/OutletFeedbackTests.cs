// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
using System.Text.Json.Nodes;

using Crestron.DeviceDrivers.SDK;
using KasaTapoClient;
using KasaTapoClient.Internal;
using KasaTapoCrestronDriver;
using DeviceType = KasaTapoClient.DeviceType;

namespace KasaTapoCrestronDriver.Tests;

#if NETFRAMEWORK
[Category ("Processor")]
#endif
[TestFixture]
public sealed class OutletFeedbackTests
	{
	private sealed class Transport (bool initialOn, bool strip) : IDeviceTransport
		{
		public bool IsOn = initialOn;
		public bool IgnoreCommands;
		public bool FailCommands;
		public bool FailReads;
		public int ReadFailuresRemaining;
		public TaskCompletionSource<bool>? CommandGate;
		public readonly TaskCompletionSource<bool> CommandStarted = new (TaskCreationOptions.RunContinuationsAsynchronously);

		public async Task<string> SendAsync (string json, CancellationToken token)
			{
			token.ThrowIfCancellationRequested ();
			var request = JsonNode.Parse (json)!;
			if (request["system"]?["set_relay_state"] is JsonObject relay)
				{
				Assert.That (request["context"]?["child_ids"]?[0]?.GetValue<string> (), Is.EqualTo (strip ? "a" : null));
				CommandStarted.TrySetResult (true);
				if (CommandGate is not null)
					await CommandGate.Task.WaitForTestAsync (token);
				if (FailCommands)
					throw new IOException ("Simulated failed command.");
				if (!IgnoreCommands)
					IsOn = relay["state"]!.GetValue<int> () != 0;
				return "{\"system\":{\"set_relay_state\":{\"err_code\":0}}}";
				}
			if (FailReads || Interlocked.Decrement (ref ReadFailuresRemaining) >= 0)
				throw new IOException ("Simulated failed readback.");
			string children = strip ? ",\"children\":[{\"id\":\"a\",\"alias\":\"Socket A\",\"state\":" + (IsOn ? 1 : 0) + "}]" : "";
			return "{\"system\":{\"get_sysinfo\":{\"alias\":\"Fixture outlet\",\"model\":\"" + (strip ? "KP303" : "HS100")
				+ "\",\"deviceId\":\"feedback-fixture\",\"relay_state\":" + (IsOn ? 1 : 0) + children + "}}}";
			}

		public Task<string> SendManyAsync (IReadOnlyList<string> commands, CancellationToken token) =>
			Task.FromResult ("{\"emeter\":{\"err_code\":-1}}");
		}

	private sealed class Fixture : IDisposable
		{
		public readonly Transport Transport;
		public readonly KasaDevice Device;
		public readonly KasaOutletEntity Outlet;
		public readonly List<bool> Events = new ();
		private readonly Dictionary<string, Crestron.DeviceDrivers.EntityModel.Data.DriverEntityValue> _published = new ();
		public readonly List<object> SdkEvents = new ();
		private readonly ManagedParentDevicePoller? _poller;
		private readonly DriverLogger _logger = new ("outlet-feedback-test");

		public Fixture (bool initialOn, bool parentPolling)
			{
			Transport = new Transport (initialOn, parentPolling);
			Device = new KasaDevice (new DeviceConfiguration ("127.0.0.1"), Transport);
			var settings = new PlatformSharedConfiguration ();
			settings.Update ("", "", TimeSpan.FromSeconds (5), false, TimeSpan.FromSeconds (15), TimeSpan.FromSeconds (30), false, "", "", "");
			if (parentPolling)
				_poller = new ManagedParentDevicePoller ("feedback-fixture", Device.Configuration, settings, null, "test", true,
					(_, _) => Task.FromResult (Device), (d, t) => d.UpdateAsync (t), (_, t) => Task.Delay (Timeout.Infinite, t));
			var resources = new DriverImplementationResources { Logger = _logger, InitLogger = _logger.GetComponentLogger ("test", "feedback") };
			var descriptor = new ManagedLightDescriptor ("feedback-outlet", "127.0.0.1",
				parentPolling ? DeviceType.Strip : DeviceType.Plug, "Fixture outlet", parentPolling ? "KP303" : "HS100",
				"feedback-fixture", ManagedLightKind.OnOff, childId: parentPolling ? "a" : null, childKind: ManagedChildKind.Outlet);
			Outlet = new KasaOutletEntity ("feedback-outlet", descriptor, Device.Configuration, null, settings, resources, null!, "test", DriverTestPaths.DataDirectory, _poller);
			Outlet.ValuesChanged += (_, e) => { foreach (var p in e.Update.Changes) if (p.Value.Value is {} value) _published[p.Key] = value; };
			Outlet.OutletTurnedOn += (_, _) => RecordEvent (true);
			Outlet.OutletTurnedOff += (_, _) => RecordEvent (false);
			Outlet.EventRaised += (_, e) => { lock (SdkEvents) SdkEvents.Add (e); };
			}

		private void RecordEvent (bool isOn)
			{
			Assert.That (Outlet.OutletIsOn, Is.EqualTo (isOn), "State must be published before its event.");
			Assert.That (Outlet.OutletStatus, Is.EqualTo (isOn ? "On" : "Off"));
			Assert.That (Outlet.OutletIcon, Is.EqualTo (isOn ? "icGenericDeviceOn" : "icGenericDeviceOff"));
			Assert.That (Outlet.OnlineIndicatorIsOnline && Outlet.ReadyIndicatorIsReady, Is.True, "Availability must precede state events, including startup.");
			Assert.That (_published["outletIsOn"].GetValue<bool> (), Is.EqualTo (isOn));
			Assert.That (_published["outletStatus"].GetValue<string> (), Is.EqualTo (Outlet.OutletStatus));
			Assert.That (_published["outletIcon"].GetValue<string> (), Is.EqualTo (Outlet.OutletIcon));
			Assert.That (_published["onlineIndicatorIsOnline"].GetValue<bool> (), Is.True);
			Assert.That (_published["readyIndicatorIsReady"].GetValue<bool> (), Is.True);
			Assert.That (_published["hasEnergyReporting"].GetValue<bool> (), Is.EqualTo (Outlet.HasEnergyReporting));
			lock (Events)
				Events.Add (isOn);
			}

		public async Task InitializeAsync (bool retainStartupEvents = false)
			{
			await Device.UpdateAsync ();
			Assert.That (Outlet.TryAttachConnectedDevice (Device, "feedback-test"), Is.True);
			await Outlet.SetConfiguredAsync (true, "feedback-test", default);
			await Outlet.RefreshAsync (default);
			if (!retainStartupEvents) { Events.Clear (); SdkEvents.Clear (); }
			}

		public async Task WaitForCommandsAsync (int completed)
			{
			using var timeout = new CancellationTokenSource (TimeSpan.FromSeconds (10));
			while (true)
				{
				var activity = JsonNode.Parse (Outlet.ControlStatus)!;
				if (activity["Pending"]!.GetValue<int> () == 0 && activity["Completed"]!.GetValue<int> () >= completed)
					return;
				await Task.Delay (5, timeout.Token);
				}
			}

		public void Dispose ()
			{
			Outlet.Dispose ();
			_poller?.Dispose ();
			Device.Dispose ();
			_logger.Dispose ();
			}
		}

	[TestCase (false, false)]
	[TestCase (true, false)]
	[TestCase (false, true)]
	[TestCase (true, true)]
	public async Task InitialStateEmitsMatchingEventOnceAndSnapshotsDoNotRepeatIt (bool initialOn, bool parentPolling)
		{
		using var fixture = new Fixture (initialOn, parentPolling);
		await fixture.InitializeAsync (retainStartupEvents: true);
		fixture.Outlet.NotifyChildPublished ();
		fixture.Outlet.NotifyChildRunning ("test");
		await fixture.Outlet.RefreshAsync (default);
		Assert.That (fixture.Outlet.GetState ().PropertyValues["outletIsOn"].GetValue<bool> (), Is.EqualTo (initialOn));
		Assert.That (fixture.Events, Is.EqualTo (new[] { initialOn }));
		Assert.That (fixture.SdkEvents, Has.Count.EqualTo (1));
		}

	[TestCase (false)]
	[TestCase (true)]
	public async Task ExternalChangesRaiseOneEventPerConfirmedTransition (bool parentPolling)
		{
		using var fixture = new Fixture (false, parentPolling);
		await fixture.InitializeAsync ();
		fixture.Transport.IsOn = true;
		await fixture.Outlet.RefreshAsync (default);
		await fixture.Outlet.RefreshAsync (default);
		fixture.Outlet.PublishStateSnapshot ();
		fixture.Transport.IsOn = false;
		await fixture.Outlet.RefreshAsync (default);
		await fixture.Outlet.RefreshAsync (default);
		Assert.That (fixture.Events, Is.EqualTo (new[] { true, false }));
		Assert.That (fixture.SdkEvents, Has.Count.EqualTo (2), "Transitions must reach the SDK event interface consumed by Home.");
		}

	[TestCase (false)]
	[TestCase (true)]
	public async Task CommandsWaitForReadbackAndUnchangedCommandsDoNotRepeatEvents (bool parentPolling)
		{
		using var fixture = new Fixture (false, parentPolling);
		await fixture.InitializeAsync ();
		fixture.Transport.CommandGate = new (TaskCreationOptions.RunContinuationsAsynchronously);
		fixture.Outlet.OutletOn ();
		try
			{
			await fixture.Transport.CommandStarted.Task.WaitForTestAsync (TimeSpan.FromSeconds (5));
			Assert.That (fixture.Events, Is.Empty);
			Assert.That (fixture.Outlet.OutletIsOn, Is.False);
			}
		finally { fixture.Transport.CommandGate.TrySetResult (true); }
		await fixture.WaitForCommandsAsync (1);
		fixture.Outlet.SetOutletIsOn (true);
		await fixture.WaitForCommandsAsync (2);
		fixture.Outlet.OutletToggle ();
		await fixture.WaitForCommandsAsync (3);
		fixture.Outlet.OutletOff ();
		await fixture.WaitForCommandsAsync (4);
		Assert.That (fixture.Events, Is.EqualTo (new[] { true, false }));
		Assert.That (fixture.Outlet.OutletIsOn, Is.False);
		}

	[TestCase (false)]
	[TestCase (true)]
	public async Task QueuedTogglesUseThePreviousCommandsConfirmedState (bool parentPolling)
		{
		using var fixture = new Fixture (false, parentPolling);
		await fixture.InitializeAsync ();
		fixture.Transport.CommandGate = new (TaskCreationOptions.RunContinuationsAsynchronously);
		fixture.Outlet.OutletToggle ();
		await fixture.Transport.CommandStarted.Task.WaitForTestAsync (TimeSpan.FromSeconds (5));
		fixture.Outlet.OutletToggle ();
		fixture.Transport.CommandGate.TrySetResult (true);
		await fixture.WaitForCommandsAsync (2);
		Assert.That (fixture.Events, Is.EqualTo (new[] { true, false }));
		Assert.That (fixture.Outlet.OutletIsOn, Is.False);
		}

	[TestCase (false)]
	[TestCase (true)]
	public async Task SuccessfulAcknowledgementWithoutStateChangeDoesNotRaiseEvent (bool parentPolling)
		{
		using var fixture = new Fixture (false, parentPolling);
		await fixture.InitializeAsync ();
		fixture.Transport.IgnoreCommands = true;
		fixture.Outlet.OutletOn ();
		await fixture.WaitForCommandsAsync (1);
		Assert.That (fixture.Events, Is.Empty);
		Assert.That (fixture.Outlet.OutletIsOn, Is.False);
		}

	[Test]
	public async Task ToggleRetryRetainsItsTargetAfterLostReadback ()
		{
		using var fixture = new Fixture (false, true);
		await fixture.InitializeAsync ();
		fixture.Transport.ReadFailuresRemaining = 1;
		fixture.Outlet.OutletToggle ();
		await fixture.WaitForCommandsAsync (1);
		Assert.That (fixture.Transport.IsOn, Is.True);
		Assert.That (fixture.Outlet.OutletIsOn, Is.True);
		Assert.That (fixture.Events, Is.EqualTo (new[] { true }));
		}

	[TestCase (false)]
	[TestCase (true)]
	public async Task FailedCommandOrReadbackDoesNotPublishUnconfirmedState (bool readbackFailure)
		{
		// The parent poller's injected connector keeps retries entirely on the fake transport.
		using var fixture = new Fixture (false, true);
		await fixture.InitializeAsync ();
		fixture.Transport.FailCommands = !readbackFailure;
		fixture.Transport.FailReads = readbackFailure;
		fixture.Outlet.OutletOn ();
		await fixture.WaitForCommandsAsync (1);
		Assert.That (fixture.Events, Is.Empty);
		Assert.That (fixture.Outlet.OutletIsOn, Is.False);
		fixture.Transport.FailCommands = false;
		fixture.Transport.FailReads = false;
		fixture.Transport.IsOn = true;
		await fixture.Outlet.RefreshAsync (default);
		await fixture.Outlet.RefreshAsync (default);
		Assert.That (fixture.Events, Is.EqualTo (new[] { true }), "Recovery must compare against the last confirmed state.");
		}
	}

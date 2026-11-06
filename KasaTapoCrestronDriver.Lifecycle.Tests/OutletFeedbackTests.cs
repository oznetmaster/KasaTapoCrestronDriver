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
		public int Reads;
		public int? PowerMilliwatts;
		public int VoltageMillivolts = 230000;
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
			Interlocked.Increment (ref Reads);
			if (FailReads || Interlocked.Decrement (ref ReadFailuresRemaining) >= 0)
				throw new IOException ("Simulated failed readback.");
			string children = strip ? ",\"children\":[{\"id\":\"a\",\"alias\":\"Socket A\",\"state\":" + (IsOn ? 1 : 0) + "}]" : "";
			return "{\"system\":{\"get_sysinfo\":{\"alias\":\"Fixture outlet\",\"model\":\"" + (strip ? "KP303" : "HS100")
				+ "\",\"deviceId\":\"feedback-fixture\",\"relay_state\":" + (IsOn ? 1 : 0) + children + "}}}";
			}

		public Task<string> SendManyAsync (IReadOnlyList<string> commands, CancellationToken token) =>
			Task.FromResult (PowerMilliwatts.HasValue
				? "{\"emeter\":{\"get_realtime\":{\"power_mw\":" + PowerMilliwatts.Value + ",\"voltage_mv\":" + VoltageMillivolts + ",\"current_ma\":159,\"total_wh\":1250}}}"
				: "{\"emeter\":{\"err_code\":-1}}");
		}

	private sealed class Fixture : IDisposable
		{
		public readonly Transport Transport;
		public readonly KasaDevice Device;
		public readonly KasaOutletEntity Outlet;
		public readonly List<bool> Events = new ();
		public readonly List<string> PublishedIds = new ();
		public readonly PlatformSharedConfiguration Settings = new ();
		private readonly Dictionary<string, Crestron.DeviceDrivers.EntityModel.Data.DriverEntityValue> _published = new ();
		public readonly List<object> SdkEvents = new ();
		private readonly ManagedParentDevicePoller? _poller;
		private readonly DriverLogger _logger = new ("outlet-feedback-test");

		public Fixture (bool initialOn, bool parentPolling)
			{
			Transport = new Transport (initialOn, parentPolling);
			Device = new KasaDevice (new DeviceConfiguration ("127.0.0.1"), Transport);
			var settings = Settings;
			settings.Update ("", "", TimeSpan.FromSeconds (5), false, TimeSpan.FromSeconds (15), TimeSpan.FromSeconds (30), false, "", "", "");
			if (parentPolling)
				_poller = new ManagedParentDevicePoller ("feedback-fixture", Device.Configuration, settings, null, "test", true,
					(_, _) => Task.FromResult (Device), (d, t) => d.UpdateAsync (t), (_, t) => Task.Delay (Timeout.Infinite, t));
			var resources = new DriverImplementationResources { Logger = _logger, InitLogger = _logger.GetComponentLogger ("test", "feedback") };
			var descriptor = new ManagedLightDescriptor ("feedback-outlet", "127.0.0.1",
				parentPolling ? DeviceType.Strip : DeviceType.Plug, "Fixture outlet", parentPolling ? "KP303" : "HS100",
				"feedback-fixture", ManagedLightKind.OnOff, childId: parentPolling ? "a" : null, childKind: ManagedChildKind.Outlet);
			Outlet = new KasaOutletEntity ("feedback-outlet", descriptor, Device.Configuration, null, settings, resources, null!, "test", DriverTestPaths.DataDirectory, _poller);
			Outlet.ValuesChanged += (_, e) => { foreach (var p in e.Update.Changes) { PublishedIds.Add (p.Key); if (p.Value.Value is {} value) _published[p.Key] = value; } };
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

		public async Task InitializeAsync (bool retainStartupEvents = false, bool runningBeforeState = false)
			{
			if (runningBeforeState) { Outlet.NotifyChildRunning ("fixture-before-state"); Outlet.NotifyHostStateRead (); }
			await Device.UpdateAsync ();
			Assert.That (Outlet.TryAttachConnectedDevice (Device, "feedback-test"), Is.True);
			await Outlet.SetConfiguredAsync (true, "feedback-test", default);
			await Outlet.RefreshAsync (default);
			if (!runningBeforeState) Assert.That (Events, Is.Empty, "Confirmed startup waits until the host is Running.");
			Outlet.NotifyChildRunning ("fixture-after-state");
			if (!runningBeforeState) Assert.That (Events, Is.Empty, "Running alone is not host state-read readiness.");
			Outlet.NotifyHostStateRead ();
			Outlet.NotifyHostStateRead ();
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

	[TestCase (false)]
	[TestCase (true)]
	public async Task RepeatedConfirmedRefreshesPublishOnlyChanges (bool parentPolling)
		{
		using var fixture = new Fixture (false, parentPolling);
		await fixture.InitializeAsync ();
		fixture.Outlet.NotifyChildPublished ();
		fixture.PublishedIds.Clear ();
		int reads = fixture.Transport.Reads;
		for (int i = 0; i < 5; i++) await fixture.Outlet.RefreshAsync (default);
		Assert.That (fixture.Transport.Reads, Is.GreaterThan (reads), "Quiet notifications must not disable polling.");
		Assert.That (fixture.PublishedIds, Is.Empty, "Unchanged polls must not replay UI, state, energy or availability.");
		fixture.Transport.IsOn = true;
		await fixture.Outlet.RefreshAsync (default);
		Assert.That (fixture.PublishedIds, Does.Contain ("outletIsOn"));
		Assert.That (fixture.Events, Is.EqualTo (new[] { true }));
		fixture.PublishedIds.Clear ();
		await fixture.Outlet.RefreshAsync (default);
		Assert.That (fixture.PublishedIds, Is.Empty);
		}

	[Test]
	public async Task EnergyChangesPublishTelemetryWithoutRepeatingSwitchStateOrEvent ()
		{
		using var fixture = new Fixture (false, false);
		fixture.Transport.PowerMilliwatts = 36500;
		await fixture.InitializeAsync ();
		Assert.That (fixture.Outlet.HasEnergyReporting, Is.True);
		Assert.That (fixture.Outlet.OutletEnergyCurrentPowerWatts, Is.EqualTo (36.5));
		fixture.Outlet.NotifyChildPublished ();
		fixture.PublishedIds.Clear ();
		for (int i = 0; i < 5; i++) await fixture.Outlet.RefreshAsync (default);
		Assert.That (fixture.PublishedIds, Is.Empty, "Unchanged energy readings must remain quiet too.");
		fixture.Transport.PowerMilliwatts = 41000;
		await fixture.Outlet.RefreshAsync (default);
		Assert.That (fixture.Outlet.OutletEnergyCurrentPowerWatts, Is.EqualTo (41));
		Assert.That (fixture.PublishedIds, Is.EqualTo (new[] { "outletEnergyCurrentPowerWatts" }));
		Assert.That (fixture.Events, Is.Empty);
		fixture.PublishedIds.Clear ();
		await fixture.Outlet.RefreshAsync (default);
		Assert.That (fixture.PublishedIds, Is.Empty);
		}

	[TestCase (120041, 120049, 120.0, false)]
	[TestCase (120041, 120050, 120.1, true)]
	[TestCase (120041, 120100, 120.1, true)]
	[TestCase (120141, 120041, 120.0, true)]
	public async Task VoltagePublishesAtOneDecimalPlaceWithoutRepeatingOtherFeedback (int initialMillivolts, int nextMillivolts, double expectedVolts, bool voltageChanges)
		{
		using var fixture = new Fixture (false, false);
		fixture.Transport.PowerMilliwatts = 36500;
		fixture.Transport.VoltageMillivolts = initialMillivolts;
		await fixture.InitializeAsync ();
		Assert.That (fixture.Outlet.OutletEnergyVoltageVolts, Is.EqualTo (initialMillivolts == 120141 ? 120.1 : 120.0));
		fixture.PublishedIds.Clear ();
		fixture.Transport.VoltageMillivolts = nextMillivolts;
		await fixture.Outlet.RefreshAsync (default);
		Assert.That (fixture.Outlet.OutletEnergyVoltageVolts, Is.EqualTo (expectedVolts));
		Assert.That (fixture.Outlet.GetState ().PropertyValues["outletEnergyVoltageVolts"].GetValue<double> (), Is.EqualTo (expectedVolts));
		Assert.That (fixture.PublishedIds, Is.EqualTo (voltageChanges ? new[] { "outletEnergyVoltageVolts" } : Array.Empty<string> ()));
		Assert.That (fixture.Events, Is.Empty, "Voltage changes must not raise switch-state events.");
		fixture.PublishedIds.Clear ();
		await fixture.Outlet.RefreshAsync (default);
		Assert.That (fixture.PublishedIds, Is.Empty, "An unchanged rounded reading must remain quiet.");
		}

	[Test]
	public async Task ScheduledPollingReadsDeviceWithoutReplayingSnapshot ()
		{
		using var fixture = new Fixture (false, false);
		await fixture.InitializeAsync ();
		fixture.Outlet.NotifyChildPublished ();
		fixture.PublishedIds.Clear ();
		var before = fixture.Settings.Snapshot ();
		fixture.Settings.Update ("", "", TimeSpan.FromSeconds (5), true, TimeSpan.FromMilliseconds (100), TimeSpan.FromSeconds (30), false, "", "", "");
		int reads = fixture.Transport.Reads;
		fixture.Outlet.ApplyRuntimeConfiguration (before, fixture.Settings.Snapshot ());
        using var wait = new CancellationTokenSource (TimeSpan.FromSeconds (4));
        while (Volatile.Read (ref fixture.Transport.Reads) <= reads)
            await Task.Delay (15, wait.Token);
		fixture.Outlet.Stop ();
		Assert.That (fixture.Transport.Reads, Is.GreaterThan (reads));
		Assert.That (fixture.PublishedIds, Is.Empty);
		Assert.That (fixture.Events, Is.Empty);
		}

	[TestCase (false, false, false)]
	[TestCase (false, false, true)]
	[TestCase (false, true, false)]
	[TestCase (false, true, true)]
	[TestCase (true, false, false)]
	[TestCase (true, false, true)]
	[TestCase (true, true, false)]
	[TestCase (true, true, true)]
	public async Task InitialStateEmitsMatchingEventOnceAndSnapshotsDoNotRepeatIt (bool initialOn, bool parentPolling, bool runningBeforeState)
		{
		using var fixture = new Fixture (initialOn, parentPolling);
		await fixture.InitializeAsync (retainStartupEvents: true, runningBeforeState: runningBeforeState);
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
#if OUTLET_DIAGNOSTICS
    [Test]
    public void DiagnosticFailureIdentifiesTheInstanceAndDoesNotSerializeExceptionMessages()
    {
        using var fixture = new Fixture(false, false);
        var records = new List<OutletDiagnosticRecord>();
        fixture.Outlet.DiagnosticObserverForTests = r => records.Add(r);
        fixture.Outlet.Stop();
        var method = typeof(KasaOutletEntity).GetMethod("TraceOutlet", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        method.Invoke(fixture.Outlet, new object?[] { "connect-failure", new InvalidDataException("The TPAP discover response did not contain a tpap object. SECRET_PASSWORD"), null });
        var record = records.Last();
        Assert.That(record.Controller, Is.EqualTo("feedback-outlet"));
        Assert.That(record.Host, Is.EqualTo("127.0.0.1"));
        Assert.That(record.Stopped, Is.True);
        Assert.That(record.Instance, Is.EqualTo(records.First().Instance));
        Assert.That(record.ErrorReason, Is.EqualTo("MissingTpapDiscoveryObject"));
        Assert.That(System.Text.Json.JsonSerializer.Serialize(record), Does.Not.Contain("SECRET_PASSWORD"));
    }
    [Test]
    public async Task DiagnosticCaptureExplainsRawRoundedAndUnchangedReadingsWithoutChangingFeedback()
    {
        using var fixture = new Fixture(false, false);
        fixture.Transport.PowerMilliwatts = 1234;
        fixture.Transport.VoltageMillivolts = 120041;
        var records = new List<OutletDiagnosticRecord>();
        fixture.Outlet.DiagnosticObserverForTests = r => records.Add(r);
        await fixture.InitializeAsync(); records.Clear();
        await fixture.Outlet.RefreshAsync(default);
        var sample = records.Single(r => r.Event == "confirmed-sample");
        Assert.That(sample.Raw!.Voltage, Is.EqualTo(120.041));
        Assert.That(sample.Published!.Voltage, Is.EqualTo(120.0));
        Assert.That(records.Single(r => r.Event == "numeric-decision" && r.Property == "outletEnergyVoltageVolts").Changed, Is.False);
        Assert.That(records.Any(r => r.Event.StartsWith("published:outletEnergyVoltageVolts:")), Is.False);
        Assert.That(fixture.Events, Is.Empty);
        records.Clear(); fixture.Transport.VoltageMillivolts = 120051;
        await fixture.Outlet.RefreshAsync(default);
        var decision = records.Single(r => r.Event == "numeric-decision" && r.Property == "outletEnergyVoltageVolts");
        Assert.That(decision.Previous, Is.EqualTo(120.0)); Assert.That(decision.Value, Is.EqualTo(120.1)); Assert.That(decision.Changed, Is.True);
        Assert.That(records.Count(r => r.Event.StartsWith("published:outletEnergyVoltageVolts:")), Is.EqualTo(1));
        Assert.That(fixture.Events, Is.Empty);
        fixture.Outlet.Stop();
        Assert.That(records.Last(r => r.Event == "stop").Stopped, Is.True);
    }
#endif

	}

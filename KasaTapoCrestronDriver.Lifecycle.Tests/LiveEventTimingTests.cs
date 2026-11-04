// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
using Crestron.DeviceDrivers.EntityModel.Data;
using Crestron.DeviceDrivers.SDK;
using KasaTapoClient;
using KasaTapoClient.Internal;
using KasaTapoCrestronDriver;
using System.Collections.Concurrent;
using System.Reflection;

namespace KasaTapoCrestronDriver.Tests;

public sealed partial class LiveDriverEntityTests
{
    // This decorator forwards real network traffic unchanged. The gate withholds an actual
    // device response from the client so request/acknowledgement/readback can be distinguished.
    private sealed class TimingTransport(DeviceConfiguration configuration) : IDisposableDeviceTransport
    {
        private readonly IDeviceTransport _inner = DeviceTransportFactory.Create(configuration);
        private int _sequence;
        public readonly ConcurrentQueue<string> Trace = new();
        public int LastReadReturned;
        public TaskCompletionSource<bool>? ReadHeld, ReleaseRead;
        public int Mark(string text)
        {
            int sequence = Interlocked.Increment(ref _sequence);
            Trace.Enqueue($"{sequence:D4} {text}");
            return sequence;
        }
        public void HoldNextRead()
        {
            ReadHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ReleaseRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        private async Task<string> Forward(IReadOnlyList<string> commands, CancellationToken token, bool many)
        {
            bool stateRead = commands.Any(c => c.Contains("get_sysinfo") || c.Contains("get_device_info") || c.Contains("get_child_device_list"));
            Mark(stateRead ? "state-read-request" : "command-or-module-request");
            string result = many ? await _inner.SendManyAsync(commands, token) : await _inner.SendAsync(commands[0], token);
            Mark(stateRead ? "state-response-received" : "command-or-module-response");
            if (stateRead)
            {
                var release = ReleaseRead;
                if (release is not null)
                {
                    ReadHeld!.TrySetResult(true);
                    await release.Task.WaitForTestAsync(token);
                    ReleaseRead = null;
                }
                LastReadReturned = Mark("state-response-returned-to-client");
            }
            return result;
        }
        public Task<string> SendAsync(string json, CancellationToken token) => Forward(new[] { json }, token, false);
        public Task<string> SendManyAsync(IReadOnlyList<string> json, CancellationToken token) => Forward(json, token, true);
        public void Dispose() { ReleaseRead?.TrySetResult(true); (_inner as IDisposable)?.Dispose(); }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        while (!condition()) await Task.Delay(50, timeout.Token);
    }

    private void RequireDemoControl(KasaDevice device, string? childId = null)
    {
        Assert.That(_settings.AllowDemoPowerControl, Is.True, "Live controls require the explicit private opt-in.");
        Assert.That((childId is null ? device.SystemInfo?.Alias : device.GetChild(childId)?.Alias)?.IndexOf("demo", StringComparison.OrdinalIgnoreCase) >= 0,
            Is.True, "Only a currently named Demo device may be controlled.");
    }

    [Test]
    public async Task LiveInventory_ReportsConfiguredTargets()
    {
        foreach (var found in _discovered.Where(d => d.Model?.StartsWith("L530", StringComparison.OrdinalIgnoreCase) == true || d.Model?.StartsWith("H100", StringComparison.OrdinalIgnoreCase) == true || d.Model?.StartsWith("KP303", StringComparison.OrdinalIgnoreCase) == true))
        {
            using var device = await Discover.ConnectAsync(Discover.CreateConfiguration(found,
                new DeviceCredentials(_settings.Credentials?.UserName, _settings.Credentials?.Password), _timeout));
            TestContext.Progress.WriteLine($"Authenticated: alias={device.SystemInfo?.Alias}, model={device.SystemInfo?.Model}, discoveryId={found.DeviceId}");
            foreach (var child in device.Children) TestContext.Progress.WriteLine($"Child: alias={child.Alias}, model={child.Model}, id={child.Id}");
        }
    }
    [Test, Category("PhysicalTriggers")]
    public async Task Hub_PhysicalEventsFollowObservedStatePublication()
    {
        int seconds = int.Parse(TestContext.Parameters.Get("HubObservationSeconds", "300"));
        Assert.That(seconds, Is.InRange(15, 600));
        var expected = TestContext.Parameters.Get("ExpectedPhysicalEvents", "buttonTriggered,contactOpened,contactClosed,motionDetectedEvent,motionCleared").Split(',');
        using var selected = await Connect("hub");
        using var transport = new TimingTransport(selected.Configuration);
        using var hub = new KasaDevice(selected.Configuration, transport);
        await hub.UpdateAsync();
        using var logger = new DriverLogger("live-hub-events");
        var resources = new DriverImplementationResources { Logger = logger, InitLogger = logger.GetComponentLogger("test", "hub") };
        var entities = new List<IKasaHubChildEntity>();
        var seen = new ConcurrentDictionary<string, int>();
        var failures = new ConcurrentQueue<string>();
        void Subscribe(KasaSensorEntity sensor, string eventName, string property, bool value,
            ConcurrentDictionary<string, DriverEntityValue> published, Action<EventHandler> subscribe)
        {
            subscribe((_, _) => {
                transport.Mark("event:" + eventName);
                // Read the confirmed signal directly. GetState rebuilds the entire SDK definition,
                // which is not a callback-safe observation for this standalone live test entity.
                bool current = property switch {
                    "contactIsOpen" => sensor.ContactIsOpen, "motionDetected" => sensor.MotionDetected,
                    "leakDetected" => sensor.LeakDetected, "batteryIsLow" => sensor.BatteryIsLow,
                    "temperatureWarningIsActive" => sensor.TemperatureWarningIsActive,
                    "humidityWarningIsActive" => sensor.HumidityWarningIsActive,
                    _ => throw new InvalidOperationException("Unknown sensor signal: " + property)
                };
                if (current != value
                    || !published.TryGetValue(property, out var update) || update.GetValue<bool>() != value
                    || !published.TryGetValue("sensorIcon", out var icon) || icon.GetValue<string>() != sensor.SensorIcon
                    || !published.TryGetValue("sensorStatus", out var status) || status.GetValue<string>() != sensor.SensorStatus
                    || !sensor.OnlineIndicatorIsOnline || !sensor.ReadyIndicatorIsReady)
                    failures.Enqueue(eventName + " preceded state/status/icon/availability publication.");
                seen.AddOrUpdate(eventName, 1, (_, n) => n + 1);
                TestContext.Progress.WriteLine("OBSERVED PHYSICAL EVENT: " + eventName);
            });
        }
        try
        {
            foreach (var info in hub.Children)
            {
                bool button = info.Model?.StartsWith("S200", StringComparison.OrdinalIgnoreCase) == true;
                var category = HubChildCategoryResolver.FromModel(info.Model ?? "");
                if (!button && category == HubChildCategory.None) continue;
                var descriptor = new ManagedLightDescriptor("live-" + info.Id, hub.Host, hub.DeviceType,
                    info.Alias ?? info.Id, info.Model ?? "", info.Id, ManagedLightKind.Unknown, childId: info.Id,
                    childKind: button ? ManagedChildKind.Button : ManagedChildKind.Sensor, hubChildCategory: category);
                var published = new ConcurrentDictionary<string, DriverEntityValue>();
                if (button)
                {
                    var entity = new KasaButtonEntity(descriptor.ControllerId, descriptor, hub.Configuration, null, Settings(), resources, null!, "hub-events", DriverTestPaths.DataDirectory);
                    if (hub.GetChildDevice(info.Id)?.DoubleClick.Enabled is bool enabled) entity.SetAllowDoubleClick(enabled);
                    entity.ValuesChanged += (_, e) => { foreach (var p in e.Update.Changes) if (p.Value.Value is {} v) { published[p.Key] = v; transport.Mark("property:" + info.Model + ":" + p.Key); } };
                    entity.ButtonTriggered += (_, _) => {
                        transport.Mark("event:buttonTriggered");
                        if (!entity.OnlineIndicatorIsOnline || !entity.ReadyIndicatorIsReady
                            || !published.TryGetValue("lastTriggerType", out var kind) || kind.GetValue<string>() != entity.LastTriggerType
                            || !published.TryGetValue("buttonIcon", out var icon) || icon.GetValue<string>() != entity.ButtonIcon)
                            failures.Enqueue("Button event preceded gesture/icon/availability publication.");
                        seen.AddOrUpdate("buttonTriggered", 1, (_, n) => n + 1);
                        TestContext.Progress.WriteLine("OBSERVED PHYSICAL EVENT: buttonTriggered");
                    };
                    entity.SinglePressed += (_, _) => { transport.Mark("event:buttonSinglePressed"); seen.AddOrUpdate("buttonSinglePressed", 1, (_, n) => n + 1); };
                    entity.DoublePressed += (_, _) => { transport.Mark("event:buttonDoublePressed"); seen.AddOrUpdate("buttonDoublePressed", 1, (_, n) => n + 1); };
                    entity.Held += (_, _) => { transport.Mark("event:buttonHeld"); seen.AddOrUpdate("buttonHeld", 1, (_, n) => n + 1); };
                    entity.Released += (_, _) => { transport.Mark("event:buttonReleased"); seen.AddOrUpdate("buttonReleased", 1, (_, n) => n + 1); };
                    entity.NotifyChildPublished(); entity.NotifyChildRunning("live-listener"); entities.Add(entity);
                }
                else
                {
                    var entity = new KasaSensorEntity(descriptor.ControllerId, descriptor, hub.Configuration, null, Settings(), resources, null!, "hub-events", DriverTestPaths.DataDirectory);
                    entity.ValuesChanged += (_, e) => { foreach (var p in e.Update.Changes) if (p.Value.Value is {} v) { published[p.Key] = v; transport.Mark("property:" + info.Model + ":" + p.Key); } };
                    Subscribe(entity, "contactOpened", "contactIsOpen", true, published, h => entity.ContactOpened += h);
                    Subscribe(entity, "contactClosed", "contactIsOpen", false, published, h => entity.ContactClosed += h);
                    Subscribe(entity, "motionDetectedEvent", "motionDetected", true, published, h => entity.MotionDetectedEvent += h);
                    Subscribe(entity, "motionCleared", "motionDetected", false, published, h => entity.MotionCleared += h);
                    Subscribe(entity, "leakDetectedEvent", "leakDetected", true, published, h => entity.LeakDetectedEvent += h);
                    Subscribe(entity, "leakCleared", "leakDetected", false, published, h => entity.LeakCleared += h);
                    Subscribe(entity, "batteryLowEvent", "batteryIsLow", true, published, h => entity.BatteryLowEvent += h);
                    Subscribe(entity, "batteryNormal", "batteryIsLow", false, published, h => entity.BatteryNormal += h);
                    Subscribe(entity, "temperatureWarningDetected", "temperatureWarningIsActive", true, published, h => entity.TemperatureWarningDetected += h);
                    Subscribe(entity, "temperatureWarningCleared", "temperatureWarningIsActive", false, published, h => entity.TemperatureWarningCleared += h);
                    Subscribe(entity, "humidityWarningDetected", "humidityWarningIsActive", true, published, h => entity.HumidityWarningDetected += h);
                    Subscribe(entity, "humidityWarningCleared", "humidityWarningIsActive", false, published, h => entity.HumidityWarningCleared += h);
                    entity.NotifyChildPublished(); entity.NotifyChildRunning("live-listener"); if (entity is IConfirmedStartupEventEntity startup) startup.NotifyHostStateRead(); entities.Add(entity);
                }
            }
            void Push() { foreach (var entity in entities) entity.ApplyPushedState(hub.GetChildDevice(entity.ChildId), hub); }
            Push();
            Assert.That(seen.Keys.Any(e => e.StartsWith("button", StringComparison.Ordinal)), Is.False, "Historical button gestures must remain suppressed.");
            Assert.That(failures, Is.Empty, "Initial sensor state events must follow complete property publication.");
            foreach (var item in seen) TestContext.Progress.WriteLine($"Observed startup state: {item.Key} x{item.Value}");
            seen.Clear(); // Startup state events are not evidence of the requested physical actions.
            TestContext.Progress.WriteLine("LIVE HUB LISTENER READY: operate button/contact/motion now.");
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until && !expected.All(seen.ContainsKey))
            {
                await Task.Delay(1500);
                await hub.UpdateAsync(); Push();
            }
            Assert.That(failures, Is.Empty);
            Assert.That(expected.Where(e => !seen.ContainsKey(e)).ToArray(), Is.Empty, "Required physical transitions were not observed; this is not a live pass.");
        }
        finally
        {
            foreach (var entity in entities) entity.Dispose();
            foreach (var item in seen) TestContext.Progress.WriteLine($"Observed physical event: {item.Key} x{item.Value}");
            foreach (var line in transport.Trace) TestContext.Progress.WriteLine("hub " + line);
        }
    }

    [TestCase("plug")]
    [TestCase("strip")]
    public async Task DemoOutlet_EventsFollowActualReadbackAndPropertyPublication(string role)
    {
        using var observer = await Connect(role);
        string? childId = role == "strip" ? Target(role).ChildDeviceId : null;
        RequireDemoControl(observer, childId);
        bool Reported(KasaDevice d) => (childId is null ? d.IsOn : d.GetChild(childId)?.IsOn)
            ?? throw new InvalidDataException("Outlet power is not reported.");
        bool original = Reported(observer);
        using var transport = new TimingTransport(observer.Configuration);
        using var device = new KasaDevice(observer.Configuration, transport);
        await device.UpdateAsync();
        using var logger = new DriverLogger("live-outlet-timing");
        var resources = new DriverImplementationResources { Logger = logger, InitLogger = logger.GetComponentLogger("test", "timing") };
        var descriptor = new ManagedLightDescriptor("timing-outlet", device.Host, device.DeviceType,
            device.SystemInfo!.Alias!, device.SystemInfo.Model!, device.SystemInfo.DeviceId!, ManagedLightKind.OnOff,
            childId: childId, childKind: ManagedChildKind.Outlet);
        using var entity = new KasaOutletEntity("timing-outlet", descriptor, device.Configuration, null, Settings(), resources, null!, "timing", DriverTestPaths.DataDirectory);
        Assert.That(entity.TryAttachConnectedDevice(device, "timing"), Is.True);
        await entity.SetConfiguredAsync(true, "timing", default);
        await entity.RefreshAsync(default);
        entity.NotifyChildPublished();
        entity.NotifyChildRunning("live-startup"); entity.NotifyHostStateRead();
        var published = new ConcurrentDictionary<string, DriverEntityValue>();
        var notified = new ConcurrentQueue<string>();
        int events = 0;
        var callbackFailures = new ConcurrentQueue<string>();
        entity.ValuesChanged += (_, e) => {
            foreach (var pair in e.Update.Changes) if (pair.Value.Value is {} value) {
                published[pair.Key] = value; notified.Enqueue(pair.Key); transport.Mark("property:" + pair.Key);
            }
        };
        void Event(bool on)
        {
            int sequence = transport.Mark("event:" + (on ? "on" : "off"));
            // Capture failures: driver event-dispatch exception handling must not hide them.
            if (transport.LastReadReturned >= sequence || entity.OutletIsOn != on
                || entity.OutletStatus != (on ? "On" : "Off")
                || entity.OutletIcon != (on ? "icGenericDeviceOn" : "icGenericDeviceOff")
                || !published.TryGetValue("outletIsOn", out var value) || value.GetValue<bool>() != on
                || !published.TryGetValue("outletStatus", out var status) || status.GetValue<string>() != entity.OutletStatus
                || !published.TryGetValue("outletIcon", out var icon) || icon.GetValue<string>() != entity.OutletIcon)
                callbackFailures.Enqueue("Event preceded confirmed state/status/icon publication.");
            Interlocked.Increment(ref events);
        }
        entity.OutletTurnedOn += (_, _) => Event(true);
        entity.OutletTurnedOff += (_, _) => Event(false);
        try
        {
            transport.HoldNextRead();
            entity.ExecuteCommand(original ? "outletOff" : "outletOn", new Dictionary<string, DriverEntityValue>(),
                r => Assert.That(r.Failed, Is.False));
            await transport.ReadHeld!.Task.WaitForTestAsync(TimeSpan.FromSeconds(35));
            await observer.UpdateAsync();
            Assert.That(Reported(observer), Is.EqualTo(!original), "Independent network read must observe the physical change.");
            Assert.That(entity.OutletIsOn, Is.EqualTo(original), "Withheld readback cannot update feedback.");
            Assert.That(events, Is.Zero, "No transition event may escape while readback is withheld.");
            transport.ReleaseRead!.TrySetResult(true);
            await WaitUntil(() => Volatile.Read(ref events) == 1);
            Assert.That(callbackFailures, Is.Empty);
            await entity.RefreshAsync(default);
            Assert.That(events, Is.EqualTo(1), "An unchanged poll must not repeat the event.");

            // A second independent client acts like the Kasa app. No entity command is sent.
            if (childId is null) { if (original) await observer.TurnOnAsync(); else await observer.TurnOffAsync(); }
            else { if (original) await observer.TurnChildOnAsync(childId); else await observer.TurnChildOffAsync(childId); }
            await observer.UpdateAsync();
            Assert.That(Reported(observer), Is.EqualTo(original));
            Assert.That(events, Is.EqualTo(1), "External control alone cannot announce an unpolled transition.");
            await entity.RefreshAsync(default);
            Assert.That(events, Is.EqualTo(2));
            await entity.RefreshAsync(default);
            Assert.That(events, Is.EqualTo(2));
            Assert.That(callbackFailures, Is.Empty);
            notified.Clear();
            for (int i = 0; i < 3; i++) await entity.RefreshAsync(default);
            string[] stableProperties = { "extension:uiDefinition", "deviceLabel", "controlDeviceId", "outletIsOn", "outletIcon", "outletStatus", "hasEnergyReporting", "onlineIndicatorIsOnline", "onlineIndicator:isOnline", "readyIndicatorIsReady", "readyIndicator:isReady" };
            Assert.That(notified.Where(id => stableProperties.Contains(id)), Is.Empty, "Real device polls must not replay unchanged outlet properties. Energy telemetry may change.");
            Assert.That(events, Is.EqualTo(2));
        }
        finally
        {
            transport.ReleaseRead?.TrySetResult(true);
            await observer.UpdateAsync(); RequireDemoControl(observer, childId);
            if (childId is null) { if (original) await observer.TurnOnAsync(); else await observer.TurnOffAsync(); }
            else { if (original) await observer.TurnChildOnAsync(childId); else await observer.TurnChildOffAsync(childId); }
            await observer.UpdateAsync();
            Assert.That(Reported(observer), Is.EqualTo(original), "Restore the original outlet state.");
            foreach (var line in transport.Trace) TestContext.Progress.WriteLine($"{role} {line}");
        }
    }

    [TestCase("light")]
    [TestCase("lightSmart")]
    public async Task DemoLight_FeedbackWaitsForActualReadback(string role)
    {
        using var observer = await Connect(role); RequireDemoControl(observer);
        var original = observer.Light.State ?? throw new InvalidDataException("Light state is required.");
        Assert.That(original.Brightness, Is.InRange(1, 100));
        using var transport = new TimingTransport(observer.Configuration);
        using var device = new KasaDevice(observer.Configuration, transport);
        await device.UpdateAsync();
        using var logger = new DriverLogger("live-light-timing");
        var resources = new DriverImplementationResources { Logger = logger, InitLogger = logger.GetComponentLogger("test", "timing") };
        var descriptor = new ManagedLightDescriptor("timing-light", device.Host, device.DeviceType,
            device.SystemInfo!.Alias!, device.SystemInfo.Model!, device.SystemInfo.DeviceId!, KasaLightEntity.InferManagedLightKind(device, device.DeviceType));
        using var entity = new KasaLightEntity("timing-light", descriptor, device.Configuration, null, Settings(), null, resources, null!, "timing");
        entity.TryAttachConnectedDevice(device, "timing"); await entity.SetConfiguredAsync(true, "timing", default);
        typeof(KasaLightEntity).GetProperty("_startupDimmerReplayGuardUntilUtc", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(entity, null);
        double Level() => entity.GetState().PropertyValues["lightDimmer:level"].GetValue<double>();
        int levelNotifications = 0;
        entity.ValuesChanged += (_, e) => {
            foreach (var pair in e.Update.Changes) {
                transport.Mark("property:" + pair.Key);
                if (pair.Key == "lightDimmer:level") Interlocked.Increment(ref levelNotifications);
            }
        };
        try
        {
            foreach (bool on in new[] { original.IsOn != true, original.IsOn == true })
            {
                double baseline = Level(); int count = levelNotifications;
                transport.HoldNextRead();
                entity.ExecuteCommand("lightDimmer:setLevel", new Dictionary<string, DriverEntityValue> { ["level"] = new(on ? 1d : 0d) },
                    r => Assert.That(r.Failed, Is.False));
                await transport.ReadHeld!.Task.WaitForTestAsync(TimeSpan.FromSeconds(35));
                await observer.UpdateAsync();
                Assert.That(observer.Light.State!.IsOn, Is.EqualTo(on));
                Assert.That(Level(), Is.EqualTo(baseline), "Physical power changed, but unconsumed readback must not change feedback.");
                Assert.That(levelNotifications, Is.EqualTo(count));
                transport.ReleaseRead!.TrySetResult(true);
                await WaitUntil(() => (Level() > 0) == on && Volatile.Read(ref levelNotifications) > count);
                Assert.That(levelNotifications, Is.GreaterThan(count));
                Assert.That(observer.Light.State.Brightness, Is.EqualTo(original.Brightness));
            }
        }
        finally
        {
            transport.ReleaseRead?.TrySetResult(true);
            await observer.UpdateAsync(); RequireDemoControl(observer);
            await observer.TurnLightOffAsync(); await observer.UpdateAsync();
            Assert.That(observer.Light.State!.IsOn, Is.False, "The requested resting state of demo bulbs is off.");
            foreach (var line in transport.Trace) TestContext.Progress.WriteLine($"{role} {line}");
        }
    }
}

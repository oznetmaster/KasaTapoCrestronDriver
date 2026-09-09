using KasaTapoClient;
using KasaTapoClient.Internal;
using KasaTapoCrestronDriver;
using Newtonsoft.Json.Linq;
using Crestron.DeviceDrivers.SDK;
using System.Reflection;
using DeviceType = KasaTapoClient.DeviceType;

namespace KasaTapoCrestronDriver.Tests;

[TestClass]
public sealed class ParentDeviceTests
{
    private sealed class Transport : IDeviceTransport
    {
        public readonly List<string> Commands = new();
        public bool A;
        public bool B = true;
        public int Updates;
        public Task<string> SendAsync(string json, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Commands.Add(json);
            var command = JObject.Parse(json);
            if (command["system"]?["set_relay_state"] is JObject relay)
            {
                bool on = relay["state"]!.Value<int>() != 0;
                string? id = command["context"]?["child_ids"]?[0]?.Value<string>();
                if (id == "a") A = on;
                else if (id == "b") B = on;
                else throw new AssertFailedException("A strip command omitted the socket ID.");
                return Task.FromResult("{\"system\":{\"set_relay_state\":{\"err_code\":0}}}");
            }
            Interlocked.Increment(ref Updates);
            return Task.FromResult("{\"system\":{\"get_sysinfo\":{\"alias\":\"Strip\",\"model\":\"KP303\",\"deviceId\":\"strip\",\"relay_state\":1,\"children\":[{\"id\":\"a\",\"alias\":\"A\",\"state\":"+(A?1:0)+"},{\"id\":\"b\",\"alias\":\"B\",\"state\":"+(B?1:0)+"}]}}}");
        }
        public Task<string> SendManyAsync(IReadOnlyList<string> commands, CancellationToken token) =>
            Task.FromResult("{\"emeter\":{\"err_code\":-1}}");
    }

    private class Child(string id) : IParentDeviceChild
    {
        public string ChildId => id;
        public int Pushes;
        public int Offline;
        public bool? IsOn;
        public void ApplyPushedState(ChildDevice? child, KasaDevice parent)
        { Interlocked.Increment(ref Pushes); IsOn = parent.GetChild(id)?.IsOn; }
        public void ApplyConnectionState(bool online) { if (!online) Interlocked.Increment(ref Offline); }
    }

    // Isolate subscription policy from the SDK, which subscribes to entity events itself.
    private sealed class HubChild(string id) : Child(id), IKasaHubChildEntity
    {
        public bool HasEventSubscribers { get; private set; }
        public event Action? EventSubscribersChanged;
        public void Subscribe(bool value) { HasEventSubscribers = value; EventSubscribersChanged?.Invoke(); }
        public string DeviceName => ChildId;
        public string ModelName => "T310";
        public string SerialNumber => ChildId;
        public void UpdateDescriptor(ManagedLightDescriptor descriptor, DeviceConfiguration configuration) { }
        public void UpdateConfiguration(DeviceConfiguration configuration) { }
        public bool TryAttachConnectedDevice(KasaDevice device, string context) => false;
        public void SetConfigured(bool configured, string context) { }
        public Task SetConfiguredAsync(bool configured, string context, CancellationToken token) => Task.CompletedTask;
        public void ApplyRuntimeConfiguration(PlatformSharedConfigurationSnapshot previous, PlatformSharedConfigurationSnapshot current) { }
        public void Stop() { }
        public Task RefreshAsync(CancellationToken token) => Task.CompletedTask;
        public void NotifyChildPublished() { }
        public void NotifyChildRunning(string context) { }
        public void PublishStateSnapshot() { }
        public void Dispose() { }
    }

    private sealed class HubTransport : IDeviceTransport
    {
        public int IntervalA = 16;
        public int IntervalB = 32;
        public Task<string> SendAsync(string json, CancellationToken token) => Task.FromResult(
            new JObject { ["result"] = new JObject { ["responses"] = new JArray(
                new JObject { ["method"] = "get_device_info", ["result"] = new JObject { ["model"] = "H100", ["type"] = "SMART.TAPOHUB", ["device_id"] = "hub" } },
                new JObject { ["method"] = "component_nego", ["result"] = new JObject { ["component_list"] = new JArray(new JObject { ["id"] = "child_device", ["ver_code"] = 1 }) } },
                new JObject { ["method"] = "get_child_device_list", ["result"] = new JObject { ["child_device_list"] = new JArray(
                    new JObject { ["device_id"] = "a", ["model"] = "T310", ["category"] = "subg.trigger.temp-hmdt", ["report_interval"] = IntervalA, ["at_low_battery"] = false },
                    new JObject { ["device_id"] = "b", ["model"] = "T315", ["category"] = "subg.trigger.temp-hmdt", ["report_interval"] = IntervalB, ["at_low_battery"] = false }) } }) } }.ToString());
        public Task<string> SendManyAsync(IReadOnlyList<string> commands, CancellationToken token) => SendAsync("",token);
    }

    [TestMethod]
    public async Task HubRetainsShortestReportIntervalAndSubscriberDrivenPolling()
    {
        var transport = new HubTransport();
        var config = new DeviceConfiguration("127.0.0.1", connectionOptions: new DeviceConnectionOptions(
            connectionParameters: new DeviceConnectionParameters(DeviceFamilyKind.SmartTapoHub, DeviceEncryptionKind.Aes)));
        using var device = new KasaDevice(config,transport);
        var settings = Settings();
        int updates = 0;
        var sleeping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken delayToken = default;
        using var poller = new ManagedParentDevicePoller("hub",config,settings,null,"test",false,
            (_,_) => Task.FromResult(device), async (d,t) => { updates++; await d.UpdateAsync(t); },
            (_,t) => { delayToken = t; sleeping.TrySetResult(); return Task.Delay(Timeout.Infinite,t); });
        using var sensor = new HubChild("a");
        poller.RegisterChild(sensor);
        poller.RegisterChild(new Child("b"));
        Assert.AreEqual(TimeSpan.FromSeconds(30),poller.ComputeNextDelay());
        await poller.RefreshAsync(default);
        Assert.AreEqual(1,updates, "No automatic loop without event subscribers.");
        Assert.AreEqual(TimeSpan.FromSeconds(16),poller.ComputeNextDelay());
        transport.IntervalB = 8;
        await poller.RefreshAsync(default);
        Assert.AreEqual(TimeSpan.FromSeconds(8),poller.ComputeNextDelay());
        transport.IntervalB = 16;
        sensor.Subscribe(true);
        await sleeping.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(3,updates);
        Assert.AreEqual(TimeSpan.FromSeconds(16),poller.ComputeNextDelay());
        sensor.Subscribe(false);
        Assert.IsTrue(delayToken.IsCancellationRequested);
    }

    [TestMethod]
    public async Task RuntimeSettingsRestartOneLoop_AndDisposeCancelsPendingWork()
    {
        var transport = new Transport();
        using var device = new KasaDevice(new DeviceConfiguration("127.0.0.1"),transport);
        var settings = Settings();
        var firstSleep = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSleep = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken firstToken = default;
        int delays = 0;
        using var poller = new ManagedParentDevicePoller("strip",device.Configuration,settings,null,"test",true,
            (_,_) => Task.FromResult(device),(d,t) => d.UpdateAsync(t),
            (_,t) => { if (++delays == 1) { firstToken = t; firstSleep.TrySetResult(); } else secondSleep.TrySetResult(); return Task.Delay(Timeout.Infinite,t); });
        var a = new Child("a");
        poller.RegisterChild(a);
        await firstSleep.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var before = settings.Snapshot();
        settings.Update("","",TimeSpan.FromSeconds(8),true,TimeSpan.FromSeconds(12),TimeSpan.FromSeconds(30),false,"","","");
        poller.ApplyRuntimeConfiguration(before,settings.Snapshot());
        await secondSleep.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(firstToken.IsCancellationRequested);
        Assert.AreEqual(TimeSpan.FromSeconds(12),poller.ComputeNextDelay());
        Assert.AreEqual(2,transport.Updates);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = poller.ExecuteCommandAsync(async (_,t) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite,t); },default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        poller.Dispose();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        Assert.AreEqual(2,a.Pushes);
        Assert.IsFalse(device.IsDisposed);
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    private static PlatformSharedConfiguration Settings(bool enabled = true, int seconds = 5)
    {
        var settings = new PlatformSharedConfiguration();
        settings.Update("", "", TimeSpan.FromSeconds(8), enabled, TimeSpan.FromSeconds(seconds),
            TimeSpan.FromSeconds(30), false, "", "", "");
        return settings;
    }

    [TestMethod]
    public async Task ActualOutletAndLight_ShareStateAndSurviveSiblingRemoval()
    {
        var transport = new Transport();
        using var device = new KasaDevice(new DeviceConfiguration("127.0.0.1"), transport);
        await device.UpdateAsync();
        var settings = Settings();
        using var poller = new ManagedParentDevicePoller("test", device.Configuration, settings, null, "test", true,
            (_,_) => Task.FromResult(device), (d,t) => d.UpdateAsync(t),
            (_,t) => Task.Delay(Timeout.Infinite,t));
        using var logger = new DriverLogger("strip-test");
        var resources = new DriverImplementationResources { Logger = logger, InitLogger = logger.GetComponentLogger("test", "strip") };
        var outletDescriptor = new ManagedLightDescriptor("outlet-a", "127.0.0.1", DeviceType.Strip,
            "A", "KP303", "strip", ManagedLightKind.OnOff, childId: "a", childKind: ManagedChildKind.Outlet);
        var lightDescriptor = new ManagedLightDescriptor("light-b", "127.0.0.1", DeviceType.Strip,
            "B", "KP303", "strip", ManagedLightKind.OnOff, childId: "b", childKind: ManagedChildKind.Light);
        using var outlet = new KasaOutletEntity("outlet-a", outletDescriptor, device.Configuration, null,
            settings, resources, null!, "test", AppContext.BaseDirectory, poller);
        using var light = new KasaLightEntity("light-b", lightDescriptor, device.Configuration, null,
            settings, null, resources, null!, "test", poller);
        bool LightIsOn() => light.GetState().PropertyValues["light:isOn"].GetValue<bool>();
        await outlet.SetConfiguredAsync(true, "test", default);
        await light.SetConfiguredAsync(true, "test", default);
        await poller.RefreshAsync(default);
        Assert.IsFalse(outlet.OutletIsOn);
        Assert.IsTrue(LightIsOn());
        Assert.IsTrue(light.GetState().Definition.Properties.ContainsKey("light:isOn"));
        outlet.OutletOn();
        await Until(() => outlet.OutletIsOn && transport.A);
        typeof(KasaLightEntity).GetMethod("LightOff", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(light, null);
        await Until(() => !LightIsOn() && !transport.B);
        Assert.IsTrue(outlet.OutletIsOn);
        outlet.Dispose();
        Assert.IsFalse(device.IsDisposed);
        transport.B = true;
        await poller.RefreshAsync(default);
        Assert.IsTrue(LightIsOn());
        Assert.IsTrue(light.OnlineIndicatorIsOnline);
    }

    [TestMethod]
    public async Task Failure_NotifiesSiblingsAndRecoveryResetsBackoff()
    {
        var transport = new Transport();
        using var device = new KasaDevice(new DeviceConfiguration("127.0.0.1"), transport);
        bool fail = true;
        int connects = 0;
        var sleeping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var poller = new ManagedParentDevicePoller("test", device.Configuration, Settings(), null, "test", true,
            (_,_) => { connects++; return Task.FromResult(device); },
            (d,t) => fail ? Task.FromException(new IOException("simulated disconnect")) : d.UpdateAsync(t),
            (_,t) => { sleeping.TrySetResult(); return Task.Delay(Timeout.Infinite,t); });
        var a = new Child("a"); var b = new Child("b");
        poller.RegisterChild(a); poller.RegisterChild(b);
        await sleeping.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await poller.RefreshAsync(default);
        Assert.IsTrue(a.Offline > 0 && b.Offline > 0);
        Assert.AreEqual(TimeSpan.FromSeconds(20), poller.ComputeNextDelay());
        fail = false;
        await poller.RefreshAsync(default);
        Assert.AreEqual(3, connects);
        Assert.AreEqual(TimeSpan.FromSeconds(5), poller.ComputeNextDelay());
        Assert.IsFalse(a.IsOn); Assert.IsTrue(b.IsOn);
        Assert.IsFalse(device.IsDisposed);
    }

    [TestMethod]
    public async Task CommandsAndRefresh_SerializeAndQueuedCancellationDoesNotRun()
    {
        var transport = new Transport();
        using var device = new KasaDevice(new DeviceConfiguration("127.0.0.1"), transport);
        var sleeping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var poller = new ManagedParentDevicePoller("test", device.Configuration, Settings(), null, "test", true,
            (_,_) => Task.FromResult(device), (d,t) => d.UpdateAsync(t),
            (_,t) => { sleeping.TrySetResult(); return Task.Delay(Timeout.Infinite,t); });
        poller.RegisterChild(new Child("a"));
        await sleeping.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = poller.ExecuteCommandAsync(async (d,t) => {
            started.SetResult(); await release.Task.WaitAsync(t);
            await StripOutletControl.SetIsOnAsync(d,"a",true,t);
        }, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        int updatesBefore = transport.Updates;
        var refresh = poller.RefreshAsync(default);
        bool ran = false;
        using var cancellation = new CancellationTokenSource();
        var canceled = poller.ExecuteCommandAsync((_,_) => { ran = true; return Task.CompletedTask; }, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => canceled);
        Assert.IsFalse(ran);
        Assert.IsFalse(refresh.IsCompleted);
        Assert.AreEqual(updatesBefore, transport.Updates);
        release.SetResult();
        await Task.WhenAll(first,refresh).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(updatesBefore+2, transport.Updates);
    }

    [TestMethod]
    public async Task RemovingChildDuringRefresh_PreventsLateStateDelivery()
    {
        var transport = new Transport();
        using var device = new KasaDevice(new DeviceConfiguration("127.0.0.1"), transport);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sleeping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var poller = new ManagedParentDevicePoller("test", device.Configuration, Settings(), null, "test", true,
            (_,_) => Task.FromResult(device), async (d,t) => { started.TrySetResult(); await release.Task.WaitAsync(t); await d.UpdateAsync(t); },
            (_,t) => { sleeping.TrySetResult(); return Task.Delay(Timeout.Infinite,t); });
        var a = new Child("a"); var b = new Child("b");
        poller.RegisterChild(a); poller.RegisterChild(b);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        poller.UnregisterChild(a);
        release.SetResult();
        await sleeping.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0,a.Pushes);
        Assert.AreEqual(1,b.Pushes);
    }

    [TestMethod]
    public async Task DisabledPolling_InitializesOnceAndCommandsStillRefresh()
    {
        var transport = new Transport();
        using var device = new KasaDevice(new DeviceConfiguration("127.0.0.1"), transport);
        int delays = 0;
        var sleeping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var poller = new ManagedParentDevicePoller("test", device.Configuration, Settings(false), null, "test", true,
            (_,_) => Task.FromResult(device), (d,t) => d.UpdateAsync(t),
            (_,t) => { if (++delays < 3) return Task.CompletedTask; sleeping.TrySetResult(); return Task.Delay(Timeout.Infinite,t); });
        var a = new Child("a");
        poller.RegisterChild(a);
        await sleeping.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1,transport.Updates);
        await poller.ExecuteCommandAsync((d,t) => StripOutletControl.SetIsOnAsync(d,"a",true,t),default);
        Assert.IsTrue(a.IsOn);
        Assert.AreEqual(2,transport.Updates);
    }

    [TestMethod]
    public async Task StripCommandsAndState_AreSocketSpecific()
    {
        var transport = new Transport();
        using var device = new KasaDevice(new DeviceConfiguration("127.0.0.1"), transport);
        await device.UpdateAsync();
        Assert.IsFalse(StripOutletControl.ReadIsOn(device, "a"));
        Assert.IsTrue(StripOutletControl.ReadIsOn(device, "b"));
        await StripOutletControl.SetIsOnAsync(device, "a", true, default);
        Assert.IsTrue(transport.A);
        Assert.IsTrue(transport.B);
        await StripOutletControl.SetIsOnAsync(device, "b", false, default);
        Assert.IsTrue(transport.A);
        Assert.IsFalse(transport.B);
    }

    [TestMethod]
    public async Task OneLoop_FansOutAndOldRegistrationCannotRemoveReplacement()
    {
        var transport = new Transport();
        using var device = new KasaDevice(new DeviceConfiguration("127.0.0.1"), transport);
        var releaseUpdate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sleeping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int connects = 0;
        using var poller = new ManagedParentDevicePoller("test", device.Configuration, new PlatformSharedConfiguration(), null, "test", true,
            (_, _) => { Interlocked.Increment(ref connects); return Task.FromResult(device); },
            async (d,t) => { await releaseUpdate.Task.WaitAsync(t); await d.UpdateAsync(t); },
            (_,t) => { sleeping.TrySetResult(); return Task.Delay(Timeout.Infinite,t); });
        var oldA = new Child("a"); var newA = new Child("a"); var b = new Child("b");
        poller.RegisterChild(oldA);
        poller.RegisterChild(b);
        poller.RegisterChild(newA);
        poller.UnregisterChild(oldA);
        releaseUpdate.SetResult();
        await sleeping.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, transport.Updates);
        Assert.AreEqual(1, connects);
        Assert.AreEqual(0, oldA.Pushes);
        Assert.AreEqual(1, newA.Pushes);
        Assert.AreEqual(1, b.Pushes);
        await poller.ExecuteCommandAsync((d,t) => StripOutletControl.SetIsOnAsync(d,"a",true,t),default);
        Assert.AreEqual(2, transport.Updates, "The command's built-in refresh must not be followed by a duplicate coordinator refresh.");
        Assert.IsTrue(newA.IsOn);
        Assert.IsTrue(b.IsOn);
        poller.UnregisterChild(newA); poller.UnregisterChild(b);
        await poller.RefreshAsync(default);
        Assert.AreEqual(2, transport.Updates);
        Assert.IsFalse(device.IsDisposed, "A projection/poller must not dispose the shared device.");
    }
}

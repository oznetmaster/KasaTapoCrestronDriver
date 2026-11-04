// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
using Crestron.DeviceDrivers.EntityModel.Data;
using Crestron.DeviceDrivers.SDK;
using KasaTapoClient;
using KasaTapoClient.Internal;
using KasaTapoCrestronDriver;
using System.Text.Json;

namespace KasaTapoCrestronDriver.Tests;

#if NETFRAMEWORK
[Category("Processor")]
#endif
[TestFixture]
public sealed class LightRefreshPublicationTests
{
    sealed class Transport : IDeviceTransport
    {
        public int Reads;
        public bool On = true;
        public int Brightness = 60, Kelvin, Hue = 210, Saturation = 80;
        public Task<string> SendAsync(string json, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Reads);
            Assert.That(json, Does.Not.Contain("\"set_").And.Not.Contain("transition_light_state"), "A refresh must not write to the bulb.");
            return Task.FromResult(JsonSerializer.Serialize(new { system = new { get_sysinfo = new {
                alias = "Fixture bulb", model = "KL130", deviceId = "fixture-bulb", light_state = new {
                    on_off = On ? 1 : 0, brightness = Brightness, color_temp = Kelvin, hue = Hue, saturation = Saturation, mode = Kelvin > 0 ? "normal" : "hsv"
                } } } }));
        }
        public Task<string> SendManyAsync(IReadOnlyList<string> json, CancellationToken token) => Task.FromResult("{\"emeter\":{\"err_code\":-1}}");
    }

    [Test]
    public async Task PollingRefreshPublishesOnlyChangedStateAndConfirmedModeSwitches()
    {
        var transport = new Transport();
        using var device = new KasaDevice(new DeviceConfiguration("127.0.0.1"), transport);
        await device.UpdateAsync();
        var settings = new PlatformSharedConfiguration();
        settings.Update("", "", TimeSpan.FromSeconds(5), false, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), false, "", "", "");
        using var logger = new DriverLogger("refresh-test");
        var resources = new DriverImplementationResources { Logger = logger, InitLogger = logger.GetComponentLogger("test", "refresh") };
        var descriptor = new ManagedLightDescriptor("fixture-light", "127.0.0.1", device.DeviceType,
            "Fixture light", "KL130", "fixture-bulb", ManagedLightKind.Color);
        using var light = new KasaLightEntity("fixture-light", descriptor, device.Configuration, null, settings, null, resources, null!, "refresh-test");
        Assert.That(light.TryAttachConnectedDevice(device, "fixture"), Is.True);
        await light.SetConfiguredAsync(true, "fixture", default);
        var updates = new Dictionary<string, DriverEntityValue>();
        light.ValuesChanged += (_, e) => { foreach (var entry in e.Update.Changes) if (entry.Value.Value is {} value) updates[entry.Key] = value; };
        async Task Poll()
        {
            await light.RefreshAsync(default);
        }

        transport.Kelvin = 6100;
        await Poll();
        Assert.That(light.GetState().PropertyValues["lightEmulatedColorTemperature:level"].GetValue<long>(), Is.EqualTo(6100));
        Assert.That(updates.ContainsKey("lightEmulatedColorTemperature:level"), Is.True, "The host must hear the external colour-to-white change, not only GetState.");
        Assert.That(updates.ContainsKey("lightColor:hue"), Is.False, "Retained HSV must not override active white mode.");

        updates.Clear();
        transport.Kelvin = 0;
        await Poll();
        Assert.That(updates.ContainsKey("lightColor:hue"), Is.True, "Returning to the same stored hue still changes the active mode.");
        Assert.That(updates.ContainsKey("lightColor:saturation"), Is.True);
        Assert.That(updates.ContainsKey("lightEmulatedColorTemperature:level"), Is.False, "Do not assert white mode with inactive colour temperature.");


        updates.Clear();
        await Poll();
        Assert.That(updates, Is.Empty, "An unchanged poll must not replay level or colour notifications into Home programming.");
        Assert.That(updates.ContainsKey("lightEmulatedColorTemperature:level"), Is.False, "The repeated snapshot must keep the active colour mode.");

        transport.Kelvin = 6100;
        await Poll();
        Assert.That(updates["lightEmulatedColorTemperature:level"].GetValue<long>(), Is.EqualTo(6100), "Returning to the same retained white value must still assert the confirmed mode switch.");
        Assert.That(updates.ContainsKey("lightDimmer:level"), Is.False, "A mode change must not replay unchanged brightness.");

        transport.On = false;
        await Poll();
        Assert.That(updates["lightDimmer:level"].GetValue<double>(), Is.Zero, "External power-off must also notify the host.");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ScheduledPollingDoesNotRepublishUnchangedLightProperties(bool on)
    {
        var transport = new Transport { On = on };
        using var device = new KasaDevice(new DeviceConfiguration("127.0.0.1"), transport);
        await device.UpdateAsync();
        var settings = new PlatformSharedConfiguration();
        settings.Update("", "", TimeSpan.FromSeconds(5), false, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), false, "", "", "");
        using var logger = new DriverLogger("poll-loop-test");
        var resources = new DriverImplementationResources { Logger = logger, InitLogger = logger.GetComponentLogger("test", "poll-loop") };
        var descriptor = new ManagedLightDescriptor("fixture-light", "127.0.0.1", device.DeviceType, "Fixture light", "KL130", "fixture-bulb", ManagedLightKind.Color);
        using var light = new KasaLightEntity("fixture-light", descriptor, device.Configuration, null, settings, null, resources, null!, "poll-loop-test");
        Assert.That(light.TryAttachConnectedDevice(device, "fixture"), Is.True);
        await light.SetConfiguredAsync(true, "fixture", default);
        light.NotifyChildPublished();
        var updates = new System.Collections.Concurrent.ConcurrentQueue<string>();
        light.ValuesChanged += (_, e) => { foreach (var entry in e.Update.Changes) if (entry.Key.StartsWith("light", StringComparison.Ordinal)) updates.Enqueue(entry.Key); };
        int baseline = Volatile.Read(ref transport.Reads);
        var previous = settings.Snapshot();
        settings.Update("", "", TimeSpan.FromSeconds(5), true, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(30), false, "", "", "");
        light.ApplyRuntimeConfiguration(previous, settings.Snapshot());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (Volatile.Read(ref transport.Reads) < baseline + 3) await Task.Delay(10, timeout.Token);
        await Task.Delay(100, timeout.Token);
        light.Stop();
        Assert.That(updates, Is.Empty, "Actual scheduled polls must not replay native light events for unchanged values.");
    }

    [TestCase(false, 0)]
    [TestCase(true, 0)]
    [TestCase(false, 4000)]
    [TestCase(true, 4000)]
    public async Task InitializationSetsReadableBaselineWithoutChangeNotifications(bool on, int kelvin)
    {
        var transport = new Transport { On = on, Kelvin = kelvin };
        using var device = new KasaDevice(new DeviceConfiguration("127.0.0.1"), transport);
        await device.UpdateAsync();
        var settings = new PlatformSharedConfiguration();
        settings.Update("", "", TimeSpan.FromSeconds(5), false, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), false, "", "", "");
        using var logger = new DriverLogger("initial-baseline-test");
        var resources = new DriverImplementationResources { Logger = logger, InitLogger = logger.GetComponentLogger("test", "initial-baseline") };
        var descriptor = new ManagedLightDescriptor("fixture-light", "127.0.0.1", device.DeviceType, "Fixture light", "KL130", "fixture-bulb", ManagedLightKind.Color);
        using var light = new KasaLightEntity("fixture-light", descriptor, device.Configuration, null, settings, null, resources, null!, "initial-baseline-test");
        var updates = new List<string>();
        int definitions = 0;
        light.DefinitionChanged += (_, _) => definitions++;
        light.ValuesChanged += (_, e) => updates.AddRange(e.Update.Changes.Keys);
        Assert.That(light.TryAttachConnectedDevice(device, "fixture"), Is.True);
        await light.SetConfiguredAsync(true, "fixture", default);
        light.NotifyChildPublished();
        light.NotifyChildRunning("fixture");
        await light.RefreshAsync(default);
        Assert.That(updates.Where(id => id.StartsWith("light", StringComparison.Ordinal)), Is.Empty, "Construction, initialization, publication and Running must not announce startup light state as a change.");
        Assert.That(definitions, Is.EqualTo(1), "Availability must not replay the initial channel definition.");
        var baseline = light.GetState().PropertyValues;
        Assert.That(baseline["onlineIndicator:isOnline"].GetValue<bool>(), Is.True);
        Assert.That(baseline["readyIndicator:isReady"].GetValue<bool>(), Is.True);
        Assert.That(baseline["lightDimmer:level"].GetValue<double>(), Is.EqualTo(on ? .6 : 0));
        Assert.That(baseline["lightEmulatedColorTemperature:level"].GetValue<long>(), Is.EqualTo(kelvin > 0 ? kelvin : 2500));
        Assert.That(baseline["lightColor:hue"].GetValue<double>(), Is.EqualTo(kelvin > 0 ? 0 : 210d / 359d).Within(.001));
        transport.On = !on;
        await light.RefreshAsync(default);
        Assert.That(updates.Count(id => id == "lightDimmer:level"), Is.EqualTo(1), "A subsequent confirmed transition must still be published once.");
    }

    [TestCase(false, 0)]
    [TestCase(true, 0)]
    [TestCase(false, 4000)]
    [TestCase(true, 4000)]
    public async Task StartupAwaitsProcessorBaselineWithConfirmedChannelsBeforeReadiness(bool on, int kelvin)
    {
        var transport = new Transport { On = on, Kelvin = kelvin };
        using var device = new KasaDevice(new DeviceConfiguration("127.0.0.1"), transport);
        await device.UpdateAsync();
        var settings = new PlatformSharedConfiguration();
        settings.Update("", "", TimeSpan.FromSeconds(5), false, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), true, "fixture-host", "fixture-user", "fixture-password");
        using var logger = new DriverLogger("awaited-baseline-test");
        var resources = new DriverImplementationResources { Logger = logger, InitLogger = logger.GetComponentLogger("test", "awaited-baseline") };
        var descriptor = new ManagedLightDescriptor("fixture-light", "127.0.0.1", device.DeviceType, "Fixture light", "KL130", "fixture-bulb", ManagedLightKind.Color);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? actualName = null;
        ProcessorLightTuningMode actualMode = default;
        double actualLevel = -1, actualHue = -1, actualSaturation = -1;
        long actualKelvin = -1;
        Task Synchronize(string name, ProcessorLightTuningMode mode, double level, double hue, double saturation, long temperature, CancellationToken token)
        {
            actualName = name; actualMode = mode; actualLevel = level;
            actualHue = hue; actualSaturation = saturation; actualKelvin = temperature;
            entered.TrySetResult(true);
            return release.Task;
        }
        using var light = new KasaLightEntity("fixture-light", descriptor, device.Configuration, null, settings, Synchronize, resources, null!, "awaited-baseline-test");
        var updates = new List<string>();
        light.ValuesChanged += (_, e) => updates.AddRange(e.Update.Changes.Keys);
        Assert.That(light.TryAttachConnectedDevice(device, "fixture"), Is.True);
        Task initialize = light.SetConfiguredAsync(true, "fixture", default);
        try
        {
            Assert.That(await Task.WhenAny(entered.Task, Task.Delay(TimeSpan.FromSeconds(5))), Is.SameAs(entered.Task));
            Assert.That(initialize.IsCompleted, Is.False, "Readiness must wait for the processor baseline operation.");
            var pending = light.GetState().PropertyValues;
            Assert.That(pending["onlineIndicator:isOnline"].GetValue<bool>(), Is.False);
            Assert.That(pending["readyIndicator:isReady"].GetValue<bool>(), Is.False);
            Assert.That(actualName, Is.EqualTo("Fixture bulb"));
            Assert.That(actualMode, Is.EqualTo(kelvin > 0 ? ProcessorLightTuningMode.White : ProcessorLightTuningMode.Color));
            Assert.That(actualLevel, Is.EqualTo(on ? .6 : 0));
            Assert.That(actualHue, Is.EqualTo(kelvin > 0 ? 0 : 210d / 359d).Within(.001));
            Assert.That(actualSaturation, Is.EqualTo(kelvin > 0 ? 0 : .8));
            Assert.That(actualKelvin, Is.EqualTo(kelvin > 0 ? kelvin : 2500));
            Assert.That(updates, Is.Empty, "Correcting the processor baseline must not emit initial driver transitions.");
        }
        finally { release.TrySetResult(true); await initialize; }
        var ready = light.GetState().PropertyValues;
        Assert.That(ready["onlineIndicator:isOnline"].GetValue<bool>(), Is.True);
        Assert.That(ready["readyIndicator:isReady"].GetValue<bool>(), Is.True);
        Assert.That(ready["lightDimmer:level"].GetValue<double>(), Is.EqualTo(actualLevel));
        Assert.That(ready["lightColor:hue"].GetValue<double>(), Is.EqualTo(actualHue));
        Assert.That(ready["lightColor:saturation"].GetValue<double>(), Is.EqualTo(actualSaturation));
        Assert.That(ready["lightEmulatedColorTemperature:level"].GetValue<long>(), Is.EqualTo(actualKelvin));
        Assert.That(updates, Is.EquivalentTo(new[]{"onlineIndicator:isOnline", "readyIndicator:isReady"}));
    }
    [TestCase(-1d, 0)]
    [TestCase(0d, 0)]
    [TestCase(.01d, 1)]
    [TestCase(.6d, 60)]
    [TestCase(1d, 100)]
    [TestCase(2d, 100)]
    public void ProcessorBaselineUsesPercentageRatherThanRelativeUnits(double relative, int percentage)
    {
        Assert.That(LightTuningDecisions.ToProcessorLevelPercent(relative), Is.EqualTo(percentage));
    }

}

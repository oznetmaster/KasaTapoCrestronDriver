// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
using Crestron.DeviceDrivers.EntityModel.Data;
using Crestron.DeviceDrivers.SDK;
using KasaTapoClient;
using KasaTapoClient.Internal;
using KasaTapoCrestronDriver;
using System.Reflection;
using System.Text.Json.Nodes;

namespace KasaTapoCrestronDriver.Tests;

#if NETFRAMEWORK
[Category("Processor")]
#endif
[TestFixture]
public sealed class LightFeedbackTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private sealed class Transport : IDeviceTransport
    {
        public bool On = true, IgnoreCommands, FailCommands, FailReads, MissingState, OnOffOnly;
        public int Brightness = 40, Hue = 120, Saturation = 70, Kelvin;
        public readonly List<string> Writes = new();
        public TaskCompletionSource<bool>? CommandGate, ReadGate;
        public readonly TaskCompletionSource<bool> CommandStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> ReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> SendAsync(string json, CancellationToken token)
        {
            var command = JsonNode.Parse(json)!;
            var state = command["smartlife.iot.smartbulb.lightingservice"]?["transition_light_state"] ?? command["system"]?["set_relay_state"];
            if (state is not null)
            {
                lock (Writes) Writes.Add(json);
                CommandStarted.TrySetResult(true);
                if (CommandGate is not null) await CommandGate.Task.WaitForTestAsync(token);
                if (FailCommands) throw new IOException("Simulated command failure");
                if (!IgnoreCommands)
                {
                    if ((state["on_off"] ?? state["state"]) is {} on) On = on.GetValue<int>() != 0;
                    if (state["brightness"] is {} brightness) Brightness = brightness.GetValue<int>();
                    if (state["hue"] is {} hue) Hue = hue.GetValue<int>();
                    if (state["saturation"] is {} saturation) Saturation = saturation.GetValue<int>();
                    if (state["color_temp"] is {} kelvin) Kelvin = kelvin.GetValue<int>();
                }
                return OnOffOnly ? "{\"system\":{\"set_relay_state\":{\"err_code\":0}}}" : "{\"smartlife.iot.smartbulb.lightingservice\":{\"transition_light_state\":{\"err_code\":0}}}";
            }
            if (ReadGate is not null) { ReadStarted.TrySetResult(true); await ReadGate.Task.WaitForTestAsync(token); }
            if (FailReads) throw new IOException("Simulated readback failure");
            if (OnOffOnly) return new JsonObject { ["system"] = new JsonObject { ["get_sysinfo"] = new JsonObject { ["model"] = "HS200", ["deviceId"] = "fixture", ["relay_state"] = On ? 1 : 0 } } }.ToJsonString();
            var light = new JsonObject { ["on_off"] = On ? 1 : 0, ["brightness"] = Brightness, ["hue"] = Hue, ["saturation"] = Saturation, ["color_temp"] = Kelvin };
            if (MissingState) { light.Remove("on_off"); light.Remove("brightness"); }
            return new JsonObject { ["system"] = new JsonObject { ["get_sysinfo"] = new JsonObject {
                ["model"] = "KL130", ["alias"] = "Fixture", ["deviceId"] = "fixture", ["light_state"] = light } } }.ToJsonString();
        }
        public Task<string> SendManyAsync(IReadOnlyList<string> json, CancellationToken token) => Task.FromResult("{\"emeter\":{\"err_code\":-1}}");
    }

    private sealed class Fixture : IDisposable
    {
        public readonly Transport Transport = new();
        public readonly KasaDevice Device;
        public readonly KasaLightEntity Light;
        private readonly DriverLogger _logger = new("light-feedback");
        public Fixture(bool initiallyOn = true, bool onOffOnly = false)
        {
            Transport.On = initiallyOn; Transport.OnOffOnly = onOffOnly;
            Device = new KasaDevice(new DeviceConfiguration("127.0.0.1"), Transport);
            var settings = new PlatformSharedConfiguration();
            settings.Update("", "", TimeSpan.FromSeconds(5), false, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), false, "", "", "");
            var resources = new DriverImplementationResources { Logger = _logger, InitLogger = _logger.GetComponentLogger("test", "light") };
            Light = new KasaLightEntity("light", new ManagedLightDescriptor("light", "127.0.0.1", onOffOnly ? KasaTapoClient.DeviceType.Plug : KasaTapoClient.DeviceType.Bulb,
                "Fixture", "KL130", "fixture", onOffOnly ? ManagedLightKind.OnOff : ManagedLightKind.Color), Device.Configuration, null, settings, null, resources, null!, "light-feedback");
        }
        public async Task Initialize()
        {
            await Device.UpdateAsync();
            Light.TryAttachConnectedDevice(Device, "fixture");
            await Light.SetConfiguredAsync(true, "fixture", default);
            typeof(KasaLightEntity).GetProperty("_startupDimmerReplayGuardUntilUtc", Private)!.SetValue(Light, null);
        }
        public double Level => Light.GetState().PropertyValues["lightDimmer:level"].GetValue<double>();
        public void Command(string id, string parameter, double value) => Light.ExecuteCommand(id,
            new Dictionary<string, DriverEntityValue> { [parameter] = id.Contains("ColorTemperature") ? new DriverEntityValue((long)value) : new DriverEntityValue(value) }, result => Assert.That(result.Failed, Is.False));
        public Task Attempt(Func<KasaDevice, CancellationToken, Task> action, bool refresh = true) =>
            (Task)typeof(KasaLightEntity).GetMethod("ExecuteDeviceCommandAttemptAsync", Private)!.Invoke(Light,
                new object?[] { action, refresh, false, null, true, CancellationToken.None })!;
        public void Dispose() { Transport.CommandGate?.TrySetResult(true); Transport.ReadGate?.TrySetResult(true); Light.Dispose(); Device.Dispose(); _logger.Dispose(); }
    }

    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task OnOffLightInitializationSetsBaselineWithoutEvents(bool on)
    {
        using var f = new Fixture(on, onOffOnly: true);
        var updates = new List<string>();
        f.Light.ValuesChanged += (_, e) => updates.AddRange(e.Update.Changes.Keys);
        await f.Initialize();
        f.Light.NotifyChildPublished(); f.Light.NotifyChildRunning("fixture");
        await f.Light.RefreshAsync(default);
        Assert.That(f.Light.GetState().PropertyValues["light:isOn"].GetValue<bool>(), Is.EqualTo(on));
        Assert.That(updates, Is.EquivalentTo(new[]{"onlineIndicator:isOnline", "readyIndicator:isReady"}));
        f.Transport.On = !on;
        await f.Light.RefreshAsync(default);
        Assert.That(updates.Count(id => id == "light:isOn"), Is.EqualTo(1));
    }
    [TestCase(true, 0d, 0d)]
    [TestCase(false, 1d, .4d)]
    [TestCase(true, .75d, .75d)]
    public async Task CommandIntentAndAcknowledgementDoNotPublishBeforeReadback(bool on, double target, double expected)
    {
        using var f = new Fixture(on); await f.Initialize();
        double baseline = f.Level;
        f.Transport.CommandGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Transport.ReadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var levels = new List<double>();
        f.Light.ValuesChanged += (_, e) => { if (e.Update.Changes.TryGetValue("lightDimmer:level", out var update) && update.Value is {} value) lock (levels) levels.Add(value.GetValue<double>()); };
        f.Command("lightDimmer:setLevel", "level", target);
        await f.Transport.CommandStarted.Task.WaitForTestAsync(TimeSpan.FromSeconds(5));
        Assert.That(f.Level, Is.EqualTo(baseline));
        lock (levels) Assert.That(levels, Is.Empty);
        f.Transport.CommandGate.TrySetResult(true);
        await f.Transport.ReadStarted.Task.WaitForTestAsync(TimeSpan.FromSeconds(5));
        Assert.That(f.Level, Is.EqualTo(baseline), "An acknowledgement and the client's mutated cache are not confirmation.");
        lock (levels) Assert.That(levels, Is.Empty);
        f.Transport.ReadGate.TrySetResult(true);
        await Until(() => Math.Abs(f.Level - expected) < .001);
        lock (levels) Assert.That(levels, Does.Contain(expected));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SimpleLightPowerWaitsForReadback(bool on)
    {
        using var f = new Fixture(on, onOffOnly: true); await f.Initialize();
        f.Transport.ReadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Light.ExecuteCommand(on ? "light:off" : "light:on", new Dictionary<string, DriverEntityValue>(),
            result => Assert.That(result.Failed, Is.False));
        await f.Transport.ReadStarted.Task.WaitForTestAsync(TimeSpan.FromSeconds(5));
        Assert.That(f.Light.GetState().PropertyValues["light:isOn"].GetValue<bool>(), Is.EqualTo(on));
        f.Transport.ReadGate.TrySetResult(true);
        await Until(() => f.Light.GetState().PropertyValues["light:isOn"].GetValue<bool>() != on);
    }

    [Test]
    public async Task FailedCommandRetainsConfirmedState()
    {
        using var f = new Fixture(); await f.Initialize();
        f.Transport.FailCommands = true;
        await Assert.ThrowsAsync<IOException>(async () => await f.Attempt((d, t) => d.TurnLightOffAsync(t)));
        Assert.That(f.Level, Is.EqualTo(.4d));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task IgnoredCommandsAndFailedReadbackRetainConfirmedState(bool failRead)
    {
        using var f = new Fixture(); await f.Initialize();
        f.Transport.IgnoreCommands = !failRead;
        f.Transport.FailReads = failRead;
        Task operation = f.Attempt((d, t) => d.TurnLightOffAsync(t));
        if (failRead) await Assert.ThrowsAsync<IOException>(async () => await operation);
        else await operation;
        Assert.That(f.Level, Is.EqualTo(.4d));
    }

    [Test]
    public async Task IntermediateSliderAcknowledgementDoesNotApplyClientCache()
    {
        using var f = new Fixture(); await f.Initialize();
        await f.Attempt((d, t) => d.SetBrightnessAsync(90, t), refresh: false);
        Assert.That(f.Level, Is.EqualTo(.4d));
        await f.Light.RefreshAsync(default);
        Assert.That(f.Level, Is.EqualTo(.9d));
    }

    [Test]
    public async Task MissingPowerAndBrightnessAreNotAnOffReading()
    {
        using var f = new Fixture(); await f.Initialize();
        f.Transport.MissingState = true;
        await f.Light.RefreshAsync(default);
        Assert.That(f.Level, Is.EqualTo(.4d));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ColourRequestsDoNotMutateConfirmedProperties(bool temperature)
    {
        using var f = new Fixture(); await f.Initialize();
        f.Transport.CommandGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        double originalHue = f.Light.GetState().PropertyValues["lightColor:hue"].GetValue<double>();
        long originalKelvin = f.Light.GetState().PropertyValues["lightEmulatedColorTemperature:level"].GetValue<long>();
        if (temperature) f.Command("lightEmulatedColorTemperature:setLevel", "level", 5000);
        else { f.Command("lightColor:setHue", "level", .8); f.Command("lightColor:setSaturation", "level", .6); }
        await f.Transport.CommandStarted.Task.WaitForTestAsync(TimeSpan.FromSeconds(5));
        Assert.That(f.Light.GetState().PropertyValues["lightColor:hue"].GetValue<double>(), Is.EqualTo(originalHue));
        Assert.That(f.Light.GetState().PropertyValues["lightEmulatedColorTemperature:level"].GetValue<long>(), Is.EqualTo(originalKelvin));
        f.Transport.CommandGate.TrySetResult(true);
        if (temperature) await Until(() => f.Light.GetState().PropertyValues["lightEmulatedColorTemperature:level"].GetValue<long>() == 5000);
        else await Until(() => Math.Abs(f.Light.GetState().PropertyValues["lightColor:hue"].GetValue<double>() - .8) < .003);
        if (!temperature) Assert.That(f.Transport.Saturation, Is.EqualTo(60), "Paired commands must retain the requested hue and saturation.");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PairedBrightnessAndColourRequestsRetainBothDesiredChannels(bool temperature)
    {
        using var f = new Fixture(); await f.Initialize();
        if (temperature) {
            f.Command("lightDimmer:setLevel", "level", .8);
            f.Command("lightEmulatedColorTemperature:setLevel", "level", 5000);
        } else {
            f.Command("lightColor:setHue", "level", .8);
            f.Command("lightDimmer:setLevel", "level", .8);
        }
        await Until(() => Math.Abs(f.Level - .8) < .001);
        if (temperature) Assert.That(f.Transport.Kelvin, Is.EqualTo(5000));
        else Assert.That(f.Transport.Hue, Is.EqualTo(287));
    }

    [Test]
    public async Task PassiveOffStateColourReplaysCannotChangeConfirmedModeOrValues()
    {
        using var f = new Fixture(false); await f.Initialize();
        var before = f.Light.GetState().PropertyValues["lightColor:hue"].GetValue<double>();
        f.Command("lightColor:setHue", "level", .1);
        f.Command("lightColor:setSaturation", "level", .1);
        f.Command("lightEmulatedColorTemperature:setLevel", "level", 5000);
        Assert.That(f.Light.GetState().PropertyValues["lightColor:hue"].GetValue<double>(), Is.EqualTo(before));
        Assert.That(f.Transport.Writes, Is.Empty);
    }
}

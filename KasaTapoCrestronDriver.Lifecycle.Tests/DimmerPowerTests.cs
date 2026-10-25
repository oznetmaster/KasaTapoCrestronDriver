// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using Crestron.DeviceDrivers.EntityModel.Data;
using Crestron.DeviceDrivers.SDK;
using KasaTapoClient;
using KasaTapoClient.Internal;
using KasaTapoCrestronDriver;
using System.Reflection;
using System.Text.Json.Nodes;
using DeviceType = KasaTapoClient.DeviceType;

namespace KasaTapoCrestronDriver.Tests;

#if NETFRAMEWORK
[Category ("Processor")]
#endif
[TestFixture]
public sealed class DimmerPowerTests
    {
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    // Captured component lists: python-kasa/tests/fixtures/smart/
    // HS220(US)_3.26_1.0.1.json and KS225(US)_1.0_1.1.1.json.
    // State/transport behavior below is synthetic, not an HS220 firmware 1.1.1 capture.
    private const string HS220_COMPONENTS = """[{"id":"device","ver_code":2},{"id":"firmware","ver_code":2},{"id":"quick_setup","ver_code":3},{"id":"inherit","ver_code":1},{"id":"time","ver_code":1},{"id":"wireless","ver_code":1},{"id":"schedule","ver_code":2},{"id":"countdown","ver_code":2},{"id":"antitheft","ver_code":1},{"id":"account","ver_code":1},{"id":"synchronize","ver_code":1},{"id":"sunrise_sunset","ver_code":1},{"id":"led","ver_code":3},{"id":"cloud_connect","ver_code":1},{"id":"iot_cloud","ver_code":1},{"id":"device_local_time","ver_code":1},{"id":"default_states","ver_code":1},{"id":"brightness","ver_code":1},{"id":"preset","ver_code":1},{"id":"on_off_gradually","ver_code":2},{"id":"dimmer_calibration","ver_code":1},{"id":"overheat_protection","ver_code":1},{"id":"smart_switch","ver_code":1},{"id":"dimmer_custom_action","ver_code":1}]""";
    private const string KS225_COMPONENTS = """[{"id":"device","ver_code":2},{"id":"firmware","ver_code":2},{"id":"quick_setup","ver_code":3},{"id":"inherit","ver_code":1},{"id":"time","ver_code":1},{"id":"wireless","ver_code":1},{"id":"schedule","ver_code":2},{"id":"countdown","ver_code":2},{"id":"antitheft","ver_code":1},{"id":"account","ver_code":1},{"id":"synchronize","ver_code":1},{"id":"sunrise_sunset","ver_code":1},{"id":"led","ver_code":1},{"id":"cloud_connect","ver_code":1},{"id":"iot_cloud","ver_code":1},{"id":"device_local_time","ver_code":1},{"id":"default_states","ver_code":1},{"id":"brightness","ver_code":1},{"id":"preset","ver_code":1},{"id":"on_off_gradually","ver_code":2},{"id":"dimmer_calibration","ver_code":1},{"id":"overheat_protection","ver_code":1},{"id":"matter","ver_code":2}]""";
    private sealed class Transport (DeviceType type, string smartModel = "HS220") : IDeviceTransport
        {
        private readonly object _gate = new ();
        private readonly List<string> _writes = new ();
        public bool On = true;
        public int Brightness = 37;
        public string[] Writes { get { lock (_gate) return _writes.ToArray (); } }

        public Task<string> SendAsync (string json, CancellationToken token)
            {
            token.ThrowIfCancellationRequested ();
            lock (_gate)
                {
                var command = JsonNode.Parse (json)!;
                if (type == DeviceType.Dimmer)
                    {
                    if (command["method"]?.GetValue<string> () == "set_device_info")
                        {
                        var parameters = command["params"]!;
                        _writes.Add (parameters.ToJsonString ());
                        if (parameters["device_on"] is {} on) On = on.GetValue<bool> ();
                        if (parameters["brightness"] is {} brightness) Brightness = brightness.GetValue<int> ();
                        }
                    return Task.FromResult ("""
                        {"result":{"responses":[
                          {"method":"get_device_info","result":{"model":"@MODEL@","type":"SMART.KASASWITCH","device_id":"fixture-dimmer","nickname":"Rml4dHVyZQ==","device_on":@ON@,"brightness":@BRIGHTNESS@,"hw_ver":"3.26","fw_ver":"1.0.1"}},
                          {"method":"component_nego","result":{"component_list":@COMPONENTS@}},
                          {"method":"set_device_info","result":{}}
                        ]}}
                        """.Replace ("@MODEL@", smartModel).Replace ("@ON@", On.ToString ().ToLowerInvariant ()).Replace ("@BRIGHTNESS@", Brightness.ToString (System.Globalization.CultureInfo.InvariantCulture))
                        .Replace ("@COMPONENTS@", smartModel == "KS225" ? KS225_COMPONENTS : HS220_COMPONENTS));
                    }

                var relay = command["system"]?["set_relay_state"];
                var service = type == DeviceType.LightStrip ? "smartlife.iot.lightStrip" : "smartlife.iot.smartbulb.lightingservice";
                var light = command[service]?[type == DeviceType.LightStrip ? "set_light_state" : "transition_light_state"];
                if (relay is not null || light is not null)
                    {
                    _writes.Add (json);
                    if (relay is not null) On = relay["state"]!.GetValue<int> () != 0;
                    if (light?["on_off"] is {} on) On = on.GetValue<int> () != 0;
                    if (light?["brightness"] is {} brightness) Brightness = brightness.GetValue<int> ();
                    return Task.FromResult (relay is not null
                        ? "{\"system\":{\"set_relay_state\":{\"err_code\":0}}}"
                        : new JsonObject { [service] = new JsonObject { [type == DeviceType.LightStrip ? "set_light_state" : "transition_light_state"] = new JsonObject { ["err_code"] = 0 } } }.ToJsonString ());
                    }
                string rawType = type == DeviceType.Plug ? "IOT.SMARTPLUGSWITCH" : type == DeviceType.LightStrip ? "IOT.LIGHTSTRIP" : "IOT.SMARTBULB";
                string model = type == DeviceType.Plug ? "HS100" : type == DeviceType.LightStrip ? "KL430" : "KL130";
                string state = type == DeviceType.Plug ? "" : ", \"light_state\":" + new JsonObject { ["on_off"] = On ? 1 : 0, ["brightness"] = Brightness }.ToJsonString ();
                return Task.FromResult ("""{"system":{"get_sysinfo":{"alias":"Fixture","model":"@MODEL@","type":"@TYPE@","deviceId":"fixture","relay_state":@RELAY@@STATE@}}}""".Replace ("@MODEL@", model).Replace ("@TYPE@", rawType).Replace ("@RELAY@", On ? "1" : "0").Replace ("@STATE@", state));
                }
            }

        public Task<string> SendManyAsync (IReadOnlyList<string> commands, CancellationToken token) =>
            Task.FromResult ("{\"emeter\":{\"err_code\":-1}}");
        }

    private static KasaDevice CreateDevice (DeviceType type, Transport transport) => new (
        new DeviceConfiguration ("127.0.0.1", connectionOptions: type == DeviceType.Dimmer
            ? new DeviceConnectionOptions (connectionParameters: new DeviceConnectionParameters (DeviceFamilyKind.SmartKasaSwitch, DeviceEncryptionKind.Aes))
            : null), transport);

    private static KasaLightEntity CreateEntity (KasaDevice device, DriverLogger logger)
        {
        var settings = new PlatformSharedConfiguration ();
        settings.Update ("", "", TimeSpan.FromSeconds (5), false, TimeSpan.FromSeconds (15), TimeSpan.FromSeconds (30), false, "", "", "");
        var resources = new DriverImplementationResources { Logger = logger, InitLogger = logger.GetComponentLogger ("test", "dimmer") };
        var descriptor = new ManagedLightDescriptor ("fixture-light", "127.0.0.1", device.DeviceType,
            "Fixture", device.SystemInfo!.Model!, "fixture", KasaLightEntity.InferManagedLightKind (device, device.DeviceType));
        var entity = new KasaLightEntity ("fixture-light", descriptor, device.Configuration, null, settings, null, resources, null!, "dimmer-test");
        Assert.That (entity.TryAttachConnectedDevice (device, "fixture"), Is.True);
        return entity;
        }

    private static Task InvokeAsync (KasaLightEntity entity, string method, params object[] args) =>
        (Task)typeof (KasaLightEntity).GetMethod (method, PrivateInstance)!.Invoke (entity, args)!;

    [TestCase ("HS220")]
    [TestCase ("KS225")]
    public async Task SmartDimmerPower_UsesDeviceOnWithoutChangingBrightness (string model)
        {
        var transport = new Transport (DeviceType.Dimmer, model);
        using var device = CreateDevice (DeviceType.Dimmer, transport);
        await device.UpdateAsync ();
        Assert.That (device.DeviceType, Is.EqualTo (DeviceType.Dimmer));
        using var logger = new DriverLogger ("dimmer-power-test");
        using var entity = CreateEntity (device, logger);

        await InvokeAsync (entity, "ExecutePowerAsync", device, false, CancellationToken.None);
        Assert.That (device.IsOn, Is.False);
        await InvokeAsync (entity, "ExecutePowerAsync", device, true, CancellationToken.None);
        Assert.That (device.IsOn, Is.True);
        Assert.That (transport.Brightness, Is.EqualTo (37));
        Assert.That (transport.Writes, Is.EqualTo (new[] { "{\"device_on\":false}", "{\"device_on\":true}" }));
        }

    [TestCase ("HS220")]
    [TestCase ("KS225")]
    public async Task SmartDimmerHomeLevels_ZeroTurnsOff_OneRestores_OneWhileOnSetsFullBrightness (string model)
        {
        var transport = new Transport (DeviceType.Dimmer, model);
        using var device = CreateDevice (DeviceType.Dimmer, transport);
        await device.UpdateAsync ();
        using var logger = new DriverLogger ("dimmer-level-test");
        using var entity = CreateEntity (device, logger);
        await entity.SetConfiguredAsync (true, "fixture", default);
        // Exercise steady-state user commands, outside the existing post-reload replay guard.
        typeof (KasaLightEntity).GetProperty ("_startupDimmerReplayGuardUntilUtc", PrivateInstance)!.SetValue (entity, null);
        Assert.That (entity.GetState ().Definition.Commands.ContainsKey ("lightDimmer:setLevel"), Is.True);
        Assert.That (entity.GetState ().Definition.Commands.ContainsKey ("light:on"), Is.False);

        async Task SetLevel (double level, bool on, int brightness)
            {
            int previousWrites = transport.Writes.Length;
            entity.ExecuteCommand ("lightDimmer:setLevel", new Dictionary<string, DriverEntityValue> { ["level"] = new (level) },
                result => Assert.That (result.Failed, Is.False));
            var until = DateTime.UtcNow.AddSeconds (5);
            while (DateTime.UtcNow < until)
                {
                double published = entity.GetState ().PropertyValues["lightDimmer:level"].GetValue<double> ();
                if (transport.Writes.Length > previousWrites && device.IsOn == on
                    && device.LightState?.Brightness == brightness && Math.Abs (published - (on ? brightness / 100d : 0d)) < .001)
                    return;
                await Task.Delay (20);
                }
            Assert.Fail ($"Level {level} did not settle: power={device.IsOn}, brightness={device.LightState?.Brightness}.");
            }

        await SetLevel (0, false, 37);
        await SetLevel (1, true, 37);
        await SetLevel (1, true, 100);
        Assert.That (transport.Writes.Take (2), Is.EqualTo (new[] { "{\"device_on\":false}", "{\"device_on\":true}" }));
        Assert.That (transport.Writes.Skip (2).All (w => w == "{\"brightness\":100}"), Is.True);
        }

    [TestCase ("HS220", false)]
    [TestCase ("HS220", true)]
    [TestCase ("KS225", false)]
    [TestCase ("KS225", true)]
    public async Task SmartDimmerBrightnessPath_UsesDimmerPowerWhenNeeded (string model, bool initiallyOn)
        {
        var transport = new Transport (DeviceType.Dimmer, model) { On = initiallyOn };
        using var device = CreateDevice (DeviceType.Dimmer, transport);
        await device.UpdateAsync ();
        using var logger = new DriverLogger ("dimmer-slider-test");
        using var entity = CreateEntity (device, logger);
        await InvokeAsync (entity, "ApplyBrightnessCommandAsync", device, initiallyOn ? 0d : .4d, CancellationToken.None);
        Assert.That (device.IsOn, Is.EqualTo (!initiallyOn));
        Assert.That (transport.Brightness, Is.EqualTo (37), "Power routing must preserve existing retained-brightness behavior.");
        Assert.That (transport.Writes.Single (), Is.EqualTo (initiallyOn ? "{\"device_on\":false}" : "{\"device_on\":true}"));
        }

    [TestCase (DeviceType.Bulb, "smartlife.iot.smartbulb.lightingservice", "transition_light_state")]
    [TestCase (DeviceType.LightStrip, "smartlife.iot.lightStrip", "set_light_state")]
    [TestCase (DeviceType.Plug, "system", "set_relay_state")]
    public async Task ExistingDevicePower_PreservesItsWireCommands (DeviceType type, string service, string method)
        {
        var transport = new Transport (type);
        using var device = CreateDevice (type, transport);
        await device.UpdateAsync ();
        Assert.That (device.DeviceType, Is.EqualTo (type));
        using var logger = new DriverLogger ("existing-power-test");
        using var entity = CreateEntity (device, logger);
        await InvokeAsync (entity, "ExecutePowerAsync", device, false, CancellationToken.None);
        Assert.That (device.IsOn, Is.False);
        await InvokeAsync (entity, "ExecutePowerAsync", device, true, CancellationToken.None);
        Assert.That (device.IsOn, Is.True);
        Assert.That (transport.Writes.Length, Is.EqualTo (2));
        for (int i = 0; i < 2; i++)
            {
            var parameters = JsonNode.Parse (transport.Writes[i])![service]![method]!;
            Assert.That (parameters[type == DeviceType.Plug ? "state" : "on_off"]!.GetValue<int> (), Is.EqualTo (i));
            Assert.That (parameters["brightness"], Is.Null);
            if (type == DeviceType.Bulb)
                Assert.That (parameters.AsObject ().Select (p => p.Key), Is.EquivalentTo (new[] { "on_off", "ignore_default", "transition_period" }));
            else
                Assert.That (parameters.AsObject ().Count, Is.EqualTo (1));
            }
        Assert.That (transport.Brightness, Is.EqualTo (37));
        }
    }

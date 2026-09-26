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
        public bool On = true;
        public int Brightness = 60, Kelvin, Hue = 210, Saturation = 80;
        public Task<string> SendAsync(string json, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.That(json, Does.Not.Contain("\"set_").And.Not.Contain("transition_light_state"), "A refresh must not write to the bulb.");
            return Task.FromResult(JsonSerializer.Serialize(new { system = new { get_sysinfo = new {
                alias = "Fixture bulb", model = "KL130", deviceId = "fixture-bulb", light_state = new {
                    on_off = On ? 1 : 0, brightness = Brightness, color_temp = Kelvin, hue = Hue, saturation = Saturation, mode = Kelvin > 0 ? "normal" : "hsv"
                } } } }));
        }
        public Task<string> SendManyAsync(IReadOnlyList<string> json, CancellationToken token) => Task.FromResult("{\"emeter\":{\"err_code\":-1}}");
    }

    [Test]
    public async Task ExternalWhiteAndColorChangesNotifyTheHostEvenWhenReturningToRetainedValues()
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

        transport.Kelvin = 6100;
        await light.RefreshAsync(default);
        Assert.That(light.GetState().PropertyValues["lightEmulatedColorTemperature:level"].GetValue<long>(), Is.EqualTo(6100));
        Assert.That(updates.ContainsKey("lightEmulatedColorTemperature:level"), Is.True, "The host must hear the external colour-to-white change, not only GetState.");
        Assert.That(updates.ContainsKey("lightColor:hue"), Is.False, "Retained HSV must not override active white mode.");

        updates.Clear();
        transport.Kelvin = 0;
        await light.RefreshAsync(default);
        Assert.That(updates.ContainsKey("lightColor:hue"), Is.True, "Returning to the same stored hue still changes the active mode.");
        Assert.That(updates.ContainsKey("lightColor:saturation"), Is.True);
        Assert.That(updates.ContainsKey("lightEmulatedColorTemperature:level"), Is.False, "Do not assert white mode with inactive colour temperature.");

        updates.Clear();
        await light.RefreshAsync(default);
        Assert.That(updates, Is.Empty, "An unchanged poll must not republish colour controls.");

        transport.On = false;
        await light.RefreshAsync(default);
        Assert.That(updates["lightDimmer:level"].GetValue<double>(), Is.Zero, "External power-off must also notify the host.");
    }
}

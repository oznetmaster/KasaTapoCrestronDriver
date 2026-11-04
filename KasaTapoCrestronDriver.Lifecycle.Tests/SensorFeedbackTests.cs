// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
using Crestron.DeviceDrivers.EntityModel.Data;
using Crestron.DeviceDrivers.SDK;
using KasaTapoClient;
using KasaTapoClient.Internal;
using KasaTapoCrestronDriver;
using System.Text.Json.Nodes;

namespace KasaTapoCrestronDriver.Tests;

#if NETFRAMEWORK
[Category("Processor")]
#endif
[TestFixture]
public sealed class SensorFeedbackTests
{
    private sealed class Transport : IDeviceTransport
    {
        public JsonObject Child = new() { ["device_id"] = "sensor", ["model"] = "T315", ["category"] = "subg.sensor" };
        public Task<string> SendAsync(string json, CancellationToken token) => Task.FromResult(new JsonObject {
            ["result"] = new JsonObject { ["responses"] = new JsonArray(
                new JsonObject { ["method"] = "get_device_info", ["result"] = new JsonObject { ["model"] = "H100", ["type"] = "SMART.TAPOHUB", ["device_id"] = "hub" } },
                new JsonObject { ["method"] = "component_nego", ["result"] = new JsonObject { ["component_list"] = new JsonArray(new JsonObject { ["id"] = "child_device", ["ver_code"] = 1 }) } },
                new JsonObject { ["method"] = "get_child_device_list", ["result"] = new JsonObject { ["child_device_list"] = new JsonArray(Child.DeepClone()) } }) }
        }.ToJsonString());
        public Task<string> SendManyAsync(IReadOnlyList<string> json, CancellationToken token) => SendAsync("", token);
    }

    [TestCase("battery", false)]
    [TestCase("battery", true)]
    [TestCase("contact", false)]
    [TestCase("contact", true)]
    [TestCase("motion", false)]
    [TestCase("motion", true)]
    [TestCase("leak", false)]
    [TestCase("leak", true)]
    [TestCase("temperature", false)]
    [TestCase("temperature", true)]
    [TestCase("humidity", false)]
    [TestCase("humidity", true)]
    public async Task FirstReportedStateAndTransitionsPublishCompleteStateBeforeMatchingEvent(string signal, bool initial)
    {
        var transport = new Transport();
        var config = new DeviceConfiguration("127.0.0.1", connectionOptions: new DeviceConnectionOptions(
            connectionParameters: new DeviceConnectionParameters(DeviceFamilyKind.SmartTapoHub, DeviceEncryptionKind.Aes)));
        using var device = new KasaDevice(config, transport);
        var category = signal switch {
            "contact" => HubChildCategory.Contact, "motion" => HubChildCategory.Motion,
            "leak" => HubChildCategory.WaterLeak, _ => HubChildCategory.TemperatureHumidity };
        var descriptor = new ManagedLightDescriptor("sensor", config.Host, KasaTapoClient.DeviceType.Hub,
            "Fixture sensor", "Fixture", "sensor", ManagedLightKind.Unknown, childId: "sensor",
            childKind: ManagedChildKind.Sensor, hubChildCategory: category);
        using var logger = new DriverLogger("sensor-feedback");
        var resources = new DriverImplementationResources { Logger = logger, InitLogger = logger.GetComponentLogger("test", "sensor") };
        using var sensor = new KasaSensorEntity("sensor", descriptor, config, null, new PlatformSharedConfiguration(), resources, null!, "sensor-feedback", DriverTestPaths.DataDirectory);
        sensor.NotifyChildPublished();
        var published = new Dictionary<string, DriverEntityValue>();
        sensor.ValuesChanged += (_, e) => { foreach (var item in e.Update.Changes) if (item.Value.Value is {} value) published[item.Key] = value; };
        string property = signal switch {
            "battery" => "batteryIsLow", "contact" => "contactIsOpen", "motion" => "motionDetected",
            "leak" => "leakDetected", "temperature" => "temperatureWarningIsActive", _ => "humidityWarningIsActive" };
        bool expected = initial;
        int events = 0;
        void OnEvent(object? sender, EventArgs args)
        {
            events++;
            Assert.That(sensor.GetState().PropertyValues[property].GetValue<bool>(), Is.EqualTo(expected));
            Assert.That(published[property].GetValue<bool>(), Is.EqualTo(expected), "Property notification must precede the event.");
            Assert.That(published["sensorStatus"].GetValue<string>(), Is.EqualTo(sensor.SensorStatus));
            Assert.That(published["sensorIcon"].GetValue<string>(), Is.EqualTo(sensor.SensorIcon));
            Assert.That(sensor.OnlineIndicatorIsOnline && sensor.ReadyIndicatorIsReady, Is.True);
        }
        switch (signal) {
            case "battery": sensor.BatteryLowEvent += OnEvent; sensor.BatteryNormal += OnEvent; break;
            case "contact": sensor.ContactOpened += OnEvent; sensor.ContactClosed += OnEvent; break;
            case "motion": sensor.MotionDetectedEvent += OnEvent; sensor.MotionCleared += OnEvent; break;
            case "leak": sensor.LeakDetectedEvent += OnEvent; sensor.LeakCleared += OnEvent; break;
            case "temperature": sensor.TemperatureWarningDetected += OnEvent; sensor.TemperatureWarningCleared += OnEvent; break;
            case "humidity": sensor.HumidityWarningDetected += OnEvent; sensor.HumidityWarningCleared += OnEvent; break;
        }
        void SetValue(bool? value)
        {
            string key = signal switch {
                "battery" => "at_low_battery", "contact" => "open", "motion" => "detected",
                "leak" => "in_alarm", "temperature" => "current_temp_exception", _ => "current_humidity_exception" };
            if (value is null) transport.Child.Remove(key);
            else if (signal is "temperature" or "humidity") transport.Child[key] = value.Value ? 1 : 0;
            else transport.Child[key] = value.Value;
            if (signal == "temperature") transport.Child["current_temp"] = 22;
            if (signal == "humidity") transport.Child["current_humidity"] = 55;
        }
        async Task Push()
        {
            await device.UpdateAsync();
            sensor.ApplyPushedState(device.GetChildDevice("sensor"), device);
        }
        // Other data can arrive first. Never announce an unknown signal as false.
        await Push();
        Assert.That(events, Is.Zero);
        SetValue(initial); await Push(); await Push();
        Assert.That(events, Is.EqualTo(1), "The first reported signal announces its confirmed state once, including inactive states.");
        expected = !initial; SetValue(expected); await Push(); await Push();
        Assert.That(events, Is.EqualTo(2));
        SetValue(null); await Push();
        Assert.That(events, Is.EqualTo(2), "Missing telemetry is not a clearing event.");
        sensor.ApplyPushedState(null, device);
        Assert.That(sensor.OnlineIndicatorIsOnline, Is.False);
        Assert.That(events, Is.EqualTo(2), "A missing child is not a clearing event.");
        ((IParentDeviceChild)sensor).ApplyConnectionState(false);
        expected = initial; SetValue(expected); await Push(); await Push();
        Assert.That(events, Is.EqualTo(3), "Recovery must compare against the last confirmed value once.");
    }
    [Test]
    public async Task ButtonGesturesPublishAvailabilityIconAndPerGestureStateBeforeEvents()
    {
        var transport = new Transport();
        transport.Child["model"] = "S200B";
        transport.Child["category"] = "subg.trigger.button";
        var config = new DeviceConfiguration("127.0.0.1", connectionOptions: new DeviceConnectionOptions(
            connectionParameters: new DeviceConnectionParameters(DeviceFamilyKind.SmartTapoHub, DeviceEncryptionKind.Aes)));
        using var device = new KasaDevice(config, transport);
        using var logger = new DriverLogger("button-feedback");
        var resources = new DriverImplementationResources { Logger = logger, InitLogger = logger.GetComponentLogger("test", "button") };
        var descriptor = new ManagedLightDescriptor("button", config.Host, KasaTapoClient.DeviceType.Hub,
            "Button", "S200B", "sensor", ManagedLightKind.Unknown, childId: "sensor", childKind: ManagedChildKind.Button);
        using var button = new KasaButtonEntity("button", descriptor, config, null, new PlatformSharedConfiguration(), resources, null!, "button-feedback", DriverTestPaths.DataDirectory);
        var published = new Dictionary<string, DriverEntityValue>();
        button.ValuesChanged += (_, e) => { foreach (var item in e.Update.Changes) if (item.Value.Value is {} value) published[item.Key] = value; };
        var events = new List<string>();
        button.ButtonTriggered += (_, _) => {
            Assert.That(button.OnlineIndicatorIsOnline && button.ReadyIndicatorIsReady, Is.True);
            Assert.That(published["buttonIcon"].GetValue<string>(), Is.EqualTo("icGenericDeviceOn"));
            Assert.That(published["lastTriggerType"].GetValue<string>(), Is.EqualTo(button.LastTriggerType));
            events.Add(button.LastTriggerType);
        };
        var gestures = new List<string>();
        button.SinglePressed += (_, _) => gestures.Add("singleClick");
        button.DoublePressed += (_, _) => gestures.Add("doubleClick");
        button.Held += (_, _) => gestures.Add("hold");
        button.Released += (_, _) => gestures.Add("release");
        void Logs(params (long time, string gesture)[] entries) => transport.Child["trigger_logs"] = new JsonObject {
            ["logs"] = new JsonArray(entries.Select(e => (JsonNode)new JsonObject { ["timestamp"] = e.time, ["event"] = e.gesture }).ToArray()) };
        async Task Push() { await device.UpdateAsync(); button.ApplyPushedState(device.GetChildDevice("sensor"), device); }
        Logs((1, "singleClick")); await Push();
        Assert.That(events, Is.Empty, "Historical startup logs must not fire.");
        button.ApplyPushedState(null, device);
        Assert.That(button.OnlineIndicatorIsOnline, Is.False);
        Logs((5, "release"), (4, "hold"), (3, "doubleClick"), (2, "singleClick"), (1, "singleClick"));
        await Push(); await Push();
        Assert.That(events, Is.EqualTo(new[] { "singleClick", "doubleClick", "hold", "release" }));
        Assert.That(gestures, Is.EqualTo(events));
    }

}

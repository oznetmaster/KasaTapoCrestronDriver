

using Crestron.DeviceDrivers.EntityModel.Data;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel;
using KasaTapoClient;
using Driver = KasaTapoCrestronDriver;

namespace KasaTapoCrestronDriver.Tests;

[TestClass]
public sealed class SensorPublicationTests
{
    [TestMethod]
    [DataRow("T310", "TemperatureHumidity", "temperatureValue", "motionDetected")]
    [DataRow("T315", "TemperatureHumidity", "humidityValuePercent", "contactIsOpen")]
    [DataRow("T100", "Motion", "motionDetected", "temperatureValue")]
    [DataRow("T310", "None", "temperatureValue", "motionDetected")]
    [DataRow("T315", "None", "humidityValuePercent", "contactIsOpen")]
    [DataRow("t100(EU)", "Contact", "motionDetected", "contactIsOpen")]
    [DataRow("T110", "None", "contactIsOpen", "motionDetected")]
    public void CachedSensor_FirstPublishedDefinitionIsFinal(string model, string categoryName, string supported, string unsupported)
    {
        var category = (Driver.HubChildCategory)Enum.Parse(typeof(Driver.HubChildCategory), categoryName);
        var descriptor = new Driver.ManagedLightDescriptor("sensor-test", "127.0.0.1", KasaTapoClient.DeviceType.Hub,
            model, model, "sensor-serial", Driver.ManagedLightKind.OnOff,
            childId: "sensor-child", childKind: Driver.ManagedChildKind.Sensor, hubChildCategory: category);
        using var logger = new DriverLogger("sensor-test");
        var resources = new DriverImplementationResources
        {
            Logger = logger,
            InitLogger = logger.GetComponentLogger("test", "sensor")
        };
        using var sensor = new Driver.KasaSensorEntity(descriptor.ControllerId, descriptor,
            new DeviceConfiguration("127.0.0.1"), null, new Driver.PlatformSharedConfiguration(),
            resources, null!, "sensor-test", AppContext.BaseDirectory);
        using var parent = new BaseDriverEntity("parent");
        DriverEntityDefinition? firstDefinition = null;
        parent.SubControllersChanged += (_, _) => firstDefinition = sensor.GetState().Definition;

        // Match both cached and async publication: the SDK sees UpdateSubControllers first.
        parent.UpdateSubControllers(new[] { new ConfigurableDriverEntity(descriptor.ControllerId, sensor) }, null);
        sensor.NotifyChildPublished();

        Assert.IsNotNull(firstDefinition);
        Assert.IsTrue(firstDefinition.Properties.ContainsKey(supported));
        Assert.IsFalse(firstDefinition.Properties.ContainsKey(unsupported),
            "The host's first snapshot must not advertise unsupported sensor capabilities.");
        CollectionAssert.AreEquivalent(firstDefinition.Properties.Keys.ToArray(), sensor.GetState().Definition.Properties.Keys.ToArray(),
            "NotifyChildPublished must not silently change the already-published definition.");
        CollectionAssert.AreEquivalent(firstDefinition.Events.Keys.ToArray(), sensor.GetState().Definition.Events.Keys.ToArray());
    }
}



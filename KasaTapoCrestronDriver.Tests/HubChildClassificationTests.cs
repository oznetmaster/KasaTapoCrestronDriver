extern alias driverassembly;

using System.Reflection;
using KasaTapoClient;
using Driver = driverassembly::KasaTapoCrestronDriver;

namespace KasaTapoCrestronDriver.Tests;

[TestClass]
public sealed class HubChildClassificationTests
{
    [TestMethod]
    [DataRow("T100", "subg.trigger.motion-sensor", "Motion")]
    [DataRow("t100(EU)", "", "Motion")]
    [DataRow("T110", "", "Contact")]
    [DataRow("T310", "", "TemperatureHumidity")]
    [DataRow("T315", "", "TemperatureHumidity")]
    public void Discovery_ClassifiesKnownSensorModels(string model, string category, string expected)
    {
        // KasaTapoClient's discovery DTO constructor is internal. Supply a discovery
        // record with no feature list to exercise the driver's model fallback.
        var child = (ChildDeviceInfo)Activator.CreateInstance(typeof(ChildDeviceInfo),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object?[] { "child", "Sensor", model, DeviceType.Hub, null, "{}", category, null, null }, null)!;
        var classify = typeof(Driver.PlatformDriver).GetMethod("ResolveHubChildKind", BindingFlags.Static | BindingFlags.NonPublic)!;
        var result = ((Driver.ManagedChildKind ChildKind, Driver.HubChildCategory Category))classify.Invoke(null, new object[] { child })!;

        Assert.AreEqual(Driver.ManagedChildKind.Sensor, result.ChildKind);
        Assert.AreEqual(expected, result.Category.ToString());
    }
}

using Crestron.DeviceDrivers.EntityModel;
using Crestron.DeviceDrivers.EntityModel.Data;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel;
using Crestron.DeviceDrivers.SDK.EntityModel.Attributes;
using KasaTapoCrestronDriver;

namespace KasaTapoCrestronDriver.Tests;

[TestClass]
public sealed class RegistrationTracingTests
{
    private sealed class Child : ReflectedAttributeDriverEntity
    {
        public Child() : base("child") { }
        [EntityProperty(Id = "answer")]
        public long Answer => 42;
    }

    private static DriverControllerCreationArgs Args(DriverLogger logger) =>
        new("trace-test", AppContext.BaseDirectory, logger.AppLogger, null!);

    private sealed class Platform : ReflectedAttributeDriverEntity
    {
        public Platform() : base("root") { }
        [EntityProperty(Id = "platform:managedDevices")]
        public IDictionary<string, string> Devices => new Dictionary<string, string> { ["child"] = "private payload" };
    }

    [TestMethod]
    public void RootState_RecordsReturnedManagedIdsWithoutValues()
    {
        using var logger = new DriverLogger("trace-test");
        using var parent = new Platform();
        var log = new List<string>();
        using var controller = new RegistrationTracingController(new ConfigurableDriverEntity("root", parent), Args(logger), log.Add);
        Assert.IsTrue(controller.GetState("root").PropertyValues.ContainsKey("platform:managedDevices"));
        Assert.IsTrue(log.Any(line => line.Contains("managedIds=[child]")));
        Assert.IsFalse(log.Any(line => line.Contains("private payload")));
    }

    [TestMethod]
    public void HostLookup_RecordsActualRegistryAndReturnsChildState()
    {
        using var logger = new DriverLogger("trace-test");
        using var parent = new BaseDriverEntity("root");
        using var child = new Child();
        var log = new List<string>();
        using var controller = new RegistrationTracingController(new ConfigurableDriverEntity("root", parent), Args(logger), log.Add);
        parent.UpdateSubControllers(new[] { new ConfigurableDriverEntity("child", child) }, null);
        Assert.IsTrue(controller.ControllerIds.Contains("child"));
        var state = controller.GetState("child");
        Assert.AreEqual(42L, state.PropertyValues["answer"].GetValue<long>());
        Assert.IsTrue(log.Any(line => line.Contains("ControllerIdsChanged; added=[child]")));
        Assert.IsTrue(log.Any(line => line.Contains("method=GetState") && line.Contains("registryContainsId=True")));
        Assert.IsTrue(log.Any(line => line.Contains("properties=[answer]")));
        parent.UpdateSubControllers(null, new[] { "child" });
        Assert.IsFalse(controller.ControllerIds.Contains("child"));
        Assert.IsTrue(log.Any(line => line.Contains("removed=[child]")));
    }

    [TestMethod]
    public void MissingController_KeepsSdkEmptyStateAndRecordsMiss()
    {
        using var logger = new DriverLogger("trace-test");
        using var parent = new BaseDriverEntity("root");
        var log = new List<string>();
        using var controller = new RegistrationTracingController(new ConfigurableDriverEntity("root", parent), Args(logger), log.Add);
        Assert.AreEqual(0, controller.GetState("missing").Definition.Properties.Count);
        Assert.IsTrue(log.Any(line => line.Contains("method=GetState") && line.Contains("registryContainsId=False")));
    }

    [TestMethod]
    public void LoggerFailure_DoesNotBreakHostCalls()
    {
        using var logger = new DriverLogger("trace-test");
        using var child = new Child();
        using var controller = new RegistrationTracingController(new ConfigurableDriverEntity("child", child), Args(logger),
            _ => throw new InvalidOperationException("Test logging failure"));
        Assert.IsTrue(controller.ControllerIds.Contains("child"));
        Assert.AreEqual(42L, controller.GetState("child").PropertyValues["answer"].GetValue<long>());
        Assert.AreEqual(DriverControllerStatus.Running, controller.GetStatus("child"));
    }
}

// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
using Crestron.DeviceDrivers.EntityModel.Data;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel;
using KasaTapoClient;
using KasaTapoCrestronDriver;

namespace KasaTapoCrestronDriver.Tests;

#if NETFRAMEWORK
[Category("Processor")]
#endif
[TestFixture]
public sealed class ExtensionAvailabilityTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void HubChildAvailabilityPublishesStandardAndExistingExtensionBindings(bool button)
    {
        var settings = new PlatformSharedConfiguration();
        using var logger = new DriverLogger("availability-test");
        var resources = new DriverImplementationResources { Logger = logger, InitLogger = logger.GetComponentLogger("test", "availability") };
        var descriptor = new ManagedLightDescriptor("fixture-child", "127.0.0.1", KasaTapoClient.DeviceType.Hub,
            "Fixture child", button ? "S200B" : "T310", "fixture-child", ManagedLightKind.Unknown);
        var configuration = new DeviceConfiguration("127.0.0.1");
        using ReflectedAttributeDriverEntity entity = button
            ? new KasaButtonEntity("fixture-child", descriptor, configuration, null, settings, resources, null!, "availability-test", DriverTestPaths.DataDirectory)
            : new KasaSensorEntity("fixture-child", descriptor, configuration, null, settings, resources, null!, "availability-test", DriverTestPaths.DataDirectory);
        var changes = new Dictionary<string, DriverEntityValue>();
        entity.ValuesChanged += (_, e) => { foreach (var entry in e.Update.Changes) if (entry.Value.Value is {} value) changes[entry.Key] = value; };
        foreach (bool online in new[] { true, false, true })
        {
            changes.Clear();
            ((IParentDeviceChild)entity).ApplyConnectionState(online);
            foreach (string key in new[] { "onlineIndicator:isOnline", "readyIndicator:isReady", "onlineIndicatorIsOnline", "readyIndicatorIsReady" })
            {
                Assert.That(changes.ContainsKey(key), Is.True, "Missing availability notification: " + key);
                Assert.That(changes[key].GetValue<bool>(), Is.EqualTo(online));
                Assert.That(entity.GetState().PropertyValues[key].GetValue<bool>(), Is.EqualTo(online));
            }
        }
    }
}

// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
using Crestron.DeviceDrivers.EntityModel.Data;
using Crestron.DeviceDrivers.SDK;
using KasaTapoClient;
using KasaTapoClient.Internal;
using KasaTapoCrestronDriver;

namespace KasaTapoCrestronDriver.Tests;

#if NETFRAMEWORK
[Category("Processor")]
#endif
[TestFixture]
public sealed class OutletRefreshFailureTests
{
    sealed class Transport : IDeviceTransport
    {
        public Exception? Failure;
        public Task<string> SendAsync(string json, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Failure is {} error) throw error;
            return Task.FromResult("{\"system\":{\"get_sysinfo\":{\"alias\":\"Fixture plug\",\"model\":\"HS100\",\"deviceId\":\"fixture-plug\",\"relay_state\":0}}}");
        }
        public Task<string> SendManyAsync(IReadOnlyList<string> json, CancellationToken token) =>
            Task.FromResult("{\"emeter\":{\"err_code\":-1}}");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RefreshFailurePublishesOfflineAndNextRefreshRecovers(bool timeoutCancellation)
    {
        var transport = new Transport();
        using var device = new KasaDevice(new DeviceConfiguration("127.0.0.1"), transport);
        await device.UpdateAsync();
        var settings = new PlatformSharedConfiguration();
        settings.Update("", "", TimeSpan.FromSeconds(5), false, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), false, "", "", "");
        using var logger = new DriverLogger("outlet-failure-test");
        var resources = new DriverImplementationResources { Logger = logger, InitLogger = logger.GetComponentLogger("test", "outlet") };
        var descriptor = new ManagedLightDescriptor("fixture-outlet", "127.0.0.1", device.DeviceType,
            "Fixture plug", "HS100", "fixture-plug", ManagedLightKind.Dimmable);
        using var outlet = new KasaOutletEntity("fixture-outlet", descriptor, device.Configuration, null, settings, resources, null!, "outlet-failure-test");
        Assert.That(outlet.TryAttachConnectedDevice(device, "fixture"), Is.True);
        await outlet.SetConfiguredAsync(true, "fixture", default);
        var changes = new Dictionary<string, DriverEntityValue>();
        outlet.ValuesChanged += (_, e) => { foreach (var entry in e.Update.Changes) if (entry.Value.Value is {} value) changes[entry.Key] = value; };

        transport.Failure = timeoutCancellation ? new OperationCanceledException("Transport request timeout; caller token remains active.") : new System.IO.IOException("Device unreachable");
        await outlet.RefreshAsync(default);
        outlet.PublishStateSnapshot();
        Assert.That(outlet.OnlineIndicatorIsOnline, Is.False);
        Assert.That(changes["onlineIndicatorIsOnline"].GetValue<bool>(), Is.False);

        transport.Failure = null;
        await outlet.RefreshAsync(default);
        outlet.PublishStateSnapshot();
        Assert.That(changes["onlineIndicatorIsOnline"].GetValue<bool>(), Is.True);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.That(async () => await outlet.RefreshAsync(cancelled.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(outlet.OnlineIndicatorIsOnline, Is.True, "Caller cancellation must not manufacture an outage.");
    }
}



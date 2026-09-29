// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
using System.Globalization;
using System.Text.Json;
using CrestronHomeDevTools;
using NUnit.Framework;

namespace KasaTapoCrestronDriver.AndroidTests;

[TestFixture, NonParallelizable]
public sealed class PlatformConfigurationTests
{
    const string Setting = "DiscoveryTimeoutSeconds";
    static readonly string[] SafeSettings = [Setting, "EnableLightPolling", "LightPollIntervalSeconds", "SensorPollIntervalSeconds", "EnableProcessorBaselineWorkaround"];

    [Test]
    public async Task DiscoverySettingPersistsAndRestores()
    {
        if (!SensorSession.Settings!.PlatformConfigurationAuthorized) Assert.Ignore("Platform configuration changes are not authorized.");
        if (!SensorSession.PhysicalRestorationConfirmed) throw new InvalidOperationException("Earlier restoration is unresolved.");
        var session = SensorSession.Current!;
        var started = DateTimeOffset.UtcNow;
        int id = session.Context.InstalledDriverId;
        const string apply = "cp.driverConfiguration:applyConfiguration", first = "cp.driverConfiguration:getFirstConfigurationStep";
        string root = Path.Combine(session.Context.EvidenceDirectory, "configuration-platform");
        Directory.CreateDirectory(root);
        int sequence = 0;
        async Task<string> Record(string phase, object data)
        {
            string path = Path.Combine(root, (++sequence).ToString("D3") + "-" + phase + ".json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Phase = phase, DeviceId = id, Data = data, Utc = DateTimeOffset.UtcNow }));
            return path;
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = deadline.Token;
        async Task<Dictionary<string, string>> Read(CancellationToken ct)
        {
            var device = await SensorSession.Api!.GetDeviceAsync(id, ct) ?? throw new InvalidDataException("Platform missing.");
            if (device.Model != "KasaTapoPlatform" || Version.Parse(device.PropertyValues["cp.driverInformation:version"].GetString()!) != Version.Parse(session.Context.DriverVersion) ||
                !device.PropertyValues["onlineIndicator:isOnline"].GetBoolean() || !device.PropertyValues["readyIndicator:isReady"].GetBoolean() ||
                !device.Commands.Contains(apply) || !device.Commands.Contains(first)) throw new InvalidDataException("Platform identity, readiness or configuration commands changed.");
            var snapshot = await DriverConfigurationInspection.GetAsync(SensorSession.Api, id, ct);
            if (snapshot.IsConfigured != true || snapshot.IsReconfigurable != true) throw new InvalidDataException("Platform configuration is unavailable.");
            return SafeSettings.ToDictionary(key => key, key =>
            {
                var item = snapshot.Items.Single(i => i.Id == key);
                if (item.Masked || item.ReadOnly != false || item.CurrentValue == null || string.IsNullOrWhiteSpace(item.Title))
                    throw new InvalidDataException("Expected public setting is unavailable.");
                return item.CurrentValue.Value.ValueKind == JsonValueKind.String ? item.CurrentValue.Value.GetString()! : item.CurrentValue.Value.GetRawText();
            });
        }
        async Task Write(Dictionary<string, string> values, CancellationToken ct)
        {
            await Read(ct);
            // Home marks omitted editable settings as requiring review. Submit the complete
            // non-secret settings group, retaining every value except the tested timeout.
            await Record("write-intent", new { Settings = values });
            var result = await SensorSession.Api!.ExecuteDeviceCommandAsync(id, apply,
                new { configurationItemValues = values, isoCulture = "en-GB" }, ct);
            if (result is { ValueKind: not JsonValueKind.Null } && (result.Value.ValueKind != JsonValueKind.Array || result.Value.GetArrayLength() != 0))
                throw new InvalidOperationException("Platform configuration rejected the change; no request was retried.");
        }
        async Task ReopenAndVerify(Dictionary<string, string> expected, CancellationToken ct)
        {
            var step = await SensorSession.Api!.ExecuteDeviceCommandAsync(id, first, new { isReconfiguring = true }, ct);
            if (step?.ValueKind != JsonValueKind.Object || step.Value.GetProperty("ConfigurationErrors").ValueKind != JsonValueKind.Null)
                throw new InvalidDataException("Reopening platform configuration did not return a valid step.");
            // Do not retain the full step: it contains account configuration.
            var ids = step.Value.GetProperty("Items").EnumerateArray().Select(i => i.GetProperty("Id").GetString()).ToArray();
            Assert.That(ids, Does.Contain(Setting));
            var current = await Read(ct);
            Assert.That(current, Is.EquivalentTo(expected));
            foreach (var target in SensorSession.Settings!.Sensors)
            {
                int childId = target.DeviceId > 0 ? target.DeviceId : session.Context.RequireManagedDevice(target.Alias).DeviceId;
                var child = await SensorSession.Api.GetDeviceAsync(childId, ct) ?? throw new InvalidDataException("Selected child missing.");
                Assert.That(child.ParentDeviceId, Is.EqualTo(id));
                Assert.That(child.Model, Is.EqualTo(target.Model));
                Assert.That(child.PropertyValues["onlineIndicator:isOnline"].GetBoolean(), Is.True);
                Assert.That(child.PropertyValues["readyIndicator:isReady"].GetBoolean(), Is.True);
            }
            await Record("reopened", new { Settings = current, SelectedChildrenReady = true });
        }
        var original = await Read(token);
        int oldValue = int.Parse(original[Setting], CultureInfo.InvariantCulture);
        if (oldValue is < 1 or > 60) throw new InvalidDataException("Discovery timeout is outside this fixture's supported range.");
        var changed = new Dictionary<string, string>(original) { [Setting] = (oldValue == 10 ? 11 : 10).ToString(CultureInfo.InvariantCulture) };
        string originalFile = await Record("original", original);
        var originalAt = DateTimeOffset.UtcNow;
        var actionAt = DateTimeOffset.UtcNow;
        string? restoredFile = null;
        SensorSession.PhysicalRestorationConfirmed = false;
        try
        {
            await Write(changed, token);
            await ReopenAndVerify(changed, token);
        }
        finally
        {
            using var restore = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await Write(original, restore.Token);
            await ReopenAndVerify(original, restore.Token);
            var readiness = await DriverReadiness.InspectAsync(SensorSession.Api!, id, "KasaTapoPlatform", session.Context.DriverVersion,
                cancellationToken: restore.Token);
            await Record("restoration-readiness-start", readiness);
            while (!readiness.Ready)
            {
                await Task.Delay(500, restore.Token);
                readiness = await DriverReadiness.InspectAsync(SensorSession.Api!, id, "KasaTapoPlatform", session.Context.DriverVersion,
                    cancellationToken: restore.Token);
            }
            await Record("restored-readiness", readiness);
            restoredFile = await Record("restored", original);
            SensorSession.PhysicalRestorationConfirmed = true;
        }
        AppEvidence.Write("configuration.platform", "Changed only the platform discovery timeout, reopened configuration, verified the saved value and unchanged polling settings plus selected child readiness, then restored and reopened to verify the original settings. Configuration API evidence; no Configure Pro visual, reboot persistence or physical button-setting claim.",
            started, originalAt, actionAt, originalFile, restoredFile);
    }
}

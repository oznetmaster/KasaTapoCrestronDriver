// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
using System.Text.Json;
using CrestronHomeDevTools;
using CrestronHomeNUnit.Android;
using KasaTapoClient;
using NUnit.Framework;

namespace KasaTapoCrestronDriver.AndroidTests;

[TestFixture, NonParallelizable]
public sealed class OutletControlsTests
{
    sealed record Activity(string Epoch, long Completed, int Pending);

    [TestCase("energy")]
    [TestCase("basic")]
    public async Task AppPowerControlChangesPhysicalOutletAndRestoresIt(string alias)
    {
        var session = SensorSession.Current!;
        var settings = SensorSession.Settings!;
        if (settings.Outlets == null) Assert.Ignore("No explicitly authorized outlet bindings supplied.");
        var target = settings.Outlets!.Single(t => t.Alias == alias);
        if (!target.ControlsAuthorized || target.DeviceId <= 0 || string.IsNullOrWhiteSpace(target.DiscoveryId) ||
            string.IsNullOrWhiteSpace(target.AuthenticatedId) || settings.DeviceCredentialsFile == null ||
            !Path.IsPathFullyQualified(settings.DeviceCredentialsFile) || new FileInfo(settings.DeviceCredentialsFile).Length > 65536)
            throw new InvalidDataException("Explicit private control authorization and device bindings are required.");
        if (!SensorSession.PhysicalRestorationConfirmed) throw new InvalidOperationException("Earlier restoration is unresolved.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var token = deadline.Token;
        string physicalId = target.DiscoveryId + (string.IsNullOrWhiteSpace(target.ChildId) ? "" : "/" + target.ChildId);
        string evidence = Path.Combine(session.Context.EvidenceDirectory, "outlet-" + alias);
        Directory.CreateDirectory(evidence);
        int sequence = 0;
        async Task Record(string phase, object data) => await File.WriteAllTextAsync(Path.Combine(evidence,
            (++sequence).ToString("D3") + "-" + phase + ".json"), JsonSerializer.Serialize(new { phase, target.DeviceId, physicalId, data, Utc = DateTimeOffset.UtcNow }));

        async Task<DeviceInfo> Device(CancellationToken ct)
        {
            var d = await SensorSession.Api!.GetDeviceAsync(target.DeviceId, ct) ?? throw new InvalidDataException("Outlet missing.");
            if (d.ParentDeviceId != session.Context.InstalledDriverId || d.Name != target.Name || d.Model != target.Model || d.LocationId != target.LocationId ||
                Version.Parse(d.PropertyValues["cp.driverInformation:version"].GetString()!) != Version.Parse(session.Context.DriverVersion) ||
                d.PropertyValues["controlDeviceId"].GetString() != physicalId || !d.PropertyValues["onlineIndicator:isOnline"].GetBoolean() ||
                !d.PropertyValues["readyIndicator:isReady"].GetBoolean()) throw new InvalidDataException("Outlet identity or readiness changed.");
            return d;
        }
        async Task<Activity> Idle(CancellationToken ct)
        {
            while (true)
            {
                var a = JsonSerializer.Deserialize<Activity>((await Device(ct)).PropertyValues["controlStatus"].GetString()!)!;
                if (a == null || string.IsNullOrWhiteSpace(a.Epoch) || a.Completed < 0 || a.Pending < 0) throw new InvalidDataException("Invalid activity.");
                if (a.Pending == 0) return a;
                await Task.Delay(250, ct);
            }
        }
        async Task<bool> Physical(CancellationToken ct)
        {
            try
            {
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(settings.DeviceCredentialsFile!, ct));
                var auth = json.RootElement.GetProperty("credentials");
                var found = (await Discover.DiscoverAsync(TimeSpan.FromSeconds(3), cancellationToken: ct))
                    .Where(d => string.Equals(d.DeviceId, target.DiscoveryId, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (found.Select(d => d.Host).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1) throw new InvalidDataException();
                var selected = found.OrderByDescending(d => d.TpapPreferred == true || d.TpapMetadata != null).First();
                using var device = await Discover.ConnectAsync(Discover.CreateConfiguration(selected,
                    new DeviceCredentials(auth.GetProperty("userName").GetString(), auth.GetProperty("password").GetString()), TimeSpan.FromSeconds(15)), ct);
                if (!string.Equals(device.SystemInfo?.DeviceId, target.AuthenticatedId, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
                return (string.IsNullOrWhiteSpace(target.ChildId) ? device.IsOn : device.GetChild(target.ChildId)?.IsOn) ?? throw new InvalidDataException();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { throw new InvalidDataException("Independent physical observation failed (" + error.GetType().Name + "); inspect private bindings and connectivity."); }
        }
        async Task VerifyState(bool expected, Activity activity, CancellationToken ct)
        {
            int matches = 0;
            while (matches < 2)
            {
                if (await Idle(ct) != activity) throw new InvalidOperationException("Concurrent command or driver restart.");
                bool actual = await Physical(ct);
                bool home = (await Device(ct)).PropertyValues["outletIsOn"].GetBoolean();
                if (await Idle(ct) != activity) throw new InvalidOperationException("Concurrent observation activity.");
                matches = actual == expected && home == expected ? matches + 1 : 0;
                await Record("observation", new { expected, actual, home, activity });
                if (matches < 2) await Task.Delay(500, ct);
            }
        }
        async Task<Activity> Completion(Activity before, CancellationToken ct)
        {
            while (true)
            {
                var now = await Idle(ct);
                if (now.Epoch != before.Epoch || now.Completed < before.Completed || now.Completed > before.Completed + 1)
                    throw new InvalidOperationException("Command attribution changed.");
                if (now.Completed == before.Completed + 1) return now;
                await Task.Delay(250, ct);
            }
        }

        var baseline = await Idle(token);
        bool original = await Physical(token);
        await VerifyState(original, baseline, token);
        string title = (await Device(token)).PropertyValues["deviceLabel"].GetString()!;
        await Record("original", new { original, baseline });
        bool attempted = false;
        try
        {
            await RoomNavigation.Open(target.Room, token);
            AndroidSelector selector;
            Action<AndroidHierarchy> guard;
            if (target.EnergyPage)
            {
                var dots = new AndroidSelector(AndroidSelectorKind.ResourceId, RoomNavigation.Prefix + "serviceDots") { SiblingText = target.Name };
                await session.Device.TapAsync(dots, h => RoomNavigation.RequireVisibleRoomControl(h, target.Room, dots), token);
                selector = CrestronHomePages.Resource("customdevicetoggle_switch");
                guard = h => {
                    CrestronHomePages.RequireExtensionPage(h, title);
                    if ((string?)RoomNavigation.Node(h, "customdevicetoggle_switch").Attribute("checked") != original.ToString().ToLowerInvariant())
                        throw new InvalidDataException("App's initial power state differs.");
                };
            }
            else
            {
                // Select the exact title text, never a nearby room-wide lighting button.
                selector = new(AndroidSelectorKind.Text, target.Name);
                guard = h => RoomNavigation.RequireVisibleRoomControl(h, target.Room, selector);
            }
            if (await Idle(token) != baseline) throw new InvalidOperationException("Activity changed before input.");
            await session.CaptureAsync("outlet." + alias + ".before", guard, token);
            await Record("ui-intent", new { original, requested = !original, baseline });
            attempted = true; SensorSession.PhysicalRestorationConfirmed = false;
            await session.Device.TapAsync(selector, guard, token);
            var completed = await Completion(baseline, token);
            await VerifyState(!original, completed, token);
            await session.CaptureAsync("outlet." + alias + ".changed", h => {
                if (target.EnergyPage)
                {
                    CrestronHomePages.RequireExtensionPage(h, title);
                    Assert.That((string?)RoomNavigation.Node(h, "customdevicetoggle_switch").Attribute("checked"), Is.EqualTo((!original).ToString().ToLowerInvariant()));
                }
                else {
                    CrestronHomePages.RequireRoom(h, target.Room);
                    Assert.That(h.RequireUnique(new(AndroidSelectorKind.ResourceId, RoomNavigation.Prefix + "serviceSubtitle") { SiblingText = target.Name }).Text,
                        Is.EqualTo(!original ? "ON" : "OFF").IgnoreCase);
                }
            }, token);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            try
            {
                if (attempted)
                {
                    var current = await Idle(cleanup.Token);
                    if (current == baseline) current = await Completion(baseline, cleanup.Token);
                    if (current.Epoch != baseline.Epoch || current.Completed < baseline.Completed || current.Completed > baseline.Completed + 1)
                        throw new InvalidOperationException("Concurrent changes prevent automatic restoration.");
                    // Wait for the submitted UI operation before issuing the absolute restore.
                    await Record("restore-intent", new { original, current });
                    await SensorSession.Api!.ExecuteDeviceCommandAsync(target.DeviceId, original ? "outletOn" : "outletOff", null, cleanup.Token);
                    var done = await Completion(current, cleanup.Token);
                    await VerifyState(original, done, cleanup.Token);
                    await Record("restored", new { original, done });
                    SensorSession.PhysicalRestorationConfirmed = true;
                }
            }
            finally { await RoomNavigation.Restore(target.Room, target.EnergyPage ? title : null, cleanup.Token); }
        }
    }
}

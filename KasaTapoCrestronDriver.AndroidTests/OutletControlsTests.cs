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
        var started = DateTimeOffset.UtcNow;
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
        async Task<Activity> Completion(Activity before, CancellationToken ct, int commands = 1)
        {
            while (true)
            {
                var now = await Idle(ct);
                if (now.Epoch != before.Epoch || now.Completed < before.Completed || now.Completed > before.Completed + commands)
                    throw new InvalidOperationException("Command attribution changed.");
                if (now.Completed == before.Completed + commands) return now;
                await Task.Delay(250, ct);
            }
        }

        var baseline = await Idle(token);
        bool original = await Physical(token);
        await VerifyState(original, baseline, token);
        string title = (await Device(token)).PropertyValues["deviceLabel"].GetString()!;
        await Record("original", new { original, baseline });
        var originalAt = DateTimeOffset.UtcNow;
        var actionAt = originalAt;
        bool attempted = false;
        int intendedCommands = 0;
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
                guard = h => CrestronHomePages.RequireExtensionPage(h, title);
            }
            else
            {
                // Select the exact title text, never a nearby room-wide lighting button.
                selector = new(AndroidSelectorKind.Text, target.Name);
                guard = h => RoomNavigation.RequireVisibleRoomControl(h, target.Room, selector);
            }
            if (await Idle(token) != baseline) throw new InvalidOperationException("Activity changed before input.");
            void RequirePower(AndroidHierarchy h, bool expected)
            {
                guard(h);
                if (target.EnergyPage)
                    Assert.That((string?)RoomNavigation.Node(h, "customdevicetoggle_switch").Attribute("checked"), Is.EqualTo(expected.ToString().ToLowerInvariant()));
                else
                    Assert.That(h.RequireUnique(new(AndroidSelectorKind.ResourceId, RoomNavigation.Prefix + "serviceSubtitle") { SiblingText = target.Name }).Text,
                        Is.EqualTo(expected ? "ON" : "OFF").IgnoreCase);
            }
            await session.CaptureAsync("outlet." + alias + ".before", h => {
                RequirePower(h, original);
                if (target.EnergyPage)
                {
                    // Verify the complete displayed inventory. Values may change between
                    // polling and screen capture; this is not an exact telemetry comparison.
                    foreach (string label in new[] { "Current Power (W)", "Voltage (V)", "Current (A)", "Today (kWh)", "This Month (kWh)", "Total (kWh)" })
                    {
                        var row = h.RequireUnique(new(AndroidSelectorKind.ResourceId, RoomNavigation.Prefix + "customdevice_textdisplay_firstlinetext") { SiblingText = label });
                        Assert.That(double.TryParse(row.Text, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double value) && double.IsFinite(value), Is.True, label);
                    }
                }
            }, token);
            await Record("ui-intent", new { original, requested = !original, baseline });
            attempted = true; SensorSession.PhysicalRestorationConfirmed = false;
            actionAt = DateTimeOffset.UtcNow;
            intendedCommands++;
            await session.Device.TapAsync(selector, guard, token);
            var completed = await Completion(baseline, token);
            await VerifyState(!original, completed, token);
            await session.CaptureAsync("outlet." + alias + ".changed", h => RequirePower(h, !original), token);
            await Record("ui-return-intent", new { requested = original, completed });
            intendedCommands++;
            await session.Device.TapAsync(selector, guard, token);
            completed = await Completion(baseline, token, intendedCommands);
            await VerifyState(original, completed, token);
            await session.CaptureAsync("outlet." + alias + ".returned", h => RequirePower(h, original), token);

            // Guard the stable page once, then use its exact observed coordinates.
            // A full hierarchy capture between presses takes several seconds and
            // would turn this into another slow sequence instead of a short burst.
            var burstPage = await session.Device.CaptureAsync(token);
            RequirePower(burstPage, original);
            var burstControl = burstPage.RequireUnique(selector);
            if (burstControl.Right <= burstControl.Left || burstControl.Bottom <= burstControl.Top || burstControl.Left < 0 || burstControl.Top < 0)
                throw new InvalidDataException("Unusable observed burst control.");
            string x = ((burstControl.Left + burstControl.Right) / 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
            string y = ((burstControl.Top + burstControl.Bottom) / 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var profile = session.Context.Profile;
            var transport = new AdbCommandTransport(profile.AdbExecutable, profile.DeviceSerial, TimeSpan.FromSeconds(25));
            var burstStarted = DateTimeOffset.UtcNow;
            for (int index = 0; index < 3; index++)
            {
                await Record("burst-intent", new { index, expectedCompleted = baseline.Completed + intendedCommands + 1 });
                intendedCommands++;
                await transport.ExecuteAsync(["shell", "input", "tap", x, y], token);
            }
            await Record("burst-sent", new { burstStarted, burstFinished = DateTimeOffset.UtcNow, presses = 3 });
            completed = await Completion(baseline, token, intendedCommands);
            await VerifyState(!original, completed, token);
            await session.CaptureAsync("outlet." + alias + ".burst-result", h => RequirePower(h, !original), token);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            try
            {
                if (attempted)
                {
                    var current = await Completion(baseline, cleanup.Token, intendedCommands);
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
        AppEvidence.Write("outlet." + alias,
            "Verified the selected outlet identity and displayed control inventory, operated the individual app power control and its return, then sent three further guarded presses without waiting for acknowledgements. Correlated the exact completed command count with two independent physical/API observations and app feedback after each sequence, restored its original physical state and returned Home. Burst timestamps are retained; no fixed tap rate, outage, telemetry accuracy or quantified response-time assertion.",
            started, originalAt, actionAt,
            Directory.GetFiles(evidence, "*-original.json").Single(), Directory.GetFiles(evidence, "*-restored.json").Single());
    }
}

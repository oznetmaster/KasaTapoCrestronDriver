// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CrestronHomeDevTools;
using CrestronHomeNUnit.Android;
using NUnit.Framework;

namespace KasaTapoCrestronDriver.AndroidTests;

internal sealed record SensorEventSnapshot(DateTimeOffset ObservedUtc, double Marker, Dictionary<string, string> Rows);

internal static class SensorEventReading
{
    internal static SensorEventSnapshot Read(string alias, DeviceInfo device)
    {
        var p = device.PropertyValues;
        string Text(string name) => p[name].GetString() is { Length: > 0 } text
            ? text : throw new InvalidDataException("Missing sensor display value: " + name);
        if (!p["onlineIndicator:isOnline"].GetBoolean() || !p["readyIndicator:isReady"].GetBoolean())
            throw new InvalidDataException("Sensor is not online and ready.");
        if (alias == "button")
        {
            double time = p["lastTriggerTime"].GetDouble();
            if (!double.IsFinite(time) || time < 0) throw new InvalidDataException("Invalid button event timestamp.");
            return new(DateTimeOffset.UtcNow, time, new() { ["Last Press"] = Text("lastGestureLabel"),
                ["Last Press Time"] = Text("lastTriggerTimeDisplay") });
        }
        var (capability, raw, display, label) = alias switch
        {
            "motion" => ("hasMotion", "motionDetected", "motionStatusLabel", "Motion"),
            "contact" => ("hasContact", "contactIsOpen", "contactStatusLabel", "Contact"),
            "leak" => ("hasLeak", "leakDetected", "leakStatusLabel", "Leak"),
            _ => throw new InvalidDataException("Unsupported physical event target.")
        };
        if (!p[capability].GetBoolean()) throw new InvalidDataException("Sensor lacks selected event capability.");
        return new(DateTimeOffset.UtcNow, p[raw].GetBoolean() ? 1 : 0, new() { [label] = Text(display) });
    }

    internal static bool IsNew(string alias, SensorEventSnapshot baseline, SensorEventSnapshot current) =>
        alias == "button" ? current.Marker > baseline.Marker : baseline.Marker == 0 && current.Marker == 1;

    internal static bool DisplayChanged(SensorEventSnapshot baseline, SensorEventSnapshot current) =>
        current.Rows.Any(p => !baseline.Rows.TryGetValue(p.Key, out var old) ||
            !string.Equals(old, p.Value, StringComparison.OrdinalIgnoreCase));

    internal static bool Matches(AndroidHierarchy hierarchy, string title, SensorEventSnapshot snapshot)
    {
        CrestronHomePages.RequireExtensionPage(hierarchy, title);
        return snapshot.Rows.All(pair => string.Equals(hierarchy.RequireUnique(new AndroidSelector(
            AndroidSelectorKind.ResourceId, CrestronHomePages.ResourcePrefix + "customdevice_textdisplay_firstlinetext")
            { SiblingText = pair.Key }).Text, pair.Value, StringComparison.OrdinalIgnoreCase));
    }
}

// Explicit selection is mandatory: an unattended suite must never wait for a person.
[TestFixture, NonParallelizable]
public sealed class SensorEventTests
{
    [TestCase("button", Explicit = true)]
    [TestCase("motion", Explicit = true)]
    [TestCase("contact", Explicit = true)]
    [TestCase("leak", Explicit = true)]
    public async Task PhysicalEventReachesVisibleDetailPage(string alias)
    {
        if (!SensorSession.PhysicalRestorationConfirmed)
            throw new InvalidOperationException("Resolve the preceding test's physical restoration before recording another event.");
        var session = SensorSession.Current!;
        var target = SensorSession.Settings!.Sensors.Single(s => s.Alias == alias);
        int id = target.DeviceId > 0 ? target.DeviceId : session.Context.RequireManagedDevice(alias).DeviceId;
        string prefix = "sensor-event." + alias;
        string folder = Path.Combine(session.Context.EvidenceDirectory, prefix);
        if (Directory.Exists(folder)) throw new IOException("Event evidence already exists; use a new workflow invocation.");
        Directory.CreateDirectory(folder);
        async Task Save(string name, object value)
        {
            await using var file = new FileStream(Path.Combine(folder, name + ".json"), FileMode.CreateNew);
            await JsonSerializer.SerializeAsync(file, value, new JsonSerializerOptions { WriteIndented = true });
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        var token = deadline.Token;
        async Task<DeviceInfo> ReadDevice(CancellationToken ct)
        {
            var device = await SensorSession.Api!.GetDeviceAsync(id, ct) ?? throw new InvalidDataException("Sensor missing.");
            if (device.ParentDeviceId != session.Context.InstalledDriverId || device.Model != target.Model ||
                device.Name != target.Name || device.LocationId != target.LocationId ||
                Version.Parse(device.PropertyValues["cp.driverInformation:version"].GetString()!) != Version.Parse(session.Context.DriverVersion))
                throw new InvalidDataException("Sensor identity changed.");
            return device;
        }
        var first = await ReadDevice(token);
        string title = first.PropertyValues["deviceLabel"].GetString() ?? throw new InvalidDataException("Sensor title missing.");
        SensorEventSnapshot? baseline = null;
        bool armed = false;
        Exception? failure = null;
        async Task<SensorEventSnapshot> WaitFor(Func<SensorEventSnapshot, bool> predicate)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
            wait.CancelAfter(TimeSpan.FromMinutes(5));
            while (true)
            {
                var reading = SensorEventReading.Read(alias, await ReadDevice(wait.Token));
                if (predicate(reading)) return reading;
                await Task.Delay(TimeSpan.FromSeconds(1), wait.Token);
            }
        }
        async Task ObserveUi(string phase, SensorEventSnapshot reading)
        {
            // This is an observer interval from the API observation, NOT the physical
            // event time or exact first-visible app latency. No checklist timing claim.
            var clock = Stopwatch.StartNew();
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
            wait.CancelAfter(TimeSpan.FromSeconds(30));
            while (!SensorEventReading.Matches(await session.Device.CaptureAsync(wait.Token), title, reading))
                await Task.Delay(250, wait.Token);
            await session.CaptureAsync(prefix + "." + phase, h =>
            {
                if (!SensorEventReading.Matches(h, title, reading)) throw new InvalidDataException("App changed during capture.");
            }, wait.Token);
            await Save(phase, new { Reading = reading, AppRecordedUtc = DateTimeOffset.UtcNow,
                ObservationAndCaptureMilliseconds = clock.Elapsed.TotalMilliseconds });
        }
        try
        {
            await RoomNavigation.Open(target.Room, token);
            await RoomNavigation.RevealTile(target.Room, target.Name, token);
            await session.Device.TapAsync(RoomNavigation.Tile(target.Name),
                h => RoomNavigation.InspectTile(h, target.Room, target.Name, false), token);
            baseline = SensorEventReading.Read(alias, await ReadDevice(token));
            if (alias != "button" && baseline.Marker != 0)
                throw new InvalidDataException("Start with no motion, contact closed or leak sensor dry, then retry in a new invocation.");
            await ObserveUi("baseline", baseline);
            armed = true;
            if (alias != "button") SensorSession.PhysicalRestorationConfirmed = false;
            await Save("ready", new { Alias = alias, DeviceId = id, ReadyUtc = DateTimeOffset.UtcNow,
                Instruction = "Trigger this physical sensor once; do not navigate the app. Restore after restore-request appears." });
            TestContext.Progress.WriteLine($"READY: {alias}; trigger the selected sensor once.");
            var changed = await WaitFor(s => SensorEventReading.IsNew(alias, baseline, s));
            if (!SensorEventReading.DisplayChanged(baseline, changed))
                throw new InvalidDataException("Fresh event has no distinguishable display change; app feedback cannot be proved.");
            await ObserveUi("event", changed);
            if (alias != "button")
            {
                await Save("restore-request", new { RequestedUtc = DateTimeOffset.UtcNow,
                    Instruction = "Close contact, dry leak sensor or leave motion detection area." });
                TestContext.Progress.WriteLine($"RESTORE: {alias}; return it to its original inactive state.");
                var restored = await WaitFor(s => s.Marker == baseline.Marker);
                await ObserveUi("recovery", restored);
                SensorSession.PhysicalRestorationConfirmed = true;
            }
        }
        catch (Exception error)
        {
            failure = error;
            await Save("failure", new { Utc = DateTimeOffset.UtcNow, ErrorType = error.GetType().Name });
            throw;
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try
            {
                if (armed && alias != "button")
                {
                    if (!File.Exists(Path.Combine(folder, "restore-request.json")))
                        await Save("restore-request", new { RequestedUtc = DateTimeOffset.UtcNow,
                            Instruction = "Return this sensor to its original inactive state, even if recording failed." });
                    var final = SensorEventReading.Read(alias, await ReadDevice(cleanup.Token));
                    SensorSession.PhysicalRestorationConfirmed = baseline != null && final.Marker == baseline.Marker;
                    await Save("final-physical-state", final);
                }
            }
            finally
            {
                try { await RoomNavigation.Restore(target.Room, title, cleanup.Token); }
                catch (Exception error) when (failure != null)
                { throw new AggregateException("Event recording and Home restoration failed.", failure, error); }
            }
        }
        if (!SensorSession.PhysicalRestorationConfirmed) throw new InvalidDataException("Physical restoration not confirmed.");
        string assembly = typeof(SensorEventTests).Assembly.Location;
        await Save("complete", new { Outcome = "Passed", CompletedUtc = DateTimeOffset.UtcNow,
            session.Context.PackageSha256, session.Context.ReleaseSourceCommit, Target = target,
            FixtureAssemblySha256 = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(assembly))),
            Scope = "Physical sensor event reflected in visible detail rows; stateful sensor recovery. No Room-tile or physical-to-app timing assertion." });
    }
}

// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using CrestronHomeDevTools;
using CrestronHomeNUnit.Android;
using NUnit.Framework;

namespace KasaTapoCrestronDriver.AndroidTests;

internal sealed record SensorEventSnapshot(DateTimeOffset ObservedUtc, double Marker, Dictionary<string, string> Rows, string? Gesture = null);

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
                ["Last Press Time"] = Text("lastTriggerTimeDisplay") }, Text("lastGestureLabel"));
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
    internal static bool IsNewGesture(string expected, SensorEventSnapshot baseline, SensorEventSnapshot current) =>
        expected is "Single" or "Double" && IsNew("button", baseline, current) && current.Gesture == expected;

    internal static bool DisplayChanged(SensorEventSnapshot baseline, SensorEventSnapshot current) =>
        current.Rows.Any(p => !baseline.Rows.TryGetValue(p.Key, out var old) ||
            !string.Equals(old, p.Value, StringComparison.OrdinalIgnoreCase));

    internal static SensorEventSnapshot ReadTile(string alias, DeviceInfo device)
    {
        var reading = Read(alias, device);
        string property = alias == "button" ? "lastTriggerDisplay" : "sensorStatus";
        string text = device.PropertyValues[property].GetString() ?? "";
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Tile status missing.");
        return reading with { Rows = new() { ["Room tile"] = text } };
    }

    internal static bool TileMatches(AndroidHierarchy hierarchy, string room, string name, SensorEventSnapshot snapshot)
    {
        // Check visibility/title/icon/affordances before comparing the subtitle.
        // A wrong page or obscured tile is an error, not a pending status update.
        RoomNavigation.InspectTile(hierarchy, room, name, false);
        var rows = RoomNavigation.TileNode(hierarchy, name).Descendants("node").Where(n =>
            (string?)n.Attribute("resource-id") == CrestronHomePages.ResourcePrefix + "serviceSubtitle").ToArray();
        if (rows.Length != 1) throw new InvalidDataException("Ambiguous or missing Room tile subtitle.");
        return string.Equals((string?)rows[0].Attribute("text"), snapshot.Rows["Room tile"], StringComparison.OrdinalIgnoreCase);
    }

    internal static bool Matches(AndroidHierarchy hierarchy, string title, SensorEventSnapshot snapshot)
    {
        CrestronHomePages.RequireExtensionPage(hierarchy, title);
        return snapshot.Rows.All(pair => string.Equals(hierarchy.RequireUnique(new AndroidSelector(
            AndroidSelectorKind.ResourceId, CrestronHomePages.ResourcePrefix + "customdevice_textdisplay_firstlinetext")
            { SiblingText = pair.Key }).Text, pair.Value, StringComparison.OrdinalIgnoreCase));
    }
}

// Explicit selection is mandatory: physical tests require a provisioned operator inbox.
[TestFixture, NonParallelizable]
public sealed class SensorEventTests
{
    [TestCase("button", Explicit = true)]
    [TestCase("motion", Explicit = true)]
    [TestCase("contact", Explicit = true)]
    [TestCase("leak", Explicit = true)]
    public Task PhysicalEventReachesVisibleDetailPage(string alias) => Record(alias, false);

    [TestCase("button", Explicit = true)]
    [TestCase("motion", Explicit = true)]
    [TestCase("contact", Explicit = true)]
    [TestCase("leak", Explicit = true)]
    public Task PhysicalEventReachesVisibleRoomTile(string alias) => Record(alias, true);

    [TestCase("Single", Explicit = true)]
    [TestCase("Double", Explicit = true)]
    public Task ButtonGestureReachesVisibleRoomTile(string gesture) => Record("button", true, gesture);

    private static async Task Record(string alias, bool roomTile, string? gesture = null)
    {
        if (!SensorSession.PhysicalRestorationConfirmed)
            throw new InvalidOperationException("Resolve the preceding test's physical restoration before recording another event.");
        var session = SensorSession.Current!;
        var settings = SensorSession.Settings!;
        var inbox = settings.OperatorInbox ?? throw new InvalidDataException("Physical tests require a planned operator inbox before starting.");
        var target = SensorSession.Settings!.Sensors.Single(s => s.Alias == alias);
        int id = target.DeviceId > 0 ? target.DeviceId : session.Context.RequireManagedDevice(alias).DeviceId;
        string prefix = "sensor-event." + alias + (roomTile ? ".tile" : ".page") + (gesture == null ? "" : "." + gesture.ToLowerInvariant());
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
        SensorEventSnapshot Reading(DeviceInfo device) => roomTile
            ? SensorEventReading.ReadTile(alias, device) : SensorEventReading.Read(alias, device);
        bool Matches(AndroidHierarchy h, SensorEventSnapshot s) => roomTile
            ? SensorEventReading.TileMatches(h, target.Room, target.Name, s) : SensorEventReading.Matches(h, title, s);
        SensorEventSnapshot? baseline = null;
        bool armed = false;
        Exception? failure = null;
        async Task<SensorEventSnapshot> WaitFor(Func<SensorEventSnapshot, bool> predicate, CancellationToken observationToken)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(observationToken);
            wait.CancelAfter(TimeSpan.FromMinutes(5));
            while (true)
            {
                var reading = Reading(await ReadDevice(wait.Token));
                if (predicate(reading)) return reading;
                await Task.Delay(TimeSpan.FromSeconds(1), wait.Token);
            }
        }
        async Task<SensorEventSnapshot> Ask(string phase, string instructions, Func<SensorEventSnapshot,bool> predicate,
            CancellationToken actionToken, bool captureUi)
        {
            SubmissionOperatorHandle? handle = null;
            try {
            var result = await SubmissionPhysicalAction.ObserveAsync(inbox, prefix + "." + phase,
                $"{settings.ProcessorHost}: {target.Name} ({target.Model}, device {id})", instructions,
                TimeSpan.FromMinutes(5), async ct => {
                    var reading = await WaitFor(predicate, ct);
                    if(captureUi) await ObserveUi(phase, reading, ct);
                    return reading;
                }, actionToken, published => handle = published);
            return result.Observation;
            } finally { if(handle != null) await Save(phase + "-operator", SubmissionOperatorStep.Read(handle)); }
        }
        async Task ObserveUi(string phase, SensorEventSnapshot reading, CancellationToken observationToken)
        {
            // This is an observer interval from the API observation, NOT the physical
            // event time or exact first-visible app latency. No checklist timing claim.
            var clock = Stopwatch.StartNew();
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(observationToken);
            wait.CancelAfter(TimeSpan.FromSeconds(30));
            while (!Matches(await session.Device.CaptureAsync(wait.Token), reading))
                await Task.Delay(250, wait.Token);
            await session.CaptureAsync(prefix + "." + phase, h =>
            {
                if (!Matches(h, reading)) throw new InvalidDataException("App changed during capture.");
            }, wait.Token);
            await Save(phase, new { Reading = reading, AppRecordedUtc = DateTimeOffset.UtcNow,
                ObservationAndCaptureMilliseconds = clock.Elapsed.TotalMilliseconds });
        }
        try
        {
            await RoomNavigation.Open(target.Room, token);
            await RoomNavigation.RevealTile(target.Room, target.Name, token);
            if (!roomTile)
                await session.Device.TapAsync(RoomNavigation.Tile(target.Name),
                    h => RoomNavigation.InspectTile(h, target.Room, target.Name, false), token);
            baseline = Reading(await ReadDevice(token));
            if (alias != "button" && baseline.Marker != 0)
                throw new InvalidDataException("Start with no motion, contact closed or leak sensor dry, then retry in a new invocation.");
            await ObserveUi("baseline", baseline, token);
            // Navigation and capture finish before the user is asked to be ready.
            // The public coordinator retains the reservations and pauses only this declared wait.
            deadline.CancelAfter(Timeout.InfiniteTimeSpan);
            var readiness = await SubmissionPreparedReadiness.WaitAsync(inbox,
                $"{settings.ProcessorHost}: {target.Name} ({target.Model}, device {id})", token);
            deadline.CancelAfter(TimeSpan.FromMinutes(12));
            var handoff = Stopwatch.StartNew();
            if(readiness != null) await Save("prepared-readiness", readiness);
            // Events during an overnight readiness wait must never satisfy the upcoming test.
            baseline = Reading(await ReadDevice(token));
            if(alias != "button" && baseline.Marker != 0)
                throw new InvalidDataException("Sensor is active after readiness; preserve this attempt and restore it before a new recording.");
            armed = true;
            if (alias != "button") SensorSession.PhysicalRestorationConfirmed = false;
            string? expectedGesture = alias == "button"
                ? gesture ?? (baseline.Gesture == "Single" ? "Double" : "Single") : null;
            string instruction = expectedGesture == "Double" ? $"DOUBLE-PRESS {target.Name}, then choose Done. Do not navigate the app." :
                expectedGesture == "Single" ? $"Press {target.Name} ONCE, then choose Done. Do not navigate the app." :
                "Trigger this sensor once, then choose Done. Do not navigate the app. Wait for the restoration request before restoring this sensor.";
            await Save("ready", new { Alias = alias, DeviceId = id, ReadyUtc = DateTimeOffset.UtcNow,
                Baseline = baseline,
                Instruction = instruction });
            await Save("handoff", new { ReadyConsumedUtc = DateTimeOffset.UtcNow,
                WorkerElapsedMilliseconds = handoff.Elapsed.TotalMilliseconds,
                Meaning = "Worker preparation after readiness, before arming and publishing the physical action. Not cross-computer UTC subtraction." });
            var changed = await Ask("event", instruction,
                s => expectedGesture == null ? SensorEventReading.IsNew(alias, baseline, s) : SensorEventReading.IsNewGesture(expectedGesture, baseline, s), token, true);
            if (!SensorEventReading.DisplayChanged(baseline, changed))
                throw new InvalidDataException("Fresh event has no distinguishable display change; app feedback cannot be proved.");
            if (alias != "button")
            {
                await Save("restore-request", new { RequestedUtc = DateTimeOffset.UtcNow,
                    Instruction = "Close contact, dry leak sensor or leave motion detection area." });
                await Ask("recovery", "Restore this sensor: close the contact, dry the leak sensor, or leave the motion detection area. Then choose Done.",
                    s => s.Marker == baseline.Marker, token, true);
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
                    if(!SensorSession.PhysicalRestorationConfirmed)
                        await Ask("cleanup", "The test has ended or failed. Restore this sensor to its original inactive state now, then choose Done. This is restoration, not a request to trigger it again.",
                            s => baseline != null && s.Marker == baseline.Marker, cleanup.Token, false);
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
            Scope = roomTile
                ? "Physical sensor event reflected in unobscured Room tile text; stateful sensor recovery. No detail-page, icon-glyph or physical-to-app timing assertion."
                : "Physical sensor event reflected in visible detail rows; stateful sensor recovery. No Room-tile or physical-to-app timing assertion." });
    }
}

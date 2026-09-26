// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
using System.Diagnostics;
using System.Text.Json;
using KasaTapoClient;
using CrestronHomeNUnit.Android;
using NUnit.Framework;

namespace KasaTapoCrestronDriver.AndroidTests;

public sealed record PhysicalPowerTarget(string DiscoveryId, string AuthenticatedId, string? ChildId, bool ControlsAuthorized);
public sealed record PowerInterruptionSettings(string OutletAlias, PhysicalPowerTarget Supply, PhysicalPowerTarget[] CollateralLights);

// A preparatory recorder. It asserts a real selected-device interruption,
// API recovery and physical restoration, and retains app captures for review.
// It deliberately emits no checklist pass for app timing or whole-system power.
[TestFixture, NonParallelizable]
public sealed class PowerInterruptionTests
{
    internal sealed record LightSnapshot(bool On, int Brightness, int Temperature, int Hue, int Saturation, bool EffectEnabled);

    internal static void Validate(PowerInterruptionSettings plan, OutletTarget target)
    {
        bool Valid(PhysicalPowerTarget p) => p.ControlsAuthorized && !string.IsNullOrWhiteSpace(p.DiscoveryId) &&
            !string.IsNullOrWhiteSpace(p.AuthenticatedId);
        if (!target.ControlsAuthorized || target.ChildId != null || !Valid(plan.Supply) ||
            plan.Supply.DiscoveryId.Equals(target.DiscoveryId, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(plan.Supply.ChildId) || plan.CollateralLights.Length > 4 ||
            plan.CollateralLights.Any(p => !Valid(p) || p.ChildId != null) ||
            plan.CollateralLights.Select(p => p.AuthenticatedId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.CollateralLights.Length ||
            plan.CollateralLights.Any(p => p.AuthenticatedId.Equals(target.AuthenticatedId, StringComparison.OrdinalIgnoreCase) ||
                p.AuthenticatedId.Equals(plan.Supply.AuthenticatedId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Power interruption requires distinct, explicitly authorized physical bindings and a strip socket supply.");
    }

    [Test]
    public async Task RecordDevicePowerInterruptionAndRestore()
    {
        var session = SensorSession.Current!;
        var settings = SensorSession.Settings!;
        if (settings.PowerInterruption == null) Assert.Ignore("No authorized power-interruption bindings supplied.");
        var plan = settings.PowerInterruption!;
        var target = settings.Outlets?.Single(t => t.Alias == plan.OutletAlias)
            ?? throw new InvalidDataException("Power test outlet is missing.");
        Validate(plan, target);
        if (!SensorSession.PhysicalRestorationConfirmed || target.DeviceId <= 0 || settings.DeviceCredentialsFile == null)
            throw new InvalidOperationException("Earlier restoration or device resolution is incomplete.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var token = timeout.Token;
        var subject = new PhysicalPowerTarget(target.DiscoveryId, target.AuthenticatedId, null, true);
        string folder = Path.Combine(session.Context.EvidenceDirectory, "device-power-interruption");
        if (Directory.Exists(folder)) throw new IOException("Power recording already exists.");
        Directory.CreateDirectory(folder);
        async Task Record(string phase, object value) => await File.AppendAllTextAsync(Path.Combine(folder, "journal.jsonl"),
            JsonSerializer.Serialize(new { Utc = DateTimeOffset.UtcNow, Phase = phase, Value = value }) + Environment.NewLine);

        async Task<KasaDevice> Connect(PhysicalPowerTarget physical, CancellationToken ct)
        {
            try
            {
                using var input = JsonDocument.Parse(await File.ReadAllTextAsync(settings.DeviceCredentialsFile, ct));
                var auth = input.RootElement.GetProperty("credentials");
                var matches = (await Discover.DiscoverAsync(TimeSpan.FromSeconds(2), cancellationToken: ct))
                    .Where(d => string.Equals(d.DeviceId, physical.DiscoveryId, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Select(d => d.Host).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
                    throw new InvalidDataException("Physical identity not uniquely discovered.");
                var selected = matches.OrderByDescending(d => d.TpapPreferred == true || d.TpapMetadata != null).First();
                var device = await Discover.ConnectAsync(Discover.CreateConfiguration(selected,
                    new DeviceCredentials(auth.GetProperty("userName").GetString(), auth.GetProperty("password").GetString()), TimeSpan.FromSeconds(10)), ct);
                if (!string.Equals(device.SystemInfo?.DeviceId, physical.AuthenticatedId, StringComparison.OrdinalIgnoreCase))
                { device.Dispose(); throw new InvalidDataException("Authenticated identity differs."); }
                return device;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { throw new InvalidDataException("Power test physical connection failed (" + e.GetType().Name + ")."); }
        }
        async Task<bool> ReadPower(PhysicalPowerTarget physical, CancellationToken ct)
        {
            using var d = await Connect(physical, ct);
            return (physical.ChildId == null ? d.IsOn : d.GetChild(physical.ChildId)?.IsOn)
                ?? throw new InvalidDataException("Physical power state is missing.");
        }
        async Task SetPower(PhysicalPowerTarget physical, bool on, CancellationToken ct)
        {
            using var d = await Connect(physical, ct);
            if (physical.ChildId != null)
            { if (on) await d.TurnChildOnAsync(physical.ChildId, ct); else await d.TurnChildOffAsync(physical.ChildId, ct); }
            else { if (on) await d.TurnOnAsync(ct); else await d.TurnOffAsync(ct); }
            if (await ReadPower(physical, ct) != on) throw new InvalidDataException("Physical power change was not confirmed.");
        }
        async Task<LightSnapshot> ReadLight(PhysicalPowerTarget physical, CancellationToken ct)
        {
            using var d = await Connect(physical, ct);
            var light = d.LightState ?? throw new InvalidDataException("Expected light state.");
            if (light.IsOn is not bool on || light.Brightness is not int brightness || light.ColorTemperature is not int temperature ||
                light.Hue is not int hue || light.Saturation is not int saturation)
                throw new InvalidDataException("Complete restorable colour/light state is required before interruption.");
            return new(on, brightness, temperature, hue, saturation, light.Effect?.IsEnabled == true);
        }
        async Task<bool> DriverOnline(CancellationToken ct)
        {
            var d = await SensorSession.Api!.GetDeviceAsync(target.DeviceId, ct) ?? throw new InvalidDataException("Selected driver child is missing.");
            if (d.ParentDeviceId != session.Context.InstalledDriverId || d.Model != target.Model || d.Name != target.Name ||
                d.LocationId != target.LocationId || d.PropertyValues["controlDeviceId"].GetString() != target.DiscoveryId ||
                Version.Parse(d.PropertyValues["cp.driverInformation:version"].GetString()!) != Version.Parse(session.Context.DriverVersion))
                throw new InvalidDataException("Selected driver identity changed.");
            bool? Status(string key) => d.PropertyValues.TryGetValue(key, out var value) &&
                value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
            await Record("driver-status-properties", new {
                Online = Status("onlineIndicator:isOnline"), ExtensionOnline = Status("onlineIndicatorIsOnline"),
                Ready = Status("readyIndicator:isReady"), ExtensionReady = Status("readyIndicatorIsReady") });
            return d.PropertyValues["onlineIndicator:isOnline"].GetBoolean();
        }
        async Task WaitDriver(bool online, TimeSpan maximum, CancellationToken ct)
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < maximum)
            {
                bool actual = await DriverOnline(ct);
                await Record("driver-state", new { Expected = online, Actual = actual });
                if (actual == online) return;
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            throw new TimeoutException("Driver did not report the expected connection state within the bounded recording window.");
        }
        async Task Capture(string phase, CancellationToken ct)
        {
            await session.CaptureAsync("device-power." + phase, h => {
                CrestronHomePages.RequireRoom(h, target.Room);
                RoomNavigation.RequireVisibleRoomControl(h, target.Room, RoomNavigation.Tile(target.Name));
            }, ct);
            await Record("app-capture", new { Phase = phase, CompletedUtc = DateTimeOffset.UtcNow,
                AppOfflineStateAsserted = false });
        }

        bool originalSupply = await ReadPower(plan.Supply, token);
        bool originalSubject = await ReadPower(subject, token);
        if (!originalSupply || !await DriverOnline(token)) throw new InvalidDataException("Power test requires an energized supply and online subject.");
        var originalLights = new List<LightSnapshot>();
        foreach (var light in plan.CollateralLights)
        {
            var original = await ReadLight(light, token);
            if (original.EffectEnabled) throw new InvalidDataException("An initially active lighting effect needs a separately validated restoration path.");
            originalLights.Add(original);
        }
        await File.WriteAllTextAsync(Path.Combine(folder, "original.json"), JsonSerializer.Serialize(new {
            plan, OriginalSupply = originalSupply, OriginalSubject = originalSubject, OriginalLights = originalLights }), token);
        bool attempted = false;
        Exception? testFailure = null;
        try
        {
            await RoomNavigation.Open(target.Room, token);
            await RoomNavigation.RevealTile(target.Room, target.Name, token);
            await Capture("before", token);
            await Record("supply-off-intent", new { plan.Supply });
            attempted = true;
            SensorSession.PhysicalRestorationConfirmed = false;
            await SetPower(plan.Supply, false, token);
            var interrupted = Stopwatch.StartNew();
            await Record("supply-off-confirmed", new { Subject = subject.AuthenticatedId });
            try { await WaitDriver(false, TimeSpan.FromSeconds(120), token); }
            catch (TimeoutException)
            {
                // Retain the app's actual response even when the API condition
                // fails. A capture is diagnostic, never a substitute for a pass.
                using var captureTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { await Capture("offline-timeout", captureTimeout.Token); }
                catch (Exception captureError)
                { await Record("diagnostic-capture-failed", new { ErrorType = captureError.GetType().Name }); }
                throw;
            }
            await Record("driver-offline-observed", new { SincePowerOffSeconds = interrupted.Elapsed.TotalSeconds });
            await Capture("offline-observation", token);
            var remainder = TimeSpan.FromSeconds(60) - interrupted.Elapsed;
            if (remainder > TimeSpan.Zero) await Task.Delay(remainder, token);
            if (await ReadPower(plan.Supply, token)) throw new InvalidDataException("Supply changed during the interruption.");
            await Record("supply-on-intent", new { MinimumInterruptedSeconds = interrupted.Elapsed.TotalSeconds });
            await SetPower(plan.Supply, true, token);
            await Record("supply-on-confirmed", new { Subject = subject.AuthenticatedId });
            await WaitDriver(true, TimeSpan.FromSeconds(120), token);
            await Capture("recovery-observation", token);
        }
        catch (Exception e) { testFailure = e; throw; }
        finally
        {
            using var recovery = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            var ct = recovery.Token;
            try
            {
                if (attempted)
                {
                    var errors = new List<Exception>();
                    async Task Recover(Func<Task> action)
                    {
                        try { await action(); }
                        catch (Exception e) { errors.Add(e); }
                    }
                    await Recover(() => Record("restore-supply-intent", new { OriginalSupply = originalSupply }));
                    await Recover(() => SetPower(plan.Supply, originalSupply, ct));
                    // Allow devices to boot before reconnecting. This is restoration,
                    // not a timed recovery pass or an automatic replay of the test.
                    await Task.Delay(TimeSpan.FromSeconds(20), ct);
                    await Recover(() => SetPower(subject, originalSubject, ct));
                    for (int i = 0; i < plan.CollateralLights.Length; i++)
                    {
                        int index = i;
                        await Recover(async () =>
                        {
                            var expected = originalLights[index];
                            if (await ReadLight(plan.CollateralLights[index], ct) != expected)
                            {
                                using var d = await Connect(plan.CollateralLights[index], ct);
                                if (d.LightState?.Effect?.IsEnabled == true) await d.ClearLightEffectAsync(ct);
                                await d.SetHsvAsync(expected.Hue, expected.Saturation, expected.Brightness, ct);
                                if (expected.Temperature > 0) await d.SetColorTemperatureAsync(expected.Temperature, ct);
                                if (expected.On) await d.TurnLightOnAsync(ct); else await d.TurnLightOffAsync(ct);
                            }
                            if (await ReadLight(plan.CollateralLights[index], ct) != expected) throw new InvalidDataException("Collateral light restoration differs.");
                        });
                    }
                    if (errors.Count != 0) throw new AggregateException("Physical restoration needs attention; retained reservations must not be released.", errors);
                    await WaitDriver(true, TimeSpan.FromSeconds(90), ct);
                    if (await ReadPower(plan.Supply, ct) != originalSupply || await ReadPower(subject, ct) != originalSubject)
                        throw new InvalidDataException("Physical restoration differs.");
                    await File.WriteAllTextAsync(Path.Combine(folder, "restored.json"), JsonSerializer.Serialize(new {
                        VerifiedUtc = DateTimeOffset.UtcNow, OriginalSupply = originalSupply, OriginalSubject = originalSubject, Lights = originalLights }), ct);
                    SensorSession.PhysicalRestorationConfirmed = true;
                }
                await RoomNavigation.Restore(target.Room, null, ct);
            }
            catch (Exception recoveryError) when (testFailure != null)
            { throw new AggregateException("Power recording and restoration both failed.", testFailure, recoveryError); }
        }
        await File.WriteAllTextAsync(Path.Combine(folder, "scope.json"), JsonSerializer.Serialize(new {
            Scope = "Selected device power supply only; processor and network equipment stayed powered.",
            PhysicalRestorationConfirmed = SensorSession.PhysicalRestorationConfirmed,
            AppOfflinePresentation = "Captured for review, not asserted by this recorder.",
            WholeSystemPowerRequirementPassed = false, NetworkInterruptionTested = false }), token);
    }
}

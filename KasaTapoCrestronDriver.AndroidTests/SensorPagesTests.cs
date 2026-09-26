// Copyright (c) 2026 Neil Colvin. Licensed under the MIT License with Commons Clause.
using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using CrestronHomeDevTools;
using CrestronHomeNUnit.Android;
using NUnit.Framework;

namespace KasaTapoCrestronDriver.AndroidTests;

public sealed record SensorTarget(string Alias, int DeviceId, string Model, string Name, string Room, int LocationId,
    string[] DisplayProperties);
public sealed record OutletTarget(string Alias, int DeviceId, string Model, string Name, string Room,
    int LocationId, string DiscoveryId, string AuthenticatedId, string? ChildId, bool EnergyPage, bool ControlsAuthorized);
public sealed record LightTarget(int DeviceId, int WrapperId, string Model, string Name, string Room,
    int LocationId, string DiscoveryId, string AuthenticatedId, bool ControlsAuthorized);
public sealed record FixtureSettings(string ProcessorHost, string CredentialBindings, SensorTarget[] Sensors,
    string? DeviceCredentialsFile = null, OutletTarget[]? Outlets = null, LightTarget? Light = null,
    SubmissionEvidenceIdentity? EvidenceIdentity = null, bool PlatformConfigurationAuthorized = false)
{
    public static FixtureSettings Read(AndroidRunContext context)
    {
        string evidence = Path.GetFullPath(context.EvidenceDirectory);
        var stage = Directory.GetParent(evidence);
        if (Path.GetFileName(evidence) != "AndroidUI" || stage?.Name is not ("installed-app" or "nunit") || stage.Parent == null)
            throw new InvalidDataException("Use the public installed-app or NUnit workflow stage.");
        string path = Path.Combine(stage.Parent.FullName, "app-fixture-settings.json");
        if (!SubmissionEvidence.SafeEvidencePath(stage.Parent.FullName, "app-fixture-settings.json", out _) || new FileInfo(path).Length > 65536)
            throw new InvalidDataException("Invalid private app fixture settings.");
        var settings = JsonSerializer.Deserialize<FixtureSettings>(File.ReadAllText(path), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, AllowDuplicateProperties = false
        }) ?? throw new InvalidDataException("Missing fixture settings.");
        if (settings.ProcessorHost != context.ProcessorAddress || !Path.IsPathFullyQualified(settings.CredentialBindings) ||
            settings.Sensors.Length != 3 || settings.Sensors.Select(s => s.Alias).Distinct(StringComparer.Ordinal).Count() != 3 ||
            settings.Sensors.Any(s => s.Alias is not ("temperature" or "motion" or "button") || s.DisplayProperties.Length == 0 ||
                s.DisplayProperties.Any(p => p is not ("temperatureDisplay" or "humidityDisplay" or "batteryStatusLabel" or "motionStatusLabel" or "lastGestureLabel" or "lastTriggerTimeDisplay"))))
            throw new InvalidDataException("Sensor bindings do not match this fixture's read-only scope.");
        if (settings.EvidenceIdentity != null) AppEvidence.Validate(settings.EvidenceIdentity, context);
        return settings;
    }
}

[SetUpFixture]
public sealed class SensorSession
{
    internal static AndroidWorkflowSession? Current;
    internal static CrestronHomeNavigation? Navigation;
    internal static ConfigurationClient? Api;
    internal static FixtureSettings? Settings;
    internal static bool PhysicalRestorationConfirmed = true;

    [OneTimeSetUp]
    public async Task Open()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AndroidWorkflowSession.CONTEXT_VARIABLE)))
            Assert.Ignore("Invoke through the public installed-driver or deployment Android workflow.");
        Current = await AndroidWorkflowSession.OpenFromEnvironmentAsync();
        Navigation = new(Current);
        Settings = FixtureSettings.Read(Current.Context);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Saved bindings require Windows.");
        var credential = DevToolsCredentialBindings.Read(Settings.CredentialBindings)
            .Resolve(DevToolsCredentialPurpose.Processor, Settings.ProcessorHost);
        Api = await ConfigurationClient.ConnectAsync(new() { Host = Settings.ProcessorHost, CertificateSha256 = credential.CertificateSha256 },
            new NetworkCredential(credential.UserName, credential.Password));
        await Navigation.VerifySavedEndpointAsync("sensor.endpoint", Current.Context.Profile.LocalPort);
    }

    [OneTimeTearDown]
    public async Task Close()
    {
        if (Current == null) return;
        bool restored = false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            if (Navigation != null) { await Navigation.RestoreHomeAsync(timeout.Token); restored = Navigation.HomeRestored; }
        }
        finally
        {
            if (Api != null) await Api.DisposeAsync();
            Current.Complete(restored && PhysicalRestorationConfirmed);
        }
    }
}

[TestFixture, NonParallelizable]
public sealed class SensorPagesTests
{
    [TestCase("temperature")]
    [TestCase("motion")]
    [TestCase("button")]
    public async Task RoomSensorValuesMatchInstalledDriver(string alias)
    {
        var started = DateTimeOffset.UtcNow;
        var session = SensorSession.Current!;
        var target = SensorSession.Settings!.Sensors.Single(s => s.Alias == alias);
        int id = target.DeviceId > 0 ? target.DeviceId : session.Context.RequireManagedDevice(alias).DeviceId;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var token = timeout.Token;
        var device = await SensorSession.Api!.GetDeviceAsync(id, token) ?? throw new InvalidDataException("Sensor missing.");
        Assert.That(device.ParentDeviceId, Is.EqualTo(session.Context.InstalledDriverId));
        Assert.That(device.Model, Is.EqualTo(target.Model));
        Assert.That(device.Name, Is.EqualTo(target.Name));
        Assert.That(device.LocationId, Is.EqualTo(target.LocationId));
        Assert.That(Version.Parse(device.PropertyValues["cp.driverInformation:version"].GetString()!), Is.EqualTo(Version.Parse(session.Context.DriverVersion)));
        Assert.That(device.PropertyValues["onlineIndicator:isOnline"].GetBoolean(), Is.True);
        Assert.That(device.PropertyValues["readyIndicator:isReady"].GetBoolean(), Is.True);
        string title = device.PropertyValues["deviceLabel"].GetString() ?? throw new InvalidDataException("Sensor label missing.");
        var values = target.DisplayProperties.ToDictionary(p => p, p => device.PropertyValues[p].GetString()!);
        Assert.That(values.Values.All(v => !string.IsNullOrWhiteSpace(v)), Is.True);
        string Label(string property) => property switch
        {
            "temperatureDisplay" => "Temperature", "humidityDisplay" => "Humidity",
            "batteryStatusLabel" => "Battery", "motionStatusLabel" => "Motion",
            "lastGestureLabel" => "Last Press", "lastTriggerTimeDisplay" => "Last Press Time",
            _ => throw new InvalidDataException("Unsupported sensor row.")
        };
        // Verify the selected hardware's conditional rows, not just a caller-selected
        // subset of its display. Unsupported sensor types need separate hardware.
        var defined = alias == "button"
            ? new[] { ("lastGestureLabel", ""), ("lastTriggerTimeDisplay", ""), ("batteryStatusLabel", "hasBattery") }
            : new[] { ("leakStatusLabel", "hasLeak"), ("motionStatusLabel", "hasMotion"),
                ("contactStatusLabel", "hasContact"), ("temperatureDisplay", "hasTemperature"),
                ("humidityDisplay", "hasHumidity"), ("batteryStatusLabel", "hasBattery") };
        var expectedProperties = defined.Where(p => p.Item2 == "" || device.PropertyValues[p.Item2].GetBoolean())
            .Select(p => p.Item1).ToArray();
        Assert.That(target.DisplayProperties, Is.EquivalentTo(expectedProperties), "Complete selected sensor display binding");
        // These tiles only open telemetry pages. Never use this path for load tiles.
        await RoomNavigation.Open(target.Room, token);
        await RoomNavigation.RevealTile(target.Room, target.Name, token);
        await session.CaptureAsync("sensor." + alias + ".room-tile",
            h => RoomNavigation.InspectTile(h, target.Room, target.Name, false), token);
        await session.Device.TapAsync(RoomNavigation.Tile(target.Name),
            h => RoomNavigation.InspectTile(h, target.Room, target.Name, false), token);
        await session.CaptureAsync("sensor." + alias + ".controls", hierarchy =>
        {
            CrestronHomePages.RequireExtensionPage(hierarchy, title);
            var rows = XDocument.Parse(hierarchy.MaskedXml).Descendants("node").Where(n =>
                (string?)n.Attribute("package") == "com.crestron.phoenix.app" &&
                (string?)n.Attribute("resource-id") == CrestronHomePages.ResourcePrefix + "customdevice_textdisplay_firstlinetext").ToArray();
            Assert.That(rows, Has.Length.EqualTo(values.Count), "No missing or extra conditional display rows");
            foreach (var pair in values)
            {
                string label = Label(pair.Key);
                var row = hierarchy.RequireUnique(new AndroidSelector(AndroidSelectorKind.ResourceId,
                    CrestronHomePages.ResourcePrefix + "customdevice_textdisplay_firstlinetext") { SiblingText = label });
                // The app capitalizes status values. Keep the value bound to its
                // own labelled row while allowing that presentation difference.
                Assert.That(row.Text, Is.EqualTo(pair.Value).IgnoreCase, label);
            }
        }, token);
        await session.Device.TapAsync(CrestronHomePages.Resource("customdevices_toolbarClose"),
            h => CrestronHomePages.RequireExtensionPage(h, title), token);
        await session.CaptureAsync("sensor." + alias + ".room-returned", h => CrestronHomePages.RequireRoom(h, target.Room), token);
        await RoomNavigation.Restore(target.Room, title, token);
        string report = Path.Combine(session.Context.EvidenceDirectory, "sensor-" + alias + "-api.json");
        await using (var file = new FileStream(report, FileMode.CreateNew, FileAccess.Write))
            await JsonSerializer.SerializeAsync(file, new { DeviceId = id, target.Model, target.Name, Values = values, ObservedUtc = DateTimeOffset.UtcNow }, cancellationToken: token);
        AppEvidence.Write("sensor." + alias,
            "Verified the selected installed child's identity and ready state, its unobscured Room tile title, icon presence and absence of ellipsis; tapped the tile to open the expected read-only page; checked all conditional display rows for the selected sensor against the API snapshot; closed to the same Room and returned Home. No icon glyph, sensor stimulation, freshness, offline or response-time assertion.",
            started, started, started);
        foreach (var assertion in new[] {
            ("tile", "Unobscured Room tile has the expected title, an icon and no ellipsis. Icon glyph correctness is not asserted."),
            ("navigation", "Pressing the selected read-only Room tile opens its expected default detail page."),
            ("close", "Closing the detail page returns to the same Room, followed by a verified return Home."),
            ("display", "All conditional display rows for this selected sensor are present with their labelled API values; no extra display rows. No physical sensor stimulus or unsupported sensor variant asserted.") })
            AppEvidence.Write("sensor." + alias + "." + assertion.Item1, assertion.Item2, started, started, started);
    }
}

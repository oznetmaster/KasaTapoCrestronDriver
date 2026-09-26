// Copyright (c) 2026 Neil Colvin. Licensed under the MIT License with Commons Clause.
using System.Net;
using System.Text.Json;
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
    string? DeviceCredentialsFile = null, OutletTarget[]? Outlets = null, LightTarget? Light = null)
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
        // These pages only display telemetry. Never use this navigation on a tile that toggles a load.
        await SensorSession.Navigation!.InspectRoomExtensionAsync("sensor." + alias, target.Room, target.Name, title, hierarchy =>
        {
            foreach (var pair in values)
            {
                string label = pair.Key switch
                {
                    "temperatureDisplay" => "Temperature", "humidityDisplay" => "Humidity",
                    "batteryStatusLabel" => "Battery", "motionStatusLabel" => "Motion",
                    "lastGestureLabel" => "Last Press", "lastTriggerTimeDisplay" => "Last Press Time",
                    _ => throw new InvalidDataException("Unsupported sensor row.")
                };
                var row = hierarchy.RequireUnique(new AndroidSelector(AndroidSelectorKind.ResourceId,
                    CrestronHomePages.ResourcePrefix + "customdevice_textdisplay_firstlinetext") { SiblingText = label });
                // The app capitalizes status values. Keep the value bound to its
                // own labelled row while allowing that presentation difference.
                Assert.That(row.Text, Is.EqualTo(pair.Value).IgnoreCase, label);
            }
        }, token);
        string report = Path.Combine(session.Context.EvidenceDirectory, "sensor-" + alias + "-api.json");
        await using var file = new FileStream(report, FileMode.CreateNew, FileAccess.Write);
        await JsonSerializer.SerializeAsync(file, new { DeviceId = id, target.Model, target.Name, Values = values, ObservedUtc = DateTimeOffset.UtcNow }, cancellationToken: token);
        Assert.That(SensorSession.Navigation.HomeRestored, Is.True);
    }
}

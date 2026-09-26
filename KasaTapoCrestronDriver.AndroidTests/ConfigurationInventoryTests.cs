// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
using System.Text.Json;
using CrestronHomeDevTools;
using NUnit.Framework;

namespace KasaTapoCrestronDriver.AndroidTests;

// These assertions use the public configuration API. No Configure Pro visual claims.
[TestFixture, NonParallelizable]
public sealed class ConfigurationInventoryTests
{
    static JsonDocument Definition() => JsonDocument.Parse(typeof(ConfigurationInventoryTests).Assembly
        .GetManifestResourceStream("Kasa.ExpectedDefinition.json") ?? throw new InvalidDataException("Candidate definition missing."));
    static async Task<DeviceInfo> Root(CancellationToken ct)
    {
        var context = SensorSession.Current!.Context;
        var device = await SensorSession.Api!.GetDeviceAsync(context.InstalledDriverId, ct) ?? throw new InvalidDataException("Platform missing.");
        Assert.That(device.Model, Is.EqualTo("KasaTapoPlatform"));
        Assert.That(Version.Parse(device.PropertyValues["cp.driverInformation:version"].GetString()!), Is.EqualTo(Version.Parse(context.DriverVersion)));
        return device;
    }
    static async Task Record(string scope, object data, string rationale, DateTimeOffset started)
    {
        string path = Path.Combine(SensorSession.Current!.Context.EvidenceDirectory, "configuration-" + scope + "-api.json");
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            await JsonSerializer.SerializeAsync(file, new { ObservedUtc = DateTimeOffset.UtcNow, Data = data });
        AppEvidence.Write("configuration." + scope, rationale, started, started, started);
    }

    [Test]
    public async Task CatalogueAdvertisesExpectedPlatform()
    {
        var started = DateTimeOffset.UtcNow;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        using var definition = Definition();
        var general = definition.RootElement.GetProperty("GeneralInformation");
        var root = await Root(deadline.Token);
        var matches = (await SensorSession.Api!.GetDriversAsync(general.GetProperty("BaseModel").GetString()!, deadline.Token))
            .Where(d => d.Model == root.Model && d.Developer == general.GetProperty("Developer").GetProperty("Company").GetString() &&
                Version.TryParse(d.Version, out var version) && version == Version.Parse(SensorSession.Current!.Context.DriverVersion)).ToArray();
        Assert.That(matches, Has.Length.EqualTo(1));
        var catalogue = matches.Single();
        Assert.That(catalogue.Manufacturer, Is.EqualTo(general.GetProperty("Manufacturer").GetString()));
        Assert.That(catalogue.PrimaryUxCategory, Is.EqualTo(general.GetProperty("DeviceType").GetString()));
        Assert.That(catalogue.AdditionalFields!["ControlType"].GetString(), Is.EqualTo("cloud"));
        Assert.That(catalogue.AdditionalFields["IsExtensionDevice"].GetBoolean(), Is.False);
        Assert.That(general.GetProperty("OtherSupportedModels").GetArrayLength(), Is.Zero,
            "If additional catalogue models are declared, extend this inventory assertion.");
        await Record("catalogue", new { catalogue.Id, catalogue.Model, catalogue.Manufacturer, catalogue.Version, catalogue.Developer,
            catalogue.PrimaryUxCategory, ControlType = "cloud", IsExtensionDevice = false, AdditionalCatalogueModels = Array.Empty<string>() },
            "The configuration catalogue contains the exact installed version, TP-Link manufacturer, Platform category and KasaTapoPlatform model. The candidate declares no additional catalogue models. Package GUID/hash verification is supplied by the public runner; this does not assert certification or visual catalogue rendering.", started);
    }

    [Test]
    public async Task CloudConnectionExposesDefinedAccountFields()
    {
        var started = DateTimeOffset.UtcNow;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var root = await Root(deadline.Token);
        Assert.That(root.PropertyValues["cp.driverInformation:controlType"].GetString(), Is.EqualTo("cloud"));
        var snapshot = await DriverConfigurationInspection.GetAsync(SensorSession.Api!, root.Id, deadline.Token);
        var user = snapshot.Items.Single(i => i.Id == "UserName");
        var password = snapshot.Items.Single(i => i.Id == "Password");
        Assert.That(user.ValueType, Is.EqualTo("String"));
        Assert.That(user.Title, Is.EqualTo("Tapo User Name"));
        Assert.That(user.Masked, Is.False);
        Assert.That(password.ValueType, Is.EqualTo("String"));
        Assert.That(password.Title, Is.EqualTo("Tapo Password"));
        Assert.That(password.Masked, Is.True);
        Assert.That(new[] { user.ReadOnly, password.ReadOnly }, Is.All.False);
        Assert.That(root.Commands, Does.Contain("cp.driverConfiguration:getFirstConfigurationStep"));
        // Only metadata is retained; username and password values stay in memory.
        await Record("connection", new { root.Id, ControlType = "cloud", UserField = user.Id, PasswordField = password.Id,
            PasswordMasked = true, AccountFieldsWritable = true },
            "The configured platform advertises cloud control and the defined writable Tapo account fields, with password masking. Device discovery supplies endpoints; the candidate does not define an IP/IR/COM connection prompt. Processor SSH fields belong to the optional baseline workaround. This is advertised API-schema evidence, not a visual add-dialog observation.", started);
    }

    sealed record Field(string Id, string Title, string Description, string Type, bool Masked = false);
    static readonly Field Activation = new("ActivationMarker", "Ready", "Confirms the device configuration has been applied.", "Boolean");
    static readonly Field ReportInterval = new("ReportIntervalSeconds", "Report Interval (Seconds)", "The device's own internal sensor/button reporting interval, in seconds. Use 0 to leave the device's own default interval unchanged.", "String");
    static readonly Field DoubleClick = new("AllowDoubleClick", "Allow Double Click", "Enable double-click gesture detection and reporting on this button device.", "Boolean");
    static readonly Field TreatAsLight = new("TreatAsLight", "Treat As Light", "Expose this plug/outlet as a light entity when it controls a lamp or other lighting load.", "Boolean");

    [Test]
    public async Task PlatformAndSelectedChildSchemasMatchDefinedFields()
    {
        var started = DateTimeOffset.UtcNow;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var records = new List<object>();
        async Task Check(DeviceInfo device, Field[] expected, bool child)
        {
            var items = device.PropertyValues["cp.driverConfiguration:configurationItems"].EnumerateArray().ToArray();
            var publicItems = items.Where(i => i.GetProperty("Id").GetString() != "DriverDataStore").ToArray();
            Assert.That(publicItems.Select(i => i.GetProperty("Id").GetString()), Is.EquivalentTo(expected.Select(i => i.Id)));
            foreach (var field in expected)
            {
                var item = publicItems.Single(i => i.GetProperty("Id").GetString() == field.Id);
                Assert.That(item.GetProperty("Title").GetString(), Is.EqualTo(field.Title));
                Assert.That(item.GetProperty("Description").GetString(), Is.EqualTo(field.Description));
                Assert.That(item.GetProperty("ValueType").GetString(), Is.EqualTo(field.Type));
                var value = item.GetProperty("Value");
                Assert.That(value.GetProperty("Masked").GetBoolean(), Is.EqualTo(field.Masked));
                Assert.That(value.GetProperty("Persistent").GetBoolean(), Is.True);
                Assert.That(value.GetProperty("ReadOnly").GetBoolean(), Is.False);
                records.Add(new { device.Id, device.Model, Field = field, ReportedRequired = item.GetProperty("Required").GetBoolean(), Persistent = true, ReadOnly = false });
            }
            Assert.That(items.Length, Is.EqualTo(expected.Length + (child ? 1 : 0)));
            await Task.CompletedTask;
        }
        using var definition = Definition();
        var expectedRoot = definition.RootElement.GetProperty("ConfigurationSteps").GetProperty("Items").EnumerateArray().Select(i =>
            new Field(i.GetProperty("Id").GetString()!, i.GetProperty("Title").GetString()!, i.GetProperty("Description").GetString()!,
                i.GetProperty("ValueType").GetString()!, i.TryGetProperty("Masked", out var masked) && masked.GetBoolean())).ToArray();
        await Check(await Root(deadline.Token), expectedRoot, false);
        foreach (var target in SensorSession.Settings!.Sensors)
        {
            int id = target.DeviceId > 0 ? target.DeviceId : SensorSession.Current!.Context.RequireManagedDevice(target.Alias).DeviceId;
            var d = await SensorSession.Api!.GetDeviceAsync(id, deadline.Token) ?? throw new InvalidDataException("Child missing.");
            Assert.That(d.ParentDeviceId, Is.EqualTo(SensorSession.Current!.Context.InstalledDriverId));
            Assert.That(d.Model, Is.EqualTo(target.Model));
            await Check(d, target.Alias == "button" ? [Activation, DoubleClick, ReportInterval] : [Activation, ReportInterval], true);
        }
        if (SensorSession.Settings.Outlets is not { Length: 2 } outlets) Assert.Fail("Both outlet variants are required for schema coverage.");
        foreach (var target in SensorSession.Settings.Outlets!)
        {
            var d = await SensorSession.Api!.GetDeviceAsync(target.DeviceId, deadline.Token) ?? throw new InvalidDataException("Outlet missing.");
            Assert.That(d.ParentDeviceId, Is.EqualTo(SensorSession.Current!.Context.InstalledDriverId));
            Assert.That(d.Model, Is.EqualTo(target.Model));
            await Check(d, [Activation, TreatAsLight], true);
        }
        await Record("attributes", records, "Matched all advertised public field IDs, titles, descriptions, types, masking, persistence and writability for the platform, three selected sensor/button children and both selected outlet variants. Internal DriverDataStore contents and account values are excluded. Required flags are retained as reported after configuration, not equated to initial-wizard validation. Native load controls are covered separately.", started);
    }

    [Test]
    public async Task InstalledPlatformAndSelectedChildrenHaveExpectedPlacement()
    {
        var started = DateTimeOffset.UtcNow;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var root = await Root(deadline.Token);
        var settings = SensorSession.Settings!;
        var records = new List<object>();
        void Ready(DeviceInfo d)
        {
            Assert.That(d.PropertyValues["cp.driverConfiguration:isConfigured"].GetBoolean(), Is.True);
            Assert.That(d.PropertyValues["onlineIndicator:isOnline"].GetBoolean(), Is.True);
            Assert.That(d.PropertyValues["readyIndicator:isReady"].GetBoolean(), Is.True);
        }
        Ready(root);
        async Task Child(int id, string model, string name, int location)
        {
            var d = await SensorSession.Api!.GetDeviceAsync(id, deadline.Token) ?? throw new InvalidDataException("Child missing.");
            Assert.That(d.ParentDeviceId, Is.EqualTo(root.Id));
            Assert.That(d.Model, Is.EqualTo(model)); Assert.That(d.Name, Is.EqualTo(name)); Assert.That(d.LocationId, Is.EqualTo(location));
            Assert.That(Version.Parse(d.PropertyValues["cp.driverInformation:version"].GetString()!), Is.EqualTo(Version.Parse(SensorSession.Current!.Context.DriverVersion)));
            Ready(d);
            records.Add(new { d.Id, d.ParentDeviceId, d.Model, d.Name, d.LocationId, Ready = true });
        }
        foreach (var target in settings.Sensors)
            await Child(target.DeviceId > 0 ? target.DeviceId : SensorSession.Current!.Context.RequireManagedDevice(target.Alias).DeviceId, target.Model, target.Name, target.LocationId);
        Assert.That(settings.Outlets, Has.Length.EqualTo(2));
        foreach (var target in settings.Outlets!) await Child(target.DeviceId, target.Model, target.Name, target.LocationId);
        var light = settings.Light ?? throw new InvalidDataException("Native light binding required.");
        var wrapper = await SensorSession.Api!.GetDeviceAsync(light.WrapperId, deadline.Token) ?? throw new InvalidDataException("Native wrapper missing.");
        var load = await SensorSession.Api.GetDeviceAsync(light.DeviceId, deadline.Token) ?? throw new InvalidDataException("Native load missing.");
        Assert.That(wrapper.ParentDeviceId, Is.EqualTo(root.Id)); Assert.That(wrapper.Model, Is.EqualTo(light.Model));
        Assert.That(load.ParentDeviceId, Is.EqualTo(wrapper.Id)); Assert.That(load.Model, Is.EqualTo(light.Model));
        Assert.That(load.Name, Is.EqualTo(light.Name)); Assert.That(load.LocationId, Is.EqualTo(light.LocationId));
        records.Add(new { load.Id, load.ParentDeviceId, load.Model, load.Name, load.LocationId, WrapperId = wrapper.Id });
        await Record("installation", new { PlatformId = root.Id, PlatformReady = true, Children = records },
            "Verified installed platform readiness, all selected extension children configured/online/ready with matching parent/model/name/version/room, and the native wrapper/load parent chain and room assignment. Package and actual-platform identity are separately verified by the invoking public runner. No physical command or visual tile assertion.", started);
    }
}

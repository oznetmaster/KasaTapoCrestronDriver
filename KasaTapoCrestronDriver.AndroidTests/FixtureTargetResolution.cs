// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
using System.Text.Json;
using CrestronHomeDevTools;
using CrestronHomeNUnit.Android;

namespace KasaTapoCrestronDriver.AndroidTests;

// Resolve only within the workflow's verified candidate. This never commissions,
// renames, moves or controls a device, and never grants control authorization.
internal static class FixtureTargetResolution
{
    internal static FixtureSettings Resolve(FixtureSettings settings, AndroidRunContext context, IReadOnlyList<DeviceInfo> devices)
    {
        if (!settings.ResolveDeviceIds) return settings;
        // The real inventory also contains negative built-in gateway IDs.
        if (devices.Select(d => d.Id).Distinct().Count() != devices.Count)
            throw new InvalidDataException("Invalid device inventory.");
        var root = devices.SingleOrDefault(d => d.Id == context.InstalledDriverId);
        if (root == null || root.Id <= 0 || root.Model != "KasaTapoPlatform" || !VersionMatches(root, context.DriverVersion))
            throw new InvalidDataException("Candidate platform identity differs from the workflow.");
        DeviceInfo Select(int parent, string model, string name, int location)
        {
            var matches = devices.Where(d => d.ParentDeviceId == parent && d.Model == model && d.Name == name && d.LocationId == location).ToArray();
            if (matches.Length != 1 || matches[0].Id <= 0 || !VersionMatches(matches[0], context.DriverVersion))
                throw new InvalidDataException("Selected child identity is missing, ambiguous or belongs to another candidate version.");
            return matches[0];
        }
        var sensors = settings.Sensors.Select(t => t with { DeviceId = Select(root.Id, t.Model, t.Name, t.LocationId).Id }).ToArray();
        var outlets = settings.Outlets?.Select(t =>
        {
            var d = Select(root.Id, t.Model, t.Name, t.LocationId);
            string physical = t.DiscoveryId + (string.IsNullOrWhiteSpace(t.ChildId) ? "" : "/" + t.ChildId);
            if (!d.PropertyValues.TryGetValue("controlDeviceId", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != physical)
                throw new InvalidDataException("Resolved outlet differs from the authorized physical device/child.");
            return t with { DeviceId = d.Id };
        }).ToArray();
        LightTarget? light = null;
        if (settings.Light is {} target)
        {
            // Native loads live beneath an unlocated wrapper. Require the complete
            // candidate parent chain, not a similarly named light elsewhere.
            var loads = devices.Where(d => d.Model == target.Model && d.Name == target.Name && d.LocationId == target.LocationId &&
                devices.Any(w => w.Id == d.ParentDeviceId && w.ParentDeviceId == root.Id && w.Model == target.Model)).ToArray();
            if (loads.Length != 1) throw new InvalidDataException("The selected native light parent chain is missing or ambiguous.");
            var load = loads.Single();
            var wrapper = devices.Single(d => d.Id == load.ParentDeviceId);
            // Native wrappers do not always publish a driver version. Their
            // verified root and managed-device identity bind them to this candidate.
            if (load.Id <= 0 || wrapper.Id <= 0 ||
                wrapper.PropertyValues.ContainsKey("cp.driverInformation:version") && !VersionMatches(wrapper, context.DriverVersion))
                throw new InvalidDataException("Native light wrapper belongs to another candidate version.");
            if (!wrapper.PropertyValues.TryGetValue("platform:managedDevices", out var managed) || managed.ValueKind != JsonValueKind.Array ||
                managed.GetArrayLength() != 1 || !managed[0].TryGetProperty("Id", out var managedId) || managedId.ValueKind != JsonValueKind.String ||
                !string.Equals(managedId.GetString(), "device_" + target.DiscoveryId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Native light wrapper differs from the authorized physical light.");
            light = target with { DeviceId = load.Id, WrapperId = wrapper.Id };
        }
        var selected = sensors.Select(s => s.DeviceId).Concat(outlets?.Select(o => o.DeviceId) ?? []).Concat(light == null ? [] : new[] { light.DeviceId, light.WrapperId }).ToArray();
        if (selected.Distinct().Count() != selected.Length) throw new InvalidDataException("Selected fixture targets overlap.");
        return settings with { Sensors = sensors, Outlets = outlets, Light = light };
    }

    private static bool VersionMatches(DeviceInfo device, string expected) =>
        device.PropertyValues.TryGetValue("cp.driverInformation:version", out var value) && value.ValueKind == JsonValueKind.String &&
        Version.TryParse(value.GetString(), out var version) && Version.TryParse(expected, out var required) && version == required;

    internal static void Write(string path, FixtureSettings settings, int root)
    {
        using var file = new FileStream(path, FileMode.CreateNew);
        // Never serialize the full settings or the processor property bag.
        JsonSerializer.Serialize(file, new { PlatformId = root, settings.ResolveDeviceIds,
            Sensors = settings.Sensors.Select(t => new { t.Alias, t.DeviceId, t.Model, t.Name, t.LocationId }),
            Outlets = settings.Outlets?.Select(t => new { t.Alias, t.DeviceId, t.Model, t.Name, t.LocationId, t.ControlsAuthorized }),
            Light = settings.Light == null ? null : new { settings.Light.DeviceId, settings.Light.WrapperId, settings.Light.Model,
                settings.Light.Name, settings.Light.LocationId, settings.Light.ControlsAuthorized } });
    }
}

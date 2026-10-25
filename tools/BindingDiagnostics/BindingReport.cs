// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace BindingDiagnostics;

internal interface IReadOnlyFiles
{
    bool Exists(string path);
    long Size(string path);
    Stream OpenRead(string path);
}

internal static class BindingReport
{
    internal const string ManifestPath = "/user/Data/PyngDeviceManifest/DeviceManifest.cfg";
    internal const string CachePath = "/user/Data/ThirdParty/NeilColvin/KasaTapoCrestronDriver/managed-devices-cache.json";
    internal const int MaximumBytes = 16 * 1024 * 1024;
    internal const string DriverGuid = "EE8EF1AA-E993-4482-9F64-A79DCC7913B8";
    internal const string CatalogueKey = "tp-link.kasatapoplatform.cloud.neilcolvin";
    internal const string ToolVersion = "1.0.0";

    internal static JavaScriptSerializer Serializer() => new JavaScriptSerializer { MaxJsonLength = MaximumBytes, RecursionLimit = 128 };
    private static Dictionary<string, object> Object(object? value) => value as Dictionary<string, object> ?? new Dictionary<string, object>();
    private static object? Member(object? value, string key) => Object(value).TryGetValue(key, out var found) ? found : null;
    private static IEnumerable<object> Array(object? value) => value is object[] array ? array : Enumerable.Empty<object>();
    private static int? Number(object? value) => value is int number ? number : value is long big && big >= int.MinValue && big <= int.MaxValue ? (int)big : (int?)null;
    private static string? Text(object? value) => value is string text && text.Length <= 256 ? text : null;

    internal static string Read(IReadOnlyFiles remote, string path)
    {
        long size = remote.Size(path);
        if (size < 0 || size > MaximumBytes) throw new InvalidDataException("A source file exceeds the collection limit.");
        using var input = remote.OpenRead(path);
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[32768];
        long remaining = size;
        while (remaining > 0)
        {
            int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0) throw new IOException("A source file changed during collection. Please collect again.");
            bytes.Write(buffer, 0, read); remaining -= read;
        }
        return new UTF8Encoding(false, true).GetString(bytes.ToArray());
    }

    internal static void Collect(IReadOnlyFiles remote, string destination)
    {
        if (File.Exists(destination)) throw new IOException("Choose a new filename; an existing report will not be overwritten.");
        if (!remote.Exists(ManifestPath)) throw new FileNotFoundException("The Home device manifest was not found. No report was saved.");
        // Full source files exist in memory only. Only the fixed field selection below is saved.
        string filtered = Build(Read(remote, ManifestPath), remote.Exists(CachePath) ? Read(remote, CachePath) : null);
        string temporary = destination + ".collect-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(file, new UTF8Encoding(false))) writer.Write(filtered);
            File.Move(temporary, destination);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static string Build(string manifest, string? cache)
    {
        var serializer = Serializer();
        // DeserializeObject does not instantiate types named by Home's $type metadata.
        var document = Object(serializer.DeserializeObject(manifest));
        var references = new Dictionary<string, object>(StringComparer.Ordinal);
        int nodes = 0;
        void Index(object? value)
        {
            if (++nodes > 300000) throw new InvalidDataException("The Home manifest exceeds the object limit.");
            if (value is Dictionary<string, object> obj)
            {
                if (Member(obj, "$id") is string id)
                {
                    if (references.ContainsKey(id)) throw new InvalidDataException("The Home manifest contains duplicate references.");
                    references.Add(id, obj);
                }
                foreach (var child in obj.Values) Index(child);
            }
            else if (value is object[] array) foreach (var child in array) Index(child);
        }
        Index(document);
        object Resolve(object? value)
        {
            if (Member(value, "$ref") is string id)
            {
                if (!references.TryGetValue(id, out var found)) throw new InvalidDataException("The Home manifest contains an unresolved reference.");
                return found;
            }
            return value ?? new Dictionary<string, object>();
        }
        var devices = Object(Member(document, "Devices")).Where(p => !p.Key.StartsWith("$", StringComparison.Ordinal))
            .Select(p => Resolve(p.Value)).ToArray();
        if (devices.Length == 0) throw new InvalidDataException("The Home device list could not be read.");
        object Driver(object d) => Resolve(Member(d, "DriverInformation"));
        bool IsRoot(object d)
        {
            string? identity = Text(Member(Driver(d), "DriverGuid"));
            return Text(Member(d, "Model")) == "KasaTapoPlatform"
                && (string.Equals(identity, CatalogueKey, StringComparison.OrdinalIgnoreCase)
                    || Guid.TryParse(identity, out var guid) && guid == Guid.Parse(DriverGuid));
        }
        var roots = devices.Where(IsRoot).ToArray();
        if (roots.Length == 0) throw new InvalidDataException("No matching KasaTapoPlatform record was found. No configuration was exported.");
        var rootIds = roots.Select(d => Number(Member(d, "Id")) ?? throw new InvalidDataException("A platform ID was invalid.")).ToArray();
        int? ParentId(object d) => Number(Member(Resolve(Member(d, "ParentDeviceInfo")), "ParentDeviceId"));
        int? ParentInfoId(object d) => Number(Member(Resolve(Member(Resolve(Member(d, "ParentDeviceInfo")), "ParentInfo")), "Id"));

        var cacheRecords = new List<Dictionary<string, object?>>();
        var cacheIds = new HashSet<string>(StringComparer.Ordinal);
        if (cache != null)
        {
            var parsedCache = Object(serializer.DeserializeObject(cache));
            foreach (var item in Array(Member(parsedCache, "devices")))
            {
                var immutable = Member(item, "immutable") ?? item;
                var mutable = Member(item, "mutable") ?? item;
                var id = Text(Member(immutable, "controllerId"));
                if (id == null || !System.Text.RegularExpressions.Regex.IsMatch(id, @"^device_[a-z0-9_]+$"))
                    throw new InvalidDataException("A cache controller ID was invalid.");
                if (!cacheIds.Add(id)) throw new InvalidDataException("The cache contains duplicate controller IDs.");
                var row = new Dictionary<string, object?> { ["ControllerId"] = id };
                foreach (string key in new[] { "model", "uxCategory", "discoveredDeviceType", "managedLightKind" })
                {
                    object? value = Member(immutable, key);
                    if (value is string) value = Text(value);
                    if (value is string || value is int || value is long) row[key] = value;
                }
                if (Member(mutable, "isConfigured") is bool configured) row["IsConfigured"] = configured;
                cacheRecords.Add(row);
            }
        }

        object Describe(object d)
        {
            var row = new Dictionary<string, object?>
            {
                ["DeviceId"] = Number(Member(d, "Id")), ["Name"] = Text(Member(d, "Name")),
                ["Model"] = Text(Member(d, "Model")), ["RoomId"] = Number(Member(d, "RoomId")),
                ["ParentDeviceId"] = ParentId(d), ["ConfigurationNeeded"] = Number(Member(d, "ConfigurationNeeded")),
                ["ParentInfoDeviceId"] = ParentInfoId(d),
                ["DevType"] = Number(Member(d, "DevType")), ["InfoType"] = Number(Member(d, "InfoType")),
                ["DriverVersion"] = Text(Member(Driver(d), "DriverVersion")),
                ["SavedDriverIdentity"] = Text(Member(Driver(d), "DriverGuid")),
                ["DriverUxCategory"] = Text(Member(Driver(d), "DriverUxCategory"))
            };
            var bindings = new List<object>();
            foreach (var connection in Array(Member(d, "ConnectionInfos")))
            {
                var c = Resolve(connection);
                var id = Text(Member(c, "GatewayManagedDeviceId"));
                if (id != null && !System.Text.RegularExpressions.Regex.IsMatch(id, @"^device_[a-z0-9_]+$"))
                    throw new InvalidDataException("A saved managed-device identifier was not in the expected format.");
                bindings.Add(new { GatewayId = Number(Member(c, "GatewayId")), ControllerId = id,
                    PresentInDriverCache = id == null || cache == null ? (bool?)null : cacheIds.Contains(id),
                    CommProtocol = Number(Member(c, "CommProtocol")) });
            }
            row["Bindings"] = bindings;
            var attributes = new List<object>();
            foreach (var entry in Array(Member(d, "UserAttributes")))
            {
                var attr = Resolve(entry);
                string? id = Text(Member(attr, "AttributeId"));
                if (id is "ActivationMarker" or "TreatAsLight")
                {
                    bool valid = bool.TryParse(Member(attr, "PersistedValue") as string, out bool value);
                    attributes.Add(new { Id = id, HasBooleanValue = valid, BooleanValue = valid ? (bool?)value : null });
                }
                else if (id == "DriverDataStore")
                {
                    var value = Member(attr, "PersistedValue");
                    attributes.Add(new { Id = id, HasValue = value != null,
                        CharacterCount = value is string text ? (int?)text.Length : null });
                }
            }
            row["ConfigurationMarkers"] = attributes;
            return row;
        }
        // Home's canonical parent/child membership is the ChildDevices collection. Some
        // persisted ParentDeviceId values are defaults while ParentInfo carries the link.
        var selectedById = new Dictionary<int, object>();
        void Add(object d)
        {
            int id = Number(Member(d, "Id")) ?? throw new InvalidDataException("A selected Home device ID was invalid.");
            if (!selectedById.ContainsKey(id)) selectedById.Add(id, d);
        }
        foreach (var root in roots)
        {
            Add(root);
            foreach (var child in Array(Member(root, "ChildDevices"))) Add(Resolve(child));
        }
        foreach (var d in devices)
            if (ParentId(d) is int parent && rootIds.Contains(parent)
                || ParentInfoId(d) is int owner && rootIds.Contains(owner)
                || Array(Member(d, "ConnectionInfos")).Any(c => Number(Member(Resolve(c), "GatewayId")) is int gateway && rootIds.Contains(gateway))) Add(d);
        var selected = selectedById.Values.ToArray();
        return serializer.Serialize(new
        {
            Report = "Kasa/Tapo saved Home binding comparison", ToolVersion, CollectedUtc = DateTime.UtcNow.ToString("O"),
            Source = ManifestPath, CacheAvailable = cache != null,
            Note = "Filtered read-only snapshot. Source configuration files and credential values are not included. A matching ID alone does not prove all definition metadata is unchanged.",
            HomeRecords = selected.Select(Describe).ToArray(), DriverCacheRecords = cacheRecords
        });
    }
}

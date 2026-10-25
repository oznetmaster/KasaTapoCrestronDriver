// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace BindingDiagnostics;

internal static class SelfTests
{
    private sealed class Files : IReadOnlyFiles
    {
        internal readonly Dictionary<string, byte[]> Data = new Dictionary<string, byte[]>();
        internal readonly List<string> Reads = new List<string>();
        internal long? OverrideSize;
        public bool Exists(string path) => Data.ContainsKey(path);
        public long Size(string path) => OverrideSize ?? Data[path].LongLength;
        public Stream OpenRead(string path) { Reads.Add(path); return new MemoryStream(Data[path], false); }
    }
    internal static string Run()
    {
        var checks = new List<string>();
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException("Self-test failed: " + name); checks.Add(name); }
        void Reject(Action action)
        {
            try { action(); } catch (Exception e) when (e is IOException || e is InvalidDataException || e is ArgumentException) { return; }
            throw new InvalidOperationException("Expected rejection.");
        }
        string manifest = @"{""Devices"":{
          ""52677"":{""$id"":""root"",""Id"":52677,""Name"":""Lighting"",""Model"":""KasaTapoPlatform"",
            ""DriverInformation"":{""DriverGuid"":""GUID"",""DriverVersion"":""2.1.002.0004""},
            ""ParentDeviceInfo"":{""ParentDeviceId"":-6},""UserAttributes"":[{""AttributeId"":""Password"",""PersistedValue"":""SECRET_PASSWORD""}],
            ""ChildDevices"":[{""$id"":""child"",""Id"":53272,""Name"":""Test dimmer"",""Model"":""HS220(US)"",""RoomId"":52012,
              ""DriverInformation"":{""DriverVersion"":""2.1.002.0004"",""DriverUxCategory"":""Light""},
              ""ParentDeviceInfo"":{""ParentDeviceId"":52677,""ParentInfo"":{""$ref"":""root""}},
              ""ConnectionInfos"":[{""GatewayId"":52677,""GatewayManagedDeviceId"":""device_testabc"",""CommProtocol"":1,""Password"":""SECRET_CONNECTION""}],
              ""UserAttributes"":[{""AttributeId"":""ActivationMarker"",""PersistedValue"":""true""},{""AttributeId"":""DriverDataStore"",""PersistedValue"":""SECRET_STORE""}],
              ""UnrecognizedFutureField"":{""Token"":""SECRET_FUTURE""}}]},
          ""53272"":{""$ref"":""child""},
          ""99999"":{""Id"":99999,""Name"":""SECRET_UNRELATED"",""Model"":""Other"",""ParentDeviceInfo"":{""ParentDeviceId"":-6},""Password"":""SECRET_OTHER""}
        }}".Replace("GUID", BindingReport.DriverGuid);
        string cache = @"{""devices"":[{""immutable"":{""controllerId"":""device_testabc"",""model"":""HS220(US)"",""discoveredDeviceType"":4,""managedLightKind"":1},""mutable"":{""isConfigured"":true,""password"":""SECRET_CACHE"",""host"":""SECRET_HOST""}}]}";
        string report = BindingReport.Build(manifest, cache);
        Check(!report.Contains("SECRET_"), "credential, data-store and unrelated values excluded by fixed field selection");
        Check(!report.Contains("UnrecognizedFutureField") && !report.Contains("Password"), "unknown fields are not exported");
        Check(report.Contains("\"DeviceId\":53272") && report.Contains("device_testabc"), "Home reference objects resolve to the saved child mapping");
        Check(report.Contains("\"PresentInDriverCache\":true"), "matching Home and cache identifiers detected");
        Check(BindingReport.Build(manifest.Replace(BindingReport.DriverGuid, BindingReport.CatalogueKey), cache).Contains("\"DeviceId\":53272"), "Home catalogue-key driver identity accepted");
        Check(BindingReport.Build(manifest.Replace("\"ParentDeviceId\":52677", "\"ParentDeviceId\":0"), cache).Contains("\"DeviceId\":53272"), "canonical child membership retained when numeric parent field is default");
        Check(BindingReport.Build(manifest.Replace("device_testabc", "device_oldabc"), cache).Contains("\"PresentInDriverCache\":false"), "changed identifiers detected");
        Check(report.Contains("\"CharacterCount\":12") && !report.Contains("SECRET_STORE"), "data-store presence summarized without its content");
        Check(BindingReport.Build(manifest, null).Contains("\"CacheAvailable\":false"), "missing cache explicitly reported without inventing matches");
        Reject(() => BindingReport.Build(manifest.Replace(BindingReport.DriverGuid, Guid.NewGuid().ToString()), cache));
        Check(true, "unrelated driver GUID rejected");
        Reject(() => BindingReport.Build(manifest.Replace("\"$ref\":\"child\"", "\"$ref\":\"missing\""), cache));
        Check(true, "unresolved references rejected");
        Reject(() => BindingReport.Build(manifest.Replace("\"$id\":\"child\"", "\"$id\":\"root\""), cache));
        Check(true, "duplicate reference definitions rejected");
        Reject(() => BindingReport.Build(manifest, cache.Replace("device_testabc", "invalid identifier")));
        Check(true, "unexpected controller identifier format rejected");

        string directory = Path.Combine(Path.GetTempPath(), "kasa-binding-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var remote = new Files(); remote.Data[BindingReport.ManifestPath] = Encoding.UTF8.GetBytes(manifest);
            remote.Data[BindingReport.CachePath] = Encoding.UTF8.GetBytes(cache);
            string destination = Path.Combine(directory, "report.json");
            BindingReport.Collect(remote, destination);
            Check(!File.ReadAllText(destination).Contains("SECRET_") && remote.Reads.SequenceEqual(new[] { BindingReport.ManifestPath, BindingReport.CachePath }), "collector reads only the two declared files and saves filtered output");
            string original = File.ReadAllText(destination); int reads = remote.Reads.Count;
            Reject(() => BindingReport.Collect(remote, destination));
            Check(File.ReadAllText(destination) == original && remote.Reads.Count == reads, "existing local report preserved without collecting again");
            Reject(() => BindingReport.Collect(new Files(), Path.Combine(directory, "missing.json")));
            Check(!File.Exists(Path.Combine(directory, "missing.json")), "missing manifest leaves no output");
            remote.OverrideSize = BindingReport.MaximumBytes + 1L;
            Reject(() => BindingReport.Collect(remote, Path.Combine(directory, "oversized.json")));
            Check(!File.Exists(Path.Combine(directory, "oversized.json")), "oversized source rejected before saving");
            remote.OverrideSize = remote.Data[BindingReport.ManifestPath].LongLength + 1;
            Reject(() => BindingReport.Collect(remote, Path.Combine(directory, "short.json")));
            Check(Directory.GetFiles(directory).Length == 1, "short source read leaves no partial output");
        }
        finally { foreach (var file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
        return BindingReport.Serializer().Serialize(new { Passed = checks.Count, Checks = checks });
    }
}

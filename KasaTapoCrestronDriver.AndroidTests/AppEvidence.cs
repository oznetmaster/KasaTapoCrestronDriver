// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CrestronHomeDevTools;
using CrestronHomeNUnit.Android;

namespace KasaTapoCrestronDriver.AndroidTests;

// Optional structured output for consumers of the public evidence validator.
// Each ID describes only assertions made by its fixture, never an entire checklist.
internal static class AppEvidence
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    internal static void Validate(SubmissionEvidenceIdentity identity, AndroidRunContext context)
    {
        static bool Hash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
        if (!Hash(identity.PackageSha256) || !Hash(identity.PolicySha256) || !Hash(identity.TemplateSha256) ||
            !string.Equals(identity.PackageSha256, context.PackageSha256, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(context.ReleaseSourceCommit) || identity.SourceCommit != context.ReleaseSourceCommit)
            throw new InvalidDataException("Evidence identity differs from the selected release candidate.");
    }

    internal static void Write(string scope, string rationale, DateTimeOffset started,
        DateTimeOffset originalAt, DateTimeOffset actionAt, string? originalFile = null,
        string? restoredFile = null)
    {
        var session = SensorSession.Current!;
        var identity = SensorSession.Settings!.EvidenceIdentity;
        if (identity == null) return;
        WriteForContext(session.Context, identity, SensorSession.PhysicalRestorationConfirmed,
            scope, rationale, started, originalAt, actionAt, originalFile, restoredFile);
    }

    internal static void WriteForContext(AndroidRunContext context, SubmissionEvidenceIdentity identity,
        bool physicallyRestored, string scope, string rationale, DateTimeOffset started,
        DateTimeOffset originalAt, DateTimeOffset actionAt, string? originalFile, string? restoredFile)
    {
        Validate(identity, context);
        if (!physicallyRestored)
            throw new InvalidOperationException("Cannot emit passed evidence before physical restoration.");
        string[] parts = scope.Split('.');
        bool sensorDetail = parts.Length == 3 && parts[0] == "sensor" &&
            parts[1] is "temperature" or "motion" or "button" && parts[2] is "tile" or "navigation" or "close" or "display";
        bool outletDetail = parts.Length == 3 && parts[0] == "outlet" &&
            (parts[1] is "energy" or "basic" && parts[2] == "tile-action" ||
             parts[1] == "energy" && parts[2] is "navigation" or "display" or "close");
        if (!sensorDetail && !outletDetail && scope is not ("sensor.temperature" or "sensor.motion" or "sensor.button" or "outlet.energy" or "outlet.basic" or "native-light" or "configuration.platform" or
            "configuration.catalogue" or "configuration.connection" or "configuration.attributes" or "configuration.installation"))
            throw new InvalidDataException("Unknown app assertion scope.");
        string root = context.EvidenceDirectory;
        string stage = Directory.GetParent(root)!.Name;
        if (Path.GetFileName(root) != "AndroidUI" || stage is not ("installed-app" or "nunit"))
            throw new InvalidDataException("Use the workflow-owned Android evidence directory.");
        string Reference(string file)
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (!SubmissionEvidence.SafeEvidencePath(root, relative, out _))
                throw new InvalidDataException("Evidence file is outside the captured stage.");
            return stage + "/AndroidUI/" + relative;
        }
        var finished = DateTimeOffset.UtcNow;
        if (started > originalAt || originalAt > actionAt || actionAt > finished)
            throw new InvalidDataException("Evidence timestamps are out of order.");
        // Capture files are immutable within a session. Retain their exact digests;
        // earlier captures may provide shared endpoint/navigation context.
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).EndsWith("-observations.json", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).Select(f =>
            {
                using var stream = File.OpenRead(f);
                return new SubmissionEvidenceFile(Reference(f), Convert.ToHexStringLower(SHA256.HashData(stream)));
            }).ToArray();
        if (files.Length == 0) throw new InvalidDataException("No retained app evidence.");
        SubmissionRestorationObservation? restoration = null;
        if (originalFile != null || restoredFile != null)
        {
            if (originalFile == null || restoredFile == null || !File.Exists(originalFile) || !File.Exists(restoredFile))
                throw new InvalidDataException("Both original and restored observations are required.");
            restoration = new(originalAt, actionAt, finished, finished, true, Reference(originalFile), Reference(restoredFile));
        }
        var observation = new SubmissionObservation("kasa.app." + scope, identity, SubmissionEvidenceOutcome.Passed,
            started, finished, files, rationale, new SubmissionExecutionObservation("$kasa." + scope,
                scope.StartsWith("configuration.", StringComparison.Ordinal) ? "configuration" : "android", Restoration: restoration));
        using var output = new FileStream(Path.Combine(root, "kasa-" + scope + "-observations.json"), FileMode.CreateNew);
        JsonSerializer.Serialize(output, new SubmissionEvidenceDocument(1, [observation]), Json);
    }
}

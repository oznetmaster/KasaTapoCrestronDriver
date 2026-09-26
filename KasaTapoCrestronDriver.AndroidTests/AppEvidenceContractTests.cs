// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
using System.Text.Json;
using System.Text.Json.Serialization;
using CrestronHomeDevTools;
using CrestronHomeNUnit.Android;
using KasaTapoCrestronDriver.AndroidTests;
using NUnit.Framework;

// Separate namespace: these offline contracts do not open the hardware SetUpFixture.
namespace KasaAppEvidenceContracts;

[TestFixture, Category("unit")]
public sealed class AppEvidenceContractTests
{
    static readonly string Digest = new('a', 64);
    static readonly string Commit = new('b', 40);
    static SubmissionEvidenceIdentity Identity => new(Digest, Commit, Digest, Digest);
    static AndroidRunContext Context(string root) => new(1, "offline-contract", "synthetic", 1, 1,
        "example.invalid", 1, "synthetic", "1.0.0", Digest, Digest,
        new("unused", "unused", "unused", "unused", "unused"), root) { ReleaseSourceCommit = Commit };

    [Test]
    public void CandidateAndPolicyPinsAreMandatory()
    {
        var context = Context("unused");
        Assert.DoesNotThrow(() => AppEvidence.Validate(Identity, context));
        Assert.Throws<InvalidDataException>(() => AppEvidence.Validate(Identity with { PackageSha256 = new('c',64) }, context));
        Assert.Throws<InvalidDataException>(() => AppEvidence.Validate(Identity with { SourceCommit = new('c',40) }, context));
        Assert.Throws<InvalidDataException>(() => AppEvidence.Validate(Identity with { PolicySha256 = "invalid" }, context));
        Assert.Throws<InvalidDataException>(() => AppEvidence.Validate(Identity, context with { ReleaseSourceCommit = null }));
    }

    [TestCase("outlet.energy", "android")]
    [TestCase("configuration.platform", "configuration")]
    [TestCase("sensor.temperature.navigation", "android")]
    [TestCase("outlet.energy.tile-action", "android")]
    [TestCase("native-light.slider", "android")]
    public void StructuredOutputRetainsFilesAndRejectsUnrestoredOrRepeatedEvidence(string scope, string method)
    {
        string testRoot = Path.Combine(Path.GetTempPath(), "kasa-evidence-contract-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(testRoot, "installed-app", "AndroidUI");
        Directory.CreateDirectory(root);
        try
        {
            string original = Path.Combine(root, "original.json"), restored = Path.Combine(root, "restored.json");
            File.WriteAllText(original, "{\"synthetic\":true}");
            File.WriteAllText(restored, "{\"synthetic\":true}");
            var time = DateTimeOffset.UtcNow;
            void Write(bool restoredOk, string? originalPath = null) => AppEvidence.WriteForContext(Context(root), Identity,
                restoredOk, scope, "Synthetic contract check only.", time, time, time, originalPath ?? original, restored);
            Assert.Throws<InvalidOperationException>(() => Write(false));
            Assert.That(Directory.GetFiles(root, "*-observations.json"), Is.Empty);
            Assert.Throws<InvalidDataException>(() => Write(true, Path.Combine(testRoot, "missing.json")));
            Write(true);
            using var output = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "kasa-" + scope + "-observations.json")));
            var observation = output.RootElement.GetProperty("observations")[0];
            Assert.That(observation.GetProperty("requirementId").GetString(), Is.EqualTo("kasa.app." + scope));
            Assert.That(observation.GetProperty("execution").GetProperty("method").GetString(), Is.EqualTo(method));
            Assert.That(observation.GetProperty("files").GetArrayLength(), Is.EqualTo(2));
            Assert.That(observation.GetProperty("execution").GetProperty("restoration").GetProperty("matchesOriginal").GetBoolean(), Is.True);
            var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
            var document = JsonSerializer.Deserialize<SubmissionEvidenceDocument>(output.RootElement.GetRawText(), json)!;
            SubmissionRequirement[] requirements = [new("kasa.app." + scope, TimeSpan.Zero, Execution:
                new("$kasa." + scope, method, SubmissionEvidenceOutcome.Passed, null, true))];
            Assert.That(SubmissionEvidence.Evaluate(Identity, requirements, document.Observations, testRoot,
                DateTimeOffset.UtcNow).EvidenceChecksPassed, Is.True);
            File.AppendAllText(restored, " ");
            Assert.That(SubmissionEvidence.Evaluate(Identity, requirements, document.Observations, testRoot,
                DateTimeOffset.UtcNow).Issues.Any(i => i.Code == "evidence-digest"), Is.True);
            Assert.Throws<IOException>(() => Write(true));
        }
        finally { Directory.Delete(testRoot, recursive: true); }
    }
}

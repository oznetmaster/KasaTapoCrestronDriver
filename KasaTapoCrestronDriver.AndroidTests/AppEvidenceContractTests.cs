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
    [Test]
    public void ImportedOutageAndAppCapturesPassNormalStageEvidenceValidationAndRejectTampering()
    {
        string stageRoot=Path.Combine(Path.GetTempPath(),"kasa-outage-evidence-"+Guid.NewGuid().ToString("N"));
        string appRoot=Path.Combine(stageRoot,"installed-app","AndroidUI"),outageRoot=Path.Combine(appRoot,"system-outage");
        Directory.CreateDirectory(outageRoot);
        try {
            var context=Context(appRoot);
            string Hash(byte[] bytes)=>Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
            var json=new JsonSerializerOptions {PropertyNamingPolicy=JsonNamingPolicy.CamelCase,Converters={new JsonStringEnumConverter()}};
            string Write(string name,object value) {byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(value,json);File.WriteAllBytes(Path.Combine(outageRoot,name),bytes);return Hash(bytes);}
            var policy=new SubmissionEvidencePolicy(1,[new("system.power",TimeSpan.FromSeconds(60),false,new("system","outage",SubmissionEvidenceOutcome.Passed,60,true))]);
            string policyHash=Write("policy.json",policy);var identity=Identity with {PolicySha256=policyHash};
            var plan=new SubmissionOutageMeasurementPlan(identity,"system.power",["processor","device"],["control"],TimeSpan.FromSeconds(60),TimeSpan.FromSeconds(60),SubmissionOutageRecoveryClock.ProgramLoaded,"processor");
            string planHash=Write("plan.json",plan),rawHash=Write("capture.json",new {Synthetic=true});
            DateTimeOffset start=DateTimeOffset.UtcNow.AddMinutes(-5);
            SubmissionOutageCapture Capture(int second)=>new(start.AddSeconds(second),start.AddSeconds(second),new("capture.json",rawHash));
            var record=new SubmissionOutageMeasurementRecord(1,identity,[new("processor",Capture(10),Capture(75)),new("device",Capture(10),Capture(76))],
                Capture(80),[new("control",SubmissionEvidenceOutcome.Passed,Capture(81))],Capture(0),Capture(85),true);
            string recordHash=Write("record.json",record);
            foreach(string check in new[]{"system-outage.before","system-outage.on","system-outage.off"}) {
                string folder=Path.Combine(appRoot,check);Directory.CreateDirectory(folder);
                byte[] hierarchy="<synthetic/>"u8.ToArray(),screen="synthetic image bytes, not a live app screenshot"u8.ToArray();
                File.WriteAllBytes(Path.Combine(folder,"hierarchy.xml"),hierarchy);File.WriteAllBytes(Path.Combine(folder,"screen.png"),screen);
                File.WriteAllText(Path.Combine(folder,"observation.json"),JsonSerializer.Serialize(new {context.RunId,context.PackageSha256,context.ReleaseSourceCommit,
                    context.InstalledDriverId,CheckId=check,Outcome="Passed",HierarchySha256=Hash(hierarchy),ScreenshotSha256=Hash(screen)}));
            }
            var imported=SubmissionOutageEvidence.ImportFiles(outageRoot,"plan.json",planHash,"record.json",recordHash,"policy.json",DateTimeOffset.UtcNow);
            Assert.That(imported.Measurements.MeasurementChecksPassed,Is.True);
            var exported=AppEvidence.OutageForContext(context,imported.Observations);
            var observation=exported.Observations.Single();
            Assert.That(observation.Files.Count(f=>f.RelativePath.EndsWith("screen.png",StringComparison.Ordinal)),Is.EqualTo(3));
            Assert.That(observation.Execution!.Response!.TriggerEvidence,Does.StartWith("installed-app/AndroidUI/system-outage/"));
            Assert.That(observation.Execution.Restoration!.VerificationEvidence,Does.StartWith("installed-app/AndroidUI/system-outage/"));
            var result=SubmissionEvidence.Evaluate(identity,policy.Requirements,exported.Observations,stageRoot,DateTimeOffset.UtcNow);
            Assert.That(result.EvidenceChecksPassed,Is.True,JsonSerializer.Serialize(result.Issues));
            File.AppendAllText(Path.Combine(appRoot,"system-outage.on","screen.png"),"changed");
            Assert.That(SubmissionEvidence.Evaluate(identity,policy.Requirements,exported.Observations,stageRoot,DateTimeOffset.UtcNow).Issues.Any(i=>i.Code=="evidence-digest"),Is.True);
            Assert.Throws<InvalidDataException>(()=>AppEvidence.OutageForContext(context,imported.Observations));
        } finally {Directory.Delete(stageRoot,true);}
    }
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
    [TestCase("device-power.offline", "android")]
    [TestCase("device-power.recovery", "android")]
    [TestCase("outlet.basic.presentation", "android")]
    [TestCase("outlet.energy.presentation", "android")]
    [TestCase("sensor.temperature.presentation", "android")]
    [TestCase("sensor.motion.presentation", "android")]
    [TestCase("sensor.button.presentation", "android")]
    [TestCase("configuration.platform", "configuration")]
    [TestCase("sensor.temperature.navigation", "android")]
    [TestCase("outlet.energy.tile-action", "android")]
    [TestCase("native-light.slider", "android")]
    [TestCase("sensor.temperature.inventory", "android")]
    [TestCase("sensor.motion.inventory", "android")]
    [TestCase("sensor.button.inventory", "android")]
    [TestCase("outlet.energy.tile-inventory", "android")]
    [TestCase("outlet.basic.tile-inventory", "android")]
    [TestCase("outlet.energy.room-feedback", "android")]
    [TestCase("outlet.basic.room-feedback", "android")]
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
            bool power = scope.StartsWith("device-power.", StringComparison.Ordinal);
            void Write(bool restoredOk, string? originalPath = null) => AppEvidence.WriteForContext(Context(root), Identity,
                restoredOk, scope, "Synthetic contract check only.", time, time, time, originalPath ?? original, restored,
                power ? new(time, time, original, restored) : null);
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
                new("$kasa." + scope, method, SubmissionEvidenceOutcome.Passed, power ? 15 : null, true))];
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

// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CrestronHomeDevTools;
using CrestronHomeNUnit.Android;
using KasaTapoClient;
using NUnit.Framework;

namespace KasaTapoCrestronDriver.AndroidTests;

public sealed record OutageEndpoint(string Component, string Host, int[] Ports, PhysicalPowerTarget? Physical);
// Scope is part of the workflow's pinned fixture JSON. Release expansion supplies the
// actual candidate identity separately; no per-release plan file must be hand-edited.
public sealed record OutagePlanScope(string RequirementId, string[] RequiredComponents, string[] RequiredFunctions,
    TimeSpan MinimumInterruption, TimeSpan RecoveryLimit, SubmissionOutageRecoveryClock RecoveryClock,
    string? ProgramComponent = null);
public sealed record SystemOutageSettings(string AuthorizedProcessorHost, string[] NeverInterruptHosts,
    string OutletAlias, OutageEndpoint[] Endpoints, SubmissionManualOutageSettings Manual,
    OutagePlanScope[] Plans, string PolicyFile);

// Explicitly selected only in a separately reserved initial stage. Private bindings carry the
// operator instructions, forbidden hosts and exact candidate. No automatic processor reset exists.
[TestFixture, NonParallelizable]
public sealed class SystemOutageTests
{
    internal static readonly string[] Functions = ["configuration-preserved", "control-and-app-feedback"];
    internal static void ValidateNewEpoch(ProcessorUptimeSnapshot beforeBoot, ProcessorProgramUptimeSnapshot beforeProgram,
        ProcessorUptimeSnapshot boot, ProcessorProgramUptimeSnapshot program, DateTimeOffset recoveryStarted)
    {
        if (boot.EarliestStartUtc <= beforeBoot.LatestStartUtc || boot.EarliestStartUtc < recoveryStarted ||
            program.Program != beforeProgram.Program || program.EarliestStartUtc <= beforeProgram.LatestStartUtc ||
            program.EarliestStartUtc < boot.LatestStartUtc)
            throw new InvalidDataException("A new processor boot followed by the expected Home program start has not been established.");
    }
    internal static void ValidateSameEpoch(ProcessorUptimeSnapshot originalBoot, ProcessorProgramUptimeSnapshot originalProgram,
        ProcessorUptimeSnapshot boot, ProcessorProgramUptimeSnapshot program)
    {
        if (boot.EarliestStartUtc > originalBoot.LatestStartUtc || boot.LatestStartUtc < originalBoot.EarliestStartUtc ||
            program.Program != originalProgram.Program || program.EarliestStartUtc > originalProgram.LatestStartUtc ||
            program.LatestStartUtc < originalProgram.EarliestStartUtc)
            throw new InvalidDataException("Processor or Home program restarted during recovery verification, or the timing clock is inconsistent.");
    }
    internal static void Validate(SystemOutageSettings plan, FixtureSettings settings, int rootId)
    {
        static bool Literal(string host) => IPAddress.TryParse(host, out var ip) && ip.ToString() == host;
        if (rootId <= 0 || !Literal(plan.AuthorizedProcessorHost) || plan.AuthorizedProcessorHost != settings.ProcessorHost ||
            plan.NeverInterruptHosts.Length == 0 || plan.NeverInterruptHosts.Any(h => !Literal(h)) ||
            plan.NeverInterruptHosts.Contains(settings.ProcessorHost, StringComparer.Ordinal) ||
            settings.ResolveDeviceIds || settings.OperatorInbox != plan.Manual.Inbox || settings.DeviceCredentialsFile == null ||
            !Path.IsPathFullyQualified(settings.DeviceCredentialsFile) || settings.EvidenceIdentity == null ||
            !Path.IsPathFullyQualified(plan.PolicyFile) || plan.Plans.Length is < 1 or > 2 ||
            plan.Plans.Any(p => p == null) ||
            plan.Endpoints.Length is < 2 or > 6 || plan.Endpoints.Count(e => e.Component == "processor") != 1 ||
            plan.Endpoints.Select(e => e.Component).Distinct(StringComparer.Ordinal).Count() != plan.Endpoints.Length ||
            plan.Endpoints.Select(e => e.Host).Distinct(StringComparer.Ordinal).Count() != plan.Endpoints.Length ||
            plan.Endpoints.Any(e => !Literal(e.Host) || plan.NeverInterruptHosts.Contains(e.Host, StringComparer.Ordinal) ||
                e.Ports.Length is < 1 or > 3 || e.Ports.Distinct().Count() != e.Ports.Length || e.Ports.Any(p => p is < 1 or > 65535) ||
                (e.Component == "processor" ? e.Host != settings.ProcessorHost || e.Physical != null :
                 e.Physical is not { ControlsAuthorized: true, ChildId: null } || string.IsNullOrWhiteSpace(e.Physical.DiscoveryId) || string.IsNullOrWhiteSpace(e.Physical.AuthenticatedId))))
            throw new InvalidDataException("Outage bindings must select the permitted processor, exclude every protected host, and supply explicit independent endpoint and evidence bindings.");
        var outlet = settings.Outlets?.SingleOrDefault(t => t.Alias == plan.OutletAlias);
        if (outlet is not { ControlsAuthorized: true, ChildId: null, DeviceId: > 0 } ||
            !plan.Endpoints.Any(e => e.Physical?.DiscoveryId == outlet.DiscoveryId && e.Physical.AuthenticatedId == outlet.AuthenticatedId))
            throw new InvalidDataException("Select an authorized standalone outlet contained in the interruption scope.");
    }

    internal static readonly JsonSerializerOptions PlanJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, AllowDuplicateProperties = false,
        WriteIndented = true, Converters = { new JsonStringEnumConverter(allowIntegerValues: false) } };

    internal static SubmissionOutageMeasurementPlan[] PreparePlans(SystemOutageSettings scope, FixtureSettings settings, byte[] policy)
    {
        var identity = settings.EvidenceIdentity ?? throw new InvalidDataException("Missing candidate evidence identity.");
        if (scope.Plans.Length is < 1 or > 2 || !string.Equals(Hash(policy), identity.PolicySha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Outage scope or pinned policy is invalid.");
        var parsedPolicy = JsonSerializer.Deserialize<SubmissionEvidencePolicy>(policy, PlanJson) ?? throw new InvalidDataException("Empty outage policy.");
        var plans = scope.Plans.Select(p => new SubmissionOutageMeasurementPlan(identity, p.RequirementId,
            [.. p.RequiredComponents], [.. p.RequiredFunctions], p.MinimumInterruption, p.RecoveryLimit, p.RecoveryClock, p.ProgramComponent)).ToArray();
        if (plans.Select(p => p.RequirementId).Distinct(StringComparer.Ordinal).Count() != plans.Length ||
            plans.Any(p => !p.RequiredComponents.Order().SequenceEqual(scope.Endpoints.Select(e => e.Component).Order()) ||
                !p.RequiredFunctions.SequenceEqual(Functions) || p.MinimumInterruption < TimeSpan.FromSeconds(60) ||
                p.RecoveryLimit != TimeSpan.FromSeconds(60) ||
                p.RecoveryClock == SubmissionOutageRecoveryClock.ProgramLoaded && p.ProgramComponent != "processor"))
            throw new InvalidDataException("Outage plans differ from the pinned scope or required timing.");
        foreach (var plan in plans) SubmissionOutageEvidence.ValidatePlanPolicy(plan, parsedPolicy);
        return plans;
    }

    [Test, Explicit("Requires coordinated physical interruption of the separately authorized processor and demo equipment.")]
    public async Task ManualProcessorAndDeviceInterruptionRecoversControlAndApp()
    {
        var session = SensorSession.Current!;
        var settings = SensorSession.Settings!;
        var plan = settings.SystemOutage ?? throw new InvalidDataException("Explicit outage settings required.");
        Validate(plan, settings, session.Context.InstalledDriverId);
        if (!SensorSession.PhysicalRestorationConfirmed) throw new InvalidOperationException("Earlier restoration is unresolved.");
        byte[] ReadBounded(string path) => new FileInfo(path).Length <= 1024 * 1024 ? File.ReadAllBytes(path) : throw new InvalidDataException("Oversized outage input.");
        byte[] policy = ReadBounded(plan.PolicyFile);
        var plans = PreparePlans(plan, settings, policy);
        var planBytes = plans.Select(p => JsonSerializer.SerializeToUtf8Bytes(p, PlanJson)).ToArray();
        var planHashes = planBytes.Select(Hash).ToArray();
        string inputFolder = Path.Combine(session.Context.EvidenceDirectory, "system-outage-inputs");
        if (Directory.Exists(inputFolder)) throw new IOException("Outage inputs already exist; inspect without replaying.");
        Directory.CreateDirectory(inputFolder);
        void Save(string name, byte[] bytes) {
            using var output = new FileStream(Path.Combine(inputFolder, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            output.Write(bytes); output.Flush(flushToDisk: true);
        }
        Save("policy.json", policy);
        for (int i = 0; i < planBytes.Length; i++) Save("reviewed-plan-" + i + ".json", planBytes[i]);
        Save("manifest.json", JsonSerializer.SerializeToUtf8Bytes(new { Identity = settings.EvidenceIdentity, PlanHashes = planHashes }, PlanJson));
        // One physical episode may supply both separately assessed clocks; never ask twice merely
        // because the checklist has two rows. Missing program-load timing remains Partial.
        var capturePlan = (plans.FirstOrDefault(p => p.RecoveryClock == SubmissionOutageRecoveryClock.ProgramLoaded) ?? plans[0])
            with { MinimumInterruption = plans.Max(p => p.MinimumInterruption) };
        string folder = Path.Combine(session.Context.EvidenceDirectory, "system-outage");
        await using var observer = new Observer(plan, settings, session);
        await using var hardware = new SubmissionManualOutageHardware(plan.Manual, observer);
        var result = await SubmissionOutageRecorder.RecordAsync(capturePlan, hardware, folder,
            TimeSpan.FromMinutes(25), TimeSpan.FromMinutes(5));
        File.WriteAllBytes(Path.Combine(folder, "policy.json"), policy);
        var assessments = new List<SubmissionOutageMeasurementReport>();
        if (result.RecordRelativePath != null)
        {
            var recordPath = Path.Combine(folder, result.RecordRelativePath);
            string recordHash = Hash(File.ReadAllBytes(recordPath));
            for (int i = 0; i < plans.Length; i++)
            {
                string name = "reviewed-plan-" + i + ".json";
                byte[] bytes = planBytes[i];
                File.WriteAllBytes(Path.Combine(folder, name), bytes);
                var imported = SubmissionOutageEvidence.ImportFiles(folder, name, planHashes[i],
                    result.RecordRelativePath, recordHash, "policy.json", DateTimeOffset.UtcNow);
                // The normal producer inventory is rooted at the stage, not this subdirectory.
                // Retain partial raw imports too; only complete app captures can feed the gate.
                File.WriteAllText(Path.Combine(folder, "raw-observations-" + i + ".json"), JsonSerializer.Serialize(imported.Observations,PlanJson));
                var exported=AppEvidence.OutageForContext(session.Context,imported.Observations);
                File.WriteAllText(Path.Combine(folder, "observations-" + i + ".json"), JsonSerializer.Serialize(exported,
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, Converters = { new JsonStringEnumConverter() } }));
                assessments.Add(imported.Measurements);
            }
        }
        Assert.Multiple(() => {
            foreach(var assessment in assessments)
                Assert.That(assessment.MeasurementChecksPassed, Is.True, assessment.RequirementId + ": " + string.Join(", ", assessment.Issues));
            Assert.That(result.Passed, Is.True, string.Join(", ", result.Issues.Concat(result.Measurements?.Issues ?? [])));
        });
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private sealed record PhysicalState(bool? On, Dictionary<string, bool> Children, PowerInterruptionTests.LightSnapshot? Light);
    private sealed record PlatformState(string IdentitySha256, string ConfigurationSha256, DriverReadinessReport Readiness);

    private sealed class Observer(SystemOutageSettings plan, FixtureSettings settings, AndroidWorkflowSession session)
        : ISubmissionManualOutageObserver, ISubmissionManualRestorationBounds, IAsyncDisposable
    {
        public IReadOnlyList<string> Components => plan.Endpoints.Select(e => e.Component).ToArray();
        public IReadOnlyList<string> Functions => SystemOutageTests.Functions;
        private string _root = null!;
        private int _sequence;
        private readonly Dictionary<string, PhysicalState> _original = new();
        private PlatformState? _platform;
        private DateTimeOffset _baselineStarted;
        private ProcessorUptimeSnapshot? _boot;
        private ProcessorProgramUptimeSnapshot? _program;
        private ProcessorUptimeSnapshot? _recoveryBoot;
        private ProcessorProgramUptimeSnapshot? _recoveryProgram;
        private DateTimeOffset _recoveryStarted;
        private SubmissionOutageCapture? _programLoaded;
        private static readonly ProcessorProgramIdentity HomeProgram = new("/simpl/app00", "Crestron.Seawolf", "Crestron.Seawolf.dll");
        private DevToolsStoredCredential _saved = null!;
        private ConfigurationClient? _api;
        private ProcessorOperationLease? _lease;
        private OutletTarget Target => settings.Outlets!.Single(t => t.Alias == plan.OutletAlias);
        private NetworkCredential Login => new(_saved.UserName, _saved.Password);
        private SubmissionOutageCapture Save(string phase, DateTimeOffset first, object value)
            => SaveBounded(phase, first, DateTimeOffset.UtcNow, value);
        private SubmissionOutageCapture SaveBounded(string phase, DateTimeOffset first, DateTimeOffset last, object value)
        {
            string name = $"capture-{Interlocked.Increment(ref _sequence):D4}-{phase}.json";
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { EarliestUtc = first, LatestUtc = last, CapturedUtc = DateTimeOffset.UtcNow, Value = value });
            using(var output = new FileStream(Path.Combine(_root, name), FileMode.CreateNew)) { output.Write(bytes); output.Flush(true); }
            return new(first, last, new(name, Hash(bytes)));
        }
        public async Task PreflightAsync(SubmissionOutageRecordingContext context, CancellationToken token)
        {
            Validate(plan, settings, session.Context.InstalledDriverId);
            AppEvidence.Validate(context.Identity, session.Context);
            _root = context.EvidenceDirectory;
            _baselineStarted = DateTimeOffset.UtcNow;
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            _saved = DevToolsCredentialBindings.Read(settings.CredentialBindings).Resolve(DevToolsCredentialPurpose.Processor, settings.ProcessorHost);
            if (string.IsNullOrWhiteSpace(_saved.SshFingerprint)) throw new InvalidDataException("Pinned processor SSH identity required.");
            _lease = await ProcessorOperationLease.ResumeAsync(_saved.Host, Login, _saved.SshFingerprint, session.Context.RunId, token);
            _api = await ConfigurationClient.ConnectAsync(new() { Host = _saved.Host, CertificateSha256 = _saved.CertificateSha256 }, Login, token);
            _platform = await Platform(token);
            if (!_platform.Readiness.Ready) throw new InvalidDataException("Fix installed driver configuration/readiness before requesting an outage.");
            foreach(var endpoint in plan.Endpoints)
            {
                if (!(await Ports(endpoint, token)).Values.All(v => v)) throw new InvalidDataException("An outage endpoint is not reachable before the test.");
                if (endpoint.Physical != null) _original.Add(endpoint.Component, await Physical(endpoint, token));
            }
            // Direct physical binding and platform controlDeviceId must name the same outlet.
            var outlet = await _api.GetDeviceAsync(Target.DeviceId, token) ?? throw new InvalidDataException("Outlet absent.");
            if(outlet.ParentDeviceId != session.Context.InstalledDriverId || outlet.Model != Target.Model || outlet.Name != Target.Name ||
                outlet.LocationId != Target.LocationId || outlet.PropertyValues["controlDeviceId"].GetString() != Target.DiscoveryId ||
                !new[]{"outletOn","outletOff"}.All(outlet.Commands.Contains)) throw new InvalidDataException("Control target differs from the approved installed outlet.");
            _boot = await ProcessorUptime.ReadAsync(_saved.Host, Login, _saved.SshFingerprint, TimeSpan.FromSeconds(20), token);
            Save("boot-before", _boot.RequestSentUtc, _boot);
            _program = await ProcessorProgramUptime.ReadAsync(_saved.Host, Login, _saved.SshFingerprint, HomeProgram, TimeSpan.FromSeconds(20), token);
            Save("program-before", _program.RequestSentUtc, _program);
            await SensorSession.Navigation!.RestoreHomeAsync(token);
            await RoomNavigation.Open(Target.Room, token); await RoomNavigation.RevealTile(Target.Room, Target.Name, token);
            await session.CaptureAsync("system-outage.before", h => RoomNavigation.InspectTile(h, Target.Room, Target.Name, Target.EnergyPage), token);
        }
        public async Task<SubmissionOutageCapture> CaptureOriginalAsync(CancellationToken token) {
            if(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SubmissionPreparedReadiness.EnvironmentVariable))) {
                // Readiness may have waited overnight. Refresh physical/configuration/boot baselines
                // without repeating compilation, app selection, or UI navigation after Ready.
                var previous = _api;
                _api = null;
                _api = await PreparedProcessorSession.RenewAsync(previous,
                    ct => ConfigurationClient.ConnectAsync(new() { Host = _saved.Host,
                        CertificateSha256 = _saved.CertificateSha256 }, Login, ct), token);
                _baselineStarted=DateTimeOffset.UtcNow;
                _platform=await Platform(token);
                if(!_platform.Readiness.Ready)throw new InvalidDataException("Driver readiness changed while waiting for the operator.");
                _original.Clear();
                foreach(var endpoint in plan.Endpoints) {
                    if(!(await Ports(endpoint,token)).Values.All(v=>v))throw new InvalidDataException("Outage endpoint changed while waiting.");
                    if(endpoint.Physical!=null)_original.Add(endpoint.Component,await Physical(endpoint,token));
                }
                _boot=await ProcessorUptime.ReadAsync(_saved.Host,Login,_saved.SshFingerprint!,TimeSpan.FromSeconds(20),token);
                _program=await ProcessorProgramUptime.ReadAsync(_saved.Host,Login,_saved.SshFingerprint!,HomeProgram,TimeSpan.FromSeconds(20),token);
            }
            return Save("original", _baselineStarted,
            new { Processor = settings.ProcessorHost, RootDeviceId = session.Context.InstalledDriverId, Platform = _platform, Physical = _original,
                Boot = _boot, Program = _program, session.Context.PackageSha256, session.Context.ReleaseSourceCommit, Components = plan.Endpoints.Select(e => new { e.Component,e.Host,e.Ports }) });
        }
        private async Task<Dictionary<int,bool>> Ports(OutageEndpoint endpoint, CancellationToken token)
        {
            var result = new Dictionary<int,bool>();
            foreach(int port in endpoint.Ports) {
                using var tcp = new TcpClient(); using var budget = CancellationTokenSource.CreateLinkedTokenSource(token); budget.CancelAfter(1500);
                try { await tcp.ConnectAsync(endpoint.Host,port,budget.Token); result[port]=true; }
                catch(SocketException) { result[port]=false; }
                catch(OperationCanceledException) when(!token.IsCancellationRequested) { result[port]=false; }
            }
            return result;
        }
        public Task<IReadOnlyDictionary<string,SubmissionOutageCapture>> ObserveInterruptedAsync(CancellationToken token) {
            SensorSession.PhysicalRestorationConfirmed=false;
            return ObserveEndpoints(false,token);
        }
        public Task<IReadOnlyDictionary<string,SubmissionOutageCapture>> ObserveRestoredAsync(CancellationToken token) {
            _recoveryStarted=DateTimeOffset.UtcNow;
            return ObserveEndpoints(true,token);
        }
        private async Task<IReadOnlyDictionary<string,SubmissionOutageCapture>> ObserveEndpoints(bool online,CancellationToken token)
        {
            while(true) {
                var samples=new Dictionary<string,SubmissionOutageCapture>(); bool all=true;
                foreach(var endpoint in plan.Endpoints) {
                    var first=DateTimeOffset.UtcNow; var ports=await Ports(endpoint,token);
                    samples.Add(endpoint.Component,Save(online?"reachable":"unreachable",first,new{endpoint.Component,endpoint.Host,Ports=ports}));
                    all &= ports.Values.All(v=>v==online);
                }
                if(all)return samples;
                await Task.Delay(1000,token);
            }
        }
        public async Task WatchInterruptedAsync(CancellationToken token)
        {
            while(true) {
                foreach(var endpoint in plan.Endpoints) {
                    var first=DateTimeOffset.UtcNow;var ports=await Ports(endpoint,token);
                    Save("hold",first,new{endpoint.Component,Ports=ports});
                    if(ports.Values.Any(v=>v)) throw new InvalidDataException("An interrupted endpoint returned before the reconnect request.");
                }
                await Task.Delay(2000,token);
            }
        }
        public async Task<IReadOnlyDictionary<string,SubmissionOutageCapture>> CaptureRestoredByAsync(CancellationToken token)
        {
            var boot=await ProcessorUptime.ReadAsync(_saved.Host,Login,_saved.SshFingerprint!,TimeSpan.FromSeconds(20),token);
            var program=await ProcessorProgramUptime.ReadAsync(_saved.Host,Login,_saved.SshFingerprint!,HomeProgram,TimeSpan.FromSeconds(20),token);
            // Preserve replies even if they contradict the expected interruption.
            var proof=SaveBounded("power-restored-by-new-boot",boot.EarliestStartUtc,boot.LatestStartUtc,
                new { BeforeBoot=_boot, BeforeProgram=_program, Boot=boot, Program=program, RecoveryObservationStartedUtc=_recoveryStarted,
                    Meaning="New boot establishes power was restored no later than LatestStartUtc; the operator request supplies the lower bound." });
            ValidateNewEpoch(_boot!,_program!,boot,program,_recoveryStarted);
            _recoveryBoot=boot;_recoveryProgram=program;
            _programLoaded=SaveBounded("program-start",program.EarliestStartUtc,program.LatestStartUtc,program);
            return new Dictionary<string,SubmissionOutageCapture>{{"processor",proof}};
        }
        public Task<SubmissionOutageCapture?> ObserveProgramLoadedAsync(string component,CancellationToken token)
            => Task.FromResult(component=="processor"?_programLoaded:throw new InvalidDataException("Unknown program component."));
        private async Task VerifyRecoveryEpoch(CancellationToken token)
        {
            var boot=await ProcessorUptime.ReadAsync(_saved.Host,Login,_saved.SshFingerprint!,TimeSpan.FromSeconds(20),token);
            var program=await ProcessorProgramUptime.ReadAsync(_saved.Host,Login,_saved.SshFingerprint!,HomeProgram,TimeSpan.FromSeconds(20),token);
            Save("recovery-epoch",boot.RequestSentUtc,new { Boot=boot,Program=program,OriginalBoot=_recoveryBoot,OriginalProgram=_recoveryProgram });
            ValidateSameEpoch(_recoveryBoot!,_recoveryProgram!,boot,program);
        }
        private async Task ReconnectApi(CancellationToken token)
        {
            if(_api!=null) {await _api.DisposeAsync();_api=null;}
            while(true) {
                token.ThrowIfCancellationRequested(); using var attempt=CancellationTokenSource.CreateLinkedTokenSource(token);attempt.CancelAfter(TimeSpan.FromSeconds(12));
                try { _api=await ConfigurationClient.ConnectAsync(new(){Host=_saved.Host,CertificateSha256=_saved.CertificateSha256},Login,attempt.Token);break; }
                catch(Exception e) when(e is HttpRequestException or IOException or TaskCanceledException) {
                    token.ThrowIfCancellationRequested();Save("api-not-ready",DateTimeOffset.UtcNow,new{ErrorType=e.GetType().Name});
                    await Task.Delay(1000,token);
                }
            }
            await _lease!.VerifyAfterReconnectAsync(_saved.Host,token);
        }
        private async Task<PlatformState> Platform(CancellationToken token)
        {
            var devices=await _api!.GetDevicesAsync(token);
            var readiness=DriverReadiness.Inspect(devices,session.Context.InstalledDriverId,"KasaTapoPlatform",session.Context.DriverVersion);
            var selected=devices.Where(d=>readiness.InspectedDeviceIds.Contains(d.Id)).OrderBy(d=>d.Id).ToArray();
            var values=new List<object>();
            foreach(var device in selected) {
                var configuration=await DriverConfigurationInspection.GetAsync(_api,device.Id,token);
                values.AddRange(configuration.Items.Where(i=>!i.Masked && i.CurrentValue!=null).OrderBy(i=>i.Id)
                    .Select(i=>(object)new{Device=device.Id,i.Id,Value=i.CurrentValue!.Value.GetRawText()}));
            }
            return new(Hash(JsonSerializer.SerializeToUtf8Bytes(selected.Select(d=>new{d.Id,d.Name,d.Model,d.ParentDeviceId,d.LocationId}))),
                Hash(JsonSerializer.SerializeToUtf8Bytes(values)),readiness);
        }
        public async Task<SubmissionOutageFunction> VerifyFunctionAsync(string function,CancellationToken token)
        {
            var first=DateTimeOffset.UtcNow;
            if(function=="configuration-preserved") {
                await ReconnectApi(token);
                PlatformState current;
                while(true) {current=await Platform(token);if(current.Readiness.Ready)break;Save("not-ready",DateTimeOffset.UtcNow,current.Readiness);await Task.Delay(1000,token);}
                await VerifyRecoveryEpoch(token);
                bool matches=current.IdentitySha256==_platform!.IdentitySha256 && current.ConfigurationSha256==_platform.ConfigurationSha256;
                var capture=Save(function,first,new{Platform=current,Boot=_recoveryBoot,Program=_recoveryProgram,Matches=matches});
                if(!matches)throw new InvalidDataException("Configuration/identity or processor reboot verification failed; no outlet command will be sent.");
                return new(function,SubmissionEvidenceOutcome.Passed,capture);
            }
            if(function!="control-and-app-feedback")throw new InvalidDataException("Unknown outage function.");
            var endpoint=plan.Endpoints.Single(e=>e.Physical?.DiscoveryId==Target.DiscoveryId);
            bool original=_original[endpoint.Component].On ?? throw new InvalidDataException("Outlet power baseline missing.");
            await SensorSession.Navigation!.RestoreHomeAsync(token);await RoomNavigation.Open(Target.Room,token);await RoomNavigation.RevealTile(Target.Room,Target.Name,token);
            foreach(bool expected in new[]{!original,original}) {
                await _api!.ExecuteDeviceCommandAsync(Target.DeviceId,expected?"outletOn":"outletOff",null,token);
                while(true) {
                    bool physical=(await Physical(endpoint,token)).On==expected;
                    var device=await _api.GetDeviceAsync(Target.DeviceId,token) ?? throw new InvalidDataException("Outlet disappeared.");
                    if(physical && device.PropertyValues["outletIsOn"].GetBoolean()==expected)break;
                    await Task.Delay(500,token);
                }
                while(true) {
                    var h=await session.Device.CaptureAsync(token);
                    try {RoomNavigation.InspectTile(h,Target.Room,Target.Name,Target.EnergyPage,expected?"ON":"OFF");break;}
                    catch(InvalidDataException) {await Task.Delay(500,token);}
                }
                await session.CaptureAsync("system-outage."+(expected?"on":"off"),h=>RoomNavigation.InspectTile(h,Target.Room,Target.Name,Target.EnergyPage,expected?"ON":"OFF"),token);
            }
            await VerifyRecoveryEpoch(token);
            return new(function,SubmissionEvidenceOutcome.Passed,Save(function,first,new{Target.DeviceId,RestoredPower=original,
                AppCaptures=AppEvidence.OutageAppCaptures(session.Context),Method="Driver API commands, authenticated physical state and visible unobscured Room tile."}));
        }
        private async Task<KasaDevice> Connect(OutageEndpoint endpoint,CancellationToken token)
        {
            var physical=endpoint.Physical!;
            using var input=JsonDocument.Parse(await File.ReadAllTextAsync(settings.DeviceCredentialsFile!,token));var auth=input.RootElement.GetProperty("credentials");
            var found=await Discover.DiscoverAsync(TimeSpan.FromSeconds(2),cancellationToken:token);
            var matches=found.Where(d=>d.DeviceId==physical.DiscoveryId).ToArray();
            if(matches.Length==0 || matches.Any(d=>d.Host!=endpoint.Host))throw new InvalidDataException("Physical identity or host differs from the outage binding.");
            var selected=matches.OrderByDescending(d=>d.TpapPreferred==true || d.TpapMetadata!=null).First();
            var device=await Discover.ConnectAsync(Discover.CreateConfiguration(selected,new DeviceCredentials(auth.GetProperty("userName").GetString(),auth.GetProperty("password").GetString()),TimeSpan.FromSeconds(10)),token);
            if(device.SystemInfo?.DeviceId!=physical.AuthenticatedId) {device.Dispose();throw new InvalidDataException("Physical authenticated identity differs.");}
            return device;
        }
        private static PhysicalState State(KasaDevice device)
        {
            PowerInterruptionTests.LightSnapshot? light=null;
            if(device.LightState is {} state) {
                if(state.IsOn is not bool on || state.Brightness is not int b || state.ColorTemperature is not int t || state.Hue is not int h || state.Saturation is not int s || state.Effect?.IsEnabled==true)
                    throw new InvalidDataException("Light needs complete static restorable state.");
                light=new(on,b,t,h,s,false);
            }
            return new(device.IsOn,device.Children.ToDictionary(c=>c.Id,c=>c.IsOn ?? throw new InvalidDataException("Child power state missing.")),light);
        }
        private async Task<PhysicalState> Physical(OutageEndpoint endpoint,CancellationToken token) {using var device=await Connect(endpoint,token);return State(device);}
        private static bool Same(PhysicalState a,PhysicalState b)=>a.On==b.On && a.Light==b.Light && a.Children.OrderBy(p=>p.Key).SequenceEqual(b.Children.OrderBy(p=>p.Key));
        public async Task<SubmissionOutageRestoredState> RestoreOriginalAsync(SubmissionOutageCapture original,CancellationToken token)
        {
            var first=DateTimeOffset.UtcNow;var errors=new List<string>();
            // Restore supply strips before the standalone devices plugged into them. Every target
            // gets its own attempt even if another device fails; no driver command is needed here.
            foreach(var endpoint in plan.Endpoints.Where(e=>e.Physical!=null).OrderByDescending(e=>_original[e.Component].Children.Count)) {
                try {
                    var expected=_original[endpoint.Component];using var device=await Connect(endpoint,token);
                    if(Same(expected,State(device)))continue;
                    foreach(var child in expected.Children) {
                        if(child.Value)await device.TurnChildOnAsync(child.Key,token);else await device.TurnChildOffAsync(child.Key,token);
                    }
                    if(expected.Light is {} light) {
                        if(device.LightState?.Effect?.IsEnabled==true)await device.ClearLightEffectAsync(token);
                        await device.SetHsvAsync(light.Hue,light.Saturation,light.Brightness,token);
                        if(light.Temperature>0)await device.SetColorTemperatureAsync(light.Temperature,token);
                        if(light.On)await device.TurnLightOnAsync(token);else await device.TurnLightOffAsync(token);
                    } else if(expected.Children.Count==0 && expected.On is {} on) {
                        if(on)await device.TurnOnAsync(token);else await device.TurnOffAsync(token);
                    }
                    if(!Same(expected,await Physical(endpoint,token)))throw new InvalidDataException("Physical original state did not restore.");
                } catch(Exception error) {errors.Add(endpoint.Component+":"+error.GetType().Name);}
            }
            try {await ReconnectApi(token);var current=await Platform(token);if(!current.Readiness.Ready || current.IdentitySha256!=_platform!.IdentitySha256 || current.ConfigurationSha256!=_platform.ConfigurationSha256)errors.Add("platform-restoration-differs");}
            catch(Exception error) {errors.Add("platform:"+error.GetType().Name);}
            try {await SensorSession.Navigation!.RestoreHomeAsync(token);}catch(Exception error){errors.Add("app:"+error.GetType().Name);}
            SensorSession.PhysicalRestorationConfirmed=errors.Count==0;
            return new(Save("restored",first,new{MatchesOriginal=errors.Count==0,Issues=errors,ExpectedPhysical=_original}),errors.Count==0);
        }
        public async ValueTask DisposeAsync() {try {if(_api!=null)await _api.DisposeAsync();}finally {_lease?.Dispose();}}
    }
}

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
public sealed record OutagePlanInput(string Path, string Sha256);
public sealed record SystemOutageSettings(string AuthorizedProcessorHost, string[] NeverInterruptHosts,
    string OutletAlias, OutageEndpoint[] Endpoints, SubmissionManualOutageSettings Manual,
    OutagePlanInput[] Plans, string PolicyFile);

// Explicitly selected only in a separately reserved initial stage. Private bindings carry the
// operator instructions, forbidden hosts and exact candidate. No automatic processor reset exists.
[TestFixture, NonParallelizable]
public sealed class SystemOutageTests
{
    internal static readonly string[] Functions = ["configuration-preserved", "control-and-app-feedback"];
    internal static void Validate(SystemOutageSettings plan, FixtureSettings settings, int rootId)
    {
        static bool Literal(string host) => IPAddress.TryParse(host, out var ip) && ip.ToString() == host;
        if (rootId <= 0 || !Literal(plan.AuthorizedProcessorHost) || plan.AuthorizedProcessorHost != settings.ProcessorHost ||
            plan.NeverInterruptHosts.Length == 0 || plan.NeverInterruptHosts.Any(h => !Literal(h)) ||
            plan.NeverInterruptHosts.Contains(settings.ProcessorHost, StringComparer.Ordinal) ||
            settings.ResolveDeviceIds || settings.OperatorInbox != plan.Manual.Inbox || settings.DeviceCredentialsFile == null ||
            !Path.IsPathFullyQualified(settings.DeviceCredentialsFile) || settings.EvidenceIdentity == null ||
            !Path.IsPathFullyQualified(plan.PolicyFile) || plan.Plans.Length is < 1 or > 2 ||
            plan.Plans.Any(p => !Path.IsPathFullyQualified(p.Path) || p.Sha256.Length != 64 || !p.Sha256.All(char.IsAsciiHexDigit)) ||
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

    [Test, Explicit("Requires coordinated physical interruption of the separately authorized processor and demo equipment.")]
    public async Task ManualProcessorAndDeviceInterruptionRecoversControlAndApp()
    {
        var session = SensorSession.Current!;
        var settings = SensorSession.Settings!;
        var plan = settings.SystemOutage ?? throw new InvalidDataException("Explicit outage settings required.");
        Validate(plan, settings, session.Context.InstalledDriverId);
        if (!SensorSession.PhysicalRestorationConfirmed) throw new InvalidOperationException("Earlier restoration is unresolved.");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true, AllowDuplicateProperties = false, Converters = { new JsonStringEnumConverter(allowIntegerValues: false) } };
        byte[] ReadBounded(string path) => new FileInfo(path).Length <= 1024 * 1024 ? File.ReadAllBytes(path) : throw new InvalidDataException("Oversized outage input.");
        var plans = plan.Plans.Select(p => {
            byte[] bytes = ReadBounded(p.Path);
            if (!string.Equals(Hash(bytes), p.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Outage plan changed.");
            return JsonSerializer.Deserialize<SubmissionOutageMeasurementPlan>(bytes, options) ?? throw new InvalidDataException("Empty outage plan.");
        }).ToArray();
        byte[] policy = ReadBounded(plan.PolicyFile);
        if (plans.Select(p => p.RequirementId).Distinct(StringComparer.Ordinal).Count() != plans.Length ||
            plans.Any(p => p.Identity != settings.EvidenceIdentity || !string.Equals(Hash(policy), p.Identity.PolicySha256, StringComparison.OrdinalIgnoreCase) ||
                !p.RequiredComponents.Order().SequenceEqual(plan.Endpoints.Select(e => e.Component).Order()) ||
                !p.RequiredFunctions.SequenceEqual(Functions) || p.MinimumInterruption < TimeSpan.FromSeconds(60) ||
                p.RecoveryLimit != TimeSpan.FromSeconds(60) ||
                p.RecoveryClock == SubmissionOutageRecoveryClock.ProgramLoaded && p.ProgramComponent != "processor"))
            throw new InvalidDataException("Outage plans differ from the pinned identity, scope or required timing.");
        var parsedPolicy = JsonSerializer.Deserialize<SubmissionEvidencePolicy>(policy, options) ?? throw new InvalidDataException("Empty outage policy.");
        foreach(var reviewedPlan in plans) SubmissionOutageEvidence.ValidatePlanPolicy(reviewedPlan, parsedPolicy);
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
                byte[] bytes = ReadBounded(plan.Plans[i].Path);
                File.WriteAllBytes(Path.Combine(folder, name), bytes);
                var imported = SubmissionOutageEvidence.ImportFiles(folder, name, plan.Plans[i].Sha256,
                    result.RecordRelativePath, recordHash, "policy.json", DateTimeOffset.UtcNow);
                File.WriteAllText(Path.Combine(folder, "observations-" + i + ".json"), JsonSerializer.Serialize(imported.Observations,
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
        : ISubmissionManualOutageObserver, IAsyncDisposable
    {
        public IReadOnlyList<string> Components => plan.Endpoints.Select(e => e.Component).ToArray();
        public IReadOnlyList<string> Functions => SystemOutageTests.Functions;
        private string _root = null!;
        private int _sequence;
        private readonly Dictionary<string, PhysicalState> _original = new();
        private PlatformState? _platform;
        private DateTimeOffset _baselineStarted;
        private ProcessorUptimeSnapshot? _boot;
        private DevToolsStoredCredential _saved = null!;
        private ConfigurationClient? _api;
        private ProcessorOperationLease? _lease;
        private OutletTarget Target => settings.Outlets!.Single(t => t.Alias == plan.OutletAlias);
        private NetworkCredential Login => new(_saved.UserName, _saved.Password);
        private SubmissionOutageCapture Save(string phase, DateTimeOffset first, object value)
        {
            string name = $"capture-{Interlocked.Increment(ref _sequence):D4}-{phase}.json";
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { EarliestUtc = first, LatestUtc = DateTimeOffset.UtcNow, Value = value });
            using(var output = new FileStream(Path.Combine(_root, name), FileMode.CreateNew)) { output.Write(bytes); output.Flush(true); }
            return new(first, DateTimeOffset.UtcNow, new(name, Hash(bytes)));
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
            await SensorSession.Navigation!.RestoreHomeAsync(token);
            await RoomNavigation.Open(Target.Room, token); await RoomNavigation.RevealTile(Target.Room, Target.Name, token);
            await session.CaptureAsync("system-outage.before", h => RoomNavigation.InspectTile(h, Target.Room, Target.Name, Target.EnergyPage), token);
        }
        public Task<SubmissionOutageCapture> CaptureOriginalAsync(CancellationToken token) => Task.FromResult(Save("original", _baselineStarted,
            new { Processor = settings.ProcessorHost, RootDeviceId = session.Context.InstalledDriverId, Platform = _platform, Physical = _original,
                session.Context.PackageSha256, session.Context.ReleaseSourceCommit, Components = plan.Endpoints.Select(e => new { e.Component,e.Host,e.Ports }) }));
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
        public Task<IReadOnlyDictionary<string,SubmissionOutageCapture>> ObserveRestoredAsync(CancellationToken token) => ObserveEndpoints(true,token);
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
        public Task<SubmissionOutageCapture?> ObserveProgramLoadedAsync(string component,CancellationToken token) => Task.FromResult<SubmissionOutageCapture?>(null);
        // No port or ready API response is fabricated into a program-load marker. The power
        // clock remains explicitly unverified until a validated firmware-specific marker exists.
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
                var boot=await ProcessorUptime.ReadAsync(_saved.Host,Login,_saved.SshFingerprint!,TimeSpan.FromSeconds(20),token);
                bool matches=current.IdentitySha256==_platform!.IdentitySha256 && current.ConfigurationSha256==_platform.ConfigurationSha256 && boot.EarliestStartUtc>_boot!.LatestStartUtc;
                var capture=Save(function,first,new{Platform=current,Boot=boot,Matches=matches});
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
            return new(function,SubmissionEvidenceOutcome.Passed,Save(function,first,new{Target.DeviceId,RestoredPower=original,
                AppCaptures=new[]{"system-outage.on","system-outage.off"},Method="Driver API commands, authenticated physical state and visible unobscured Room tile."}));
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

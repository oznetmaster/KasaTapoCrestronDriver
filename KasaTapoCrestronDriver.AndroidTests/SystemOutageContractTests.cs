// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
using CrestronHomeDevTools;
using KasaTapoCrestronDriver.AndroidTests;
using NUnit.Framework;
using System.Security.Cryptography;
using System.Text.Json;

namespace KasaAppEvidenceContracts;

[TestFixture, Category("unit")]
public sealed class SystemOutageContractTests
{
    private static SubmissionOperatorInbox Inbox=>new(Path.GetFullPath("synthetic-inbox"),new('a',64));
    private static FixtureSettings Settings=>new("192.0.2.44",Path.GetFullPath("bindings.json"),[],
        Path.GetFullPath("device.json"),[new("test",3,"plug","Synthetic plug","Room",1,"discovery","authenticated",null,true,true)],
        EvidenceIdentity:new(new('a',64),new('b',40),new('c',64),new('d',64))) {OperatorInbox=Inbox};
    private static SystemOutageSettings Plan=>new("192.0.2.44",["192.0.2.41"],"test",
        [new("processor","192.0.2.44",[22,443],null),new("device","192.0.2.90",[9999],new("discovery","authenticated",null,true))],
        new(Inbox,"Synthetic outage","Synthetic disconnect","Synthetic reconnect",TimeSpan.FromMinutes(2)),
        [new("power",["processor","device"],SystemOutageTests.Functions,TimeSpan.FromSeconds(60),TimeSpan.FromSeconds(60),SubmissionOutageRecoveryClock.ProgramLoaded,"processor")],Path.GetFullPath("policy.json"));

    [Test] public void ExplicitSeparateHostAndConcreteTargetsAreAccepted()=>Assert.DoesNotThrow(()=>SystemOutageTests.Validate(Plan,Settings,7));
    [Test] public void ProtectedProcessorCannotBeUsedEvenWhenSelectedAsAuthorized()=>Assert.Throws<InvalidDataException>(()=>
        SystemOutageTests.Validate(Plan with {AuthorizedProcessorHost="192.0.2.41",Endpoints=[Plan.Endpoints[0] with {Host="192.0.2.41"},Plan.Endpoints[1]]},
            Settings with {ProcessorHost="192.0.2.41"},7));
    [Test] public void ProtectedHostCannotHideAsADeviceEndpoint()=>Assert.Throws<InvalidDataException>(()=>
        SystemOutageTests.Validate(Plan with {Endpoints=[Plan.Endpoints[0],Plan.Endpoints[1] with {Host="192.0.2.41"}]},Settings,7));
    [Test] public void MainProcessorManagedBindingsCannotLeakIntoSeparateOutageStage()=>Assert.Throws<InvalidDataException>(()=>
        SystemOutageTests.Validate(Plan,Settings with {ResolveDeviceIds=true},7));
    [Test] public void DifferentRunInboxIsRejected()=>Assert.Throws<InvalidDataException>(()=>
        SystemOutageTests.Validate(Plan,Settings with {OperatorInbox=Inbox with {RunKey=new('b',64)}},7));
    [Test] public void WrongAuthenticatedOutletIdentityIsRejected()=>Assert.Throws<InvalidDataException>(()=>
        SystemOutageTests.Validate(Plan with {Endpoints=[Plan.Endpoints[0],Plan.Endpoints[1] with {Physical=Plan.Endpoints[1].Physical! with {AuthenticatedId="other"}}]},Settings,7));
    [Test] public void UnapprovedControlTargetIsRejected()=>Assert.Throws<InvalidDataException>(()=>
        SystemOutageTests.Validate(Plan,Settings with {Outlets=[Settings.Outlets![0] with {ControlsAuthorized=false}]},7));
    [TestCase("processor.local")]
    [TestCase("192.0.2.041")]
    public void HostAliasesCannotBypassLiteralEndpointRestrictions(string host)=>Assert.Throws<InvalidDataException>(()=>
        SystemOutageTests.Validate(Plan with {AuthorizedProcessorHost=host,Endpoints=[Plan.Endpoints[0] with {Host=host},Plan.Endpoints[1]]},Settings with {ProcessorHost=host},7));
    [Test] public void BlankExclusionPolicyIsRejected()=>Assert.Throws<InvalidDataException>(()=>
        SystemOutageTests.Validate(Plan with {NeverInterruptHosts=[]},Settings,7));
    private static readonly DateTimeOffset Epoch=DateTimeOffset.Parse("2026-09-28T10:00:00Z");
    private static ProcessorUptimeSnapshot Boot(int start)=>new(TimeSpan.FromSeconds(10),DateTime.MinValue,Epoch.AddSeconds(start+10),Epoch.AddSeconds(start+10).AddMilliseconds(10));
    private static ProcessorProgramUptimeSnapshot Program(int start)=>new(new("/simpl/app00","Crestron.Seawolf","Crestron.Seawolf.dll"),
        TimeSpan.FromSeconds(5),"diagnostic only",Epoch.AddSeconds(start+5),Epoch.AddSeconds(start+5).AddMilliseconds(10),"synthetic");
    [Test] public void FreshBootFollowedBySameHomeProgramIsRequired()=>Assert.DoesNotThrow(()=>
        SystemOutageTests.ValidateNewEpoch(Boot(0),Program(2),Boot(100),Program(105),Epoch.AddSeconds(95)));
    [TestCase(0,105)][TestCase(100,2)][TestCase(90,105)][TestCase(100,99)]
    public void OldOrOutOfOrderEpochCannotProveOutage(int boot,int program)=>Assert.Throws<InvalidDataException>(()=>
        SystemOutageTests.ValidateNewEpoch(Boot(0),Program(2),Boot(boot),Program(program),Epoch.AddSeconds(95)));
    [Test] public void LaterObservationMayNotMoveTheRecoveryBaseline() {
        var boot=Boot(100);var program=Program(105);
        Assert.DoesNotThrow(()=>SystemOutageTests.ValidateSameEpoch(boot,program,
            boot with {Uptime=boot.Uptime+TimeSpan.FromSeconds(20),RequestSentUtc=boot.RequestSentUtc.AddSeconds(20),ObservedUtc=boot.ObservedUtc.AddSeconds(20)},
            program with {Uptime=program.Uptime+TimeSpan.FromSeconds(20),RequestSentUtc=program.RequestSentUtc.AddSeconds(20),ObservedUtc=program.ObservedUtc.AddSeconds(20)}));
    }
    [TestCase(101,105)][TestCase(100,106)][TestCase(99,105)][TestCase(100,104)]
    public void ProcessorOrProgramResetOrClockJumpInvalidatesRecovery(int boot,int program)=>Assert.Throws<InvalidDataException>(()=>
        SystemOutageTests.ValidateSameEpoch(Boot(100),Program(105),Boot(boot),Program(program)));
    [Test] public void ChangedProgramIdentityCannotProveRecovery()=>Assert.Throws<InvalidDataException>(()=>
        SystemOutageTests.ValidateSameEpoch(Boot(100),Program(105),Boot(100),Program(105) with {Program=new("/simpl/app00","Other","Other.dll")}));

    private static (FixtureSettings Settings, byte[] Policy) Inputs(SubmissionRequirement? requirement=null) {
        requirement ??= new("power",TimeSpan.FromSeconds(60),false,new("system","outage",SubmissionEvidenceOutcome.Passed,60,true));
        byte[] policy=JsonSerializer.SerializeToUtf8Bytes(new SubmissionEvidencePolicy(1,[requirement]),SystemOutageTests.PlanJson);
        return (Settings with {EvidenceIdentity=Settings.EvidenceIdentity! with {PolicySha256=Convert.ToHexStringLower(SHA256.HashData(policy))}},policy);
    }
    [Test] public void ScopeBindsToActualReleaseIdentityWithoutExternalPlanEditing() {
        var input=Inputs();var scope=Plan;
        var first=SystemOutageTests.PreparePlans(scope,input.Settings,input.Policy).Single();
        var next=input.Settings with {EvidenceIdentity=input.Settings.EvidenceIdentity! with {PackageSha256=new('e',64),SourceCommit=new('f',40)}};
        var second=SystemOutageTests.PreparePlans(scope,next,input.Policy).Single();
        Assert.That(first.Identity,Is.EqualTo(input.Settings.EvidenceIdentity));
        Assert.That(second.Identity,Is.EqualTo(next.EvidenceIdentity));
        Assert.That(first.RequirementId,Is.EqualTo(second.RequirementId));
        Assert.That(first.RecoveryClock,Is.EqualTo(second.RecoveryClock));
        scope.Plans[0].RequiredComponents[0]="changed";
        Assert.That(first.RequiredComponents,Is.EqualTo(new[]{"processor","device"}));
    }
    [Test] public void ChangedPolicyBytesAreRejectedBeforePreparingAnOutage() {
        var input=Inputs();
        Assert.Throws<InvalidDataException>(()=>SystemOutageTests.PreparePlans(Plan,input.Settings,[..input.Policy,(byte)' ']));
    }
    [TestCase("duration")][TestCase("response")][TestCase("component")][TestCase("function")][TestCase("program")][TestCase("duplicate")]
    public void EmbeddedScopeCannotWeakenOrSubstituteRequiredChecks(string variant) {
        var input=Inputs();var original=Plan.Plans[0];
        var changed=variant switch {
            "duration"=>original with {MinimumInterruption=TimeSpan.FromSeconds(59)},
            "response"=>original with {RecoveryLimit=TimeSpan.FromSeconds(61)},
            "component"=>original with {RequiredComponents=["device"]},
            "function"=>original with {RequiredFunctions=["configuration-preserved"]},
            "program"=>original with {ProgramComponent="device"},
            _=>original
        };
        Assert.Throws<InvalidDataException>(()=>SystemOutageTests.PreparePlans(Plan with {Plans=variant=="duplicate"?[original,original]:[changed]},input.Settings,input.Policy));
    }
    [Test] public void PinnedPolicyWithStricterDeadlineStillWins() {
        var input=Inputs(new("power",TimeSpan.FromSeconds(60),false,new("system","outage",SubmissionEvidenceOutcome.Passed,30,true)));
        Assert.Throws<InvalidDataException>(()=>SystemOutageTests.PreparePlans(Plan,input.Settings,input.Policy));
    }
    [Test] public void PlansRoundTripUsingImporterCompatibleCamelCaseAndNamedClock() {
        var input=Inputs();var plans=SystemOutageTests.PreparePlans(Plan,input.Settings,input.Policy);
        string json=JsonSerializer.Serialize(plans[0],SystemOutageTests.PlanJson);
        Assert.That(json,Does.Contain("\"recoveryClock\": \"ProgramLoaded\""));
        var parsed=JsonSerializer.Deserialize<SubmissionOutageMeasurementPlan>(json,SystemOutageTests.PlanJson)!;
        Assert.That(parsed.Identity,Is.EqualTo(input.Settings.EvidenceIdentity));
        Assert.DoesNotThrow(()=>SubmissionOutageEvidence.ValidatePlanPolicy(parsed,JsonSerializer.Deserialize<SubmissionEvidencePolicy>(input.Policy,SystemOutageTests.PlanJson)!));
    }
}

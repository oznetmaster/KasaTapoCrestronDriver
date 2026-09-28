// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
using CrestronHomeDevTools;
using KasaTapoCrestronDriver.AndroidTests;
using NUnit.Framework;

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
        [new(Path.GetFullPath("plan.json"),new('a',64))],Path.GetFullPath("policy.json"));

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
}

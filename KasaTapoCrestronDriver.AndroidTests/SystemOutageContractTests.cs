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
}

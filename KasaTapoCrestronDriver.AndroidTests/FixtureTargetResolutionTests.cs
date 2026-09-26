// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
using System.Text.Json;
using CrestronHomeDevTools;
using CrestronHomeNUnit.Android;
using KasaTapoCrestronDriver.AndroidTests;
using NUnit.Framework;

namespace KasaAppEvidenceContracts;

[TestFixture, Category("unit")]
public sealed class FixtureTargetResolutionTests
{
    static AndroidRunContext Context => new(1,"synthetic","synthetic",1,1,"example.invalid",100,"KasaTapoPlatform","2.1.2.0",
        new('a',64),new('b',64),new("unused","unused","unused","unused","unused"),"unused");
    static DeviceInfo Device(int id,int? parent,string model,string name,int? location) => new()
    {
        Id=id,ParentDeviceId=parent,Model=model,Name=name,LocationId=location,
        PropertyValues=new(){["cp.driverInformation:version"]=JsonSerializer.SerializeToElement("2.1.2.0")}
    };
    static List<DeviceInfo> Inventory()
    {
        var outlet=Device(102,100,"Plug","Demo Plug",7);
        outlet.PropertyValues["controlDeviceId"]=JsonSerializer.SerializeToElement("physical/child");
        var wrapper=Device(103,100,"Bulb","Demo Light",null);
        wrapper.PropertyValues["platform:managedDevices"]=JsonSerializer.SerializeToElement(new[]{new {Id="device_light-physical"}});
        return [Device(100,-6,"KasaTapoPlatform","Platform",7),Device(101,100,"Sensor","Demo Sensor",7),outlet,
            wrapper,Device(104,103,"Bulb","Demo Light",7)];
    }
    static FixtureSettings Settings => new("example.invalid","private-bindings",
        [new("motion",1,"Sensor","Demo Sensor","Room",7,["motionStatusLabel"])],
        "secret-file",[new("basic",2,"Plug","Demo Plug","Room",7,"physical","authenticated","child",false,false)],
        new(4,3,"Bulb","Demo Light","Room",7,"light-physical","light-authenticated",false)) { ResolveDeviceIds=true };

    [Test] public void ResolvesNewIdsOnlyUnderTheVerifiedCandidateAndKeepsAuthorizationUnchanged()
    {
        var result=FixtureTargetResolution.Resolve(Settings,Context,Inventory());
        Assert.That(result.Sensors.Single().DeviceId,Is.EqualTo(101));
        Assert.That(result.Outlets!.Single().DeviceId,Is.EqualTo(102));
        Assert.That(result.Light!.DeviceId,Is.EqualTo(104));
        Assert.That(result.Light.WrapperId,Is.EqualTo(103));
        Assert.That(result.Outlets!.Single().ControlsAuthorized,Is.False);
        Assert.That(result.Light.ControlsAuthorized,Is.False);
        Assert.That(Settings.Sensors.Single().DeviceId,Is.EqualTo(1));
    }
    [TestCase("parent")][TestCase("room")][TestCase("version")][TestCase("duplicate")][TestCase("physical")][TestCase("wrapper")]
    [TestCase("light-physical")][TestCase("light-unidentified")][TestCase("wrapper-version")][TestCase("inventory-duplicate")]
    public void RejectsChangedOrAmbiguousTargetsBeforeUiInput(string fault)
    {
        var devices=Inventory();
        switch(fault)
        {
            case "parent": devices[1]=devices[1] with{ParentDeviceId=900};break;
            case "room": devices[1]=devices[1] with{LocationId=8};break;
            case "version": devices[1].PropertyValues["cp.driverInformation:version"]=JsonSerializer.SerializeToElement("2.1.1.0");break;
            case "duplicate": devices.Add(devices[1] with{Id=105});break;
            case "physical": devices[2].PropertyValues["controlDeviceId"]=JsonSerializer.SerializeToElement("other/child");break;
            case "wrapper": devices[3]=devices[3] with{ParentDeviceId=900};break;
            case "light-physical": devices[3].PropertyValues["platform:managedDevices"]=JsonSerializer.SerializeToElement(new[]{new{Id="device_other"}});break;
            case "light-unidentified": devices[3].PropertyValues.Remove("platform:managedDevices");break;
            case "wrapper-version": devices[3].PropertyValues["cp.driverInformation:version"]=JsonSerializer.SerializeToElement("2.1.1.0");break;
            case "inventory-duplicate": devices.Add(devices[2]);break;
        }
        Assert.Throws<InvalidDataException>(()=>FixtureTargetResolution.Resolve(Settings,Context,devices));
    }
    [Test] public void AcceptsRealInventoryGatewaysAndNativeWrappersWithoutVersionProperties()
    {
        var devices=Inventory();
        devices.Add(Device(-6,null,"Gateway","Driver Management Gateway",null));
        devices[3].PropertyValues.Remove("cp.driverInformation:version");
        devices[4].PropertyValues.Remove("cp.driverInformation:version");
        var result=FixtureTargetResolution.Resolve(Settings,Context,devices);
        Assert.That(result.Light!.DeviceId,Is.EqualTo(104));
        Assert.That(result.Light.WrapperId,Is.EqualTo(103));
    }
    [Test] public void ExplicitBindingsRemainUnchangedUnlessResolutionIsSelected()
    {
        var settings=Settings with{ResolveDeviceIds=false};
        Assert.That(FixtureTargetResolution.Resolve(settings,Context,[]),Is.SameAs(settings));
    }
    [Test] public void RetainedBindingExcludesCredentialsAndUnrelatedPropertyValues()
    {
        string path=Path.Combine(TestContext.CurrentContext.WorkDirectory,Guid.NewGuid().ToString("N")+".json");
        try
        {
            var result=FixtureTargetResolution.Resolve(Settings,Context,Inventory());
            FixtureTargetResolution.Write(path,result,100);
            string text=File.ReadAllText(path);
            Assert.That(text,Does.Contain("104").And.Not.Contain("secret-file").And.Not.Contain("private-bindings").And.Not.Contain("authenticated"));
            Assert.Throws<IOException>(()=>FixtureTargetResolution.Write(path,result,100));
        }
        finally { if(File.Exists(path))File.Delete(path); }
    }
}

// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
using System.Text.Json;
using CrestronHomeDevTools;
using CrestronHomeNUnit.Android;
using KasaTapoCrestronDriver.AndroidTests;
using NUnit.Framework;

namespace KasaAppEvidenceContracts;

[TestFixture, Category("unit")]
public sealed class SensorEventContractTests
{
    static DeviceInfo Device(params (string, object)[] values)
    {
        var device = new DeviceInfo { PropertyValues = new() {
            ["onlineIndicator:isOnline"] = JsonSerializer.SerializeToElement(true),
            ["readyIndicator:isReady"] = JsonSerializer.SerializeToElement(true) } };
        foreach (var (key, value) in values) device.PropertyValues[key] = JsonSerializer.SerializeToElement(value);
        return device;
    }

    [TestCase("Single", "Single", 101, true)]
    [TestCase("Double", "Double", 101, true)]
    [TestCase("Double", "Single", 101, false)]
    [TestCase("Double", "Double", 100, false)]
    [TestCase("Unknown", "Unknown", 101, false)]
    public void GestureTestRequiresExactFreshGesture(string expected,string observed,double marker,bool accepted)
    {
        var baseline = new SensorEventSnapshot(DateTimeOffset.UtcNow,100,new(),"Single");
        var current = new SensorEventSnapshot(DateTimeOffset.UtcNow,marker,new(),observed);
        Assert.That(SensorEventReading.IsNewGesture(expected,baseline,current),Is.EqualTo(accepted));
    }

    [TestCase("motion", "hasMotion", "motionDetected", "motionStatusLabel")]
    [TestCase("contact", "hasContact", "contactIsOpen", "contactStatusLabel")]
    [TestCase("leak", "hasLeak", "leakDetected", "leakStatusLabel")]
    public void StatefulEventRequiresInactiveBaselineAndActiveTransition(string alias, string capability, string raw, string display)
    {
        var inactive = SensorEventReading.Read(alias, Device((capability, true), (raw, false), (display, "Inactive")));
        var active = SensorEventReading.Read(alias, Device((capability, true), (raw, true), (display, "Active")));
        Assert.That(SensorEventReading.IsNew(alias, inactive, inactive), Is.False);
        Assert.That(SensorEventReading.IsNew(alias, active, active), Is.False);
        Assert.That(SensorEventReading.IsNew(alias, active, inactive), Is.False);
        Assert.That(SensorEventReading.IsNew(alias, inactive, active), Is.True);
        Assert.That(SensorEventReading.DisplayChanged(inactive, active), Is.True);
        Assert.Throws<InvalidDataException>(() => SensorEventReading.Read(alias,
            Device((capability, false), (raw, true), (display, "Active"))));
    }

    [TestCase(100, false)] [TestCase(99, false)] [TestCase(101, true)]
    public void ButtonRequiresIncreasingEventTimestamp(double timestamp, bool expected)
    {
        var baseline = SensorEventReading.Read("button", Device(("lastTriggerTime", 100),
            ("lastGestureLabel", "Single Press"), ("lastTriggerTimeDisplay", "10:00:00")));
        var current = SensorEventReading.Read("button", Device(("lastTriggerTime", timestamp),
            ("lastGestureLabel", "Single Press"), ("lastTriggerTimeDisplay", "10:00:00")));
        Assert.That(SensorEventReading.IsNew("button", baseline, current), Is.EqualTo(expected));
        Assert.That(SensorEventReading.DisplayChanged(baseline, current), Is.False,
            "A fresh timestamp alone cannot prove that an unchanged UI updated.");
        Assert.That(SensorEventReading.DisplayChanged(baseline,
            current with { Rows = new() { ["Last Press"] = "Single Press", ["Last Press Time"] = "10:00:01" } }), Is.True);
    }

    [TestCase("onlineIndicator:isOnline")] [TestCase("readyIndicator:isReady")]
    public void RejectsUnavailableSensor(string property)
    {
        var device = Device(("lastTriggerTime", 100), ("lastGestureLabel", "Single Press"), ("lastTriggerTimeDisplay", "10:00:00"));
        device.PropertyValues[property] = JsonSerializer.SerializeToElement(false);
        Assert.Throws<InvalidDataException>(() => SensorEventReading.Read("button", device));
    }

    [TestCase(nameof(SensorEventTests.PhysicalEventReachesVisibleDetailPage))]
    [TestCase(nameof(SensorEventTests.PhysicalEventReachesVisibleRoomTile))]
    public void PhysicalCasesCannotRunAsOrdinaryUnattendedTests(string method)
    {
        var cases = typeof(SensorEventTests).GetMethod(method)!
            .GetCustomAttributes(typeof(TestCaseAttribute), false).Cast<TestCaseAttribute>().ToArray();
        Assert.That(cases, Has.Length.EqualTo(4));
        Assert.That(cases.All(c => c.Explicit), Is.True);
    }

    [Test] public void RoomTileUsesItsOwnValueAndRequiresDistinguishableFeedback()
    {
        var device = Device(("lastTriggerTime", 100), ("lastGestureLabel", "Single Press"),
            ("lastTriggerTimeDisplay", "10:00:00"), ("lastTriggerDisplay", "Single Press - 10:00 AM"));
        var baseline = SensorEventReading.ReadTile("button", device);
        device.PropertyValues["lastTriggerTime"] = JsonSerializer.SerializeToElement(101);
        device.PropertyValues["lastTriggerTimeDisplay"] = JsonSerializer.SerializeToElement("10:00:01");
        var repeated = SensorEventReading.ReadTile("button", device);
        Assert.That(SensorEventReading.IsNew("button", baseline, repeated), Is.True);
        Assert.That(SensorEventReading.DisplayChanged(baseline, repeated), Is.False,
            "A detail-page timestamp change cannot establish Room-tile feedback.");
        device.PropertyValues["lastTriggerDisplay"] = JsonSerializer.SerializeToElement("Double Press - 10:00 AM");
        Assert.That(SensorEventReading.DisplayChanged(baseline, SensorEventReading.ReadTile("button", device)), Is.True);
    }

    [Test] public void OptionalSensorPagesRequireExplicitSelectionWithoutChangingExistingCases()
    {
        var cases = typeof(SensorPagesTests).GetMethod(nameof(SensorPagesTests.RoomSensorValuesMatchInstalledDriver))!
            .GetCustomAttributes(typeof(TestCaseAttribute), false).Cast<TestCaseAttribute>().ToArray();
        Assert.That(cases.Where(c => c.Explicit).Select(c => c.Arguments[0]), Is.EquivalentTo(new[] { "contact", "leak" }));
        Assert.That(cases.Where(c => !c.Explicit).Select(c => c.Arguments[0]), Is.EquivalentTo(new[] { "temperature", "motion", "button" }));
    }

    [Test] public void AppComparisonUsesTheLabelledRowAndRejectsWrongPage()
    {
        string Node(string resource, string text) => $"<node package='com.crestron.phoenix.app' resource-id='{CrestronHomePages.ResourcePrefix}{resource}' text='{text}' enabled='true' bounds='[0,0][100,100]'/>";
        string xml = "<hierarchy>" + Node("customdevices_toolbarTitle", "Demo Sensor") +
            Node("customdevices_toolbarClose", "Close") + "<node package='com.crestron.phoenix.app'>" + Node("label", "Motion") +
            Node("customdevice_textdisplay_firstlinetext", "Motion Detected") + "</node><node package='com.crestron.phoenix.app'>" +
            Node("label", "Other") + Node("customdevice_textdisplay_firstlinetext", "No Motion") + "</node></hierarchy>";
        var hierarchy = new AndroidHierarchy(xml, "com.crestron.phoenix.app");
        var current = new SensorEventSnapshot(DateTimeOffset.UtcNow, 1, new() { ["Motion"] = "motion detected" });
        Assert.That(SensorEventReading.Matches(hierarchy, "Demo Sensor", current), Is.True);
        Assert.That(SensorEventReading.Matches(hierarchy, "Demo Sensor",
            current with { Rows = new() { ["Motion"] = "No Motion" } }), Is.False);
        Assert.Throws<InvalidOperationException>(() => SensorEventReading.Matches(hierarchy, "Wrong Sensor", current));
    }

    [Test] public void RoomFeedbackRequiresVisibleMatchingTileNotUnrelatedText()
    {
        string Node(string id, string text, string bounds) => $"<node package='com.crestron.phoenix.app' resource-id='{CrestronHomePages.ResourcePrefix}{id}' text='{text}' enabled='true' bounds='{bounds}'/>";
        string xml = "<hierarchy>" + Node("room_name", "Lab", "[0,0][100,50]") +
            Node("room_back", "Back", "[0,0][100,50]") + Node("bottomNavigationView", "", "[0,900][500,1000]") +
            "<node package='com.crestron.phoenix.app' content-desc='room_service_Demo Sensor' enabled='true' bounds='[0,100][400,250]'>" +
            Node("serviceTitle", "Demo Sensor", "[0,100][400,150]") + Node("serviceIcon", "", "[0,150][50,200]") +
            Node("serviceSubtitle", "No Motion", "[50,150][400,200]") + "</node>" +
            Node("other", "Motion Detected", "[0,300][400,350]") + "</hierarchy>";
        var current = new SensorEventSnapshot(DateTimeOffset.UtcNow, 0, new() { ["Room tile"] = "No Motion" });
        var hierarchy = new AndroidHierarchy(xml, "com.crestron.phoenix.app");
        Assert.That(SensorEventReading.TileMatches(hierarchy, "Lab", "Demo Sensor", current), Is.True);
        Assert.That(SensorEventReading.TileMatches(hierarchy, "Lab", "Demo Sensor",
            current with { Rows = new() { ["Room tile"] = "Motion Detected" } }), Is.False);
        var hidden = new AndroidHierarchy(xml.Replace("[0,100][400,250]", "[0,950][400,1100]"), "com.crestron.phoenix.app");
        Assert.Throws<InvalidDataException>(() => SensorEventReading.TileMatches(hidden, "Lab", "Demo Sensor", current));
    }
}

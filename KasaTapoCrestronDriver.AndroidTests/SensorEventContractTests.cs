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

    [Test] public void PhysicalCasesCannotRunAsOrdinaryUnattendedTests()
    {
        var cases = typeof(SensorEventTests).GetMethod(nameof(SensorEventTests.PhysicalEventReachesVisibleDetailPage))!
            .GetCustomAttributes(typeof(TestCaseAttribute), false).Cast<TestCaseAttribute>().ToArray();
        Assert.That(cases, Has.Length.EqualTo(4));
        Assert.That(cases.All(c => c.Explicit), Is.True);
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
}

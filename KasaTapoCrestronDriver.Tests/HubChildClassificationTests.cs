// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

extern alias driverassembly;

using System.Reflection;

using KasaTapoClient;

using Driver = driverassembly::KasaTapoCrestronDriver;

namespace KasaTapoCrestronDriver.Tests;

[TestFixture]
public sealed class HubChildClassificationTests
	{
	[Test]
	[TestCase ("T100", "subg.trigger.motion-sensor", "Motion")]
	[TestCase ("t100(EU)", "", "Motion")]
	[TestCase ("T110", "", "Contact")]
	[TestCase ("T310", "", "TemperatureHumidity")]
	[TestCase ("T315", "", "TemperatureHumidity")]
	public void Discovery_ClassifiesKnownSensorModels (string model, string category, string expected)
		{
		// KasaTapoClient's discovery DTO constructor is internal. Supply a discovery
		// record with no feature list to exercise the driver's model fallback.
		var child = (ChildDeviceInfo)Activator.CreateInstance (typeof (ChildDeviceInfo),
			 BindingFlags.Instance | BindingFlags.NonPublic, null,
			 new object?[] { "child", "Sensor", model, DeviceType.Hub, null, "{}", category, null, null }, null)!;
		var classify = typeof (Driver.PlatformDriver).GetMethod ("ResolveHubChildKind", BindingFlags.Static | BindingFlags.NonPublic)!;
		var result = ((Driver.ManagedChildKind ChildKind, Driver.HubChildCategory Category))classify.Invoke (null, new object[] { child })!;

		Assert.That (result.ChildKind, Is.EqualTo (Driver.ManagedChildKind.Sensor));
		Assert.That (result.Category.ToString (), Is.EqualTo (expected));
		}
	}
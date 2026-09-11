// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using Crestron.DeviceDrivers.EntityModel.Data;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel;

using KasaTapoClient;

using Driver = KasaTapoCrestronDriver;

namespace KasaTapoCrestronDriver.Tests;

#if NETFRAMEWORK
[Category ("Processor" )]
#endif
[TestFixture]
public sealed class SensorPublicationTests
	{
	[Test]
	[TestCase ("T310", "TemperatureHumidity", "temperatureValue", "motionDetected")]
	[TestCase ("T315", "TemperatureHumidity", "humidityValuePercent", "contactIsOpen")]
	[TestCase ("T100", "Motion", "motionDetected", "temperatureValue")]
	[TestCase ("T310", "None", "temperatureValue", "motionDetected")]
	[TestCase ("T315", "None", "humidityValuePercent", "contactIsOpen")]
	[TestCase ("t100(EU)", "Contact", "motionDetected", "contactIsOpen")]
	[TestCase ("T110", "None", "contactIsOpen", "motionDetected")]
	public void CachedSensor_FirstPublishedDefinitionIsFinal (string model, string categoryName, string supported, string unsupported)
		{
		var category = (Driver.HubChildCategory)Enum.Parse (typeof (Driver.HubChildCategory), categoryName);
		var descriptor = new Driver.ManagedLightDescriptor ("sensor-test", "127.0.0.1", KasaTapoClient.DeviceType.Hub,
			 model, model, "sensor-serial", Driver.ManagedLightKind.OnOff,
			 childId: "sensor-child", childKind: Driver.ManagedChildKind.Sensor, hubChildCategory: category);
		using var logger = new DriverLogger ("sensor-test");
		var resources = new DriverImplementationResources
			{
			Logger = logger,
			InitLogger = logger.GetComponentLogger ("test", "sensor")
			};
		using var sensor = new Driver.KasaSensorEntity (descriptor.ControllerId, descriptor,
			 new DeviceConfiguration ("127.0.0.1"), null, new Driver.PlatformSharedConfiguration (),
			 resources, null!, "sensor-test", DriverTestPaths.DataDirectory);
		using var parent = new BaseDriverEntity ("parent");
		DriverEntityDefinition? firstDefinition = null;
		parent.SubControllersChanged += (_, _) => firstDefinition = sensor.GetState ().Definition;

		// Match both cached and async publication: the SDK sees UpdateSubControllers first.
		parent.UpdateSubControllers (new[] { new ConfigurableDriverEntity (descriptor.ControllerId, sensor) }, null);
		sensor.NotifyChildPublished ();

		Assert.That (firstDefinition, Is.Not.Null);
		Assert.That (firstDefinition.Properties.ContainsKey (supported), Is.True);
		Assert.That (firstDefinition.Properties.ContainsKey (unsupported), Is.False, "The host's first snapshot must not advertise unsupported sensor capabilities.");
		Assert.That (sensor.GetState ().Definition.Properties.Keys.ToArray (), Is.EquivalentTo (firstDefinition.Properties.Keys.ToArray ()), "NotifyChildPublished must not silently change the already-published definition.");
		Assert.That (sensor.GetState ().Definition.Events.Keys.ToArray (), Is.EquivalentTo (firstDefinition.Events.Keys.ToArray ()));
		}
	}
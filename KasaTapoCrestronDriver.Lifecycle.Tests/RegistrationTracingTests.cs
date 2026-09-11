// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using Crestron.DeviceDrivers.EntityModel;
using Crestron.DeviceDrivers.EntityModel.Data;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel;
using Crestron.DeviceDrivers.SDK.EntityModel.Attributes;

using KasaTapoCrestronDriver;

namespace KasaTapoCrestronDriver.Tests;

#if NETFRAMEWORK
[Category ("Processor" )]
#endif
[TestFixture]
public sealed class RegistrationTracingTests
	{
	private sealed class Child : ReflectedAttributeDriverEntity
		{
		public Child () : base ("child") { }
		[EntityProperty (Id = "answer")]
		public long Answer => 42;
		}

	private static DriverControllerCreationArgs Args (DriverLogger logger) =>
		 new ("trace-test", DriverTestPaths.DataDirectory, logger.AppLogger, null!);

	private sealed class Platform : ReflectedAttributeDriverEntity
		{
		public Platform () : base ("root") { }
		[EntityProperty (Id = "platform:managedDevices")]
		public IDictionary<string, string> Devices => new Dictionary<string, string> { ["child"] = "private payload" };
		}

	[Test]
	public void RootState_RecordsReturnedManagedIdsWithoutValues ()
		{
		using var logger = new DriverLogger ("trace-test");
		using var parent = new Platform ();
		var log = new List<string> ();
		using var controller = new RegistrationTracingController (new ConfigurableDriverEntity ("root", parent), Args (logger), log.Add);
		Assert.That (controller.GetState ("root").PropertyValues.ContainsKey ("platform:managedDevices"), Is.True);
		Assert.That (log.Any (line => line.Contains ("managedIds=[child]")), Is.True);
		Assert.That (log.Any (line => line.Contains ("private payload")), Is.False);
		}

	[Test]
	public void HostLookup_RecordsActualRegistryAndReturnsChildState ()
		{
		using var logger = new DriverLogger ("trace-test");
		using var parent = new BaseDriverEntity ("root");
		using var child = new Child ();
		var log = new List<string> ();
		using var controller = new RegistrationTracingController (new ConfigurableDriverEntity ("root", parent), Args (logger), log.Add);
		parent.UpdateSubControllers (new[] { new ConfigurableDriverEntity ("child", child) }, null);
		Assert.That (controller.ControllerIds.Contains ("child"), Is.True);
		var state = controller.GetState ("child");
		Assert.That (state.PropertyValues["answer"].GetValue<long> (), Is.EqualTo (42L));
		Assert.That (log.Any (line => line.Contains ("ControllerIdsChanged; added=[child]")), Is.True);
		Assert.That (log.Any (line => line.Contains ("method=GetState") && line.Contains ("registryContainsId=True")), Is.True);
		Assert.That (log.Any (line => line.Contains ("properties=[answer]")), Is.True);
		parent.UpdateSubControllers (null, new[] { "child" });
		Assert.That (controller.ControllerIds.Contains ("child"), Is.False);
		Assert.That (log.Any (line => line.Contains ("removed=[child]")), Is.True);
		}

	[Test]
	public void MissingController_KeepsSdkEmptyStateAndRecordsMiss ()
		{
		using var logger = new DriverLogger ("trace-test");
		using var parent = new BaseDriverEntity ("root");
		var log = new List<string> ();
		using var controller = new RegistrationTracingController (new ConfigurableDriverEntity ("root", parent), Args (logger), log.Add);
		Assert.That (controller.GetState ("missing").Definition.Properties.Count, Is.EqualTo (0));
		Assert.That (log.Any (line => line.Contains ("method=GetState") && line.Contains ("registryContainsId=False")), Is.True);
		}

	[Test]
	public void LoggerFailure_DoesNotBreakHostCalls ()
		{
		using var logger = new DriverLogger ("trace-test");
		using var child = new Child ();
		using var controller = new RegistrationTracingController (new ConfigurableDriverEntity ("child", child), Args (logger),
			 _ => throw new InvalidOperationException ("Test logging failure"));
		Assert.That (controller.ControllerIds.Contains ("child"), Is.True);
		Assert.That (controller.GetState ("child").PropertyValues["answer"].GetValue<long> (), Is.EqualTo (42L));
		Assert.That (controller.GetStatus ("child"), Is.EqualTo (DriverControllerStatus.Running));
		}
	}
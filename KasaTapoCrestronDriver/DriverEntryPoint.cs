using Crestron.DeviceDrivers.EntityModel;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel;

[assembly: DriverAssemblyEntryPoint (typeof (EntryPoint))]

/// <summary>
/// Provides the assembly entry point that Crestron Home uses to create the root driver controller.
/// </summary>
public sealed class EntryPoint : DriverAssemblyEntryPoint
	{
	/// <summary>
	/// Creates the driver controller instance for the generated scaffold driver.
	/// </summary>
	/// <param name="args">Creation arguments supplied by the Crestron driver host.</param>
	/// <returns>
	/// A dispatching controller that wraps the scaffold platform entity and its configuration controller.
	/// </returns>
	public override DriverController CreateDriverControllerInstance (DriverControllerCreationArgs args)
		{
		var resources = DriverImplementationResources.FromCreationArgs (args, typeof (EntryPoint));
		var platform = new KasaTapoCrestronDriver.PlatformDriver (args, resources);
		var rootEntity = new ConfigurableDriverEntity (platform.ControllerId, platform, platform.ConfigurationController);

		return new DispatchingDeviceController (rootEntity, args, null);
		}
	}

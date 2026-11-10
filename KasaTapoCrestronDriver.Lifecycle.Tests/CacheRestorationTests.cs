// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using System.Collections;
using System.Reflection;
using System.Runtime.Serialization.Json;

using Crestron.DeviceDrivers.EntityModel;
using Crestron.DeviceDrivers.EntityModel.Data;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel;

using KasaTapoClient;
using KasaTapoClient.Internal;

using DeviceType = KasaTapoClient.DeviceType;

namespace KasaTapoCrestronDriver.Tests;

#if NETFRAMEWORK
[Category ("Processor")]
#endif
[TestFixture]
public sealed class CacheRestorationTests
	{
	private PlatformDriver _driver = null!;
	private DriverLogger _logger = null!;
	private string _cache = null!;
	private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
	private static Type Nested (string name) => typeof (PlatformDriver).GetNestedType (name, BindingFlags.NonPublic)!;
	private static void Set (object target, string name, object value) => target.GetType ().GetProperty (name)!.SetValue (target, value);
	private object Call (string name, params object[] args) => typeof (PlatformDriver).GetMethod (name, Private)!.Invoke (_driver, args)!;
	private T Field<T> (string name) => (T)typeof (PlatformDriver).GetField (name, Private)!.GetValue (_driver)!;
	[Test]
	public void PersistentConfigurationIsEnabledForTheInstalledSdk ()
		{
		var definition = new ConfigurationStepsDefinition
			{
			Steps = new List<ConfigurationStepDefinition> { new () { StepId = "Activation" } }
			};
		PlatformDriver.IncludePersistentConfigurationValues (definition);
		var stepFlag = typeof (ConfigurationStepDefinition).GetProperty ("IncludePersistentValueData");
		object owner = stepFlag != null ? definition.Steps.Single () : definition;
		var flag = stepFlag ?? typeof (ConfigurationStepsDefinition).GetProperty ("IncludePersistentValueData");
		Assert.That (flag, Is.Not.Null, "The installed SDK must expose persistent configuration storage.");
		Assert.That (flag!.GetValue (owner), Is.True);
		}

	[SetUp]
	public void SetUp ()
		{
		_cache = Path.Combine (Path.GetTempPath (), "kasa-cache-tests-" + Guid.NewGuid ().ToString ("N"), "cache.json");
		Directory.CreateDirectory (Path.GetDirectoryName (_cache)!);
		_logger = new DriverLogger ("cache-test");
		CreateDriver ();
		}
	private void CreateDriver ()
		{
		var resources = new DriverImplementationResources
			{
			Logger = _logger,
			InitLogger = _logger.GetComponentLogger ("test", "platform"),
			DriverDefinition = Serialization.DefinitionFromJsonString (File.ReadAllText (Path.Combine (DriverTestPaths.DataDirectory, "DriverDefinition.json"))),
			Conditions = new Dictionary<string, ICondition> (),
			Transformations = new Dictionary<string, ITransformation> (),
			TransportConfigItems = new Dictionary<string, IList<ConfigurationItemDefinition>> ()
			};
		_driver = new PlatformDriver (new DriverControllerCreationArgs ("cache-test", DriverTestPaths.DataDirectory, _logger.AppLogger, null!), resources, _cache);
		}
	[TearDown]
	public async Task TearDown ()
		{
		_driver?.Dispose ();
		if (_driver != null)
			await _driver.Shutdown;
		_logger?.Dispose ();
		if (_cache != null)
			{
			if (File.Exists (_cache))
				File.Delete (_cache);
			Directory.Delete (Path.GetDirectoryName (_cache)!);
			}
		}
	[TestCase ("_refreshGate")]
	[TestCase ("_scheduledRefreshGate")]
	[TestCase ("_managedDeviceCacheWriteGate")]
	public async Task DisposalDrainsAdmittedWorkWithoutDestroyingItsGateOrCache (string gateName)
		{
		File.WriteAllText (_cache, "synthetic cache retained during reload");
		var gate = Field<SemaphoreSlim> (gateName);
		await gate.WaitAsync ();
		try
			{
			_driver.Dispose ();
			_driver.Dispose ();
			Assert.That (_driver.Shutdown.IsCompleted, Is.False);
			}
		finally { gate.Release (); }
		await _driver.Shutdown.WaitForTestAsync (TimeSpan.FromSeconds (5));
		Assert.That (File.ReadAllText (_cache), Is.EqualTo ("synthetic cache retained during reload"));
		// A terminal waiter may still execute its finally/release after shutdown.
		await gate.WaitAsync ();
		gate.Release ();
		}

	[Test]
	public async Task DetachedRuntimeWorkDrainsAndTerminalAdmissionDoesNotStartItsFactory ()
		{
		var entered = new TaskCompletionSource<bool> (TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource<bool> (TaskCreationOptions.RunContinuationsAsynchronously);
		Func<Task> work = async () => { entered.TrySetResult (true); await release.Task; };
		var active = (Task)Call ("RunRuntimeTask", work, CancellationToken.None);
		await entered.Task.WaitForTestAsync (TimeSpan.FromSeconds (5));
		_driver.Dispose ();
		Assert.That (_driver.Shutdown.IsCompleted, Is.False);
		bool ran = false;
		Func<Task> late = () => { ran = true; return Task.CompletedTask; };
		var rejected = (Task)Call ("RunRuntimeTask", late, CancellationToken.None);
		Assert.That (rejected.IsCanceled, Is.True);
		Assert.That (ran, Is.False);
		release.TrySetResult (true);
		await Task.WhenAll (active, _driver.Shutdown).WaitForTestAsync (TimeSpan.FromSeconds (5));
		}
	private void Seed (DeviceUxCategory category, DeviceType type, string model, bool treatAsLight = false, bool configured = false, int port = 80,
		string? discoveryId = null, string controllerId = "device_testparent_testparent00", string serial = "testparent00", string? childId = "testparent00", DeviceEncryptionKind? encryption = null)
		{
		object entry = Activator.CreateInstance (Nested ("ManagedDeviceCacheEntry"), true)!;
		Set (entry, "ControllerId", controllerId);
		Set (entry, "Name", "Office test device");
		Set (entry, "Manufacturer", "TP-Link");
		Set (entry, "Model", model);
		Set (entry, "SerialNumber", serial);
		Set (entry, "ChildId", childId!);
		Set (entry, "DiscoveryDeviceId", discoveryId!);
		Set (entry, "UxCategory", category);
		Set (entry, "DiscoveredDeviceType", type);
		Set (entry, "HubChildCategory", model == "T310" ? HubChildCategory.TemperatureHumidity : HubChildCategory.None);
		Set (entry, "Host", "192.0.2.1");
		Set (entry, "Port", port);
		if (encryption.HasValue)
			{
			Set (entry, "EncryptionKind", encryption.Value);
			Set (entry, "TransportKind", DeviceTransportKind.HttpToken);
			Set (entry, "DeviceFamily", DeviceFamilyKind.SmartKasaPlug);
			Set (entry, "LoginVersion", 2);
			}
		Set (entry, "ApplicationPath", "app");
		Set (entry, "TreatAsLight", treatAsLight);
		Set (entry, "IsConfigured", configured);
		object document = Activator.CreateInstance (Nested ("ManagedDeviceCacheDocument"), true)!;
		Set (document, "Version", 8);
		((IList)document.GetType ().GetProperty ("Devices")!.GetValue (document)!).Add (entry);
		using var stream = File.Create (_cache);
		new DataContractJsonSerializer (document.GetType ()).WriteObject (stream, document);
		}
	[TestCase (DeviceUxCategory.Light, DeviceType.Bulb, "L530", "Light")]
	[TestCase (DeviceUxCategory.Outlet, DeviceType.Strip, "KP303", "Outlet")]
	[TestCase (DeviceUxCategory.Sensor, DeviceType.Hub, "T310", "Sensor")]
	[TestCase (DeviceUxCategory.Switch, DeviceType.Hub, "S200B", "Button")]
	public void CacheRewriteAndDriverRecreationPreserveChildIdentityAndKind (DeviceUxCategory category, DeviceType type, string model, string expectedKind)
		{
		Seed (category, type, model);
		Call ("LoadManagedDeviceCacheIntoMemory");
		Call ("PersistManagedDeviceCache");
		_driver.Dispose ();
		CreateDriver ();
		Call ("LoadManagedDeviceCacheIntoMemory");
		IDictionary entries = Field<IDictionary> ("_managedDeviceCacheMetadata");
		Assert.That (entries.Count, Is.EqualTo (1));
		object entry = entries["device_testparent_testparent00"]!;
		object[] args = { entry, Field<PlatformSharedConfiguration> ("_sharedConfiguration").Snapshot (), null!, null! };
		Assert.That (Call ("TryCreateCachedDescriptorAndConfiguration", args), Is.EqualTo (true));
		var descriptor = (ManagedLightDescriptor)args[2];
		Assert.That (descriptor.ChildKind, Is.EqualTo (Enum.Parse (typeof (ManagedChildKind), expectedKind)));
		Assert.That (descriptor.ChildId, Is.EqualTo ("testparent00"));
		Assert.That (descriptor.ControllerId, Is.EqualTo ("device_testparent_testparent00"));
		Assert.That (descriptor.Name, Is.EqualTo ("Office test device"));
		if (model == "T310")
			Assert.That (descriptor.HubChildCategory, Is.EqualTo (HubChildCategory.TemperatureHumidity));
		}
	[TestCase ("Parent-Exact", "device_parent_exact_child", "Child", "Child", "Parent-Exact")]
	[TestCase (null, "device_aabb_aabb01", "AABB01", "AABB01", "AABB")]
	[TestCase (null, "device_aabb_aabb01", "AABB01", null, "AABB")]
	[TestCase (null, "device_aabb_ccdd", "CCDD", "CCDD", "aabb")]
	[TestCase (null, "device_192_0_2_1_ccdd", "CCDD", "CCDD", null)]
	[TestCase (null, "device_aabb_wrongchild", "CCDD", "CCDD", null)]
	[TestCase (null, "device_aabb", "AABB", null, "AABB")]
	public void DiscoveryIdentitySurvivesCacheRewriteAndReload (string? discoveryId, string controllerId, string serial, string? childId, string? expected)
		{
		Seed (DeviceUxCategory.Outlet, DeviceType.Strip, "KP303", discoveryId: discoveryId,
			controllerId: controllerId, serial: serial, childId: childId);
		for (int reload = 0; reload < 2; reload++)
			{
			Call ("LoadManagedDeviceCacheIntoMemory");
			object entry = Field<IDictionary> ("_managedDeviceCacheMetadata")[controllerId]!;
			object[] args = { entry, Field<PlatformSharedConfiguration> ("_sharedConfiguration").Snapshot (), null!, null! };
			Assert.That (Call ("TryCreateCachedDescriptorAndConfiguration", args), Is.True);
			var descriptor = (ManagedLightDescriptor)args[2];
			Assert.That (descriptor.DiscoveryDeviceId, Is.EqualTo (expected), $"Reload {reload}");
			Assert.That (descriptor.SerialNumber, Is.EqualTo (serial));
			Call ("PersistManagedDeviceCache");
			_driver.Dispose ();
			CreateDriver ();
			}
		}

	[TestCase (DeviceUxCategory.Outlet, DeviceType.Strip, "KP303")]
	[TestCase (DeviceUxCategory.Sensor, DeviceType.Hub, "T310")]
	[TestCase (DeviceUxCategory.Switch, DeviceType.Hub, "S200B")]
	public void CachedChildPublicationRegistersOnceWithTheSdk (DeviceUxCategory category, DeviceType type, string model)
		{
		Seed (category, type, model);
		Call ("LoadManagedDeviceCacheIntoMemory");
		_driver.Stop (); // No network refresh is allowed in this restoration test.
		var args = new DriverControllerCreationArgs ("cache-test", DriverTestPaths.DataDirectory, _logger.AppLogger, null!);
		using var registry = new RegistrationTracingController (new ConfigurableDriverEntity ("root", _driver), args, _ => { });
		Call ("PublishCachedChildControllers", Field<PlatformSharedConfiguration> ("_sharedConfiguration").Snapshot ());
		IDictionary children = Field<IDictionary> ("_childControllers");
		Assert.That (children.Count, Is.EqualTo (1));
		object first = children["device_testparent_testparent00"]!;
		Assert.That (registry.ControllerIds, Does.Contain ("device_testparent_testparent00"));
		Assert.That (registry.GetState ("device_testparent_testparent00").Definition.Properties, Is.Not.Empty);
		Call ("PublishCachedChildControllers", Field<PlatformSharedConfiguration> ("_sharedConfiguration").Snapshot ());
		Assert.That (children.Count, Is.EqualTo (1));
		Assert.That (children["device_testparent_testparent00"], Is.SameAs (first));
		}
	[TestCase (false, DeviceUxCategory.Outlet)]
	[TestCase (true, DeviceUxCategory.Light)]
	public void UnassignedPlugRestoresTreatAsLightPreference (bool treatAsLight, DeviceUxCategory category)
		{
		Seed (DeviceUxCategory.Light, DeviceType.Plug, "P110", treatAsLight);
		Call ("LoadManagedDeviceCacheIntoMemory");
		var devices = Field<System.Collections.Concurrent.ConcurrentDictionary<string, PlatformManagedDevice>> ("_managedDevices");
		Assert.That (devices.Values.Single ().UxCategory, Is.EqualTo (category));
		}
	[TestCase (DeviceUxCategory.Light, DeviceType.Bulb, "L530", false)]
	[TestCase (DeviceUxCategory.Light, DeviceType.Bulb, "L530", true)]
	[TestCase (DeviceUxCategory.Outlet, DeviceType.Strip, "KP303", false)]
	[TestCase (DeviceUxCategory.Outlet, DeviceType.Strip, "KP303", true)]
	[TestCase (DeviceUxCategory.Sensor, DeviceType.Hub, "T310", false)]
	[TestCase (DeviceUxCategory.Sensor, DeviceType.Hub, "T310", true)]
	[TestCase (DeviceUxCategory.Switch, DeviceType.Hub, "S200B", false)]
	[TestCase (DeviceUxCategory.Switch, DeviceType.Hub, "S200B", true)]
	public void SavedConfigurationRegistersCachedChildrenBeforeReturningToHost (DeviceUxCategory category, DeviceType type, string model, bool configured)
		{
		Seed (category, type, model, configured: configured);
		_driver.Stop (); // Cancel network work: cached SDK registration must not depend on discovery.
		var args = new DriverControllerCreationArgs ("cache-test", DriverTestPaths.DataDirectory, _logger.AppLogger, null!);
		using var registry = new RegistrationTracingController (new ConfigurableDriverEntity ("root", _driver), args, _ => { });
		Call ("ApplyConfigurationItems", DataDrivenConfigurationController.ApplyConfigurationAction.ApplyAll, "",
			new Dictionary<string, DriverEntityValue?> ());
		Assert.That (registry.ControllerIds, Does.Contain ("device_testparent_testparent00"),
			"The host must be able to instantiate a cached child before asynchronous hub discovery completes.");
		Assert.That (registry.GetState ("device_testparent_testparent00").Definition.Properties, Is.Not.Empty);
		}
	[Test]
	public void ConfiguredChildMarkerSurvivesDriverReload ()
		{
		Seed (DeviceUxCategory.Outlet, DeviceType.Strip, "KP303", configured: true);
		Call ("LoadManagedDeviceCacheIntoMemory");
		Assert.That (Field<IDictionary> ("_configuredChildControllerIds").Contains ("device_testparent_testparent00"), Is.True);
		}
	[Test]
	public void DisposalReleasesThePlatformDiagnosticListener ()
		{
		var property = typeof (System.Diagnostics.Debug).GetProperty ("Listeners", BindingFlags.Public | BindingFlags.Static);
		var listeners = property?.GetValue (null) as System.Diagnostics.TraceListenerCollection;
		int before = listeners?.Count ?? 0;
		_driver.Dispose ();
#if DEBUG && NETFRAMEWORK
		Assert.That (listeners!.Count, Is.EqualTo (before - 1));
#else
		Assert.That (listeners?.Count ?? 0, Is.EqualTo (before));
#endif
		}
	[Test]
	public void IncompleteReconnectMetadataDoesNotCreateAChild ()
		{
		Seed (DeviceUxCategory.Outlet, DeviceType.Strip, "KP303", port: 0);
		Call ("LoadManagedDeviceCacheIntoMemory");
		Call ("PublishCachedChildControllers", Field<PlatformSharedConfiguration> ("_sharedConfiguration").Snapshot ());
		Assert.That (Field<IDictionary> ("_childControllers"), Is.Empty);
		}
	[TestCase ("same-outlet", DeviceEncryptionKind.Klap)]
	[TestCase ("unrelated-outlet", DeviceEncryptionKind.Tpap)]
	public async Task OnlyVerifiedMatchingOutletReplacesCachedProtocol (string returnedId, DeviceEncryptionKind expected)
		{
		const string id = "same-outlet";
		const string controller = "device_same_outlet";
		Seed (DeviceUxCategory.Outlet, DeviceType.Plug, "KP125M(US)", configured: true,
			 discoveryId: id, controllerId: controller, serial: id, childId: null, encryption: DeviceEncryptionKind.Tpap);
		Call ("LoadManagedDeviceCacheIntoMemory");
		object GetCached () => Field<IDictionary> ("_managedDeviceCacheMetadata")[controller]!;
		object[] Restore ()
			{
			object[] args = { GetCached (), Field<PlatformSharedConfiguration> ("_sharedConfiguration").Snapshot (), null!, null! };
			Assert.That (Call ("TryCreateCachedDescriptorAndConfiguration", args), Is.True);
			return args;
			}
		var original = Restore ();
		Assert.That (((DeviceConfiguration)original[3]).ConnectionOptions.ConnectionParameters!.EncryptionKind, Is.EqualTo (DeviceEncryptionKind.Tpap));
		var config = new DeviceConfiguration ("192.0.2.1", 80, connectionOptions: new DeviceConnectionOptions (
			 DeviceTransportKind.HttpToken, new DeviceConnectionParameters (DeviceFamilyKind.SmartKasaPlug, DeviceEncryptionKind.Klap, 2, false, 80)));
		using var device = new KasaDevice (config, new VerifyTransport (returnedId));
		await device.UpdateAsync ();
		Call ("SaveVerifiedOutletConnection", original[2], device);
		_driver.Dispose ();
		CreateDriver ();
		Call ("LoadManagedDeviceCacheIntoMemory");
		var reloaded = Restore ();
		var descriptor = (ManagedLightDescriptor)reloaded[2];
		Assert.That (descriptor.Name, Is.EqualTo ("Office test device"));
		Assert.That (descriptor.ControllerId, Is.EqualTo (controller));
		Assert.That (descriptor.DiscoveryDeviceId, Is.EqualTo (id));
		Assert.That (((DeviceConfiguration)reloaded[3]).ConnectionOptions.ConnectionParameters!.EncryptionKind, Is.EqualTo (expected));
		}

	private sealed class VerifyTransport (string id) : IDeviceTransport
		{
		public Task<string> SendAsync (string json, CancellationToken token) => Task.FromResult (
			 "{\"error_code\":0,\"result\":{\"responses\":[{\"method\":\"get_device_info\",\"error_code\":0,\"result\":{\"device_id\":\"" + id + "\",\"model\":\"KP125M(US)\",\"device_on\":true}},{\"method\":\"component_nego\",\"error_code\":0,\"result\":{\"component_list\":[]}}]}}");
		public Task<string> SendManyAsync (IReadOnlyList<string> json, CancellationToken token) => throw new NotSupportedException ();
		}

	}
// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using System.Collections;
using System.Reflection;
using System.Runtime.Serialization.Json;

using Crestron.DeviceDrivers.EntityModel;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel;
using KasaTapoClient;
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
	public void TearDown ()
		{
		_driver?.Dispose ();
		_logger?.Dispose ();
		if (_cache != null)
			{
			if (File.Exists (_cache)) File.Delete (_cache);
			Directory.Delete (Path.GetDirectoryName (_cache)!);
			}
		}
	private void Seed (DeviceUxCategory category, DeviceType type, string model, bool treatAsLight = false, bool configured = false, int port = 80)
		{
		object entry = Activator.CreateInstance (Nested ("ManagedDeviceCacheEntry"), true)!;
		Set (entry, "ControllerId", "device_testparent_testparent00");
		Set (entry, "Name", "Office test device");
		Set (entry, "Manufacturer", "TP-Link");
		Set (entry, "Model", model);
		Set (entry, "SerialNumber", "testparent00");
		Set (entry, "ChildId", "testparent00");
		Set (entry, "UxCategory", category);
		Set (entry, "DiscoveredDeviceType", type);
		Set (entry, "HubChildCategory", model == "T310" ? HubChildCategory.TemperatureHumidity : HubChildCategory.None);
		Set (entry, "Host", "192.0.2.1");
		Set (entry, "Port", port);
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
		if (model == "T310") Assert.That (descriptor.HubChildCategory, Is.EqualTo (HubChildCategory.TemperatureHumidity));
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
	}
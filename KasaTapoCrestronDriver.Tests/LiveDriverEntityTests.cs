// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using Crestron.DeviceDrivers.SDK;
using KasaTapoClient;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KasaTapoCrestronDriver.Tests;

// A separate manual suite. Demo light power control requires an additional explicit opt-in.
[TestFixture, Category ("Processor"), Category ("Live"), NonParallelizable, FixtureLifeCycle (LifeCycle.SingleInstance)]
public sealed class LiveDriverEntityTests
	{
	private LiveSettings _settings = null!;
	private IReadOnlyList<DiscoveryResult> _discovered = null!;
	private TimeSpan _timeout;

	[OneTimeSetUp]
	public async Task DiscoverConfiguredDevices ()
		{
		var path = Path.Combine (TestContext.Parameters.Get ("TestDataDirectory", AppContext.BaseDirectory), "LiveTestSettings.json");
		var enabled = TestContext.Parameters.Get ("EnableLiveTests", "false").Equals ("true", StringComparison.OrdinalIgnoreCase);
		if (!File.Exists (path))
			{
			if (enabled) throw new InvalidDataException ("Enabled live tests require LiveTestSettings.json in TestDataDirectory.");
			Assert.Ignore ("Live tests require private settings and explicit enablement.");
			}
		_settings = JsonSerializer.Deserialize<LiveSettings> (File.ReadAllText (path)) ?? throw new InvalidDataException ("Invalid live settings.");
		if (!enabled && !_settings.Enabled)
			Assert.Ignore ("Live tests are disabled.");
		var seconds = _settings.TimeoutSeconds;
		if (seconds < 1 || seconds > 120) throw new InvalidDataException ("Invalid live discovery timeout.");
		_timeout = TimeSpan.FromSeconds (seconds);
		_discovered = await Discover.DiscoverAsync (_timeout).ConfigureAwait (false);
		}

	private LiveTarget Target (string role)
		{
		if (!_settings.Devices.TryGetValue (role, out var target)) throw new InvalidDataException ($"Missing live role '{role}'.");
		if (target.Hosts is List<LiveTarget> hosts)
			{
			if (hosts.Count != 1) throw new InvalidDataException ($"Driver live role '{role}' requires exactly one selected device.");
			target = hosts[0];
			}
		return target;
		}

	private async Task<KasaDevice> Connect (string role)
		{
		var target = Target (role);
		var id = target.DeviceId?.Trim ();
		var alias = target.Alias?.Trim ();
		if (string.IsNullOrWhiteSpace (id) && string.IsNullOrWhiteSpace (alias))
			throw new InvalidDataException ($"Role '{role}' requires a stable deviceId or unique discovery alias.");
		var matches = _discovered.Where (d => !string.IsNullOrWhiteSpace (id)
			? string.Equals (d.DeviceId, id, StringComparison.OrdinalIgnoreCase)
			: string.Equals (d.Alias, alias, StringComparison.OrdinalIgnoreCase)).ToArray ();
		if (matches.Select (d => d.Host).Distinct (StringComparer.OrdinalIgnoreCase).Count () != 1)
			throw new InvalidDataException ($"Role '{role}' did not resolve to exactly one reachable device.");
		var selected = matches.OrderByDescending (d => d.TpapPreferred == true || d.TpapMetadata != null).First ();
		var credentials = new DeviceCredentials (_settings.Credentials?.UserName, _settings.Credentials?.Password);
		return await Discover.ConnectAsync (Discover.CreateConfiguration (selected, credentials, _timeout)).ConfigureAwait (false);
		}

	private PlatformSharedConfiguration Settings ()
		{
		var settings = new PlatformSharedConfiguration ();
		// Explicit refresh only; no background polling or processor lighting callbacks.
		settings.Update ("", "", _timeout, false, TimeSpan.FromSeconds (5), TimeSpan.FromSeconds (30), false, "", "", "");
		return settings;
		}

	[Test]
	public async Task Light_RefreshPublishesObservedPowerAndBrightness ()
		{
		using var device = await Connect ("light");
		Assert.That (device.Light.State, Is.Not.Null);
		using var logger = new DriverLogger ("live-light-test");
		var resources = new DriverImplementationResources { Logger = logger, InitLogger = logger.GetComponentLogger ("test", "live") };
		var descriptor = new ManagedLightDescriptor ("live-light", device.Configuration.Host, device.DeviceType,
			"Live test light", "", "", ManagedLightKind.Color);
		using var entity = new KasaLightEntity ("live-light", descriptor, device.Configuration, null, Settings (), null, resources, null!, "live-test");
		Assert.That (entity.TryAttachConnectedDevice (device, "live-test"), Is.True);
		await entity.SetConfiguredAsync (true, "live-test", default);
		await entity.RefreshAsync (default);
		Assert.That (entity.OnlineIndicatorIsOnline && entity.ReadyIndicatorIsReady, Is.True);
		var state = device.Light.State!;
		Assert.That (state.IsOn, Is.Not.Null);
		Assert.That (state.Brightness, Is.Not.Null);
        // Dimmable Home lights encode power in level (zero means off); light:isOn belongs to on/off-only entities.
        var level = entity.GetState ().PropertyValues["lightDimmer:level"].GetValue<double> ();
        Assert.That (level > 0, Is.EqualTo (state.IsOn));
        Assert.That (level, Is.EqualTo (state.IsOn == true ? state.Brightness!.Value / 100.0 : 0).Within (0.001));
		}

	[TestCase ("plug")]
	[TestCase ("strip")]
	public async Task Outlet_RefreshPublishesObservedPower (string role)
		{
		using var device = await Connect (role);
		string? childId = role == "strip" ? Target (role).ChildDeviceId : null;
		if (role == "strip" && string.IsNullOrWhiteSpace (childId)) throw new InvalidDataException ("Select a strip childDeviceId.");
		using var logger = new DriverLogger ("live-outlet-test");
		var resources = new DriverImplementationResources { Logger = logger, InitLogger = logger.GetComponentLogger ("test", "live") };
		var descriptor = new ManagedLightDescriptor ("live-outlet", device.Configuration.Host, device.DeviceType,
			"Live test outlet", "", "", ManagedLightKind.OnOff, childId: childId, childKind: ManagedChildKind.Outlet);
		using var entity = new KasaOutletEntity ("live-outlet", descriptor, device.Configuration, null, Settings (), resources, null!, "live-test", DriverTestPaths.DataDirectory);
		Assert.That (entity.TryAttachConnectedDevice (device, "live-test"), Is.True);
		await entity.SetConfiguredAsync (true, "live-test", default);
		await entity.RefreshAsync (default);
		Assert.That (entity.OnlineIndicatorIsOnline && entity.ReadyIndicatorIsReady, Is.True);
		bool? expected = childId == null ? device.IsOn : device.Children.Single (child => child.Id == childId).IsOn;
		Assert.That (expected, Is.Not.Null);
		Assert.That (entity.OutletIsOn, Is.EqualTo (expected));
		}

	[Test]
	public async Task DemoLight_LevelPowerCommandsPreserveBrightnessAndRestoreState ()
		{
		if (!_settings.AllowDemoPowerControl) Assert.Ignore ("Demo power control requires its own private opt-in.");
		using var device = await Connect ("light");
		using var observer = await Connect ("light");
		void RequireDemo () => Assert.That (device.SystemInfo?.Alias?.IndexOf ("demo", StringComparison.OrdinalIgnoreCase) >= 0,
			Is.True, "Only a currently named demo light is authorized for control.");
		RequireDemo ();
		var original = observer.Light.State ?? throw new InvalidDataException ("The demo light must report its original state.");
		Assert.That (original.IsOn, Is.Not.Null);
		Assert.That (original.Brightness, Is.InRange (1, 100));
		using var logger = new DriverLogger ("demo-power-test");
		var resources = new DriverImplementationResources { Logger = logger, InitLogger = logger.GetComponentLogger ("test", "demo") };
		var kind = KasaLightEntity.InferManagedLightKind (device, device.DeviceType);
		var descriptor = new ManagedLightDescriptor ("demo-light", device.Configuration.Host, device.DeviceType,
			device.SystemInfo!.Alias!, device.SystemInfo.Model!, device.SystemInfo.DeviceId!, kind);
		using var entity = new KasaLightEntity ("demo-light", descriptor, device.Configuration, null, Settings (), null, resources, null!, "demo-test");
		Assert.That (entity.TryAttachConnectedDevice (device, "demo-test"), Is.True);
		await entity.SetConfiguredAsync (true, "demo-test", default);
		typeof (KasaLightEntity).GetProperty ("_startupDimmerReplayGuardUntilUtc", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue (entity, null);

		async Task Observe (bool on)
			{
			DateTime deadline = DateTime.UtcNow.AddSeconds (30);
			do
				{
				await observer.UpdateAsync ();
				if (observer.Light.State?.IsOn == on)
					{
					Assert.That (observer.Light.State.Brightness, Is.EqualTo (original.Brightness), "Power must retain brightness.");
					Assert.That (observer.Light.State.Hue, Is.EqualTo (original.Hue));
					Assert.That (observer.Light.State.Saturation, Is.EqualTo (original.Saturation));
					Assert.That (observer.Light.State.ColorTemperature, Is.EqualTo (original.ColorTemperature));
					TestContext.Progress.WriteLine ($"Demo observation: {device.SystemInfo.Alias} ({device.SystemInfo.Model}), power={on}, brightness={observer.Light.State.Brightness}.");
					return;
					}
				await Task.Delay (250);
				} while (DateTime.UtcNow < deadline);
			Assert.Fail ("Demo light power did not reach the requested state.");
			}

		try
			{
			foreach (double level in new[] { 0d, 1d })
				{
				await device.UpdateAsync ();
				RequireDemo ();
				entity.ExecuteCommand ("lightDimmer:setLevel", new Dictionary<string, Crestron.DeviceDrivers.EntityModel.Data.DriverEntityValue> { ["level"] = new (level) },
					result => Assert.That (result.Failed, Is.False));
				await Observe (level > 0);
				// Ensure the entity's refreshed zero level settles before sending its power-on gesture.
				await entity.RefreshAsync (default);
				}
			}
		finally
			{
			await device.UpdateAsync ();
			RequireDemo ();
			if (device.Light.State?.Brightness != original.Brightness)
				await device.SetBrightnessAsync (original.Brightness!.Value);
			if (device.Light.State?.Hue != original.Hue || device.Light.State?.Saturation != original.Saturation)
				await device.SetHsvAsync (original.Hue!.Value, original.Saturation!.Value, original.Brightness!.Value);
			if (original.ColorTemperature > 0 && device.Light.State?.ColorTemperature != original.ColorTemperature)
				await device.SetColorTemperatureAsync (original.ColorTemperature.Value);
			if (original.IsOn == true) await device.TurnOnAsync (); else await device.TurnOffAsync ();
			await Observe (original.IsOn!.Value);
			}
		}

	internal sealed class LiveSettings
		{
		[JsonPropertyName ("enabled")] public bool Enabled { get; set; }
		[JsonPropertyName ("timeoutSeconds")] public int TimeoutSeconds { get; set; } = 10;
		[JsonPropertyName ("devices")] public Dictionary<string, LiveTarget> Devices { get; set; } = new ();
		[JsonPropertyName ("credentials")] public LiveCredentials? Credentials { get; set; }
		[JsonPropertyName ("allowDemoPowerControl")] public bool AllowDemoPowerControl { get; set; }
		}
	internal sealed class LiveCredentials
		{
		[JsonPropertyName ("userName")] public string? UserName { get; set; }
		[JsonPropertyName ("password")] public string? Password { get; set; }
		}
	internal sealed class LiveTarget
		{
		[JsonPropertyName ("deviceId")] public string? DeviceId { get; set; }
		[JsonPropertyName ("alias")] public string? Alias { get; set; }
		[JsonPropertyName ("childDeviceId")] public string? ChildDeviceId { get; set; }
		[JsonPropertyName ("hosts")] public List<LiveTarget>? Hosts { get; set; }
		}

	}

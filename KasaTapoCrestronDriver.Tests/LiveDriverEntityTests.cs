// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using Crestron.DeviceDrivers.SDK;
using KasaTapoClient;
using Newtonsoft.Json.Linq;

namespace KasaTapoCrestronDriver.Tests;

// A separate manual suite: never sends a device-control command.
[TestFixture, Category ("Processor"), Category ("Live"), NonParallelizable]
public sealed class LiveDriverEntityTests
	{
	private JObject _settings = null!;
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
		_settings = JObject.Parse (File.ReadAllText (path));
		if (!enabled && (bool?)_settings["enabled"] != true)
			Assert.Ignore ("Live tests are disabled.");
		var seconds = (int?)_settings["timeoutSeconds"] ?? 10;
		if (seconds < 1 || seconds > 120) throw new InvalidDataException ("Invalid live discovery timeout.");
		_timeout = TimeSpan.FromSeconds (seconds);
		_discovered = await Discover.DiscoverAsync (_timeout).ConfigureAwait (false);
		}

	private JToken Target (string role)
		{
		var target = _settings["devices"]?[role] ?? throw new InvalidDataException ($"Missing live role '{role}'.");
		if (target["hosts"] is JArray hosts)
			{
			if (hosts.Count != 1) throw new InvalidDataException ($"Driver live role '{role}' requires exactly one selected device.");
			target = hosts[0];
			}
		return target;
		}

	private async Task<KasaDevice> Connect (string role)
		{
		var target = Target (role);
		var id = ((string?)target["deviceId"])?.Trim ();
		var alias = ((string?)target["alias"])?.Trim ();
		if (string.IsNullOrWhiteSpace (id) && string.IsNullOrWhiteSpace (alias))
			throw new InvalidDataException ($"Role '{role}' requires a stable deviceId or unique discovery alias.");
		var matches = _discovered.Where (d => !string.IsNullOrWhiteSpace (id)
			? string.Equals (d.DeviceId, id, StringComparison.OrdinalIgnoreCase)
			: string.Equals (d.Alias, alias, StringComparison.OrdinalIgnoreCase)).ToArray ();
		if (matches.Select (d => d.Host).Distinct (StringComparer.OrdinalIgnoreCase).Count () != 1)
			throw new InvalidDataException ($"Role '{role}' did not resolve to exactly one reachable device.");
		var selected = matches.OrderByDescending (d => d.TpapPreferred == true || d.TpapMetadata != null).First ();
		var credentials = new DeviceCredentials ((string?)_settings["credentials"]?["userName"], (string?)_settings["credentials"]?["password"]);
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
		string? childId = role == "strip" ? (string?)Target (role)["childDeviceId"] : null;
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
	}
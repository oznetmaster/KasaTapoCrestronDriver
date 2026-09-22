// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using System.Text.Json;
using System.Text.Json.Serialization;

using KasaTapoClient;

// Read-only physical observation. Commands are always sent through the installed driver, never this probe.
try
	{
	if (args.Length != 8)
		throw new ArgumentException ();
	var options = Enumerable.Range (0, 4).ToDictionary (i => args[2 * i], i => args[2 * i + 1], StringComparer.Ordinal);
	if (options.Keys.Any (k => k is not ("--settings" or "--role" or "--request" or "--response")))
		throw new ArgumentException ();
	var settings = JsonSerializer.Deserialize<ProbeSettings> (File.ReadAllText (options["--settings"])) ?? throw new InvalidDataException ();
	var request = JsonSerializer.Deserialize<ProbeRequest> (File.ReadAllText (options["--request"])) ?? throw new InvalidDataException ();
	string requestId = request.RequestId;
	if (!Guid.TryParseExact (requestId, "N", out _))
		throw new InvalidDataException ();
	string physicalId = request.PhysicalIdentity;
	var configured = settings.Devices[options["--role"]];
	if (configured.Hosts is { } hosts)
		{
		if (hosts.Length != 1)
			throw new InvalidDataException ();
		configured = hosts[0];
		}
	string id = configured.DeviceId;
	string? childId = configured.ChildDeviceId;
	string expectedId = id + (string.IsNullOrWhiteSpace (childId) ? "" : "/" + childId);
	if (string.IsNullOrWhiteSpace (id) || expectedId != physicalId)
		throw new InvalidDataException ();
	using var timeout = new CancellationTokenSource (TimeSpan.FromSeconds (40));
	var discovered = await Discover.DiscoverAsync (TimeSpan.FromSeconds (3), cancellationToken: timeout.Token);
	var matches = discovered.Where (d => string.Equals (d.DeviceId, id, StringComparison.OrdinalIgnoreCase)).ToArray ();
	if (matches.Select (d => d.Host).Distinct (StringComparer.OrdinalIgnoreCase).Count () != 1)
		throw new InvalidDataException ();
	var selected = matches.OrderByDescending (d => d.TpapPreferred == true || d.TpapMetadata != null).First ();
	var credentials = settings.Credentials;
	using var device = await Discover.ConnectAsync (Discover.CreateConfiguration (selected,
		new DeviceCredentials (credentials.UserName, credentials.Password), TimeSpan.FromSeconds (15)), timeout.Token);
	if (!string.Equals (device.SystemInfo?.DeviceId, id, StringComparison.OrdinalIgnoreCase))
		throw new InvalidDataException ();
	bool? power = string.IsNullOrWhiteSpace (childId) ? device.IsOn : device.GetChild (childId!)?.IsOn;
	if (power == null)
		throw new InvalidDataException ();
	await using var output = new FileStream (options["--response"], FileMode.CreateNew, FileAccess.Write, FileShare.None);
	await JsonSerializer.SerializeAsync (output, new ProbeResponse
		{
		RequestId = requestId,
		PhysicalIdentity = physicalId,
		Value = power.Value,
		RestoreValue = power.Value
		}, cancellationToken: timeout.Token);
	return 0;
	}
catch
	{
	Console.Error.WriteLine ("Independent device observation failed; verify the private settings, identity and connectivity.");
	return 1;
	}

internal sealed class ProbeSettings
	{
	[JsonPropertyName ("devices")] public Dictionary<string, ProbeTarget> Devices { get; set; } = new ();
	[JsonPropertyName ("credentials")] public ProbeCredentials Credentials { get; set; } = new ();
	}
internal sealed class ProbeCredentials
	{
	[JsonPropertyName ("userName")] public string? UserName { get; set; }
	[JsonPropertyName ("password")] public string? Password { get; set; }
	}
internal sealed class ProbeTarget
	{
	[JsonPropertyName ("deviceId")] public string DeviceId { get; set; } = "";
	[JsonPropertyName ("childDeviceId")] public string? ChildDeviceId { get; set; }
	[JsonPropertyName ("hosts")] public ProbeTarget[]? Hosts { get; set; }
	}
internal sealed class ProbeRequest
	{
	[JsonPropertyName ("RequestId")] public string RequestId { get; set; } = "";
	[JsonPropertyName ("PhysicalIdentity")] public string PhysicalIdentity { get; set; } = "";
	}
internal sealed class ProbeResponse
	{
	[JsonPropertyName ("RequestId")] public string RequestId { get; set; } = "";
	[JsonPropertyName ("PhysicalIdentity")] public string PhysicalIdentity { get; set; } = "";
	[JsonPropertyName ("Value")] public bool Value { get; set; }
	[JsonPropertyName ("RestoreValue")] public bool RestoreValue { get; set; }
	}

// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using System.Text.Json;

using KasaTapoClient;

// Read-only physical observation. Commands are always sent through the installed driver, never this probe.
try
	{
	if (args.Length != 8)
		throw new ArgumentException ();
	var options = Enumerable.Range (0, 4).ToDictionary (i => args[2 * i], i => args[2 * i + 1], StringComparer.Ordinal);
	if (options.Keys.Any (k => k is not ("--settings" or "--role" or "--request" or "--response")))
		throw new ArgumentException ();
	using var settings = JsonDocument.Parse (File.ReadAllText (options["--settings"]));
	using var request = JsonDocument.Parse (File.ReadAllText (options["--request"]));
	string requestId = request.RootElement.GetProperty ("RequestId").GetString ()!;
	if (!Guid.TryParseExact (requestId, "N", out _))
		throw new InvalidDataException ();
	string physicalId = request.RootElement.GetProperty ("PhysicalIdentity").GetString ()!;
	var configured = settings.RootElement.GetProperty ("devices").GetProperty (options["--role"]);
	if (configured.TryGetProperty ("hosts", out var hosts))
		{
		if (hosts.GetArrayLength () != 1)
			throw new InvalidDataException ();
		configured = hosts[0];
		}
	string id = configured.GetProperty ("deviceId").GetString ()!;
	string? childId = configured.TryGetProperty ("childDeviceId", out var child) ? child.GetString () : null;
	string expectedId = id + (string.IsNullOrWhiteSpace (childId) ? "" : "/" + childId);
	if (string.IsNullOrWhiteSpace (id) || expectedId != physicalId)
		throw new InvalidDataException ();
	using var timeout = new CancellationTokenSource (TimeSpan.FromSeconds (40));
	var discovered = await Discover.DiscoverAsync (TimeSpan.FromSeconds (3), cancellationToken: timeout.Token);
	var matches = discovered.Where (d => string.Equals (d.DeviceId, id, StringComparison.OrdinalIgnoreCase)).ToArray ();
	if (matches.Select (d => d.Host).Distinct (StringComparer.OrdinalIgnoreCase).Count () != 1)
		throw new InvalidDataException ();
	var selected = matches.OrderByDescending (d => d.TpapPreferred == true || d.TpapMetadata != null).First ();
	var credentials = settings.RootElement.GetProperty ("credentials");
	using var device = await Discover.ConnectAsync (Discover.CreateConfiguration (selected,
		new DeviceCredentials (credentials.GetProperty ("userName").GetString (), credentials.GetProperty ("password").GetString ()), TimeSpan.FromSeconds (15)), timeout.Token);
	if (!string.Equals (device.SystemInfo?.DeviceId, id, StringComparison.OrdinalIgnoreCase))
		throw new InvalidDataException ();
	bool? power = string.IsNullOrWhiteSpace (childId) ? device.IsOn : device.GetChild (childId!)?.IsOn;
	if (power == null)
		throw new InvalidDataException ();
	await using var output = new FileStream (options["--response"], FileMode.CreateNew, FileAccess.Write, FileShare.None);
	await JsonSerializer.SerializeAsync (output, new
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
// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using System.Collections.Generic;

namespace TestDriverSideload;

internal sealed class PayloadMetadata
    {
    public string FileName { get; set; } = "";
    public string Model { get; set; } = "";
    public string DriverId { get; set; } = "";
    public string Version { get; set; } = "";
    public string Sha256 { get; set; } = "";
    }

internal sealed class Payload
    {
    public PayloadMetadata Metadata { get; }
    private readonly byte[] _bytes;
    public long Length => _bytes.LongLength;
    public Stream Open () => new MemoryStream (_bytes, false);
    public Payload (byte[] bytes, PayloadMetadata metadata)
        {
        Metadata = metadata;
        _bytes = bytes;
        if (!string.Equals (Hash (Open ()), metadata.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException ("The embedded driver checksum is incorrect. Request a fresh download.");
        if (Path.GetFileName (metadata.FileName) != metadata.FileName || !metadata.FileName.EndsWith (".pkg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException ("Invalid embedded package filename.");
        using var zip = new ZipArchive (Open (), ZipArchiveMode.Read);
        var entries = zip.Entries.Where (e => e.FullName.EndsWith (".dat", StringComparison.OrdinalIgnoreCase) && e.FullName.IndexOfAny (new[] { '/', '\\' }) < 0).ToArray ();
        if (entries.Length != 1 || entries[0].Length > 1024 * 1024)
            throw new InvalidDataException ("Invalid embedded driver manifest.");
        using var reader = new StreamReader (entries[0].Open ());
        var manifest = new JavaScriptSerializer ().Deserialize<Dictionary<string, object>> (reader.ReadToEnd ());
        if (!string.Equals (manifest["driverId"] as string, metadata.DriverId, StringComparison.OrdinalIgnoreCase)
            || (manifest["baseModel"] as string) != metadata.Model || (manifest["driverVersion"] as string) != metadata.Version)
            throw new InvalidDataException ("Embedded driver identity or version does not match the installer.");
        }
    public static string Hash (Stream stream)
        {
        using (stream)
        using (var sha = SHA256.Create ())
            return BitConverter.ToString (sha.ComputeHash (stream)).Replace ("-", "").ToLowerInvariant ();
        }
    public static Payload Load ()
        {
        using var metadata = new StreamReader (Resource ("Payload.json"));
        using var input = Resource ("Payload.pkg");
        using var data = new MemoryStream ();
        input.CopyTo (data);
        return new Payload (data.ToArray (), new JavaScriptSerializer ().Deserialize<PayloadMetadata> (metadata.ReadToEnd ()));
        }
    public static Stream Resource (string name) => Assembly.GetExecutingAssembly ().GetManifestResourceStream (name)
        ?? throw new InvalidDataException ("Missing bundled component: " + name);
    }

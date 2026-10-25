// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
using System;
using System.IO;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace TestDriverSideload;

internal interface IRemoteFiles
    {
    bool Exists (string path);
    long Size (string path);
    Stream OpenRead (string path);
    void Upload (Stream data, string path, Action<ulong> progress);
    void Rename (string from, string to);
    void Delete (string path);
    }

internal static class Upload
    {
    public const string ImportDirectory = "/user/ThirdPartyDrivers/Import";
    // The only remote writes are a unique temporary upload, its rename into Import,
    // and cleanup of that temporary file. No installed-driver operation is exposed.
    public static bool Run (Payload payload, IRemoteFiles files, Action<ulong> progress)
        {
        if (files.Exists ("/user/CrestronHomeNUnit-WorkflowLease") || files.Exists ("/user/CrestronHomeNUnit-WorkflowLease.ActiveTest"))
            throw new IOException ("Another maintenance or test operation has reserved this processor. Please try after it finishes.");
        if (!files.Exists (ImportDirectory))
            throw new DirectoryNotFoundException ("The Crestron Home driver import folder was not found. Check the processor address and account permissions.");
        string destination = ImportDirectory + "/" + payload.Metadata.FileName;
        if (files.Exists (destination))
            {
            if (files.Size (destination) == payload.Length && Payload.Hash (files.OpenRead (destination)) == payload.Metadata.Sha256)
                return false;
            throw new IOException ("A different package with the same filename is already waiting in the import folder. Please contact Neil before continuing.");
            }
        string temporary = ImportDirectory + "/.test-upload-" + Guid.NewGuid ().ToString ("N") + ".tmp";
        bool renamed = false;
        try
            {
            using (var data = payload.Open ()) files.Upload (data, temporary, progress);
            if (files.Size (temporary) != payload.Length || Payload.Hash (files.OpenRead (temporary)) != payload.Metadata.Sha256)
                throw new IOException ("The uploaded driver did not pass its checksum check. The package was not submitted for import.");
            files.Rename (temporary, destination);
            renamed = true;
            return true;
            }
        finally
            {
            // Never remove a .pkg file, another upload, or any installed-driver file.
            if (!renamed) try { if (files.Exists (temporary)) files.Delete (temporary); } catch (Exception) { }
            }
        }
    }

internal sealed class SftpFiles : IRemoteFiles, IDisposable
    {
    private readonly SftpClient _client;
    public SftpFiles (string host, int port, string user, string password, string fingerprint)
        {
        _client = new SftpClient (host, port, user, password);
        _client.ConnectionInfo.Timeout = TimeSpan.FromSeconds (15);
        _client.OperationTimeout = TimeSpan.FromSeconds (30);
        _client.HostKeyReceived += (_, e) => e.CanTrust = string.Equals (e.FingerPrintSHA256, fingerprint, StringComparison.Ordinal);
        try { _client.Connect (); } catch { _client.Dispose (); throw; }
        }
    public static string Probe (string host, int port)
        {
        string? fingerprint = null;
        // Read the key before authentication; do not send the user's credentials.
        using var probe = new SftpClient (host, port, "host-key-probe", "");
        probe.ConnectionInfo.Timeout = TimeSpan.FromSeconds (15);
        probe.HostKeyReceived += (_, e) => { fingerprint = e.FingerPrintSHA256; e.CanTrust = false; };
        try { probe.Connect (); } catch (SshConnectionException) when (fingerprint != null) { }
        return fingerprint ?? throw new IOException ("Could not read the processor SSH identity. Check the address and SFTP access.");
        }
    public bool Exists (string path) => _client.Exists (path);
    public long Size (string path) => _client.GetAttributes (path).Size;
    public Stream OpenRead (string path) => _client.OpenRead (path);
    public void Upload (Stream data, string path, Action<ulong> progress) => _client.UploadFile (data, path, false, progress);
    public void Rename (string from, string to) => _client.RenameFile (from, to);
    public void Delete (string path) => _client.DeleteFile (path);
    public void Dispose () => _client.Dispose ();
    }

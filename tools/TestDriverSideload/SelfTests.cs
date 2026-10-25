// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace TestDriverSideload;

internal static class SelfTests
    {
    private sealed class Remote : IRemoteFiles
        {
        public readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]> ();
        public readonly List<string> Writes = new List<string> ();
        public bool Busy, Corrupt, Interrupt;
        public bool Exists (string p) => p == TestDriverSideload.Upload.ImportDirectory || (Busy && p.EndsWith ("WorkflowLease")) || Files.ContainsKey (p);
        public long Size (string p) => Files[p].LongLength;
        public Stream OpenRead (string p) => new MemoryStream (Files[p], false);
        public void Upload (Stream data, string p, Action<ulong> progress)
            {
            Writes.Add ("upload:" + p);
            using var memory = new MemoryStream ();
            data.CopyTo (memory);
            Files[p] = memory.ToArray ();
            if (Corrupt) Files[p][0] ^= 1;
            if (Interrupt) throw new IOException ("Simulated interrupted transfer.");
            progress ((ulong)memory.Length);
            }
        public void Rename (string from, string to)
            { if (Files.ContainsKey (to)) throw new IOException (); Writes.Add ("rename:" + to); Files.Add (to, Files[from]); Files.Remove (from); }
        public void Delete (string p) { Writes.Add ("delete:" + p); Files.Remove (p); }
        }
    public static string Run (Payload payload)
        {
        var checks = new List<string> ();
        void Check (bool value, string name) { if (!value) throw new Exception ("Self-test failed: " + name); checks.Add (name); }
        void Reject (Action action) { try { action (); } catch (Exception e) when (e is IOException || e is InvalidDataException) { return; } throw new Exception ("Expected rejection."); }
        string target = Upload.ImportDirectory + "/" + payload.Metadata.FileName;
        var complete = new Remote ();
        Check (Upload.Run (payload, complete, _ => { }), "successful upload");
        Check (Payload.Hash (complete.OpenRead (target)) == payload.Metadata.Sha256 && complete.Files.Count == 1, "exact payload exposed only after checksum verification");
        Check (complete.Writes.All (w => w.Contains (Upload.ImportDirectory + "/")), "writes confined to import directory");
        int writes = complete.Writes.Count;
        Check (!Upload.Run (payload, complete, _ => { }) && writes == complete.Writes.Count, "same staged package is not rewritten");
        var conflict = new Remote (); conflict.Files[target] = new byte[] { 1, 2, 3 };
        Reject (() => Upload.Run (payload, conflict, _ => { }));
        Check (conflict.Writes.Count == 0 && conflict.Files[target].Length == 3, "different staged package is preserved");
        foreach (bool interrupt in new[] { false, true })
            {
            var failed = new Remote { Corrupt = !interrupt, Interrupt = interrupt };
            Reject (() => Upload.Run (payload, failed, _ => { }));
            Check (failed.Files.Count == 0 && !failed.Writes.Any (w => w.StartsWith ("rename:")), interrupt ? "interrupted transfer is not submitted" : "corrupted transfer is not submitted");
            }
        var busy = new Remote { Busy = true };
        Reject (() => Upload.Run (payload, busy, _ => { }));
        Check (busy.Writes.Count == 0, "processor reservation prevents upload");
        using var bytes = new MemoryStream ();
        using (var data = payload.Open ()) data.CopyTo (bytes);
        var corrupt = bytes.ToArray (); corrupt[0] ^= 1;
        Reject (() => new Payload (corrupt, payload.Metadata));
        Check (true, "modified embedded package is rejected");
        var wrong = new PayloadMetadata { FileName = payload.Metadata.FileName, Model = payload.Metadata.Model,
            Version = "0.0.0.0", DriverId = payload.Metadata.DriverId, Sha256 = payload.Metadata.Sha256 };
        Reject (() => new Payload (bytes.ToArray (), wrong));
        Check (true, "incorrect version label is rejected");
        return new JavaScriptSerializer ().Serialize (new { Passed = checks.Count, Checks = checks });
        }
    }

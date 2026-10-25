// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
using System;
using System.IO;
using System.Text;

namespace TestDriverSideload;

internal static class Diagnostics
{
    public const string DirectoryPath = "/user/Data/ThirdParty/NeilColvin/KasaTapoCrestronDriver/diagnostics";
    public const int MaximumFileBytes = 2 * 1024 * 1024;
    // Read only these two bounded files. Never download device caches or configuration.
    public static void Download (IRemoteFiles remote, string destination, string version)
    {
        if (File.Exists (destination)) throw new IOException ("Choose a new filename; an existing file will not be overwritten.");
        string temporary = destination + ".download-" + Guid.NewGuid ().ToString ("N") + ".tmp";
        try
        {
            int count = 0;
            using (var output = new FileStream (temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                void Header (string value)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes (value + Environment.NewLine);
                    output.Write (bytes, 0, bytes.Length);
                }
                Header ("Kasa/Tapo diagnostic collection; utility package " + version + "; collected UTC " + DateTime.UtcNow.ToString ("O"));
                foreach (string name in new[] { "diagnostic.log.1", "diagnostic.log" })
                {
                    string path = DirectoryPath + "/" + name;
                    if (!remote.Exists (path)) continue;
                    long remaining = remote.Size (path);
                    if (remaining < 0 || remaining > MaximumFileBytes) throw new IOException ("Diagnostic file exceeds the expected size limit.");
                    using var input = remote.OpenRead (path);
                    Header ("--- " + name + " (snapshot of " + remaining + " bytes) ---");
                    byte[] buffer = new byte[32768];
                    while (remaining > 0)
                    {
                        int read = input.Read (buffer, 0, (int)Math.Min (remaining, buffer.Length));
                        if (read == 0) throw new IOException ("Diagnostic file changed during collection. Please collect it again.");
                        output.Write (buffer, 0, read);
                        remaining -= read;
                    }
                    Header ("");
                    count++;
                }
                if (count == 0) throw new FileNotFoundException ("No diagnostic file was found. Confirm the diagnostic driver has been installed and reloaded in Home Setup or Configure Pro.");
            }
            File.Move (temporary, destination);
        }
        finally
        {
            if (File.Exists (temporary)) File.Delete (temporary);
        }
    }
}

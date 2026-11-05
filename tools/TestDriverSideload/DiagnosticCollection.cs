// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
using System;
using System.Diagnostics;
using System.Windows.Forms;
#if OUTLET_DIAGNOSTICS
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
#endif
namespace TestDriverSideload;
internal sealed partial class UploadForm
{
#if OUTLET_DIAGNOSTICS
    private readonly Button _collect = new Button { Text = "Collect diagnostics", AutoSize = true };
#endif
    [Conditional("OUTLET_DIAGNOSTICS")]
    private void AddDiagnosticCollectionButton(FlowLayoutPanel buttons)
    {
#if OUTLET_DIAGNOSTICS
        buttons.Controls.Add(_collect);
        _collect.Click += async (_, _) => await CollectDiagnostics();
#endif
    }
    [Conditional("OUTLET_DIAGNOSTICS")]
    private void SetDiagnosticCollectionEnabled(bool enabled)
    {
#if OUTLET_DIAGNOSTICS
        _collect.Enabled = enabled;
#endif
    }
#if OUTLET_DIAGNOSTICS
    private async Task CollectDiagnostics()
    {
        string host = _host.Text.Trim(), user = _user.Text.Trim(), password = _password.Text;
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown || string.IsNullOrWhiteSpace(user) || password.Length == 0)
        { MessageBox.Show(this, "Enter the processor address and processor login.", Text); return; }
        using var dialog = new SaveFileDialog { Filter = "Diagnostic ZIP (*.zip)|*.zip", FileName = "KasaTapo-outlet-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _busy = true; _collect.Enabled = _upload.Enabled = _host.Enabled = _user.Enabled = _password.Enabled = _port.Enabled = false;
        int port = (int)_port.Value;
        try {
            _status.Text = "Reading processor identity...";
            string fingerprint = await Task.Run(() => SftpFiles.Probe(host, port));
            if (MessageBox.Show(this, "Collect diagnostic files from " + host + "?\r\n\r\nSSH identity (SHA256):\r\n" + fingerprint + "\r\n\r\nThe report includes device names, local addresses and readings. It is saved on this PC for you to email to support. Processor files are not modified.", "Confirm diagnostic collection", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            _status.Text = "Collecting diagnostic files...";
            int count = await Task.Run(() => {
                using var remote = new SftpFiles(host, port, user, password, fingerprint);
                return DiagnosticDownload.Run(remote, dialog.FileName, _payload.Metadata.Version);
            });
            _status.Text = "Saved " + count + " capture file(s) to:\r\n" + dialog.FileName + "\r\nPlease email this ZIP to Neil. No processor files were changed.";
        } catch (Exception e) { _status.Text = "Collection failed: " + e.Message; MessageBox.Show(this, _status.Text, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { password = ""; _password.Clear(); _busy = false; _collect.Enabled = true; _upload.Enabled = !_submitted; _host.Enabled = _user.Enabled = _password.Enabled = _port.Enabled = true; }
    }
#endif
}
#if OUTLET_DIAGNOSTICS
internal static class DiagnosticDownload
{
    internal const string DirectoryPath = "/user/Data/ThirdParty/NeilColvin/KasaTapoCrestronDriver/outlet-diagnostics-2.1.014.0002";
    internal const long Limit = 2 * 1024 * 1024;
    public static int Run(IRemoteFiles files, string destination, string version)
    {
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        int count = 0;
        try {
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create)) {
                foreach (string name in new[] { "capture.jsonl", "previous.jsonl" }) {
                    string path = DirectoryPath + "/" + name;
                    if (!files.Exists(path)) continue;
                    if (files.Size(path) > Limit) throw new InvalidDataException("Diagnostic capture exceeds its size limit.");
                    using var source = files.OpenRead(path);
                    using var target = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                    byte[] buffer = new byte[8192]; long total = 0; int read;
                    while ((read = source.Read(buffer, 0, buffer.Length)) != 0) {
                        total += read; if (total > Limit) throw new InvalidDataException("Diagnostic capture grew beyond its size limit.");
                        target.Write(buffer, 0, read);
                    }
                    count++;
                }
                if (count == 0) throw new FileNotFoundException("No diagnostic captures found. Update the running driver to 2.1.014.0002 first, then wait at least two minutes.");
                using var metadata = new StreamWriter(zip.CreateEntry("collection.json").Open(), new UTF8Encoding(false));
                metadata.Write(new JavaScriptSerializer().Serialize(new { CollectedUtc = DateTimeOffset.UtcNow.ToString("O"), ExpectedBuild = version, CaptureFiles = count, ReadOnly = true }));
            }
            if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
            return count;
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
#endif

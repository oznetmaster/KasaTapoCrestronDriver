// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace TestDriverSideload;

internal static class Program
    {
    [STAThread]
    private static int Main (string[] args)
        {
        // Keep all dependencies in this executable; no extraction or installation is needed.
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
            {
            string name = new AssemblyName (e.Name).Name!;
            using var source = Assembly.GetExecutingAssembly ().GetManifestResourceStream ("Dependencies." + name + ".dll");
            if (source == null) return null;
            using var bytes = new MemoryStream ();
            source.CopyTo (bytes);
            return Assembly.Load (bytes.ToArray ());
            };
        try { return Run (args); }
        catch (Exception error)
            {
            if (args.Length == 2 && (args[0] == "--verify" || args[0] == "--self-test"))
                File.WriteAllText (args[1], error.ToString ());
            else if (args.Length != 0) Console.Error.WriteLine (error.Message);
            else MessageBox.Show (error.Message, "Test driver upload", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
            }
        }
    private static int Run (string[] args)
        {
        var payload = Payload.Load ();
        if (args.Length == 2 && args[0] == "--verify")
            {
            // Resolve the actual crypto/SFTP dependencies in an otherwise empty directory.
            using var probe = new Renci.SshNet.SftpClient ("127.0.0.1", "offline", "unused");
            _ = probe.ConnectionInfo.HostKeyAlgorithms.Count;
            File.WriteAllText (args[1], new JavaScriptSerializer ().Serialize (new { Valid = true, payload.Metadata, payload.Length,
                Dependencies = AppDomain.CurrentDomain.GetAssemblies ().Select (a => a.GetName ().Name).OrderBy (n => n).ToArray () }));
            return 0;
            }
        if (args.Length == 2 && args[0] == "--self-test")
            { File.WriteAllText (args[1], SelfTests.Run (payload)); return 0; }
        if (args.Length == 2 && args[0] == "--extract")
            {
            using var file = new FileStream (args[1], FileMode.CreateNew, FileAccess.Write);
            using var data = payload.Open ();
            data.CopyTo (file);
            return 0;
            }
        Application.EnableVisualStyles ();
        Application.SetCompatibleTextRenderingDefault (false);
        using var form = new UploadForm (payload);
        if (args.Length == 2 && args[0] == "--preview")
            {
            form.ShowInTaskbar = false;
            typeof (Control).GetMethod ("CreateControl", BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof (bool) }, null)!.Invoke (form, new object[] { true });
            form.PerformLayout ();
            using var bitmap = new Bitmap (form.Width, form.Height);
            form.DrawToBitmap (bitmap, new Rectangle (Point.Empty, form.Size));
            bitmap.Save (args[1], System.Drawing.Imaging.ImageFormat.Png);
            return 0;
            }
        if (args.Length != 0) throw new ArgumentException ("Unknown argument.");
        Application.Run (form);
        return 0;
        }
    }

internal sealed class UploadForm : Form
    {
    private readonly Payload _payload;
    private readonly TextBox _host = new TextBox (), _user = new TextBox { Text = "admin" }, _password = new TextBox { UseSystemPasswordChar = true };
    private readonly NumericUpDown _port = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 22 };
    private readonly Button _upload = new Button { Text = "Upload test driver", AutoSize = true };
    private readonly Button _download = new Button { Text = "Download diagnostic log", AutoSize = true };
    private readonly Button _licences = new Button { Text = "Licences", AutoSize = true };
    private readonly ProgressBar _progress = new ProgressBar { Dock = DockStyle.Fill, Maximum = 100 };
    private readonly TextBox _status = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, TabStop = false };
    private bool _busy, _submitted;
    public UploadForm (Payload payload)
        {
        _payload = payload;
        Text = "Kasa/Tapo diagnostic test utility";
        Font = new Font ("Segoe UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size (680, 620);
        MinimumSize = new Size (696, 659);
        StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding (24), ColumnCount = 2, RowCount = 11 };
        layout.ColumnStyles.Add (new ColumnStyle (SizeType.Absolute, 170));
        layout.ColumnStyles.Add (new ColumnStyle (SizeType.Percent, 100));
        void Wide (Control c, int row, int height)
            {
            layout.RowStyles.Add (new RowStyle (SizeType.Absolute, height));
            c.Dock = DockStyle.Fill;
            layout.Controls.Add (c, 0, row);
            layout.SetColumnSpan (c, 2);
            }
        Wide (new Label { Text = "Kasa/Tapo diagnostic build only", Font = new Font (Font, FontStyle.Bold) }, 0, 34);
        Wide (new Label { Text = payload.Metadata.Model + "  |  Test version " + payload.Metadata.Version }, 1, 32);
        Wide (new Label { Text = "Step 1: Upload the package here.\r\nStep 2: Update the existing driver in Home Setup or Configure Pro." }, 2, 62);
        void Field (string label, Control control, int row)
            {
            layout.RowStyles.Add (new RowStyle (SizeType.Absolute, 40));
            layout.Controls.Add (new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            control.Dock = DockStyle.Top;
            layout.Controls.Add (control, 1, row);
            }
        Field ("Processor address", _host, 3);
        Field ("Processor username", _user, 4);
        Field ("Processor password", _password, 5);
        Field ("SFTP port", _port, 6);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight };
        buttons.Controls.Add (_upload);
        buttons.Controls.Add (_download);
        buttons.Controls.Add (_licences);
        Wide (buttons, 7, 46);
        Wide (_progress, 8, 22);
        Wide (_status, 9, 110);
        _status.Text = "Use the processor's login, rather than your Kasa/Tapo account.\r\nAfter updating and reloading the driver, download its diagnostic log here.\r\nCredentials are used for the connection and are never saved.";
        Controls.Add (layout);
        AcceptButton = _upload;
        _upload.Click += async (_, _) => await StartUpload ();
        _download.Click += async (_, _) => await StartDownload ();
        _licences.Click += (_, _) =>
            {
            using var dialog = new Form { Text = "Licences and notices", Size = new Size (760, 560), StartPosition = FormStartPosition.CenterParent };
            using var reader = new StreamReader (Payload.Resource ("Notices.txt"));
            dialog.Controls.Add (new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, Text = reader.ReadToEnd () });
            dialog.ShowDialog (this);
            };
        FormClosing += (_, e) => { if (_busy) { e.Cancel = true; _status.Text = "Please wait for the current transfer to finish."; } };
        }
    private async Task StartUpload ()
        {
        string host = _host.Text.Trim (), user = _user.Text.Trim (), password = _password.Text;
        if (Uri.CheckHostName (host) == UriHostNameType.Unknown || string.IsNullOrWhiteSpace (user) || password.Length == 0)
            { MessageBox.Show (this, "Enter the processor IP address or hostname, username and password.", Text); return; }
        int port = (int)_port.Value;
        _busy = true;
        _upload.Enabled = _download.Enabled = _host.Enabled = _user.Enabled = _password.Enabled = _port.Enabled = false;
        _progress.Value = 0;
        try
            {
            _status.Text = "Reading processor identity...";
            string fingerprint = await Task.Run (() => SftpFiles.Probe (host, port));
            if (MessageBox.Show (this, "Upload " + _payload.Metadata.Model + " " + _payload.Metadata.Version + " to " + host + "?\r\n\r\nSSH identity (SHA256):\r\n" + fingerprint + "\r\n\r\nConfirm that this is your intended processor. The installed driver will be updated separately in Home Setup or Configure Pro.",
                "Confirm processor and upload", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                { _status.Text = "Upload cancelled. No package was sent."; return; }
            _status.Text = "Uploading and verifying package...";
            var progress = new Progress<ulong> (bytes => _progress.Value = (int)Math.Min (100, bytes * 100 / (ulong)_payload.Length));
            bool uploaded = await Task.Run (() =>
                {
                using var remote = new SftpFiles (host, port, user, password, fingerprint);
                return Upload.Run (_payload, remote, bytes => ((IProgress<ulong>)progress).Report (bytes));
                });
            _submitted = true;
            _progress.Value = 100;
            _status.Text = (uploaded ? "Package uploaded and checksum verified." : "The same verified package is already staged.")
                + "\r\nNow open Home Setup or Configure Pro and update the existing KasaTapoPlatform driver to " + _payload.Metadata.Version + ".\r\nUploading alone does not update the active driver.";
            _upload.Text = "Package uploaded";
            }
        catch (Exception error)
            {
            _status.Text = "Upload was not confirmed. " + error.Message + "\r\nIf the connection was interrupted, check Setup before retrying. Do not remove the existing driver.";
            MessageBox.Show (this, _status.Text, "Upload not confirmed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        finally
            {
            password = "";
            _password.Clear ();
            _busy = false;
            _upload.Enabled = !_submitted;
            _download.Enabled = _host.Enabled = _user.Enabled = _password.Enabled = _port.Enabled = true;
            }
        }
    private async Task StartDownload ()
        {
        string host = _host.Text.Trim (), user = _user.Text.Trim (), password = _password.Text;
        if (Uri.CheckHostName (host) == UriHostNameType.Unknown || string.IsNullOrWhiteSpace (user) || password.Length == 0)
            { MessageBox.Show (this, "Enter the processor address, username and password to download its diagnostic log.", Text); return; }
        using var save = new SaveFileDialog { Title = "Save Kasa/Tapo diagnostic log", Filter = "Text log (*.txt)|*.txt", FileName = "KasaTapo-diagnostics-" + DateTime.Now.ToString ("yyyyMMdd-HHmmss") + ".txt", AddExtension = true, DefaultExt = "txt", OverwritePrompt = true };
        if (save.ShowDialog (this) != DialogResult.OK) return;
        int port = (int)_port.Value;
        _busy = true;
        _upload.Enabled = _download.Enabled = _host.Enabled = _user.Enabled = _password.Enabled = _port.Enabled = false;
        try
            {
            _status.Text = "Reading processor identity...";
            string fingerprint = await Task.Run (() => SftpFiles.Probe (host, port));
            if (MessageBox.Show (this, "Download the Kasa/Tapo diagnostic log from " + host + "?\r\n\r\nSSH identity (SHA256):\r\n" + fingerprint + "\r\n\r\nConfirm this is your intended processor. This only reads the diagnostic files; it does not update or reload the driver.", "Confirm processor and download", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                { _status.Text = "Download cancelled."; return; }
            _status.Text = "Downloading diagnostic log...";
            await Task.Run (() =>
                {
                using var remote = new SftpFiles (host, port, user, password, fingerprint);
                Diagnostics.Download (remote, save.FileName, _payload.Metadata.Version);
                });
            _status.Text = "Diagnostic log saved to:\r\n" + save.FileName + "\r\nPlease email this file to Neil. The driver and its configuration were not changed.";
            }
        catch (Exception error)
            {
            _status.Text = "Diagnostic download failed. " + error.Message;
            MessageBox.Show (this, _status.Text, "Download failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        finally
            {
            password = "";
            _password.Clear ();
            _busy = false;
            _upload.Enabled = !_submitted;
            _download.Enabled = _host.Enabled = _user.Enabled = _password.Enabled = _port.Enabled = true;
            }
        }
    }

// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace BindingDiagnostics;

internal sealed class ReadOnlySftp : IReadOnlyFiles, IDisposable
{
    private readonly SftpClient _client;
    internal ReadOnlySftp(string host, int port, string user, string password, string fingerprint)
    {
        _client = new SftpClient(host, port, user, password);
        _client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(15);
        _client.OperationTimeout = TimeSpan.FromSeconds(30);
        _client.HostKeyReceived += (_, e) => e.CanTrust = e.FingerPrintSHA256 == fingerprint;
        try { _client.Connect(); } catch { _client.Dispose(); throw; }
    }
    internal static string Probe(string host, int port)
    {
        string? fingerprint = null;
        using var probe = new SftpClient(host, port, "host-key-probe", "");
        probe.ConnectionInfo.Timeout = TimeSpan.FromSeconds(15);
        probe.HostKeyReceived += (_, e) => { fingerprint = e.FingerPrintSHA256; e.CanTrust = false; };
        try { probe.Connect(); } catch (SshConnectionException) when (fingerprint != null) { }
        return fingerprint ?? throw new IOException("Could not read the processor SSH identity.");
    }
    public bool Exists(string path) => _client.Exists(path);
    public long Size(string path) => _client.GetAttributes(path).Size;
    public Stream OpenRead(string path) => _client.OpenRead(path);
    public void Dispose() => _client.Dispose();
}

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            string name = new AssemblyName(e.Name).Name!;
            using var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("Dependencies." + name + ".dll");
            if (input == null) return null;
            using var bytes = new MemoryStream(); input.CopyTo(bytes); return Assembly.Load(bytes.ToArray());
        };
        try { return Run(args); }
        catch (Exception e)
        {
            // Parser messages can include input fragments. Never show/log exception messages or source files.
            string message = "Collection did not complete (" + e.GetType().Name + "). Check the processor address, login and permissions. No processor settings were changed.";
            if (args.Length == 0) MessageBox.Show(message, "Kasa/Tapo binding diagnostics", MessageBoxButtons.OK, MessageBoxIcon.Error);
            else Console.Error.WriteLine(message);
            return 1;
        }
    }
    private static int Run(string[] args)
    {
        if (args.Length == 2 && args[0] == "--self-test")
        { File.WriteAllText(args[1], SelfTests.Run()); return 0; }
        if (args.Length == 2 && args[0] == "--verify")
        {
            using var probe = new SftpClient("127.0.0.1", "offline", "unused");
            _ = probe.ConnectionInfo.HostKeyAlgorithms.Count;
            File.WriteAllText(args[1], BindingReport.Serializer().Serialize(new { Version = BindingReport.ToolVersion,
                Dependencies = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name).OrderBy(n => n).ToArray() }));
            return 0;
        }
        if (args.Length == 2 && args[0] == "--collect")
        {
            string Env(string key) => Environment.GetEnvironmentVariable(key) ?? throw new InvalidOperationException("Missing connection setting.");
            using var files = new ReadOnlySftp(Env("CRESTRON_HOME_HOST"), 22, Env("CRESTRON_HOME_USER"),
                Env("CRESTRON_HOME_PASSWORD"), Env("CRESTRON_HOME_SSH_FINGERPRINT"));
            BindingReport.Collect(files, args[1]); return 0;
        }
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        using var form = new CollectionForm();
        if (args.Length == 2 && args[0] == "--preview")
        {
            form.ShowInTaskbar = false;
            typeof(Control).GetMethod("CreateControl", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(bool) }, null)!.Invoke(form, new object[] { true });
            form.PerformLayout(); using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png); return 0;
        }
        if (args.Length != 0) throw new ArgumentException("Unknown argument.");
        Application.Run(form); return 0;
    }
}

internal sealed class CollectionForm : Form
{
    private readonly TextBox _host = new TextBox(), _user = new TextBox { Text = "admin" }, _password = new TextBox { UseSystemPasswordChar = true };
    private readonly NumericUpDown _port = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 22 };
    private readonly Button _collect = new Button { Text = "Save binding report", AutoSize = true };
    private readonly TextBox _status = new TextBox { Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None };
    private bool _busy;
    internal CollectionForm()
    {
        Text = "Kasa/Tapo binding diagnostics " + BindingReport.ToolVersion;
        Font = new Font("Segoe UI", 10); AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(680, 480); MinimumSize = new Size(696, 519); StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 2, RowCount = 8 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Wide(Control control, int row, int height)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height)); control.Dock = DockStyle.Fill;
            layout.Controls.Add(control, 0, row); layout.SetColumnSpan(control, 2);
        }
        Wide(new Label { Text = "Collect saved Kasa/Tapo device bindings", Font = new Font(Font, FontStyle.Bold) }, 0, 34);
        Wide(new Label { Text = "Reads device identifiers and configuration status for support.\r\nThe driver and processor settings are not changed." }, 1, 60);
        void Field(string label, Control control, int row)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            control.Dock = DockStyle.Top; layout.Controls.Add(control, 1, row);
        }
        Field("Processor address", _host, 2); Field("Processor username", _user, 3); Field("Processor password", _password, 4); Field("SFTP port", _port, 5);
        var buttons = new FlowLayoutPanel(); buttons.Controls.Add(_collect);
        var licences = new Button { Text = "Licences", AutoSize = true }; buttons.Controls.Add(licences);
        licences.Click += (_, _) =>
        {
            using var dialog = new Form { Text = "Licences and notices", Size = new Size(760, 560), StartPosition = FormStartPosition.CenterParent };
            using var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("Notices.txt") ?? throw new InvalidOperationException("Missing notices.");
            using var reader = new StreamReader(input);
            dialog.Controls.Add(new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, Text = reader.ReadToEnd() });
            dialog.ShowDialog(this);
        };
        Wide(buttons, 6, 46); Wide(_status, 7, 120);
        _status.Text = "Use the processor login used with the earlier utility.\r\nOnly filtered Kasa/Tapo binding details are saved.\r\nPasswords and complete configuration files are not included.";
        Controls.Add(layout); AcceptButton = _collect;
        _collect.Click += async (_, _) => await Collect();
        FormClosing += (_, e) => { if (_busy) e.Cancel = true; };
    }
    private async Task Collect()
    {
        string host = _host.Text.Trim(), user = _user.Text.Trim(), password = _password.Text;
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown || user.Length == 0 || password.Length == 0)
        { MessageBox.Show(this, "Enter the processor address, username and password.", Text); return; }
        using var save = new SaveFileDialog { Title = "Save Kasa/Tapo binding report", Filter = "JSON report (*.json)|*.json",
            FileName = "KasaTapo-bindings-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json", AddExtension = true, DefaultExt = "json" };
        if (save.ShowDialog(this) != DialogResult.OK) return;
        int port = (int)_port.Value; _busy = true;
        _collect.Enabled = _host.Enabled = _user.Enabled = _password.Enabled = _port.Enabled = false;
        try
        {
            _status.Text = "Reading processor identity...";
            string fingerprint = await Task.Run(() => ReadOnlySftp.Probe(host, port));
            if (MessageBox.Show(this, "Collect the saved Kasa/Tapo bindings from " + host + "?\r\n\r\nSSH identity (SHA256):\r\n" + fingerprint
                + "\r\n\r\nConfirm this is your intended processor. The utility reads Home's device manifest and the driver cache into memory, and saves only selected Kasa/Tapo binding fields.",
                "Confirm processor and collection", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            { _status.Text = "Collection cancelled."; return; }
            _status.Text = "Reading and filtering saved binding records...";
            await Task.Run(() => { using var files = new ReadOnlySftp(host, port, user, password, fingerprint); BindingReport.Collect(files, save.FileName); });
            _status.Text = "Binding report saved to:\r\n" + save.FileName + "\r\nPlease email this JSON file to Neil. No settings were changed.";
        }
        catch (Exception e)
        {
            _status.Text = "Collection did not complete (" + e.GetType().Name + "). Check the address, login and permissions. If it persists, send Neil this message. No settings were changed.";
            MessageBox.Show(this, _status.Text, "Collection not completed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { password = ""; _password.Clear(); _busy = false; _collect.Enabled = _host.Enabled = _user.Enabled = _password.Enabled = _port.Enabled = true; }
    }
}

// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
#if DEBUG
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Crestron.DeviceDrivers.EntityModel;
using Crestron.DeviceDrivers.EntityModel.Logging;
using Microsoft.Extensions.Logging;

namespace KasaTapoCrestronDriver;

// Temporary diagnostic-build sink. Release builds contain neither this type nor its wiring.
internal sealed class DiagnosticFileLoggerProvider : ILoggerProvider
{
    internal const string DirectoryPath = "/user/Data/ThirdParty/NeilColvin/KasaTapoCrestronDriver/diagnostics";
    internal const int DefaultMaximumBytes = 2 * 1024 * 1024;
    private static readonly object FileGate = new ();
    private static readonly HashSet<string> Secrets = new (StringComparer.Ordinal);
    private static readonly Regex SensitiveField = new (
        @"(?i)([""']?(?:password|passwd|pwd|token|access_token|refresh_token|session_token|stok|cookie|authorization)[""']?\s*[:=]\s*)(?:""[^""\r\n]*""|'[^'\r\n]*'|[^\s,;}\r\n]+)");
    private readonly string _path;
    private readonly int _maximumBytes;
    internal DiagnosticFileLoggerProvider (string directory, int maximumBytes = DefaultMaximumBytes)
    {
        _path = Path.Combine (directory, "diagnostic.log");
        _maximumBytes = maximumBytes;
    }
    internal static void RegisterSecrets (params string[] values)
    {
        lock (FileGate)
            foreach (string value in values)
                if (!string.IsNullOrEmpty (value))
                {
                    Secrets.Add (value);
                    Secrets.Add (Uri.EscapeDataString (value));
                }
    }
    internal static string Redact (string message)
    {
        // Do not retain raw HTTP headers or authentication bodies from external traces.
        if (Regex.IsMatch (message, @"(?i)\b(authorization|cookie)\s*[:=]"))
            return "[authentication header omitted]";
        string safe = SensitiveField.Replace (message, "$1[redacted]");
        lock (FileGate)
            foreach (string secret in Secrets.OrderByDescending (value => value.Length))
                safe = safe.Replace (secret, "[redacted]");
        return safe;
    }
    public ILogger CreateLogger (string categoryName) => new FileLogger (this, categoryName);
    public void Dispose () { } // Each append closes its handle; reloads retain the bounded history.
    private void Write (string category, LogLevel level, string message)
    {
        try
        {
            string safe = Redact (message);
            if (safe.Length > 12000) safe = safe.Substring (0, 12000) + " [truncated]";
            string line = DateTime.UtcNow.ToString ("O", CultureInfo.InvariantCulture) + " [" + level + "] " + category + ": " + safe.Replace ("\r", "\\r").Replace ("\n", "\\n") + Environment.NewLine;
            byte[] bytes = Encoding.UTF8.GetBytes (line);
            if (bytes.Length > _maximumBytes) return;
            lock (FileGate)
            {
                Directory.CreateDirectory (Path.GetDirectoryName (_path)!);
                if (File.Exists (_path) && new FileInfo (_path).Length + bytes.Length > _maximumBytes)
                {
                    string previous = _path + ".1";
                    if (File.Exists (previous)) File.Delete (previous);
                    File.Move (_path, previous);
                }
                using var stream = new FileStream (_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                stream.Write (bytes, 0, bytes.Length);
            }
        }
        catch { } // Diagnostics must never prevent device startup or control.
    }
    private sealed class FileLogger : ILogger
    {
        private readonly DiagnosticFileLoggerProvider _provider;
        private readonly string _category;
        internal FileLogger (DiagnosticFileLoggerProvider provider, string category) { _provider = provider; _category = category; }
        public bool IsEnabled (LogLevel level) => level != LogLevel.None;
        public IDisposable? BeginScope<TState> (TState state) where TState : notnull => null;
        public void Log<TState> (LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled (level)) return;
            try { _provider.Write (_category, level, formatter (state, exception) + (exception == null ? "" : " " + exception)); }
            catch { }
        }
    }
}

// Bridge the existing SDK logger to the Microsoft ILogger file provider without converting the driver.
internal sealed class DiagnosticControllerLogger : DriverControllerLogger
{
    private readonly DriverControllerLogger _home;
    private readonly ILogger _file;
    internal DiagnosticControllerLogger (DriverControllerLogger home, string driverId, string? directory = null)
    {
        _home = home;
        _file = new DiagnosticFileLoggerProvider (directory ?? DiagnosticFileLoggerProvider.DirectoryPath).CreateLogger ("KasaTapo[" + driverId + "]");
        _file.LogInformation ("DIAGNOSTIC BUILD ONLY: bounded file logging active; awaiting customer results before a Release test build.");
    }
    internal static DriverControllerCreationArgs Wrap (DriverControllerCreationArgs args) => new (
        args.DriverId, args.DriverDataDirectoryPath, new DiagnosticControllerLogger (args.Logger, args.DriverId), args.TransportTx, args.ObjectLookup)
        { DeviceModel = args.DeviceModel };
    public override bool IsEnabled (string controllerId, LogEntryLevel level) => _home.IsEnabled (controllerId, level);
    public override LogEntryLevel GetCurrentLevel (string controllerId) => _home.GetCurrentLevel (controllerId);
    public override void Exception (string controllerId, Exception exception, string format, params object[] args) =>
        Log (controllerId, LogEntryLevel.Error, Format (format, args) + " " + exception);
    public override void Log (string controllerId, LogEntryLevel level, string message)
    {
        try
        {
            string safe = DiagnosticFileLoggerProvider.Redact (message);
            var mapped = level switch { LogEntryLevel.Error => LogLevel.Error, LogEntryLevel.Warning => LogLevel.Warning, LogEntryLevel.Debug => LogLevel.Debug, LogEntryLevel.Trace => LogLevel.Trace, LogEntryLevel.Off => LogLevel.None, _ => LogLevel.Information };
            _file.Log (mapped, new EventId (0), safe, null, (value, _) => value);
            _home.Log (controllerId, level, safe);
        }
        catch { }
    }
    private static string Format (string format, object[] args)
    {
        try { return string.Format (CultureInfo.InvariantCulture, format, args); }
        catch { return "[invalid diagnostic message format]"; }
    }
    public override void Log (string id, LogEntryLevel level, string format, params object[] args) => Log (id, level, Format (format, args));
    public override void Log<T1> (string id, LogEntryLevel level, string format, T1 arg1) => Log (id, level, Format (format, new object[] { arg1! }));
    public override void Log<T1, T2> (string id, LogEntryLevel level, string format, T1 arg1, T2 arg2) => Log (id, level, Format (format, new object[] { arg1!, arg2! }));
    public override void Log<T1, T2, T3> (string id, LogEntryLevel level, string format, T1 arg1, T2 arg2, T3 arg3) => Log (id, level, Format (format, new object[] { arg1!, arg2!, arg3! }));
}
#endif

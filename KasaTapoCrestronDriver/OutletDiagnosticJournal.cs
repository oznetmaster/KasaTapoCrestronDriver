// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
#if OUTLET_DIAGNOSTICS
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace KasaTapoCrestronDriver;

// Customer-only capture. No host logger, credentials, raw protocol payloads or extra network requests.
internal sealed class OutletDiagnosticRecord
{
    public string Utc { get; set; } = DateTimeOffset.UtcNow.ToString("O");
    public long MonotonicTicks { get; set; } = Stopwatch.GetTimestamp();
    public string Session { get; set; } = OutletDiagnosticJournal.Session;
    public int DroppedRecords { get; set; }
    public string Build { get; set; } = "2.1.014.0002";
    public string Event { get; set; } = "";
    public string Instance { get; set; } = "";
    public string Controller { get; set; } = "";
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    public string Host { get; set; } = "";
    public int? Port { get; set; }
    public string? Transport { get; set; }
    public string? Family { get; set; }
    public string? Encryption { get; set; }
    public int? LoginVersion { get; set; }
    public bool Disposed { get; set; }
    public bool Stopped { get; set; }
    public bool Configured { get; set; }
    public int Generation { get; set; }
    public string? ErrorType { get; set; }
    public string? ErrorReason { get; set; }
    public string? ErrorStack { get; set; }
    public string? Property { get; set; }
    public double? Previous { get; set; }
    public double? Value { get; set; }
    public bool? Changed { get; set; }
    public bool? IsOn { get; set; }
    public double? DurationMs { get; set; }
    public OutletDiagnosticEnergy? Raw { get; set; }
    public OutletDiagnosticEnergy? Published { get; set; }
}
internal sealed class OutletDiagnosticEnergy
{
    public double? Voltage { get; set; }
    public double? Current { get; set; }
    public double? Power { get; set; }
    public double? Today { get; set; }
    public double? Month { get; set; }
    public double? Total { get; set; }
}
internal sealed class OutletDiagnosticJournal
{
    internal const string DirectoryPath = "/user/Data/ThirdParty/NeilColvin/KasaTapoCrestronDriver/outlet-diagnostics-2.1.014.0002";
    internal const long MaxBytes = 2 * 1024 * 1024;
    internal static readonly string Session = Guid.NewGuid().ToString("N");
    private static readonly Lazy<OutletDiagnosticJournal> Default = new(() => new OutletDiagnosticJournal(DirectoryPath));
    private readonly ConcurrentQueue<OutletDiagnosticRecord> _queue = new();
    private readonly string _directory;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TimeSpan _duration;
    private readonly long _limit;
    private int _queued, _worker, _disabled, _dropped;
    private bool _initialized;
    internal OutletDiagnosticJournal(string directory, long limit = MaxBytes, TimeSpan? duration = null)
    { _directory = directory; _limit = limit; _duration = duration ?? TimeSpan.FromMinutes(30); }
    internal static void Capture(OutletDiagnosticRecord record) => Default.Value.Enqueue(record);
    internal void Enqueue(OutletDiagnosticRecord record)
    {
        if (Volatile.Read(ref _disabled) != 0 || _clock.Elapsed >= _duration) return;
        if (Interlocked.Increment(ref _queued) > 512) { Interlocked.Decrement(ref _queued); Interlocked.Increment(ref _dropped); return; }
        _queue.Enqueue(record);
        StartWorker();
    }
    private void StartWorker()
    {
        if (Interlocked.CompareExchange(ref _worker, 1, 0) == 0)
            _ = Task.Run(Drain);
    }
    private void Drain()
    {
        try
        {
            if (!_initialized)
            {
                Directory.CreateDirectory(_directory);
                string active = Path.Combine(_directory, "capture.jsonl");
                string previous = Path.Combine(_directory, "previous.jsonl");
                if (File.Exists(active)) { if (File.Exists(previous)) File.Delete(previous); File.Move(active, previous); }
                _initialized = true;
            }
            string path = Path.Combine(_directory, "capture.jsonl");
            using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
            stream.Seek(0, SeekOrigin.End);
            while (_queue.TryDequeue(out var record))
            {
                Interlocked.Decrement(ref _queued);
                if (Volatile.Read(ref _disabled) != 0) continue;
                record.DroppedRecords = Volatile.Read(ref _dropped);
                byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record) + "\n");
                if (stream.Length + bytes.Length > _limit) { Interlocked.Exchange(ref _disabled, 1); continue; }
                stream.Write(bytes, 0, bytes.Length);
            }
        }
        catch
        {
            Interlocked.Exchange(ref _disabled, 1);
            while (_queue.TryDequeue(out _)) Interlocked.Decrement(ref _queued);
        } // Capture must never affect device control.
        finally
        {
            Interlocked.Exchange(ref _worker, 0);
            if (!_queue.IsEmpty) StartWorker();
        }
    }
    internal async Task FlushForTestAsync()
    {
        var timeout = Stopwatch.StartNew();
        while (Volatile.Read(ref _worker) != 0 || Volatile.Read(ref _queued) != 0)
        { if (timeout.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException(); await Task.Delay(10); }
    }
}
#endif
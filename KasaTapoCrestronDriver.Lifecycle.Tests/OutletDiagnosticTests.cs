// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
#if OUTLET_DIAGNOSTICS
using System.Text.Json;
using KasaTapoCrestronDriver;
namespace KasaTapoCrestronDriver.Tests;
[TestFixture]
public sealed class OutletDiagnosticTests
{
    [Test]
    public async Task CaptureIsBoundedAndRetainsCompleteJsonLines()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kasa-diagnostic-test-" + Guid.NewGuid().ToString("N"));
        try {
            var journal = new OutletDiagnosticJournal(dir, 3000);
            for (int i = 0; i < 100; i++) journal.Enqueue(new OutletDiagnosticRecord { Event = "test", Value = i });
            await journal.FlushForTestAsync();
            var info = new FileInfo(Path.Combine(dir, "capture.jsonl"));
            Assert.That(info.Length, Is.InRange(1, 3000));
            foreach (string line in File.ReadAllLines(info.FullName)) using (var doc = JsonDocument.Parse(line)) Assert.That(doc.RootElement.GetProperty("Event").GetString(), Is.EqualTo("test"));
        } finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
    [Test]
    public async Task ExpiredCaptureCreatesNoFiles()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kasa-diagnostic-expired-" + Guid.NewGuid().ToString("N"));
        var journal = new OutletDiagnosticJournal(dir, duration: TimeSpan.Zero);
        journal.Enqueue(new OutletDiagnosticRecord()); await journal.FlushForTestAsync();
        Assert.That(Directory.Exists(dir), Is.False);
    }
    [Test]
    public async Task UnwritableCaptureDoesNotThrowOrBlock()
    {
        string file = Path.GetTempFileName();
        try { var journal = new OutletDiagnosticJournal(file); journal.Enqueue(new OutletDiagnosticRecord()); await journal.FlushForTestAsync(); }
        finally { File.Delete(file); }
    }
}
#endif

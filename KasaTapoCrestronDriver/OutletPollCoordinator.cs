// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
using System.Diagnostics;

namespace KasaTapoCrestronDriver;

// One scheduler per platform instance. Direct commands never take this permit.
internal sealed class OutletPollCoordinator
{
    internal const int ConcurrencyLimit = 3;
    private const int PhaseSlots = 20;
    private readonly SemaphoreSlim _permits = new(ConcurrencyLimit, ConcurrencyLimit);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _slots, _active, _peak;
    private readonly Action<int, string, long>? _observe;
    internal OutletPollCoordinator(Action<int, string, long>? observe = null) => _observe = observe;
    internal void Observe(int slot, string kind) => _observe?.Invoke(slot, kind, ElapsedMilliseconds);
    internal int PeakConcurrency => Volatile.Read(ref _peak);
    internal int Register() => Interlocked.Increment(ref _slots) - 1;

    internal long ElapsedMilliseconds => _clock.ElapsedMilliseconds;

    internal async Task<long> WaitForSlotAsync(int slot, TimeSpan interval, CancellationToken token)
    {
        double period = Math.Max(1000, interval.TotalMilliseconds);
        double phase = period * (slot % PhaseSlots) / PhaseSlots;
        double now = _clock.Elapsed.TotalMilliseconds;
        double due = (Math.Floor((now - phase) / period) + 1) * period + phase;
        await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling(due - now))), token).ConfigureAwait(false);
        return (long)Math.Ceiling(due);
    }

    internal async Task RunAsync(Func<Task> poll, CancellationToken token)
    {
        await _permits.WaitAsync(token).ConfigureAwait(false);
        int active = Interlocked.Increment(ref _active);
        try
        {
            int peak;
            while (active > (peak = Volatile.Read(ref _peak))
                && Interlocked.CompareExchange(ref _peak, active, peak) != peak) { }
            token.ThrowIfCancellationRequested();
            await poll().ConfigureAwait(false);
        }
        finally { Interlocked.Decrement(ref _active); _permits.Release(); }
    }
}

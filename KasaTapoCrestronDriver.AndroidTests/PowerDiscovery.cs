// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
namespace KasaTapoCrestronDriver.AndroidTests;

// Keep generic asynchronous helpers outside NUnit fixtures so generated state-machine
// types do not become nested fixture candidates during NUnit 5 discovery.
internal static class PowerDiscovery
{
    internal static async Task<T> DiscoverUnique<T>(Func<CancellationToken, Task<IReadOnlyList<T>>> discover,
        Func<T, string> host, Func<int, int, int, Task> observe, CancellationToken token)
    {
        // UDP discovery can miss one response. Retry only absence, before any connection or write.
        // Conflicting identities/hosts must never be made acceptable by trying again.
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var matches = await discover(token);
            int hosts = matches.Select(host).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            await observe(attempt, matches.Count, hosts);
            if (hosts > 1) throw new InvalidDataException("Physical identity has multiple discovered hosts.");
            if (hosts == 1) return matches[0];
        }
        throw new InvalidDataException("Physical identity was absent from three discovery attempts.");
    }

}

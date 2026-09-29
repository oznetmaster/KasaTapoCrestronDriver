// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
using CrestronHomeDevTools;

namespace KasaTapoCrestronDriver.AndroidTests;

internal static class PreparedProcessorSession
{
    // A readiness wait may outlive the processor's authentication session.
    // Authenticate afresh before taking the event baseline. Never replay a command.
    internal static async Task<ConfigurationClient> RenewAsync(ConfigurationClient? previous,
        Func<CancellationToken, Task<ConfigurationClient>> connect, CancellationToken token)
    {
        if (previous != null) await previous.DisposeAsync();
        token.ThrowIfCancellationRequested();
        return await connect(token);
    }
}

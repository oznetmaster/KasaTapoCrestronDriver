// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
namespace KasaTapoCrestronDriver;

public sealed partial class PlatformDriver
{
    internal void NotifyChildHostStateRead(string controllerId)
    {
        if (_lightEntities.TryGetValue(controllerId, out var entity) && entity is IConfirmedStartupEventEntity startup)
            startup.NotifyHostStateRead();
    }
}

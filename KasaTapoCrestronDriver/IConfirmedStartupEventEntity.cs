// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
namespace KasaTapoCrestronDriver;

// The host must have started reading the running entity's definition/state before
// its pending extension startup events are released to the host event queue.
internal interface IConfirmedStartupEventEntity
{
    void NotifyHostStateRead();
}

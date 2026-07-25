# Proposal (revised): Persistent shared `KasaDevice` cache per `Host:Port` in KasaTapoClient

## Status

This supersedes the earlier `KasaClient-ConcurrentConnectDedup-Proposal.md`. That proposal only
de-duplicated *concurrent, in-flight* `Discover.ConnectAsync` calls and discarded its
bookkeeping as soon as each connect finished (`_pendingConnects.TryRemove` in the `finally`).
That is not enough: it does not give callers a **persistent, reusable instance** for a given
device identity across calls that happen at *different times*, only ones that happen to race.

## Corrected requirement

There should only ever be one live `KasaDevice` per `Host:Port` for the lifetime of that
connection, shared by every caller/module using the library - not just callers that happen to
call `ConnectAsync` concurrently. Concretely:

- `ConnectAsync` should check a persistent cache keyed by `Host:Port` first. If a live
  (non-disposed) `KasaDevice` already exists for that key, return it directly - no new TCP
  connection, no new instance.
- If a connect is already in-flight for that key (the concurrent case handled by the prior
  proposal), join it and share the result, as already implemented.
- If neither applies, connect, populate the cache with the new instance, and return it.
- No reference counting is needed. If any caller disposes the shared instance, that is fine -
  the *next* access simply gets a disposed instance and fails with `ObjectDisposedException`
  (or the transport's own "stale connection" detection fires first, per
  `LegacyTransport.EnsureConnectedAsync`'s existing idle-timeout reconnect logic). The caller's
  existing retry/reconnect handling (already present for stale/idle legacy connections) is
  sufficient recovery - the *next* `ConnectAsync` call for that key simply detects the cached
  entry is no longer usable and transparently creates and caches a fresh replacement.

This mirrors the exact pattern already used internally by `LegacyTransport`: a connection can go
stale/be closed at any time, and the recovery is "the next operation notices and reconnects,"
not "prevent it from ever happening via refcounting."

## Proposed implementation sketch

```csharp
public static class Discover
	{
	private sealed class CachedDeviceEntry
		{
		public required KasaDevice Device { get; init; }
		}

	// Persistent cache: one live KasaDevice per Host:Port, shared by all callers/modules for the
	// life of that connection. Entries are only replaced (not removed) when the cached device is
	// found to be disposed/unusable on next access - there is no explicit invalidation API and no
	// reference counting; any caller may Dispose() the shared instance and the next caller to ask
	// for that key simply gets (and caches) a fresh replacement.
	private static readonly ConcurrentDictionary<string, CachedDeviceEntry> _connectedDevices = new (StringComparer.OrdinalIgnoreCase);

	// In-flight connect de-duplication (unchanged from the prior proposal) - still needed so that
	// two callers racing to populate _connectedDevices for the same key don't each open an
	// independent TCP connection while neither has completed yet.
	private static readonly ConcurrentDictionary<string, Task<KasaDevice>> _pendingConnects = new (StringComparer.OrdinalIgnoreCase);

	private static string CreateConnectKey (DeviceConfiguration configuration) =>
		$"{configuration.Host}:{configuration.Port}";

	public static Task<KasaDevice> ConnectAsync (DeviceConfiguration configuration, bool updateState, CancellationToken cancellationToken = default)
		{
		string key = CreateConnectKey (configuration);

		// Fast path: reuse an existing live shared device for this identity.
		if (_connectedDevices.TryGetValue (key, out CachedDeviceEntry? cachedEntry) && !cachedEntry.Device.IsDisposed)
			{
			return Task.FromResult (cachedEntry.Device);
			}

		// Join an in-flight connect for the same identity instead of starting a second one.
		if (_pendingConnects.TryGetValue (key, out Task<KasaDevice>? existingConnect))
			{
			return AwaitSharedConnectAsync (existingConnect, cancellationToken);
			}

		var connectCompletionSource = new TaskCompletionSource<KasaDevice> (TaskCreationOptions.RunContinuationsAsynchronously);
		Task<KasaDevice> registeredConnect = _pendingConnects.GetOrAdd (key, connectCompletionSource.Task);

		if (!ReferenceEquals (registeredConnect, connectCompletionSource.Task))
			{
			return AwaitSharedConnectAsync (registeredConnect, cancellationToken);
			}

		return ConnectAndPublishAsync (configuration, updateState, key, connectCompletionSource, cancellationToken);
		}

	private static async Task<KasaDevice> ConnectAndPublishAsync (
		DeviceConfiguration configuration,
		bool updateState,
		string key,
		TaskCompletionSource<KasaDevice> connectCompletionSource,
		CancellationToken cancellationToken)
		{
		try
			{
			KasaDevice device = await ConnectCoreAsync (configuration, updateState, cancellationToken).ConfigureAwait (false);

			// Populate the persistent cache so future (non-concurrent) callers for this identity
			// reuse this instance instead of connecting again.
			_connectedDevices[key] = new CachedDeviceEntry { Device = device };

			connectCompletionSource.TrySetResult (device);
			return device;
			}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
			connectCompletionSource.TrySetCanceled (cancellationToken);
			throw;
			}
		catch (Exception ex)
			{
			connectCompletionSource.TrySetException (ex);
			throw;
			}
		finally
			{
			((ICollection<KeyValuePair<string, Task<KasaDevice>>>) _pendingConnects).Remove (
				new KeyValuePair<string, Task<KasaDevice>> (key, connectCompletionSource.Task));
			}
		}

	// ... AwaitSharedConnectAsync and ConnectCoreAsync unchanged from the prior proposal ...
	}
```

Requirements this places on `KasaDevice`:

- Needs a public `IsDisposed` (or similarly named) property so `Discover.ConnectAsync` can
  detect a disposed cached entry and fall through to reconnect, rather than handing back a dead
  instance. This is a small, additive, non-breaking API surface change.
- No other changes to `KasaDevice`'s disposal semantics are required - `Dispose()` continues to
  work exactly as it does today; the cache simply stops trusting an entry once its `Device` is
  disposed.

## Why no reference counting

Reference counting would require every caller across every module to reliably pair each
"acquire" with a matching "release," including on every exception path, cancellation, and
process-level shutdown ordering issue - and a single leak (missed release) permanently pins the
shared instance alive, or a single over-release disposes it while others still depend on it.
That is a much larger surface for bugs than simply allowing disposal to happen and having the
next accessor transparently reconnect, which is already the exact recovery model
`LegacyTransport` uses today for idle/stale connections. No new failure mode is introduced - a
disposed shared device just behaves like any other unusable connection already does.

## Impact on `KasaTapoCrestronDriver`

With a persistent shared-per-identity cache in the client:

- `KasaLightEntity.EnsureConnectedAsync` and `PlatformDriver.EnrichDiscoveryAliasCacheAsync` -
  the driver's two independent call sites that each connect and assign a device for the same
  `controllerId`/host - would now naturally receive **the same shared `KasaDevice` instance**
  from the client on every call (not just concurrent ones), eliminating the need for the
  driver's own `_connectionGate` to coordinate "who connects and assigns first."
- The driver's `TryAttachConnectedDevice`/`_connectedDevice` plumbing could be simplified to just
  always call `EnsureConnectedAsync` (which itself calls `Discover.ConnectAsync`), since the
  client now guarantees it will get the one shared instance for that device identity rather than
  racing to create a second one.
- The driver's existing `ResetConnectionState`/`_activeDeviceOperationCount`/
  `_deviceAwaitingDisposal` deferred-disposal logic remains relevant *within a single entity's
  own retry logic* (it decides when it personally is done with a device and safe to dispose it),
  but the cross-module "only one connect should win" problem moves fully into the client, as
  originally intended.

This is a genuine simplification opportunity for the driver once implemented - but the driver
changes should be made only after the client's persistent shared-device cache is confirmed
merged and available, so `EnsureConnectedAsync`/`TryAttachConnectedDevice` are not de-synced from
the client's actual guarantees.

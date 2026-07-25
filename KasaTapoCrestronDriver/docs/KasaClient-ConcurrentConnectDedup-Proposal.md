# Proposal: De-duplicate concurrent `Discover.ConnectAsync` calls per device in KasaTapoClient

## Problem

`Discover.ConnectAsync(DeviceConfiguration, bool, CancellationToken)` is a stateless static
factory:

```csharp
public static async Task<KasaDevice> ConnectAsync (DeviceConfiguration configuration, bool updateState, CancellationToken cancellationToken = default)
	{
	if (configuration.ConnectionOptions.TransportKind == DeviceTransportKind.Auto)
		{
		DeviceConfiguration resolvedConfiguration = await ResolveAutoConfigurationAsync (configuration, cancellationToken).ConfigureAwait (false);
		var resolvedDevice = new KasaDevice (resolvedConfiguration);
		if (updateState)
			{
			await resolvedDevice.UpdateAsync (cancellationToken).ConfigureAwait (false);
			}
		return resolvedDevice;
		}

	var device = new KasaDevice (configuration);
	if (updateState)
		{
		await device.UpdateAsync (cancellationToken).ConfigureAwait (false);
		}
	return device;
	}
```

Every call unconditionally does `new KasaDevice(configuration)` - there is no registry, cache,
or lock keyed by host/port. If two callers call `ConnectAsync` concurrently for the *same*
device (same `Host`/`Port`), each gets its own independent `KasaDevice`, each with its own
independent transport (`LegacyTransport`, `KlapTransport`, etc.) and its own independent TCP
socket.

`KasaDevice` itself only serializes commands *after* it exists, via its private
`_operationLock` (`SemaphoreSlim(1,1)` used by `RunDeviceOperationAsync`). That lock cannot help
here because the two concurrent callers are constructing two *separate* `KasaDevice` instances -
there is nothing yet to serialize against.

### Observed impact

In `KasaTapoCrestronDriver`, this forced the driver to add its own `SemaphoreSlim` gate
(`_connectionGate` in `KasaLightEntity.EnsureConnectedAsync`) around the "is there already a
connected device, and if not, connect and publish it" critical section, purely to avoid:

- Multiple concurrent TCP connections opened to the same physical device (some devices, e.g.
  KL130 bulbs, may only tolerate one or a small number of concurrent connections).
- A race on "last write wins" for whichever caller's `KasaDevice` gets adopted as the
  application's canonical instance for that device, silently orphaning the other instance's
  socket (leaked connection, never disposed).

This is a reasonable thing for the *client library* to guarantee instead of pushing it onto
every caller.

## Desired behavior

When two or more calls to connect to the *same logical device* (same `Host` + `Port`, and
arguably same `TransportKind`/`ConnectionOptions` identity) race:

1. The first caller begins connecting (TCP dial + handshake + optional `UpdateAsync`).
2. Any other concurrent caller for the same device identity should **not** start a second,
	independent connection. It should instead await the *first* caller's in-flight connect and
	receive **the same `KasaDevice` instance** once it completes.
3. Once no connect is in flight and no cached instance exists, a new connect proceeds normally.

This mirrors the common "get-or-add with in-flight de-duplication" pattern (e.g.
`ConcurrentDictionary<TKey, Lazy<Task<TValue>>>` or `AsyncLazy<T>`).

## Proposed implementation sketch

Add an internal connect-coordination cache to `Discover`, keyed by a normalized device identity
derived from `DeviceConfiguration` (`Host` + `Port` is sufficient, since those uniquely address
the physical device the socket connects to):

```csharp
public static class Discover
	{
	// Coordinates concurrent ConnectAsync calls for the same device identity so that only one
	// physical connection attempt is in flight at a time; concurrent callers await and share the
	// same resulting KasaDevice instance instead of each opening an independent TCP connection.
	private static readonly ConcurrentDictionary<string, Task<KasaDevice>> _pendingConnects = new (StringComparer.OrdinalIgnoreCase);

	private static string CreateConnectKey (DeviceConfiguration configuration) =>
		$"{configuration.Host}:{configuration.Port}";

	public static Task<KasaDevice> ConnectAsync (DeviceConfiguration configuration, bool updateState, CancellationToken cancellationToken = default)
		{
		string key = CreateConnectKey (configuration);

		// Fast path: join an in-flight connect for the same device identity instead of starting
		// a second, independent one.
		if (_pendingConnects.TryGetValue (key, out Task<KasaDevice>? existingConnect))
			{
			return AwaitSharedConnectAsync (existingConnect, cancellationToken);
			}

		var connectCompletionSource = new TaskCompletionSource<KasaDevice> (TaskCreationOptions.RunContinuationsAsynchronously);
		Task<KasaDevice> registeredConnect = _pendingConnects.GetOrAdd (key, connectCompletionSource.Task);

		if (!ReferenceEquals (registeredConnect, connectCompletionSource.Task))
			{
			// Another thread registered first between our TryGetValue and GetOrAdd; join theirs.
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
			connectCompletionSource.SetResult (device);
			return device;
			}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
			// A caller-initiated cancellation should not fault the shared task for OTHER
			// concurrent callers who did not cancel; only propagate to this caller.
			connectCompletionSource.TrySetCanceled ();
			throw;
			}
		catch (Exception ex)
			{
			connectCompletionSource.SetException (ex);
			throw;
			}
		finally
			{
			// Only the connect's own entry is removed, and only once, so a fresh reconnect is
			// attempted on the next call after this one completes (success or failure) - the
			// cache exists purely to de-duplicate concurrent in-flight connects, not to cache
			// devices indefinitely (that remains the caller's responsibility, as today).
			_pendingConnects.TryRemove (KeyValuePair.Create (key, (Task<KasaDevice>) connectCompletionSource.Task));
			}
		}

	private static async Task<KasaDevice> AwaitSharedConnectAsync (Task<KasaDevice> sharedConnect, CancellationToken cancellationToken)
		{
		// Let an unrelated caller's cancellation stop THIS caller from waiting without canceling
		// the shared in-flight connect itself (other callers may still need it).
		using CancellationTokenRegistration registration = cancellationToken.Register (static state => ((TaskCompletionSource<object?>) state!).TrySetCanceled (), null!);
		return await sharedConnect.WaitAsync (cancellationToken).ConfigureAwait (false);
		}

	// Renamed from the current inline body of ConnectAsync(configuration, updateState, cancellationToken).
	private static async Task<KasaDevice> ConnectCoreAsync (DeviceConfiguration configuration, bool updateState, CancellationToken cancellationToken)
		{
		if (configuration.ConnectionOptions.TransportKind == DeviceTransportKind.Auto)
			{
			DeviceConfiguration resolvedConfiguration = await ResolveAutoConfigurationAsync (configuration, cancellationToken).ConfigureAwait (false);
			var resolvedDevice = new KasaDevice (resolvedConfiguration);
			if (updateState)
				{
				await resolvedDevice.UpdateAsync (cancellationToken).ConfigureAwait (false);
				}
			return resolvedDevice;
			}

		var device = new KasaDevice (configuration);
		if (updateState)
			{
			await device.UpdateAsync (cancellationToken).ConfigureAwait (false);
			}
		return device;
		}
	}
```

Notes/caveats for whoever implements this in `KasaClient`:

- `Task<T>` does not natively support per-awaiter cancellation without extra plumbing; the
  sketch above (`AwaitSharedConnectAsync`) is illustrative - polyfill or use a small
  `WaitAsync(CancellationToken)` helper (e.g. via `Task.WhenAny` with a cancellation task) if
  targeting .NET Framework 4.7.2 where `Task<T>.WaitAsync(CancellationToken)` is unavailable
  (that extension only exists on newer TFMs). Since this repo multi-targets `.NET Framework
  4.7.2` and `.NET 10`, prefer a manual `TaskCompletionSource`-based wait-with-cancellation
  helper shared across both TFMs rather than the BCL `WaitAsync` extension.
- Only the *connect* is de-duplicated; this intentionally does **not** introduce any
  long-lived device cache/registry into the client - the returned `KasaDevice` is still owned
  and disposed entirely by the caller, exactly as today. Once `ConnectAsync` returns (or
  throws), the dictionary entry for that key is removed, so unrelated later connects (e.g.
  after a driver-level disconnect/reconnect) behave exactly as they do today - only genuinely
  *concurrent* connects to the same identity are coalesced.
- Cancellation semantics need care: if caller A cancels while callers B and C are also awaiting
  the same shared connect, only A's await should observe cancellation - the underlying connect
  (and B/C's results) must not be aborted just because A canceled. The sketch above accounts for
  this by only registering per-awaiter cancellation on the *join* path, not on the owning
  connect's `CancellationToken`.
- `DiscoverSingleAsync`, and the other public `ConnectAsync` overloads that resolve a
  `DeviceConfiguration` and call the `(DeviceConfiguration, bool, CancellationToken)` overload,
  automatically inherit the de-duplication for free since they funnel through it.

## Why this belongs in the client, not the driver

The driver (`KasaTapoCrestronDriver.KasaLightEntity`) cannot implement this itself in a way that
also protects other callers of the client (or a future second consumer of `KasaTapoClient`)
against the same race - only the client can guarantee "one physical connection in flight per
device identity" for *all* its callers. The driver's own `_connectionGate` will still be needed
independently for the driver's own state machine (its `_connectedDevice` field, retry/backoff,
offline detection, etc.), but with client-side de-duplication in place, a scenario like a burst
of paired UI commands racing to reconnect after an idle disconnect would no longer risk opening
multiple simultaneous sockets to the same bulb even in a hypothetical caller that lacked its own
gate.

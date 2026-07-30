# Vendored Renci.SshNet build

This directory contains a locally patched build of [SSH.NET](https://github.com/sshnet/SSH.NET)
(`Renci.SshNet.dll`, `net462`), vendored directly into this repository so the driver builds
identically on every machine and in CI, without requiring a separate local checkout of SSH.NET.

## Why a patched build instead of the published NuGet package

The published `SSH.NET` NuGet package has two behaviors that this driver's
[`ProcessorBaselineCoordinator`](../../KasaTapoCrestronDriver/ProcessorBaselineCoordinator.cs) SSH
workaround depends on being fixed:

1. **Nondeterministic key-exchange algorithm selection.** `KeyExchange`'s client/server
	HMAC and compression algorithm negotiation used a LINQ query with two `from` clauses ordered
	by the *server's* advertised algorithm list rather than the *client's* preferred order,
	which does not correctly implement the client-preference-first negotiation order the SSH
	protocol expects. The patch replaces this with an explicit, order-preserving
	client-then-server nested loop (`SelectAlgorithm`).
2. **Unreliable receive timeouts on non-Windows/mono-like socket implementations.**
	`SocketAbstraction`'s blocking reads relied on `Socket.ReceiveTimeout`, which is not enforced
	consistently across all runtime/socket implementations and could hang indefinitely instead of
	timing out. The patch adds a `Socket.Poll`-based deadline wait (`PollWithDeadline`) that maps
	directly onto the underlying `select()`/`poll()` syscall and reliably unblocks on timeout
	regardless of platform.

Both fixes are based on the upstream `2025.1.0` tag and have not yet been submitted upstream.

## License

SSH.NET is licensed under the MIT License. See [`LICENSE-Renci.SshNet.txt`](LICENSE-Renci.SshNet.txt)
in this directory for the full license text. This vendored build is a modified redistribution of
that project, permitted under its MIT license.

## Updating this vendored build

If the upstream source patch changes, rebuild `Renci.SshNet.dll` (Release, `net462`) from the
patched source and replace the file in this directory.

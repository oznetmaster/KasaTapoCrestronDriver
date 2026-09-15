# KasaTapoCrestronDriver 2.1.0

Add read-only outlet identity and command-completion diagnostics for independently verified installed-driver tests. Existing outlet controls and Home UI bindings remain unchanged.

- Outlet entities expose `controlDeviceId` and `controlStatus` so the optional NUnit workflow can verify the selected physical outlet and distinguish completed background work from a cached UI update.
- Activity counters cover initial background work and command retries, with one completion per operation and a new epoch after entity recreation.
- A separate .NET 10 `KasaTapoCrestronDriver.ControlProbe` observes a selected plug or strip outlet through its device API. It resolves the current address from a stable device ID and never sends control commands.
- Add regressions for activity tracking and real SDK dispatch of the outlet setter. Ordinary automatic test plans remain free of physical controls; live selection and private credentials stay local.

Validation: 34 portable tests and 37 SDK lifecycle tests passed locally and on the processor. A complete development workflow additionally passed three read-only processor live checks and four installed checks, including physical On/Off control and verified restoration of the original outlet state. It removed the temporary test instance and archive and released the shared reservation.

The validated installed-driver route uses the absolute `outletOn` and `outletOff` commands. The processor rejected the parameterized setter internally despite HTTP success; direct SDK setter dispatch passed on desktop and processor. No general claim of parameterized Home command support is made.

See [installed-driver control testing](README.md#installed-driver-control-testing), the [probe guide](KasaTapoCrestronDriver.ControlProbe/README.md) and [CHANGELOG.md](CHANGELOG.md). Processor test packages have independent GitHub releases and are never published to NuGet.

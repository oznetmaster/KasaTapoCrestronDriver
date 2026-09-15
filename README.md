# Kasa/Tapo Crestron Driver

See the [changelog](CHANGELOG.md) for release history and the [release notes](RELEASE-NOTES.md) for the current driver update. Driver releases are made for runtime fixes or dependency changes; adding tests alone does not require a driver release.

`KasaTapoCrestronDriver` is a **Crestron Home Entity V2 platform driver** for TP-Link Kasa and Tapo smart home devices. Unlike a single-entity/extension driver that represents one device, this is a **platform driver**: a single instance of it discovers every supported Kasa/Tapo device on the local network, then dynamically creates, publishes, and manages a separate child light entity for each one directly inside Crestron Home. This driver is designed strictly for **local network access** to devices; it does not access Tapo cloud accounts to discover devices, and there are no plans to add cloud-based discovery.

**Current scope:** this release discovers and manages **lighting, outlet, sensor, and button devices** — bulbs, light strips, smart wall switches (for example, `KS200`/`KS205`/`KS240`; currently always treated as light loads — support for using a smart wall switch as a plain switched-outlet control, the same way plugs already work, is planned for a future release), plugs/power-strip outlets, and Kasa/Tapo hub-connected sensors (contact, motion, leak, temperature/humidity) and buttons.

TP-Link, Kasa, and Tapo are trademarks of their respective owners. This project is an independent, unofficial driver and is not affiliated with, endorsed by, or sponsored by TP-Link. Crestron and Crestron Home are trademarks or registered trademarks of Crestron Electronics, Inc. This project is not affiliated with, endorsed by, or sponsored by Crestron Electronics, Inc.

[![License: MIT + Commons Clause](https://img.shields.io/badge/License-MIT%20%2B%20Commons%20Clause-blue.svg)](LICENSE)

---

## Driver Architecture

This driver is a **Crestron Home platform Entity V2 driver**. A single configured instance of `PlatformDriver` runs discovery, maintains the set of known devices, and publishes/manages one child device per discovered, supported device. This is fundamentally different from an extension driver, which represents exactly one physical device: here, one driver instance can own and manage an arbitrary number of child devices simultaneously, adding and removing them as devices are discovered, added, or removed.

Each managed device kind is implemented as its own standalone entity class with its own UI/capability definition, rather than reusing a single generic entity for every kind: `KasaLightEntity` for lights (bulb, light strip, wall switch, or plug/power-strip outlet individually configured to be treated as a light) exposes only lighting capabilities (on/off, brightness, color, color-temperature); `KasaOutletEntity` for plain outlets (plug/power-strip outlet not configured as a light) exposes only outlet capabilities (on/off, and energy usage reporting when the connected device supports it); and `KasaSensorEntity`/`KasaButtonEntity` expose only their respective hub-child sensor/button capabilities. Each is independent, not a shared base class with optional members. Future support for additional Kasa/Tapo device kinds (power-monitoring telemetry, thermostats, etc.) will follow the same pattern: each new kind gets its own dedicated entity implementation and UI definition suited to its actual capabilities.

The driver talks to TP-Link Kasa and Tapo devices through the independent [`KasaTapoClient`](https://github.com/oznetmaster/KasaTapoClient) .NET library, which implements the (unofficial, reverse-engineered) Kasa and Tapo **local** device protocols. No cloud account or internet access is required or used to discover or control devices.

---

## Features

- Discovers TP-Link Kasa devices automatically on the local network via UDP broadcast discovery.
- Discovers Tapo devices on the local network when optional Tapo device credentials are configured (required by the Tapo local protocol itself, not a cloud account lookup). Tapo devices are supported **natively** via the TPAP protocol — no special "third-party compatibility" setting needs to be enabled on the device itself.
- Automatically publishes each supported bulb, light strip, wall switch, plug/power-strip outlet, hub sensor, or hub button as its own managed child device in Crestron Home; no manual per-device setup step is required beyond configuring the platform driver itself. Every plug and power-strip outlet is always discoverable, and each individually decides (via a per-child **Treat As Light** configuration item) whether it is published as a Light or as a plain Outlet. Other Kasa/Tapo device types (power-monitoring telemetry, thermostats, etc.) are planned for future releases.
- Outlet child devices (plugs/power-strip outlets not configured as a light) expose simple on/off control, plus current-power and today's-energy-usage telemetry when the connected device reports it. Outlets do not expose any lighting-specific capabilities (brightness, color, color-temperature).
- Hub-connected sensor devices (contact, motion, leak, temperature/humidity) and button devices are published as their own managed child devices, using a push-based polling model: the shared hub connection is polled at an interval automatically derived from each connected child's own reported cadence (50% of the shortest child-reported interval currently in use, so pushed state is picked up well within a single reporting cycle), rather than a single fixed interval for every hub. Button devices additionally support an **Allow Double Click** configuration item, and both sensor and button devices support an optional per-device **Report Interval (Seconds)** configuration item to override the device's own internal reporting cadence (0 leaves the device's own default interval untouched).
- Supports brightness, full HSV color, and color-temperature capabilities per device, based on the negotiated capabilities each specific device reports supporting (rather than assumptions based on device type alone) — this correctly surfaces dimmable devices such as `P135` (a dimmable smart plug) and `KS240` (a dimmer wall switch/fan controller) as dimmable lights even though they don't classify as a `Dimmer` device type.
- Persists discovered managed-device metadata to a local cache so previously-installed child devices can be republished quickly after a driver reload, without waiting for a fresh discovery pass.
- Starts each child device's physical connection in the background, so Crestron Home's child-configuration callbacks return quickly during driver reloads instead of blocking on device I/O.
- Optional workaround for a known Crestron Home platform defect that causes tunable lights to occasionally flash the wrong color/mode at power-on or at startup — see [Known Issue: Processor Baseline Workaround](#known-issue-processor-baseline-workaround) below.

---

## Configuration

The platform driver instance exposes these configuration items in Crestron Home:

| Field | Description |
|---|---|
| **Tapo User Name** / **Tapo Password** | Optional Tapo device credentials, required by the Tapo local protocol (KLAP/TPAP) to authenticate directly with each Tapo device over its own local IP address. `KasaTapoClient` sends these credentials only to the device itself; it contains no TP-Link cloud endpoints and never contacts a Tapo cloud account. Tapo devices are supported natively over TPAP — no "third-party compatibility" option needs to be enabled on the device in the Tapo app. Leave both blank to discover and manage only local Kasa devices. |
| **Discovery Timeout (Seconds)** | Timeout used for discovery and per-device connection attempts. |
| **Enable Light Polling** | Enables background polling to detect state changes made outside of Crestron Home (for example, from the Kasa/Tapo mobile apps or a physical switch). |
| **Light Poll Interval (Seconds)** | Polling interval used when light polling is enabled. |
| **Sensor/Button Poll Interval (Seconds)** | Fallback polling interval for hub sensor/button devices, used only until a connected child device has reported its own interval; once a child reports, the shared hub is instead polled at 50% of the shortest interval currently reported by any of its children (see [Features](#features)). |
| **Enable Processor Baseline Workaround** | Optional. Enables an SSH-based workaround for a Crestron Home tuning-mode defect. Disabled by default. See [Known Issue](#known-issue-processor-baseline-workaround) below. |
| **Processor SSH Host** | Optional. Hostname or IP address of the Crestron Home processor's console/SSH endpoint. Leave blank to have the driver automatically use the processor's own primary IPv4 address. Only used if the workaround above is enabled. |
| **Processor SSH User Name** / **Processor SSH Password** | Required only if the workaround above is enabled — console/SSH credentials for the Crestron Home processor itself. |

Each hub button child device additionally exposes a per-device **Allow Double Click** configuration item (enabled by default), controlling whether the device reports double-click events in addition to single-click. Each hub sensor and button child device also exposes a per-device **Report Interval (Seconds)** configuration item; leave it at the default of `0` to use the device's own internal reporting cadence, or set a positive value to override it.

Each discovered device is published automatically as its own child device once the platform driver is added and configured; no additional per-device "add device" step is needed in Crestron Home. Every discovered plug and power-strip outlet also exposes its own per-child **Treat As Light** configuration item (found on that specific child device, not on the platform driver) — enable it to expose that plug/outlet as a light entity; leave it disabled (the default) to expose it as a plain outlet. Known dimmable plug models (for example, `P135`) are always treated as lights and do not show this choice, since a dim level can only ever control a light.

---

## Outlet / Plug / Power-Strip Support

Every plug and every power-strip outlet is always discovered and can be installed as a child device. Each one independently decides, via its own per-child **Treat As Light** configuration item, whether it is published as:

- A plain **Outlet** (default) — a Crestron extension device exposing simple on/off control, plus current-power and today's-energy-usage telemetry when the connected device reports it, or
- A **Light** — a standard Entity V2 light load, for a plug or power-strip outlet actually wiring a lamp or other light fixture, alongside the bulbs, light strips, and wall switches this driver already publishes as lights.

Known dimmable plug models (for example, `P135`) are always treated as lights and do not expose this choice, since a dim level can only ever control a light; a wall switch is likewise always treated as a light in this release — support for treating a wall switch as a plain switched-outlet control is planned for a future release.

### Known Issue: Configure Pro and Outlet â†” Light Conversion

Converting a child device between **Outlet** and **Light** (in either direction) is fully supported and works correctly for actual device operation — the Room UI tile, on/off control, and (for lights) brightness/color controls all update correctly and immediately, and the change is fully persisted. However, Crestron Home's **Configure Pro** tool has two related display bugs around this conversion that are outside this driver's control:

- Configure Pro will not show the Installer Settings/configuration section for a device currently configured as a **Light** — including a plug/outlet that has been converted to a Light — even though the device itself is fully configured and working.
- After an **Outlet â†’ Light â†’ Outlet** conversion round-trip (i.e. converting back), Configure Pro's device list can show stale/incorrect entries for the affected device until the driver is reloaded. A driver reload immediately restores a correct, fully configurable device list.

Neither issue is present in the Crestron Home **Setup** program, which correctly reflects the device's current kind and configuration state throughout the conversion in both directions; only Configure Pro is affected. If Configure Pro appears to misbehave after converting a device between Outlet and Light, reload the driver to resolve it.

---

## Known Issue: Processor Baseline Workaround

Crestron Home's Entity V2 lighting model documents a `lightTunable:mode` property that a driver is supposed to use to tell the processor whether a tunable light is currently in Color (HSV) or White (color temperature) mode. **In practice, this property has no effect** — the processor maintains its own internal, separate "baseline" and "active" tuning-mode state per light load, neither of which is updated by `lightTunable:mode`. This can cause a full-color/tunable-white bulb to briefly flash the wrong color or mode immediately after being turned on, and — because the Crestron Home UI reads the processor's "active" tuning mode when first rendering a light's tile/detail page — it can also cause the **UI itself to initialize with the wrong mode/controls** (e.g. showing color controls for a bulb that is actually in white/CT mode, or vice versa) until the mismatch is corrected.

Crestron has been made aware of this behavior (see [`CRESTRON_HOME_TUNING_MODE_DEFECT.md`](CRESTRON_HOME_TUNING_MODE_DEFECT.md) for the full original defect report, reproduction steps, and processor-level evidence) and has acknowledged awareness of the issue for some time, but **no fix has been released as of this writing**.

This driver includes an **optional, disabled-by-default** workaround (`ProcessorBaselineCoordinator`) that connects to the Crestron Home processor's own console over SSH and issues the same corrective commands a person would otherwise have to type by hand, to force the processor's internal baseline back in sync with what the driver actually wants to display. It is intentionally an unusual, "outside the documented SDK surface" fix — because the documented SDK surface currently provides no supported way to solve this problem at all. Once Crestron ships a real fix for `lightTunable:mode`, this workaround will be removed and the driver will rely on the documented property instead, as originally designed.

Full details — root cause, exactly how the workaround operates, its configuration, and its risks/limitations — are in [`docs/ProcessorBaselineWorkaround.md`](docs/ProcessorBaselineWorkaround.md).

---

## Building from Source

### Dependencies

- [Crestron.DeviceDrivers.DevKit](https://www.nuget.org/packages/Crestron.DeviceDrivers.DevKit) NuGet package
- [KasaTapoClient](https://github.com/oznetmaster/KasaTapoClient) NuGet package
- `.NET Framework 4.7.2`
- [ILRepack](https://github.com/gluck/il-repack) via `ILRepackMerge.ps1`
- `PatchMergedAssembly.ps1` to rewrite merged assemblies for Crestron Home runtime compatibility
- `ManifestUtil.exe` from the Crestron Driver SDK to produce the final `.pkg`

### Build

From the repository root:

```powershell
dotnet build .\KasaTapoCrestronDriver\KasaTapoCrestronDriver\KasaTapoCrestronDriver.csproj -c Release
```

The build pipeline:
1. Compiles the driver targeting `net472`
2. Bumps `DriverVersion` and `VersionDate` in `KasaTapoCrestronDriver.json`
3. ILRepacks runtime dependencies into the driver assembly
4. Runs `PatchMergedAssembly.ps1` against the merged assembly
5. Packages the driver into a `.pkg` using Crestron's ManifestUtil

### Installing the Driver

The best way to download and install this driver on a Crestron Home system is to use the [Crestron Home Driver Feed Installer](https://github.com/oznetmaster/Crestron-Home-Driver-Feed-Installer) repository and application.

If you prefer to install manually, use the attached `.pkg` asset from the relevant GitHub Release (once published), or build one yourself using the instructions above. The automatic GitHub `Source code (zip)` and `Source code (tar.gz)` assets are repository snapshots, not installable Crestron driver packages.

NuGet package availability: this driver is also published as the `CrestronHomeDriver.TpLink.KasaTapoPlatform` NuGet package. This NuGet package conforms to the **Crestron Home Driver NuGet Publishing Standard v1**. It is a distribution wrapper for the final `.pkg` artifact, includes the required `crestron-driver-package.json` manifest, and is not intended as a direct DLL reference package.

Crestron Home Driver NuGet Publishing Standard v1 is **not** an official Crestron product or specification. It is an open source packaging standard created to facilitate community distribution and discovery of Crestron Home drivers through NuGet.

1. Download the generated `.pkg` asset from the GitHub Release, or build it yourself using the instructions in [Building from Source](#building-from-source).
2. Upload the `.pkg` file to your Crestron Home processor manually (for example via SFTP to `/user/ThirdPartyDrivers/Import`).
3. In the Crestron Home configuration UI, add a new device and select the **Kasa/Tapo Platform** driver.
4. Configure the driver using the values in [Configuration](#configuration) above.

The platform driver instance itself has no room-facing UI and controls no physical load directly — it must be added to some room (Crestron Home requires every device to belong to a room), but which room makes no difference, since discovery happens over the local network rather than through any particular room's wiring. Only the individual child devices it publishes (lights, outlets, sensors, buttons) need to be placed in the room where the corresponding physical device actually lives.

A single instance of this driver manages all discovered Kasa/Tapo devices — you do not add a separate driver instance per bulb.

### GitHub Release Asset

This repository includes a GitHub Actions workflow ([`.github/workflows/release-package.yml`](.github/workflows/release-package.yml)) that builds the Release package and attaches the generated `.pkg` to a GitHub Release.

For production `v*` tags, the same release workflow also publishes the `CrestronHomeDriver.TpLink.KasaTapoPlatform` NuGet package, which wraps the final generated `.pkg` artifact.

NuGet publishing uses [nuget.org Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing) (GitHub OIDC) rather than a long-lived API key: the workflow's `id-token: write` permission lets the `NuGet/login` action exchange a GitHub-issued OIDC token for a short-lived nuget.org API key at publish time, using the `NUGET_USER` repository secret for the nuget.org account name. This requires a matching Trusted Publisher policy configured on nuget.org for this repository, workflow file, and (optionally) environment — no `NUGET_API_KEY` secret is needed or stored.

Typical **production driver** release flow:
1. Push the release commit and tag
2. Publish the GitHub Release for that tag
3. Let the workflow build and attach the `.pkg` asset (and publish the NuGet package) automatically

---

## Release Notes

See [CHANGELOG.md](CHANGELOG.md) for a summary of all release history, or the [GitHub releases](https://github.com/oznetmaster/KasaTapoCrestronDriver/releases) page for full per-version details and build assets.

---

## Repository Notes

- The repository includes the driver package/build scripts needed for packaging and deployment.
- [`docs/ProcessorBaselineWorkaround.md`](docs/ProcessorBaselineWorkaround.md) and [`CRESTRON_HOME_TUNING_MODE_DEFECT.md`](CRESTRON_HOME_TUNING_MODE_DEFECT.md) document a known Crestron Home platform limitation and this driver's optional workaround for it.

---

## License

MIT + Commons Clause © 2026 Neil Colvin — see [LICENSE](LICENSE).

Free to use and modify. You may not sell the Software as a standalone product or sublicense it.
Commercial system integration work (for example, a Crestron installer commissioning a customer system) is explicitly permitted, even where a fee is charged for that service.

## Trademark and Non-Association Notice

TP-Link, Kasa, and Tapo are trademarks of their respective owners. This project is an independent, unofficial Crestron Home driver and is not affiliated with, endorsed by, or sponsored by TP-Link.

Crestron, Crestron Home, and related marks are trademarks of Crestron Electronics, Inc. This project is an independent, unofficial driver built against publicly available Crestron Home Entity V2 driver SDK components and is not affiliated with, endorsed by, or sponsored by Crestron Electronics, Inc.

This driver communicates with TP-Link Kasa and Tapo devices using the independent [`KasaTapoClient`](https://github.com/oznetmaster/KasaTapoClient) .NET library, which is itself an independent, unofficial implementation and is not affiliated with or endorsed by TP-Link.

> **Note:** This project references [Crestron.DeviceDrivers.DevKit](https://www.nuget.org/packages/Crestron.DeviceDrivers.DevKit),
> which is subject to Crestron's SDK license agreement. That license governs the SDK libraries only;
> the source code in this repository is licensed independently under the terms in [LICENSE](LICENSE).
## NUnit tests and processor validation

The test projects use NUnit and its Visual Studio adapter. The `net472` project includes 34 ordinary tests and 35 processor lifecycle cases; the desktop lifecycle project runs those 35 cases with the desktop-compatible SDK. The repository `.runsettings` excludes the `Processor` category on Windows.

Build **KasaTapoCrestronDriver.ProcessorTests** in the existing solution to create the separate **Utility / KasaTapoCrestronDriver Tests** package. It runs the shared tests against the real driver and SDK on the processor, using simulated responses without operating live devices. Deployment settings and machine paths remain locally excluded. See [processor test instructions](KasaTapoCrestronDriver.ProcessorTests/README.md).


### Test-package releases

Download `KasaTapoCrestronDriver.ProcessorTests.pkg` from a release titled **KasaTapoCrestronDriver Tests**, then add **Utility â†’ Neil Colvin â†’ KasaTapoCrestronDriver Tests** in Crestron Home Configure. All processor test packages use the **Utility** category. Each has its own standalone Home tile and embedded NUnit host, and can also be selected in the [Windows runner](https://github.com/oznetmaster/CrestronHomeNUnit/releases). The production driver and test package can coexist.

The **Release processor tests** workflow takes an independent test-package version, for example `1.0.0`, and publishes a `processor-tests-v1.0.0` tag. Its assets include the test `.pkg`, exact source revisions, documentation and SHA-256 checksums. It builds only the processor package and its test dependencies, validates all 34 ordinary tests and the 35 desktop lifecycle tests, and validates merged package discovery. Running the 35 lifecycle tests against the processor SDK requires a processor.

This workflow never packs or publishes to NuGet and does not bump the production driver version. The production release workflow ignores test-package releases, and test releases do not replace the latest production release. NUnit 4.6.1 and NUnit3TestAdapter replace MSTest in both test projects. See [test-package third-party notices](KasaTapoCrestronDriver.ProcessorTests/THIRD-PARTY-NOTICES.md) for redistributed dependencies.


### Expanded driver behavior tests

Cover persisted light, outlet, sensor and button descriptors, configured-device markers, incomplete cache metadata, and publication through the real SDK registry without duplicate controllers. Each cache fixture uses its own temporary file. Release the Debug diagnostic listener when its platform driver is disposed.

A failed command releases the operation queue; a failing subscriber cannot stop healthy siblings receiving state; unregistering during offline notification prevents later delivery to that registration; disposal discards late refresh results even when the refresh ignores cancellation.

The current package contains **34 offline tests** and **35 SDK entity/lifecycle tests**. The processor package remains **net472 only**, appears under **Utility** in Configure, and can run independently through its own tile or the Windows NUnit runner. These fixtures use synthetic data and do not operate installed devices or authenticate with real accounts.

`KasaTapoCrestronDriver.Lifecycle.Tests` runs the entity checks against the real desktop SDK on .NET 10. It compiles the relevant driver sources and shares fixture sources with the net472 processor tests. Building this project does not deploy a driver.

```powershell
dotnet test KasaTapoCrestronDriver.Tests/KasaTapoCrestronDriver.Tests.csproj --filter "TestCategory!=Processor"
dotnet test KasaTapoCrestronDriver.Lifecycle.Tests/KasaTapoCrestronDriver.Lifecycle.Tests.csproj
```

Desktop success does not establish Mono compatibility. Build the processor test project in Visual Studio, deploy it, and run both suites on the processor. The fixtures cover configuration, restoration, refresh/reconnect races and disposal using simulated responses. Real installed-driver health and optional live-device checks remain separate from these repeatable suites.



### Driver build and release versions

The driver's JSON manifest is the source of its four-component build version. Debug builds increment only the fourth component; for example, `2.0.001.0005` becomes `2.0.001.0006`. MSBuild's `Version` and default `PackageVersion` are derived from that same manifest and refreshed after the increment; their numeric form is `2.0.1.6`. Assembly binding versions remain separate. Test-only references and IDE design-time builds do not increment the production driver version.

GitHub tags and NuGet releases retain three components: `v2.0.1` and `2.0.1`. Prepare the manifest's first three components for the intended release before tagging. Release CI checks that the tag matches, resets the fourth component to zero, and verifies the generated `.pkg` version against the manifest and release version before publishing. It does not increment the selected patch again. Local Release builds preserve the manifest. A later Debug build can legitimately be newer than a published release; the processor test package has its own independent version.

Deployment validation compares the exact built `.pkg` against the imported catalogue entry and installed instance, numerically including all four components. Upload/import alone does not activate the new version. Keep the tested package and its hash: rebuilding creates a new artifact that must be validated again.

Run `pwsh -File tools/Test-DriverVersioning.ps1` to check these rules with temporary manifests; this does not change the working driver manifest or deploy anything.

See [versioning details](docs/Versioning.md) for build, release and installed-instance verification rules.
### Desktop SDK dependency in CI

The SDK's desktop manifest reader needs its `Newtonsoft.Json.Compact.dll` runtime dependency. Supply a local SDK/runtime copy through the `CompactJsonPath` MSBuild property (or private `DesktopTest.Local.props`). Maintainer CI restores the same verified copy from encrypted Actions secrets into its temporary directory; it is not committed, attached to release assets or included in processor packages. Fork pull requests do not receive these secrets and require a trusted maintainer validation run.


For automated local tests, processor tests and gated driver deployment, see the [Crestron Home NUnit CI development guide](https://github.com/oznetmaster/CrestronHomeNUnit/blob/HEAD/docs/ContinuousIntegration.md). It covers private configuration, live-test gates, install/update waits, results and optional test-package removal.

## Visual Studio processor workflow

The solution includes [KasaTapoCrestronDriver.WorkflowTests](KasaTapoCrestronDriver.WorkflowTests/README.md), using the published Crestron Home Test Adapter. It exposes the complete gated workflow in Test Explorer while the ordinary NUnit fixtures remain available for local testing. Configure its private settings before execution; hosted CI verifies discovery without accessing hardware.

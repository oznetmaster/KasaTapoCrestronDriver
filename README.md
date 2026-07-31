# Kasa/Tapo Crestron Driver

`KasaTapoCrestronDriver` is a **Crestron Home Entity V2 platform driver** for TP-Link Kasa and Tapo smart home devices. Unlike a single-entity/extension driver that represents one device, this is a **platform driver**: a single instance of it discovers every supported Kasa/Tapo device on the local network, then dynamically creates, publishes, and manages a separate child light entity for each one directly inside Crestron Home. This driver is designed strictly for **local network access** to devices; it does not access Tapo cloud accounts to discover devices, and there are no plans to add cloud-based discovery.

**Current scope:** this release only discovers and manages **lighting devices** (bulbs, light strips, and plugs optionally treated as lights). Other Kasa/Tapo device types (for example, plugs/switches exposed as outlets, power-monitoring plugs, or sensors) are not yet published as child devices, though some groundwork for future device types already exists in the platform's discovery and caching layers. Support for additional device types is planned for future releases.

TP-Link, Kasa, and Tapo are trademarks of their respective owners. This project is an independent, unofficial driver and is not affiliated with, endorsed by, or sponsored by TP-Link. Crestron and Crestron Home are trademarks or registered trademarks of Crestron Electronics, Inc. This project is not affiliated with, endorsed by, or sponsored by Crestron Electronics, Inc.

[![License: MIT + Commons Clause](https://img.shields.io/badge/License-MIT%20%2B%20Commons%20Clause-blue.svg)](LICENSE)

---

## Driver Architecture

This driver is a **Crestron Home platform Entity V2 driver**. A single configured instance of `PlatformDriver` runs discovery, maintains the set of known devices, and publishes/manages one `KasaLightEntity` child device per discovered, supported light (bulb, light strip, or plug treated as a light). This is fundamentally different from an extension driver, which represents exactly one physical device: here, one driver instance can own and manage an arbitrary number of child light devices simultaneously, adding and removing them as devices are discovered, added, or removed.

The driver talks to TP-Link Kasa and Tapo devices through the independent [`KasaTapoClient`](https://github.com/oznetmaster/KasaTapoClient) .NET library, which implements the (unofficial, reverse-engineered) Kasa and Tapo **local** device protocols. No cloud account or internet access is required or used to discover or control devices.

---

## Features

- Discovers TP-Link Kasa devices automatically on the local network via UDP broadcast discovery.
- Discovers Tapo devices on the local network when optional Tapo device credentials are configured (required by the Tapo local protocol itself, not a cloud account lookup). Tapo devices are supported **natively** via the TPAP protocol — no special "third-party compatibility" setting needs to be enabled on the device itself.
- **Lights only in this release** — automatically publishes each supported bulb, light strip, or plug (optionally treated as a light) as its own managed child light device in Crestron Home; no manual per-device setup step is required beyond configuring the platform driver itself. Other Kasa/Tapo device types (outlets, power-monitoring plugs, sensors, etc.) are planned for future releases.
- Optionally exposes smart plugs as light entities, for plugs driving lighting loads.
- Supports brightness, full HSV color, and color-temperature capabilities per device, based on what each specific device reports supporting.
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
| **Treat Plugs As Lights** | When enabled, supported smart plugs are exposed as light entities instead of being ignored. |
| **Enable Light Polling** | Enables background polling to detect state changes made outside of Crestron Home (for example, from the Kasa/Tapo mobile apps or a physical switch). |
| **Light Poll Interval (Seconds)** | Polling interval used when light polling is enabled. |
| **Sensor/Button Poll Interval (Seconds)** | Reserved for future sensor and button child entity support. |
| **Enable Processor Baseline Workaround** | Optional. Enables an SSH-based workaround for a Crestron Home tuning-mode defect. Disabled by default. See [Known Issue](#known-issue-processor-baseline-workaround) below. |
| **Processor SSH Host** | Optional. Hostname or IP address of the Crestron Home processor's console/SSH endpoint. Leave blank to have the driver automatically use the processor's own primary IPv4 address. Only used if the workaround above is enabled. |
| **Processor SSH User Name** / **Processor SSH Password** | Required only if the workaround above is enabled — console/SSH credentials for the Crestron Home processor itself. |

Each discovered device is published automatically as its own child device once the platform driver is added and configured; no additional per-device "add device" step is needed in Crestron Home.

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

A single instance of this driver manages all discovered Kasa/Tapo devices — you do not add a separate driver instance per bulb.

### GitHub Release Asset

This repository includes a GitHub Actions workflow ([`.github/workflows/release-package.yml`](.github/workflows/release-package.yml)) that builds the Release package and attaches the generated `.pkg` to a GitHub Release.

The same release workflow also publishes the `CrestronHomeDriver.TpLink.KasaTapoPlatform` NuGet package, which wraps the final generated `.pkg` artifact.

NuGet publishing uses [nuget.org Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing) (GitHub OIDC) rather than a long-lived API key: the workflow's `id-token: write` permission lets the `NuGet/login` action exchange a GitHub-issued OIDC token for a short-lived nuget.org API key at publish time, using the `NUGET_USER` repository secret for the nuget.org account name. This requires a matching Trusted Publisher policy configured on nuget.org for this repository, workflow file, and (optionally) environment — no `NUGET_API_KEY` secret is needed or stored.

Typical release flow:
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

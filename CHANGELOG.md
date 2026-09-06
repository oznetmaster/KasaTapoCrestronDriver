# Changelog

All notable changes to this project are documented here. Each entry summarizes the corresponding
[GitHub release](https://github.com/oznetmaster/KasaTapoCrestronDriver/releases), which remains the
authoritative, detailed record (including build assets) for that version. This file exists as a
single, scannable index of the full version history.

The format is loosely based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this
project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

- Fixed a regression where reloading the driver (not removing it) deleted the persisted managed-device cache file, forcing full rediscovery and leaving every device shown as Offline/Not Configured in Configure Pro until identities re-resolved. The cache is no longer deleted from `Dispose()`, since the SDK does not provide a reliable way to distinguish an actual driver removal from a routine reload/restart.
- Fixed a related bug where previously-configured devices restored from the on-disk managed-device cache never actually came back online after a reload: the in-memory "configured" tracking set was never repopulated from the cache on startup, so the recreated child configuration controllers were never replayed back to Configured/Running and stayed stuck Offline no matter how many discovery passes ran afterward.
- Fixed a regression where devices restored from the on-disk managed-device cache on driver startup showed their serial number as the secondary info line in Configure Pro instead of manufacturer/model. The cache-seeding code path was missed when this was originally fixed for newly-discovered devices; it now passes `null` for the serial number here too, consistent with every other managed-device publication path.
- Fixed a bug where a previously-configured child device (e.g. a plug) could keep working correctly in the Room UI after a driver reload, yet never reappear in Configure Pro's Setup/Configure device list. The recreated child's configuration was being replayed back to Configured/Running *before* its controller was registered with the host via `UpdateSubControllers`, so the host never observed the NotConfigured -> Configured transition that Configure Pro's device list relies on. The replay now happens after host registration, matching the ordering used for newly-discovered devices.
- Fixed a bug where a power strip's individual child outlets (e.g. KP303 plugs) could show Offline/"Driver Not Loaded" in Configure Pro and the touchscreen UI after a driver reload. `PersistManagedDeviceCache` was rebuilding each cache entry's immutable fields without carrying over `ChildId`, so every cache rewrite silently dropped which physical child on the strip a given controllerId represented. On the next reload the recreated descriptor had `ChildId=null`, causing it to resolve its identity from the strip's own root alias instead of its own child alias/serial - which the host then treated as a mismatched/unrecognized device. `ChildId` is now preserved on every cache persist.
- Outlet child devices
- Each managed device kind now has its own standalone entity implementation and UI definition; this is the pattern future Kasa/Tapo device kinds will also follow.
- Added a packaged `UiDefinition.xml` and `Translations/en-US.json` for outlet entities, under `IncludeInPkg/outlet/`. Unlike `Light`, `Outlet` is a Crestron extension device type and therefore requires its own custom UI/translation assets; the layout mirrors the per-kind packaging convention used by other Entity Model V2 drivers.

## [1.2.0]

- Updated to `KasaTapoClient` 1.3.1, which negotiates device capabilities (e.g. brightness) directly from each device's SMART component list instead of relying solely on its device type.
- Fixed a discovery/classification bug so brightness-capable devices that don't classify as a `Dimmer` device type are still correctly published as dimmable lights: `P135` (a dimmable smart plug) and `KS240` (a dimmer wall switch/fan controller) are now always surfaced as `Dimmable`, regardless of the `Treat Plugs As Lights` setting.
- `WallSwitch`-classified devices are now always treated as supported light loads (previously gated in some code paths the same way plugs are), since a wall switch can only ever be wired to a light.
- Added regression test coverage for the above capability-driven classification behavior.

## [1.1.0]

First general availability release.

- Crestron Home platform driver for TP-Link Kasa and Tapo smart lights: a single driver instance discovers devices on the local network and publishes each as a managed child light entity.
- Local-only device access: devices are discovered and controlled directly over the local network. No Tapo or Kasa cloud account is used or required.
- Includes an optional, SSH-based workaround (`ProcessorBaselineCoordinator`) for a Crestron Home processor defect in which the `lightTunable:mode` property has no effect, which can also cause the UI to initialize incorrectly at startup. See [`docs/ProcessorBaselineWorkaround.md`](docs/ProcessorBaselineWorkaround.md) for details.
- Distributed as a NuGet package containing the driver `.pkg`, installable via the Crestron Home Driver Feed Installer.

## [1.0.0-preview]

Initial public preview release.

- Crestron Home platform driver for TP-Link Kasa and Tapo smart lights: a single driver instance discovers devices on the local network and publishes each as a managed child light entity.
- Local-only device access: devices are discovered and controlled directly over the local network. No Tapo or Kasa cloud account is used or required.
- Includes an optional, SSH-based workaround (`ProcessorBaselineCoordinator`) for a Crestron Home processor defect in which the `lightTunable:mode` property has no effect, which can also cause the UI to initialize incorrectly at startup. See [`docs/ProcessorBaselineWorkaround.md`](docs/ProcessorBaselineWorkaround.md) for details.
- Distributed as a NuGet package containing the driver `.pkg`, installable via the Crestron Home Driver Feed Installer.

## [1.0.001.0001]

- Improved driver reload behavior by making child-device activation nonblocking from Crestron child configuration callbacks.
- Republished cached managed child devices early during startup so installed child devices can reattach after reload.
- Kept physical device connection and state initialization in the background, allowing the platform driver to come online while child devices finish connecting.
- Added logging around discovery, cache seeding, child configuration status, activation, and startup connection timing.

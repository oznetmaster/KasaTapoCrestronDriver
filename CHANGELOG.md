# Changelog

All notable changes to this project are documented here. Each entry summarizes the corresponding
[GitHub release](https://github.com/oznetmaster/KasaTapoCrestronDriver/releases), which remains the
authoritative, detailed record (including build assets) for that version. This file exists as a
single, scannable index of the full version history.

The format is loosely based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this
project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [1.3.0]

- Added support for plugs and power-strip outlets as a new managed device kind, alongside the lights this driver already supported. Every discovered plug and power-strip outlet is always selectable for installation and, per child device, individually configured as either a plain **Outlet** (on/off control, plus current-power and today's-energy-usage telemetry when the connected device reports it) or a **Light**, via that child's own **Treat As Light** configuration item.
- Added full support for converting an already-installed plug or power-strip outlet child device between **Outlet** and **Light** in place, via the **Treat As Light** configuration item, without removing and re-adding the device. See the [Outlet / Plug / Power-Strip Support](README.md#outlet--plug--power-strip-support) section of the README for details.
- Added a packaged `UiDefinition.xml` and `Translations/en-US.json` for outlet entities, under `IncludeInPkg/outlet/`. Unlike `Light`, `Outlet` is a Crestron extension device type and therefore requires its own custom UI/translation assets; the layout mirrors the per-kind packaging convention used by other Entity Model V2 drivers.
- Each managed device kind (Light, Outlet) is now implemented as its own standalone entity, rather than sharing a single generic entity implementation.
- Documented a known Crestron Home **Configure Pro** limitation (not present in Setup): Configure Pro does not show the Installer Settings/configuration section for a device currently configured as a Light (including a plug/outlet converted to a Light), and can show a stale/incorrect device list after an Outlet → Light → Outlet conversion round-trip until the driver is reloaded. See the README for full details and the reload workaround.
- Fixed cached/reloaded child devices (e.g. KP303 strip outlets) not showing the Installer Settings section (Ready/Treat As Light/Reconfigure Driver) in Configure Pro after a driver reload, while devices that had been manually reconfigured since the reload (e.g. KP115) showed it correctly. The per-child `ConfigurationStepsDefinition` created in `CreateChildConfigurationController` never set `IncludePersistentValueData`, which defaults to `false`; without it, Configure Pro only recognizes a child controller as configured after the controller itself completes a live `ApplyConfiguration` round-trip through the host - the internal replay `PublishCachedChildControllers` performs after a reload (via a direct `GetFirstConfigurationStep`/`ApplyConfigurationStep` call, not a real Configure Pro session) is invisible to that tracking. Setting `IncludePersistentValueData = true` (and explicitly `IsNotOfflineConfigurable = false`) tells Configure Pro to honor the controller's persisted configuration values directly, so a recreated-from-cache child is recognized as already configured immediately after reload.
- Fixed a regression where devices restored from the on-disk managed-device cache on driver startup showed their serial number as the secondary info line in Configure Pro instead of manufacturer/model. The cache-seeding code path was missed when this was originally fixed for newly-discovered devices; it now passes `null` for the serial number here too, consistent with every other managed-device publication path.
- Fixed a bug where a previously-configured child device (e.g. a plug) could keep working correctly in the Room UI after a driver reload, yet never reappear in Configure Pro's Setup/Configure device list. The recreated child's configuration was being replayed back to Configured/Running *before* its controller was registered with the host via `UpdateSubControllers`, so the host never observed the NotConfigured -> Configured transition that Configure Pro's device list relies on. The replay now happens after host registration, matching the ordering used for newly-discovered devices.
- Fixed a bug where a power strip's individual child outlets (e.g. KP303 plugs) could show Offline/"Driver Not Loaded" in Configure Pro and the touchscreen UI after a driver reload. `PersistManagedDeviceCache` was rebuilding each cache entry's immutable fields without carrying over `ChildId`, so every cache rewrite silently dropped which physical child on the strip a given controllerId represented. On the next reload the recreated descriptor had `ChildId=null`, causing it to resolve its identity from the strip's own root alias instead of its own child alias/serial - which the host then treated as a mismatched/unrecognized device. `ChildId` is now preserved on every cache persist.
- Fixed the same strip-child-outlet identity bug for on-disk caches written *before* the fix above: those files still have `ChildId=null` baked in from prior rewrites, so simply stopping future drops was not enough to recover already-corrupted entries. `TryCreateCachedDescriptorAndConfiguration` now recovers a missing `ChildId` from the deterministic `device_{parentDeviceId}_{childId}` shape of a strip child's controllerId and persists the recovered value back to the cache, self-healing existing installs without requiring the cache file to be deleted manually.

## [1.2.0]

- Updated to `KasaTapoClient` 1.3.1, which negotiates device capabilities (e.g. brightness) directly from each device's SMART component list instead of relying solely on its device type.
- Fixed a discovery/classification bug so brightness-capable devices that don't classify as a `Dimmer` device type are still correctly published as dimmable lights: `P135` (a dimmable smart plug) and `KS240` (a dimmer wall switch/fan controller) are now always surfaced as `Dimmable`, regardless of the `Treat Plugs As Lights` setting.
- `WallSwitch`-classified devices are now always treated as supported light loads (previously gated in some code paths the same way plugs are), since this driver only supports wall switches as light loads.
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

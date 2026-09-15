# Changelog

## KasaTapoCrestronDriver.ProcessorTests v1.2.0 - 2026-09-15

Published processor test package on GitHub. This is a test-package release only; no driver or library NuGet package is published. See the matching package release notes for changes and validation.

## 2.1.0 - 2026-09-16

- Add read-only outlet identity and command-completion properties for independent physical control/restoration verification. Existing outlet controls and UI bindings remain unchanged.
- Add the separate read-only device probe and regression coverage for activity tracking and SDK outlet-setter dispatch. Automatic tests remain non-controlling; optional physical tests require a private device selection.
- Verify a complete Kasa workflow with physical state restoration, temporary test-host/archive cleanup and lease release. See release notes for the validated absolute On/Off command route and the parameterized-command limitation.

## Offline release workflow option - 2026-09-15 (no package release)

- Allow an explicit manual release when local hardware or the self-hosted runner is unavailable, with the reason and exact source recorded in the workflow summary.
- Keep hosted source validation mandatory and preserve all build, test and packaging steps. No runtime, API or package-version changes.

## Discovery-based CI coverage - 2026-09-15 (no package release)

- Compare desktop results and merged-package discovery with source test identities, replacing duplicated test-count constants. Verify the separate desktop lifecycle harness covers the processor fixture identities.
- Preserve portable-only net472 execution and run lifecycle cases through the desktop SDK harness. Package discovery covers every automatic and live fixture; processor CI executes the automatic suites.
- No actual driver code or public API changes. Live device tests remain excluded from hosted execution.

## CI package cleanup - 2026-09-15 (no driver or processor package release)

- Update Test Explorer workflow containers to CrestronHomeNUnit.TestAdapter 1.3.0 and document opt-in storage cleanup after successful CI runs.
- Retain original deployment filenames, protect pre-existing/manual packages and preserve failed-run evidence. Cleanup frees archive storage without rebooting; Home can retain cached catalogue entries until its next planned reboot.
- Actual driver/library code and processor test packages are unchanged by this tooling update.

## CI validation - 2026-09-15 (no package release)

- Revalidate the current default-branch source after successful release workflows, including version commits created by GitHub Actions.
- Allow maintainers to configure exact-source, App-specific checks that must pass before publishing through `RELEASE_REQUIRED_CHECKS`; missing, failed or unconfirmed checks block the release.

## KasaTapoCrestronDriver.ProcessorTests v1.1.1 - 2026-09-15

Published processor test package on GitHub. This is a test-package release only; no driver or library NuGet package is published. See the matching package release notes for changes and validation.

## 2026-09-15 - Test and development tooling (no driver release)

- Add the published Test Explorer workflow adapter, offline discovery CI and independent GitHub processor-test releases. Private workflow plans control optional live tests, actual-driver updates and temporary-instance cleanup.


All notable changes to this project are documented here. Each entry summarizes the corresponding
[GitHub release](https://github.com/oznetmaster/KasaTapoCrestronDriver/releases), which remains the
authoritative, detailed record (including build assets) for that version. This file exists as a
single, scannable index of the full version history.

The format is loosely based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this
project follows [Semantic Versioning](https://semver.org/).

## 2.0.1 — 2026-09-14

[Driver release notes](RELEASE-NOTES.md). Test-only changes do not require a driver release.

- Test cached controller restoration, stable identity and publication through the SDK registry. Use isolated temporary cache files in fixtures and release Debug diagnostic listeners when disposing a platform driver.

- Standardize driver versioning: Debug project/package metadata follows the manifest including its build increment; local Release builds preserve it; three-part release tags select the exact CI release without another patch increment. Verify source and built package versions before publication.




- Expand driver coverage to 34 offline tests and 35 SDK entity/lifecycle tests, with a desktop SDK harness and the same lifecycle fixtures in the net472 processor package.
- Fix an offline callback reaching an entity removed or replaced by an earlier callback in the same notification pass.

## [2.0.0]

Major release: native hub sensor and button device support, alongside supporting reliability and configuration improvements. Bumped to 2.0.0 due to the significant new functionality and the architectural rearchitecture of hub polling described below.

- Added native support for Kasa/Tapo hub-connected **sensor devices** (contact, motion, leak, temperature/humidity) and **button devices**, published as their own standalone managed child entities (`KasaSensorEntity` / `KasaButtonEntity`), following the same one-entity-per-kind pattern used for lights and outlets.
- Rearchitected hub child polling from a single fixed-interval poll loop per child to a shared, push-based `ManagedParentDevicePoller` model: one poller per physical hub connection fans out pushed state to all of its registered children on each refresh, and only polls at all while at least one child has active event subscribers, avoiding unnecessary traffic to hubs with no actively-displayed children.
- The shared hub poller's polling cadence is now automatically derived from the children themselves: it polls at **50% of the shortest report interval currently reported by any connected child**, so pushed state changes are picked up well within a single reporting cycle instead of only matching it. The **Sensor/Button Poll Interval (Seconds)** setting is now only a fallback used until a child has reported its own interval.
- Added a per-device **Report Interval (Seconds)** configuration item for sensor and button child devices, allowing the device's own internal reporting cadence to be overridden directly (via `KasaTapoClient` 1.8.0's `ChildReportModeModule.SetIntervalAsync`). The default value of `0` leaves the device's own default interval untouched. Changing this value also immediately notifies the owning hub poller to recompute and reset its own polling cadence, rather than waiting out a delay based on the previous interval.
- Added a per-device **Allow Double Click** configuration item for button child devices (enabled by default), controlling whether the device reports double-click events in addition to single-click.
- Added support for shared-parent power-strip topologies where multiple managed child devices are backed by the same physical parent connection, reusing the same pooled-connection model introduced for hub sensors/buttons.
- Fixed sensor/button polling to use the dedicated **Sensor/Button Poll Interval (Seconds)** setting instead of incorrectly reusing the light-polling interval/enablement settings.
- Fixed hub child readiness so a child that publishes asynchronously and is already `Running` by the time its registration completes is correctly reconciled instead of left in a stale state.
- Fixed the **Allow Double Click** configuration item not actually applying to already-connected hub button devices in some cases.
- Fixed several nullable-reference compiler warnings in the light entity's diagnostic state-snapshot formatter.

## [1.3.1]

- Fixed the NuGet package not carrying any release notes. The release workflow passed the published GitHub Release's body to MSBuild via `-p:PackageReleaseNotesFile`, but nothing in the project file ever read that file's contents into the `PackageReleaseNotes` property NuGet actually packs, so every release notes field was silently blank. Added a `SetPackageReleaseNotesFromFile` build target that reads the file and populates `PackageReleaseNotes` before the nuspec is generated.

## [1.3.0]

- Added support for plugs and power-strip outlets as a new managed device kind, alongside the lights this driver already supported. Every discovered plug and power-strip outlet is always selectable for installation and, per child device, individually configured as either a plain **Outlet** (on/off control, plus current-power and today's-energy-usage telemetry when the connected device reports it) or a **Light**, via that child's own **Treat As Light** configuration item.
- Added full support for converting an already-installed plug or power-strip outlet child device between **Outlet** and **Light** in place, via the **Treat As Light** configuration item, without removing and re-adding the device. See the [Outlet / Plug / Power-Strip Support](README.md#outlet--plug--power-strip-support) section of the README for details.
- Added a packaged `UiDefinition.xml` and `Translations/en-US.json` for outlet entities, under `IncludeInPkg/outlet/`. Unlike `Light`, `Outlet` is a Crestron extension device type and therefore requires its own custom UI/translation assets; the layout mirrors the per-kind packaging convention used by other Entity Model V2 drivers.
- Each managed device kind (Light, Outlet) is now implemented as its own standalone entity, rather than sharing a single generic entity implementation.
- Documented a known Crestron Home **Configure Pro** limitation (not present in Setup): Configure Pro does not show the Installer Settings/configuration section for a device currently configured as a Light (including a plug/outlet converted to a Light), and can show a stale/incorrect device list after an Outlet â†’ Light â†’ Outlet conversion round-trip until the driver is reloaded. See the README for full details and the reload workaround.
- Fixed cached/reloaded child devices (e.g. KP303 strip outlets) not showing the Installer Settings section (Ready/Treat As Light/Reconfigure Driver) in Configure Pro after a driver reload, while devices that had been manually reconfigured since the reload (e.g. KP115) showed it correctly. The per-child `ConfigurationStepsDefinition` created in `CreateChildConfigurationController` never set `IncludePersistentValueData`, which defaults to `false`; without it, Configure Pro only recognizes a child controller as configured after the controller itself completes a live `ApplyConfiguration` round-trip through the host - the internal replay `PublishCachedChildControllers` performs after a reload (via a direct `GetFirstConfigurationStep`/`ApplyConfigurationStep` call, not a real Configure Pro session) is invisible to that tracking. Setting `IncludePersistentValueData = true` (and explicitly `IsNotOfflineConfigurable = false`) tells Configure Pro to honor the controller's persisted configuration values directly, so a recreated-from-cache child is recognized as already configured immediately after reload.
- Fixed a regression where devices restored from the on-disk managed-device cache on driver startup showed their serial number as the secondary info line in Configure Pro instead of manufacturer/model. The cache-seeding code path was missed when this was originally fixed for newly-discovered devices; it now passes `null` for the serial number here too, consistent with every other managed-device publication path.
- Fixed a bug where a previously-configured child device (e.g. a plug) could keep working correctly in the Room UI after a driver reload, yet never reappear in Configure Pro's Setup/Configure device list. The recreated child's configuration was being replayed back to Configured/Running *before* its controller was registered with the host via `UpdateSubControllers`, so the host never observed the NotConfigured -> Configured transition that Configure Pro's device list relies on. The replay now happens after host registration, matching the ordering used for newly-discovered devices.
- Fixed a bug where a power strip's individual child outlets (e.g. KP303 plugs) could show Offline/"Driver Not Loaded" in Configure Pro and the touchscreen UI after a driver reload. `PersistManagedDeviceCache` was rebuilding each cache entry's immutable fields without carrying over `ChildId`, so every cache rewrite silently dropped which physical child on the strip a given controllerId represented. On the next reload the recreated descriptor had `ChildId=null`, causing it to resolve its identity from the strip's own root alias instead of its own child alias/serial - which the host then treated as a mismatched/unrecognized device. `ChildId` is now preserved on every cache persist.
- Fixed the same strip-child-outlet identity bug for on-disk caches written *before* the fix above: those files still have `ChildId=null` baked in from prior rewrites, so simply stopping future drops was not enough to recover already-corrupted entries. `TryCreateCachedDescriptorAndConfiguration` now recovers a missing `ChildId` from the deterministic `device_{parentDeviceId}_{childId}` shape of a strip child's controllerId and persists the recovered value back to the cache, self-healing existing installs without requiring the cache file to be deleted manually.

## [1.2.0]

- Updated to `KasaTapoClient` 1.3.1, which negotiates device capabilities (e.g. brightness) directly from each device's SMART component list instead of relying solely on its device type.
- Fixed a discovery/classification bug so brightness-capable devices that don't classify as a `Dimmer` device type are still correctly published as dimmable lights: `P135` (a dimmable smart plug) and `KS240` (a dimmer wall switch/fan controller) are now always surfaced as `Dimmable`, regardless of the `Treat Plugs As Lights` setting.
- `WallSwitch`-classified devices are now always treated as supported light loads (previously gated in some code paths the same way plugs are); this release does not yet support treating a wall switch as a plain switched-outlet control.
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
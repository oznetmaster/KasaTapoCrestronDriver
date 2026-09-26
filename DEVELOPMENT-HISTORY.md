# Development and validation history

See the [product changelog](CHANGELOG.md) for shipped changes. This document preserves test, CI and build history. Dated development entries describe work at that time, not a published product version or completed acceptance. Version headings identify the release alongside which development work was recorded; processor-test versions identify separate test packages.

## Where changes belong

- Product changelog and product release notes: shipped behavior, API, compatibility, fixes and runtime dependencies. Mention validation briefly when it helps explain a fix.
- This history: test coverage, CI, build tooling, test-package releases and work on pending candidates. Split mixed entries so the product effect remains easy to find.
- Testing and workflow guides: current setup and operating instructions.
- Test-only or documentation-only changes do not require a product release. Processor-test releases update this history, not the product changelog.

<!-- development-history -->

## Power recorder discovery - 2026-09-26

- Retry a missing UDP discovery response up to three times before connecting, while rejecting ambiguous hosts immediately. Record discovery counts and the connection stage on failure without exposing credentials or raw device payloads. Device writes are not retried by this change.
- All 49 offline Android evidence contracts pass, including missing responses, conflicting identities and cancellation. This is fixture reliability work, not a driver runtime or product release.

## 2.1.2 preparation - 2026-09-26

- Regress standard and extension availability notifications across online/offline/recovery for outlets, sensors and buttons, including full SDK state snapshots. All 58 desktop lifecycle tests pass. An actual outlet interruption distinguished the extension field turning offline from the previously unchanged standard field; app behavior is verified separately after the correction.
- Add outlet refresh regressions that distinguish transport timeouts from caller cancellation, verify offline notifications and subsequent recovery, and preserve genuine cancellation. All 56 desktop lifecycle tests pass with the documented SDK dependency. Retain both extension and standard availability properties and an app capture when the power recorder's offline wait times out.
- Add an explicitly bound power-interruption recorder with original-state capture, separate device/API/app timestamps, at least 60 seconds of supply interruption, and restoration of the outlet and selected collateral lights. It does not turn a device-only power interruption or unreviewed app capture into a checklist pass.
- Check captured Room tile glyphs and state colours against reviewed icon crops, with bounded antialiasing tolerance and explicit failure for unreviewed icons or changed geometry. Offline checks reject wrong glyphs, on/off colours, blank images and changed sizes; the image decoder is Windows test-only. Existing presence-only evidence remains separate from the new icon observations.
- Search the app's retained Rooms-list scroll position before opening a selected room, and restore Home after each sensor inspection even when an assertion fails. This changes Android test navigation only.
- Cover separate discovery/child identities across cache rewrites and driver recreation, including legacy strip entries, unrelated hub child IDs and ambiguous sanitized IDs. All 54 desktop lifecycle tests pass; the Release package builds without warnings. Hardware verification of this identity correction remains separate.
- Exercise the actual saved-configuration callback with cached lights, outlets, sensors and buttons, both configured and unconfigured. The regression detects a startup publication bypass missed by tests that called the publication helper directly. All 47 desktop lifecycle and 34 portable net472 tests pass after restoring publication before the managed-device snapshot.

- Test the complete polling refresh-and-snapshot sequence using the actual SDK property-notification event, covering external colour/white changes, retained values, repeated snapshots and external power-off. The transport rejects device writes. An earlier isolated-refresh test omitted the snapshot; remove the speculative runtime change it prompted. These checks do not establish how Crestron Home applies the published colour mode.
- Allow Android fixtures to resolve selected child IDs from exact installed driver, room, model and physical-device identities. Native wrappers must identify the selected physical device; ambiguous matches fail. This option does not provision or move devices, or grant control permission.
- Add opt-in Android fixtures for temperature, motion and button displays, energy-capable and basic outlet controls, and individual native lighting controls. Private bindings select exact devices; shared processor/emulator reservations, independent device reads and verified restoration protect the configured installation. Hosted CI compiles these fixtures without operating hardware.
- Validate the preparatory app routes on the existing driver: three sensor pages, two outlet variants, and native power, brightness, white temperature, hue and saturation. Retain original failed attempts separately; the corrected slider interaction uses dragging rather than tapping. These checks prepare reusable fixtures and illustrations, rather than asserting a future package passed.
- Build the renamed Release package and verify exact illustrated help bytes, all 25 merged dependency identities and their reviewed notices. Check the merged JSON contracts and run 34 portable and 38 desktop lifecycle cases.
- Use the complete public ManifestUtil 29.0.10 package in hosted build/test jobs, including its desktop SDK dependency. Normalize package archive paths through a checksum-pinned public tool, preserving payload bytes and retaining validation reports.

## 2.1.1 validation - 2026-09-22

- Restore KasaTapoClient 2.0.0 from public NuGet into a fresh package folder and verify its published repository commit. Validate 34 net472 unit tests, 38 desktop SDK lifecycle tests and three read-only live entity tests. All 75 corresponding cases also pass on the processor; remove the temporary test instance and successful-run archive afterward.
- Add a persistence regression exercised against both the desktop SDK and the older SDK installed on the processor. Align the net472 test assembly with the client's existing test friend identity, matching the desktop harness, and update synthetic fixtures for the removed raw-JSON constructor parameter.
- Verify the final merged production assembly's property names, null omission and derived TPAP registration contract, plus absence of merged Newtonsoft.Json/log4net. Production Release packaging completes without warnings. All 18 driver-versioning checks and workflow-adapter discovery pass.
- Use NUnit 4.6.1, NUnit3TestAdapter 6.3.0, Microsoft.NET.Test.Sdk 18.10.1, NUnit.Analyzers 4.15.0 and CrestronHomeNUnit.TestAdapter 1.12.1. Exclude desktop runners from processor merging. Share attributed live configuration models and the read-only live fixtures with the desktop harness.
- NuGet audit reports no remaining direct stable updates, vulnerable packages or deprecated packages in the driver solution. Keep SDK, compatibility and test-tool transitive versions selected by their parent packages; these include framework facades and platform libraries that should not be upgraded independently. Keep the documented patched SSH.NET build. No preview packages are introduced.

## KasaTapoCrestronDriver.ProcessorTests v1.2.0 - 2026-09-15

Published processor test package on GitHub. This is a test-package release only; no driver or library NuGet package is published. See the matching package release notes for changes and validation.

## 2.1.0 - 2026-09-16

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

## 2.0.1 — 2026-09-14

- Test cached controller restoration, stable identity and publication through the SDK registry. Use isolated temporary cache files in fixtures and release Debug diagnostic listeners when disposing a platform driver.

- Standardize driver versioning: Debug project/package metadata follows the manifest including its build increment; local Release builds preserve it; three-part release tags select the exact CI release without another patch increment. Verify source and built package versions before publication.

- Expand driver coverage to 34 offline tests and 35 SDK entity/lifecycle tests, with a desktop SDK harness and the same lifecycle fixtures in the net472 processor package.

## [1.2.0]

- Added regression test coverage for the above capability-driven classification behavior.

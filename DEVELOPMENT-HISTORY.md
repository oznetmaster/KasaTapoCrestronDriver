# Development and validation history

See the [product changelog](CHANGELOG.md) for shipped changes. This document preserves test, CI, build and submission preparation history. Dated development entries describe work at that time, not a published product version or completed acceptance. Version headings identify the release alongside which development work was recorded; processor-test versions identify separate test packages.

## Where changes belong

- Product changelog and product release notes: shipped behavior, API, compatibility, fixes and runtime dependencies. Mention validation briefly when it helps explain a fix.
- This history: test coverage, CI, build tooling, test-package releases and work on pending candidates. Split mixed entries so the product effect remains easy to find.
- Testing and workflow guides: current setup and operating instructions. Submission guides, where applicable: preparation, evidence and acceptance status.
- Test-only or documentation-only changes do not require a product release. Processor-test releases update this history, not the product changelog.

<!-- development-history -->

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
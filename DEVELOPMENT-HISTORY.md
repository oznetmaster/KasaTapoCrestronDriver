# Development and validation history

See the [product changelog](CHANGELOG.md) for shipped changes. This document preserves test, CI and build history. Dated development entries describe work at that time, not a published product version or completed acceptance. Version headings identify the release alongside which development work was recorded; processor-test versions identify separate test packages.

## Where changes belong

- Product changelog and product release notes: shipped behavior, API, compatibility, fixes and runtime dependencies. Mention validation briefly when it helps explain a fix.
- This history: test coverage, CI, build tooling, test-package releases and work on pending candidates. Split mixed entries so the product effect remains easy to find.
- Testing and workflow guides: current setup and operating instructions.
- Test-only or documentation-only changes do not require a product release. Processor-test releases update this history, not the product changelog.

<!-- development-history -->

## Customer offline diagnostic build - 2026-10-25

- Reserve diagnostic Debug package `2.1.002.0004`. Build `2.1.002.0003` was prepared but never distributed and is retained as withdrawn in the version register. Both remain below the next normal public release `2.1.3` / package `2.1.003.0000`.
- Add a small Debug-only `ILoggerProvider` file sink at the existing Crestron logger boundary, retaining native Home logging. It captures startup/child diagnostics independently of Home's logging toggle, closes each write, bounds storage to two 2 MiB files, redacts configured credentials and authentication fields, and tolerates file failures. Release compilation excludes the provider and bridge; no client or general logging migration is included.
- Extend the Windows test utility with pinned-SFTP read-only diagnostic collection. It retrieves only the two fixed log files, bounds reads, preserves existing local files and removes incomplete downloads. This diagnostic distribution is for evidence collection only; provide a Release test build after reviewing the customer's results. Preserve the customer's existing child devices.

## Customer test distribution tooling - 2026-10-25

- Add a reusable single-executable Windows uploader containing a frozen driver package, identity, checksum and dependencies. It verifies the transferred bytes before submitting the package to the import directory. The customer updates the existing driver separately in Crestron Home Setup or Configure Pro; the utility has no active-driver update or reboot action and saves no credentials.
- Reserve customer-test package versions in `driver-distributions.json`, including package/source identity, distribution status and final download hashes. Public GitHub and NuGet releases keep three-part versions. Their release workflow rejects a package version at or below any reserved test version; after test `2.1.002.0002`, the next normal public release is at least `2.1.3` / package `2.1.003.0000`. Retain the register and guard when promoting branches.
- Publish the approved test download at `https://oznetmaster.github.io/downloads/test-drivers/kasatapo/2.1.002.0002/` (Pages commit `cb024c06f8b7bc33e89af36c1d586432d9ac7602`). Independently download all six public files and confirm exact byte identity, ZIP contents, embedded uploader checksum and package identity/version. Mark the distribution register as distributed. The cover letter stays local and unsent. Upload tooling is checked with an isolated loopback SFTP server, not an active-driver update. The existing unrelated SSH.NET README edit in the original checkout is excluded.

## HS220 dimmer power candidate - 2026-10-25

- After customer feedback reported every dimmer/switch offline with the platform online, test the exact frozen package as an upgrade of lab instance 52677 from `2.1.002.0000`, preserving the existing demo tree. After initial startup, all eight child entities and the platform pass readiness; a separate tree reload also recovers. IDs, room placement, command definitions, advertised configuration items and cache bytes are preserved. All nine extracted payload files match the candidate. Native L530/KP115 off/on and independent restoration checks pass; processor reservations are released. The persistent customer condition is not reproduced, and physical HS220/KS225 validation remains outstanding. Obtain the customer's startup log and Home firmware version before attributing a cause or asking for child removal. Preserve the original probe's pre-control discovery/authenticated-ID mismatch refusal separately from the corrected successful check.
- Prepare unpublished candidate driver `2.1.002.0002` for customer validation on HS220 hardware 3.26, firmware 1.1.1. Devices classified as `Dimmer` (including SMART HS220 and KS225) use KasaTapoClient's existing general power API; bulbs, strips and other device types retain their existing power calls. Crestron Home's zero/full-level contract and brightness handling are unchanged. The client dependency remains 2.0.1.
- Preserve the existing native dimmer command mapping: zero turns off, full level from off restores retained brightness, and full level while on sets 100%. The user accepted retaining this behavior because the native dimmer integration conveys its on gesture as a full-level command. Other positive levels from off also retain the existing power-on behavior.
- Add synthetic SMART regressions using the captured HS220 3.26 and KS225 component lists for the failed power route, actual `lightDimmer:setLevel` off/on/full-brightness dispatch, retained brightness, and slider power handling. Preserve explicit legacy bulb, strip and on/off wire-command checks. The power regression fails against the preceding driver with `does not support light-state control`. Final validation passes 103 local tests (34 portable, 69 lifecycle), the same 103 on the processor, and four live tests. The demo KL130 off/on/restoration test retains its 46% brightness and original color state; an earlier demo L530 check also passes. Preserve the earlier failed live run separately: its read-only plug fixture was unavailable, and the corrected plan selects the discovered demo KP115.
- Build the Release candidate with zero warnings/errors, validate merged JSON and package notices, and normalize archive paths while preserving payload bytes. Physical HS220 and KS225 validation remains pending. This entry does not record a published release or completed hardware acceptance; the candidate platform package itself has not been installed by these processor-host tests. Upgrade the processor workflow to adapter 3.0.0 (with its DevTools 1.27.0 dependency), and pin the processor SDK to its published source revision. The private validation plan selects the user-requested Office room by name; test-instance cleanup and lease release pass.

## NUnit 5 test tooling - 2026-09-28

- Move the Windows, lifecycle and Android fixtures to NUnit 5.0.0 and the workflow adapter to CrestronHomeNUnit.TestAdapter 2.0.0. Pin the processor host to the released CrestronHomeNUnit 2.0.0 source revision. Await asynchronous assertions using NUnit 5's API.
- Local validation passed 34 ordinary tests, 58 lifecycle tests and 70 offline Android evidence contracts. The merged processor package builds and discovers 95 cases, including three live cases that were not executed by these local checks. Processor execution and integrated Android validation remain separate checks.
- Restore the test-release desktop SDK dependency from the public Crestron ManifestUtil NuGet package with its existing checksum, removing the private secret requirement. No product version or driver runtime code changes.

## Published client recovery dependency - 2026-09-26

- Update the driver and its test/support projects to the published KasaTapoClient 2.0.1 package. Refresh the exact dependency DLL digest, license reference and packaged third-party notices.
- With that published dependency, all 34 portable tests, 58 lifecycle tests and 56 offline Android contracts pass. The strict Release build and package/dependency-notice validation also pass. These checks do not replace the integrated hardware and app run.

## Power recovery app verification - 2026-09-26

- The selected P110 power interruption now recovers with the corrected TPAP client: retained Room captures show OFFLINE followed by the independently verified original OFF state. The preparation run interrupted the device supply for more than 60 seconds and restored the supply, plug, collateral light and app navigation. Processor and network equipment remained powered; this is not a whole-system outage test.
- Require the selected unobscured Room tile to show OFFLINE and then the independently read ON/OFF state. Emit separate scoped observations only after physical restoration, with retained trigger/capture references and a 15-second app-capture bound measured from the first matching driver API observation. Keep physical power loss and driver detection timestamps separate.
- All 56 offline Android contracts pass. Replaying the retained actual before/offline/recovery captures verifies the new status assertions and rejects the opposite state without operating hardware again. The full integrated run remains separate from this preparation evidence.

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

## 2026-09-26 — NuGet documentation packaging

Audited the downloaded public NuGet archive rather than relying on the GitHub release page. Package README links now use absolute repository URLs where needed. Release packaging requires nonempty NuGet release-notes metadata and checks the finished archive for missing README content, relative links and malformed URLs before publication. Driver manual releases fall back to the checked-in release notes.

Validated newly packed local archives; the original defective published archives fail the new gate. This changes documentation and packaging only. These validation archives were not published and existing NuGet versions remain unchanged. Runtime/hardware tests were not repeated for this metadata change.

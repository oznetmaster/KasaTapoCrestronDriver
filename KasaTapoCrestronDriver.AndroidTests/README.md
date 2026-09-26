# Crestron Home app tests

These optional .NET 10 NUnit fixtures exercise the installed Kasa/Tapo driver through an Android emulator. They are separate from the processor's net472 test assembly and do not run against household devices during ordinary hosted tests.

Build with the .NET 10 SDK and the public package references in this project. Invoke through `InstalledDriverTests.RunAsync` in `CrestronHomeNUnit.Workflow`, or the public DevTools automation workflow's installed-app stage. Those callers verify the installed package and reserve both the processor and emulator. The fixture refuses ordinary standalone execution without that context.

Build from the repository checkout: the fixture embeds the adjacent driver JSON as its expected configuration definition. Preserve the repository's build-output exclusions when preparing an isolated checkout; generated `bin`/`obj` files must not become source changes during a run.

Outlet fixtures retain two `command-response` journal records per case: the first
individual action and its return. A monotonic clock measures from completion of
the input page guard to observation of the exact attributed command completion.
The records separate input transport duration from total observed completion
duration and identify the control, requested state and driver command epoch/count.
They add no device commands. Compare matching controls and requested states across
initial and later runs; retain the original records from both phases. These are
observer upper bounds including ADB transport and API polling, not exact relay
latency or app-feedback latency. They do not automatically assert unchanged
response time, establish a numeric acceptance threshold or turn a shortened run
into a full endurance test. Physical restoration and app-feedback checks remain
independent of these measurements.

Provide a private `app-fixture-settings.json` beside the stage directory. For DevTools automation, use the documented `InstalledAppFixtureSettings` object; the coordinator freezes and stages that input. Do not commit device IDs, credential files or deployment selections.

The settings contain:

- `ProcessorHost` and an absolute `CredentialBindings` path for the public encrypted processor credential store.
- `Sensors`: exactly three selected display bindings, with aliases `temperature`, `motion` and `button`. Each supplies `DeviceId`, `Model`, `Name`, `Room`, `LocationId` and `DisplayProperties`. Use the properties named in `SensorPagesTests.cs`. These cases only read displayed telemetry.
- Optional `DeviceCredentialsFile`: the restricted Kasa live-test JSON containing `credentials.userName` and `credentials.password`. The fixture uses these only for independent physical observations and explicitly documented light restoration.
- Optional `Outlets`: bindings with aliases `energy` and `basic`, selected IDs/model/name/room, `DiscoveryId`, `AuthenticatedId`, optional `ChildId`, `EnergyPage` and `ControlsAuthorized: true`. Authorization applies only to those selected devices. Discovery IDs and authenticated IDs can differ on Tapo devices.
- Optional `Light`: the selected native load's `DeviceId`, its platform child `WrapperId`, model/name/room/location, discovery and authenticated IDs, and `ControlsAuthorized: true`.
- Optional `PlatformConfigurationAuthorized: true`: permits the persistence test to change and restore this installed platform's discovery timeout. It does not authorize changing settings on the physical devices.
- Optional `ResolveDeviceIds: true`: resolves fresh installed IDs beneath the workflow's verified candidate platform, using the exact configured child model, name and room. Outlet physical/child identities and the native light wrapper's managed-device identity must also match the selected hardware. Missing or ambiguous matches stop before app input; control authorization is preserved. The fixture retains a credential-free `AndroidUI/resolved-targets.json`. This only resolves devices already installed and assigned to rooms: it does not commission children or update a separate removal plan. Without this option, explicit IDs remain in use.
- Optional `EvidenceIdentity`: `packageSha256`, `sourceCommit`, `policySha256` and `templateSha256` for a consuming evidence policy. The package and commit must match the workflow's release context. Without this field the normal NUnit results and captures are still produced.

Missing control bindings skip the corresponding cases; a run containing skips is not evidence that all app controls passed. Include only devices whose operation is authorized, and require the complete expected test inventory for a full control run. Device names alone do not grant permission.

Outlet cases capture the unobscured Room tile and press its title to exercise the default action, verifying the physical change and Room-tile feedback. For the energy variant, the ellipsis then opens the detail page, where the Power control returns the outlet to its original state; the basic variant uses its tile again. Both send three consecutive presses without waiting for device acknowledgements between them. The burst uses the exact control coordinates from a guarded page and retains its actual start/end times; it does not claim a fixed tap rate. Cases compare app feedback, driver properties and fresh independent device observations, require the exact completed-command count and restore the original state with an absolute driver command. The energy page requires all six labelled numeric readings and explicitly closes back to the Room; it does not compare unsynchronized telemetry for exact equality. A retained journal records the original state before input. Unconfirmed restoration keeps the workflow reservations for review.

Additional outlet observation suffixes are `outlet.<alias>.tile-action` and `outlet.energy.navigation`, `.display` and `.close`, with the original and restored physical observations attached. These are separate assertions from the same controlled sequence, not extra device operations. Tile checks verify icon presence, not the glyph's correctness; no quantified response deadline is asserted.

`outlet.<alias>.tile-inventory` and `.room-feedback` expose the tile inventory and
unobscured Room subtitle change separately. The feedback journal records the input
intent and completion of the matching UI capture, after independent physical
verification. This is an observation interval, not a measurement of the first
visible response. Sensor cases similarly expose `sensor.<alias>.inventory` for
the selected tile and its conditional display rows. These outputs reuse the same
tested sequence and add no physical commands. Unsupported sensor types and icon
glyph correctness still require separate evidence.

The native-light case uses only the selected load's controls, never room-wide controls or scenes. Independent physical reads verify power, brightness, tunable-white temperature, hue and saturation changes. The tuning check currently targets a full-color bulb with a 2500–6500 K white range; select matching hardware. Direct Kasa calls are used only to restore the captured light state; they do not count as evidence that driver controls worked. Native tuning-page captures retain the displayed white and color controls alongside the physical observations.

The native sequence also checks the displayed power state and brightness, moves
brightness upward and downward, then sends three consecutive drags without waiting
for device acknowledgements between them. It retains their actual input times and
checks the final physical, driver and displayed brightness. White/color selectors
must report the selected tab; the corresponding page titles and values are checked.
Tuning gestures require settled observed control geometry and a target separated
from the current displayed value, followed by visible movement and independent
physical feedback. Each tuning adjustment retains before/after captures. Returning
from tuning must reach the Lights list, then the selected Room and finally Home.

Optional native observation suffixes are `native-light.slider`, `.buttons`,
`.selectors` and `.subpages`. They are emitted only when the whole native sequence
passes and restores the original physical state. They do not assert room-wide
controls, selector-icon artwork or a quantified response deadline.

The fixtures return the app to Home. On failure, inspect the original NUnit result, capture and restoration journal before recovery. Do not release retained reservations or repeat uncertain inputs automatically.

With `EvidenceIdentity`, a successful outlet sequence also saves
`outlet-<alias>/response-measurements.json` after physical restoration and return
Home. It follows the public DevTools `SubmissionResponseSeries` contract, without
requiring a newer runtime package: candidate identity, physical device/child ID,
measurement method, and the two existing timed commands. IDs distinguish control
and requested ON/OFF state. This adds no commands. A later comparison requires
matching operations and separate reviewed criteria; merely collecting timings
does not assert unchanged performance. Measurements include ADB/API observation
overhead and do not measure physical relay or first visible UI latency.

Sensor cases first scroll the selected Room tile fully into view and capture its title, icon presence and absence of an ellipsis. They press that read-only tile, verify the expected detail page and every conditional display row for the selected hardware, then explicitly close to the same Room and return Home. Separate observation suffixes `sensor.<alias>.tile`, `.navigation`, `.display` and `.close` allow policies to use those actual assertions independently while sharing the retained captures. Icon glyph correctness, Home-page absence, physical sensor stimulation and sensor types absent from the selected hardware are not asserted by these checks.

The optional configuration case changes only the platform's discovery timeout through the public configuration API, reopens configuration and checks the saved value, unchanged polling settings and selected child readiness, then restores and verifies the original settings. It uses 11 seconds when the original value is 10, or 10 seconds otherwise; supported original values are 1–60 seconds. This produces `kasa.app.configuration.platform` with target `$kasa.configuration.platform` and method `configuration`. It does not claim Configure Pro visual validation, persistence across a reboot or changes to physical device settings. Restoration failure retains the workflow reservation. Keeping the setting on the selected processor also avoids competing with other processors that may manage the same physical devices.

`ConfigurationInventoryTests` adds four read-only API cases: catalogue identity, the cloud/account connection fields, platform and selected child settings schemas, and installation/room placement. These require the three sensor bindings, both outlet variants and the native light binding, but send no configuration or physical commands. The schema check compares public IDs, titles, descriptions, types, masking, persistence and writability. It retains the processor's reported `Required` flags after configuration without equating them to initial-wizard validation. Credentials and internal driver data-store values are excluded from reports. The structured observation suffixes are `configuration.catalogue`, `configuration.connection`, `configuration.attributes` and `configuration.installation`, each with method `configuration` and its corresponding `$kasa.` target. They do not claim Configure Pro visual verification or Crestron acceptance.

With `EvidenceIdentity`, successful cases also write structured observations with fixed IDs: `kasa.app.sensor.temperature`, `kasa.app.sensor.motion`, `kasa.app.sensor.button`, `kasa.app.outlet.energy`, `kasa.app.outlet.basic` and `kasa.app.native-light`. Their execution targets are the corresponding `$kasa.` scopes, with method `android`. Each records exactly the assertions described above and hashes the retained capture/journal files. Control observations include the original and verified restored physical states. Failed or skipped cases do not produce passing observations. These are scoped test results, not a declaration that every requirement of a consuming policy has passed; offline transitions, response deadlines, configuration and endurance need separate evidence.

# Crestron Home app tests

These optional .NET 10 NUnit fixtures exercise the installed Kasa/Tapo driver through an Android emulator. They are separate from the processor's net472 test assembly and do not run against household devices during ordinary hosted tests.

Build with the .NET 10 SDK and the public package references in this project. Invoke through `InstalledDriverTests.RunAsync` in `CrestronHomeNUnit.Workflow`, or the public DevTools automation workflow's installed-app stage. Those callers verify the installed package and reserve both the processor and emulator. The fixture refuses ordinary standalone execution without that context.

Build from the repository checkout: the fixture embeds the adjacent driver JSON as its expected configuration definition. Preserve the repository's build-output exclusions when preparing an isolated checkout; generated `bin`/`obj` files must not become source changes during a run.

## Physical sensor feedback recordings

`SensorEventTests.PhysicalEventReachesVisibleDetailPage` and
`PhysicalEventReachesVisibleRoomTile` each have four **explicit** cases:
`button`, `motion`, `contact` and `leak`. Select one exact case through the
same public installed-driver Android workflow, with a person available. Ordinary
unattended suites do not run these cases. The existing three sensor bindings stay
required; optional `contact` and `leak` bindings identify additional selected
children. They must match the verified candidate, model, name and room. This
fixture only reads sensor state and navigates the app; it sends no
device commands and does not add sensors or alter polling.

Begin with the contact closed, leak sensor dry, or no motion. Wait for
`sensor-event.<alias>.<page|tile>/ready.json` (also announced in NUnit progress), then trigger
the selected sensor once without navigating the app. After `restore-request.json`,
close the contact, dry the sensor or leave the detection area. Each physical wait
is bounded to five minutes. The app has a separate 30-second observation timeout;
this is a recorder limit, **not a certification timing requirement**. Button events
require an increasing driver event timestamp and a distinguishable display value
in the selected view (choose a different gesture from the baseline);
old gestures alone cannot pass. Buttons have no persistent state to restore.

Evidence retains baseline/event/recovery screenshots and hierarchies, selected API
values, timestamps, final state, and candidate/test-assembly provenance. Intermediate
polls stay in memory. Failures retain a failure record and attempt Home restoration;
they never produce `complete.json`. Timing starts after the driver API observation,
so it cannot establish physical-event-to-app latency. The tile mode keeps the
unobscured Room tile visible throughout; page results cannot substitute for tile
feedback. Neither mode proves icon glyph correctness. Raw
recordings are limited to the assertions above; review their scope before using
them as evidence for any broader checklist. Use a fresh
workflow evidence directory for every attempt. Pin the later fixture separately
from the unchanged driver package, and never edit an active endurance run to add it.

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
- `Sensors`: the three required display bindings `temperature`, `motion` and `button`, plus optional `contact` and `leak` bindings. Each supplies `DeviceId`, `Model`, `Name`, `Room`, `LocationId` and `DisplayProperties`. Use the properties named in `SensorPagesTests.cs`. These cases only read displayed telemetry.
- Optional `DeviceCredentialsFile`: the restricted Kasa live-test JSON containing `credentials.userName` and `credentials.password`. The fixture uses these only for independent physical observations and explicitly documented light restoration.
- Optional `Outlets`: bindings with aliases `energy` and `basic`, selected IDs/model/name/room, `DiscoveryId`, `AuthenticatedId`, optional `ChildId`, `EnergyPage` and `ControlsAuthorized: true`. Authorization applies only to those selected devices. Discovery IDs and authenticated IDs can differ on Tapo devices.
- Optional `Light`: the selected native load's `DeviceId`, its platform child `WrapperId`, model/name/room/location, discovery and authenticated IDs, and `ControlsAuthorized: true`.
- Optional `PlatformConfigurationAuthorized: true`: permits the persistence test to change and restore this installed platform's discovery timeout. It does not authorize changing settings on the physical devices.
- Optional `ResolveDeviceIds: true`: resolves fresh installed IDs beneath the workflow's verified candidate platform, using the exact configured child model, name and room. Outlet physical/child identities and the native light wrapper's managed-device identity must also match the selected hardware. Missing or ambiguous matches stop before app input; control authorization is preserved. The fixture retains a credential-free `AndroidUI/resolved-targets.json`. This only resolves devices already installed and assigned to rooms: it does not commission children or update a separate removal plan. Without this option, explicit IDs remain in use.
- Optional `EvidenceIdentity`: `packageSha256`, `sourceCommit`, `policySha256` and `templateSha256` for a consuming evidence policy. The package and commit must match the workflow's release context. Without this field the normal NUnit results and captures are still produced.

Missing control bindings skip the corresponding cases; a run containing skips is not evidence that all app controls passed. Include only devices whose operation is authorized, and require the complete expected test inventory for a full control run. Device names alone do not grant permission.

`SensorPagesTests.RoomSensorValuesMatchInstalledDriver("contact")` and `("leak")`
are explicit optional cases. Select their exact names when the corresponding
hardware is installed. They require the advertised contact/leak capability and
check every conditional row, including Battery when supported. Supply the complete
`DisplayProperties` list; a subset cannot pass. Tile, icon, navigation and Home
restoration checks remain the same as for existing sensor cases. Unknown icon
states still require visual reference review. A static page check does not prove
a new physical event; use the separate physical-event cases for that.

Outlet cases capture the unobscured Room tile and press its title to exercise the default action, verifying the physical change and Room-tile feedback. For the energy variant, the ellipsis then opens the detail page, where the Power control returns the outlet to its original state; the basic variant uses its tile again. Both send three consecutive presses without waiting for device acknowledgements between them. The burst uses the exact control coordinates from a guarded page and retains its actual start/end times; it does not claim a fixed tap rate. Cases compare app feedback, driver properties and fresh independent device observations, require the exact completed-command count and restore the original state with an absolute driver command. The energy page requires all six labelled numeric readings and explicitly closes back to the Room; it does not compare unsynchronized telemetry for exact equality. A retained journal records the original state before input. Unconfirmed restoration keeps the workflow reservations for review.

Additional outlet observation suffixes are `outlet.<alias>.tile-action` and `outlet.energy.navigation`, `.display` and `.close`, with the original and restored physical observations attached. These are separate assertions from the same controlled sequence, not extra device operations. No quantified response deadline is asserted.

`outlet.<alias>.presentation` and `sensor.<alias>.presentation` verify the title, variant-specific ellipsis, glyph and state colour in the initial unobscured tile capture against [reviewed icon references](IconReferences/README.md). The outlet icon expectation comes from independently observed power; the sensor expectation comes from its advertised icon property. Unknown icon states and changed geometry fail for review. These checks use the already captured screenshot, add no device commands, and preserve the older presence-only observation scopes. They do not establish every conditional icon state or sensor feedback timing.

`outlet.<alias>.tile-inventory` and `.room-feedback` expose the tile inventory and
unobscured Room subtitle change separately. The feedback journal records the input
intent and completion of the matching UI capture, after independent physical
verification. This is an observation interval, not a measurement of the first
visible response. Sensor cases similarly expose `sensor.<alias>.inventory` for
the selected tile and its conditional display rows. These outputs reuse the same
tested sequence and add no physical commands. Unsupported sensor types and icon
glyph correctness are not asserted by those inventory outputs; use the separate `.presentation` output for the reviewed captured state.

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

The optional `PowerInterruptionTests.RecordDevicePowerInterruptionAndRestore` is a preparatory recorder, selected separately from normal controls. Private `PowerInterruption` settings identify `OutletAlias`, an independently controllable `Supply` strip socket, and any `CollateralLights` sharing the possibly interrupted supply. Every physical binding has `DiscoveryId`, `AuthenticatedId`, `ChildId` (null for lights) and explicit `ControlsAuthorized`. The selected outlet must be an authorized root plug. Pin the wiring before unattended use; a supply that powers another device causes the expected outlet-offline check to fail.

The recorder saves original states, records the supply-off intent before switching, confirms at least 60 seconds without supply power, observes the selected driver child disconnect/reconnect and captures its unobscured Room tile before, during and after. It restores the supply, outlet and explicitly selected collateral light states even after a failed observation; unresolved restoration retains reservations. Active lighting effects or incomplete colour state are rejected before the interruption because their restoration is not covered here. Restoration attempts are independent, so failure restoring one device does not skip the others.

This recorder produces no passing checklist observations: app offline appearance and timing require review of the captures, and interruption of a selected device's supply does not establish a processor/network power outage or a network-only test. Its journal, original/restored states and scope report keep these distinctions explicit. Do not select it automatically merely because a power strip was discovered.

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

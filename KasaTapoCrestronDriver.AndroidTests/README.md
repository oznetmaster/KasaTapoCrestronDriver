# Crestron Home app tests

These optional .NET 10 NUnit fixtures exercise the installed Kasa/Tapo driver through an Android emulator. They are separate from the processor's net472 test assembly and do not run against household devices during ordinary hosted tests.

Build with the .NET 10 SDK and the public package references in this project. Invoke through `InstalledDriverTests.RunAsync` in `CrestronHomeNUnit.Workflow`, or the public DevTools automation workflow's installed-app stage. Those callers verify the installed package and reserve both the processor and emulator. The fixture refuses ordinary standalone execution without that context.

Provide a private `app-fixture-settings.json` beside the stage directory. For DevTools automation, use the documented `InstalledAppFixtureSettings` object; the coordinator freezes and stages that input. Do not commit device IDs, credential files or deployment selections.

The settings contain:

- `ProcessorHost` and an absolute `CredentialBindings` path for the public encrypted processor credential store.
- `Sensors`: exactly three selected display bindings, with aliases `temperature`, `motion` and `button`. Each supplies `DeviceId`, `Model`, `Name`, `Room`, `LocationId` and `DisplayProperties`. Use the properties named in `SensorPagesTests.cs`. These cases only read displayed telemetry.
- Optional `DeviceCredentialsFile`: the restricted Kasa live-test JSON containing `credentials.userName` and `credentials.password`. The fixture uses these only for independent physical observations and explicitly documented light restoration.
- Optional `Outlets`: bindings with aliases `energy` and `basic`, selected IDs/model/name/room, `DiscoveryId`, `AuthenticatedId`, optional `ChildId`, `EnergyPage` and `ControlsAuthorized: true`. Authorization applies only to those selected devices. Discovery IDs and authenticated IDs can differ on Tapo devices.
- Optional `Light`: the selected native load's `DeviceId`, its platform child `WrapperId`, model/name/room/location, discovery and authenticated IDs, and `ControlsAuthorized: true`.

Missing control bindings skip the corresponding cases; a run containing skips is not evidence that all app controls passed. Include only devices whose operation is authorized, and require the complete expected test inventory for a full control run. Device names alone do not grant permission.

Outlet cases press the app's individual Power control or basic outlet tile. They compare app feedback, driver properties and fresh independent device observations, track command completion and restore the original state with an absolute driver command. A retained journal records the original state before input. Unconfirmed restoration keeps the workflow reservations for review.

The native-light case uses only the selected load's controls, never room-wide controls or scenes. Independent physical reads verify power, brightness, tunable-white temperature, hue and saturation changes. The tuning check currently targets a full-color bulb with a 2500–6500 K white range; select matching hardware. Direct Kasa calls are used only to restore the captured light state; they do not count as evidence that driver controls worked. Native tuning-page captures retain the displayed white and color controls alongside the physical observations.

The fixtures return the app to Home. On failure, inspect the original NUnit result, capture and restoration journal before recovery. Do not release retained reservations or repeat uncertain inputs automatically.

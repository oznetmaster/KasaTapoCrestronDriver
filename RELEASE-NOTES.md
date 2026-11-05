# KasaTapoCrestronDriver 2.1.14

Patch release for excessive outlet property notifications, confirmed event timing and reliable startup state reporting.

## Changes

- Stop replaying the full outlet snapshot after every scheduled poll, shared-parent update and completed command. Polling, real energy/state/availability changes and confirmed On/Off events remain active. Startup and lifecycle reporting still supply complete state.
- Suppress routine driver informational/debug diagnostics in Release builds, including registration, shared-parent polling and processor-baseline traces. Warning/error reporting remains enabled. Crestron Home can still log genuine property changes through its own diagnostics.
- Publish light/dimmer feedback after device readback and publish complete outlet/sensor state, status, icon and availability before corresponding events. Failed or ignored commands do not invent confirmed changes. Unchanged polls do not repeat light notifications or transition events.
- Announce confirmed initial state values and their matching events once. Extension events wait for confirmed state, the child's Running lifecycle and Home's first state read. Existing event programming may execute at startup/reload; off and inactive/normal states are included. Historical button gestures remain suppressed.
- Correct processor light-baseline percentage conversion and readiness ordering, preserving stored brightness and active colour/white mode. An off dimmer reports 0% while retaining its physical brightness setting.
- Preserve child identities, placements and programmable property/event/command IDs. Dependencies remain KasaTapoClient 2.0.1 and DevKit 29.0.10.

## Outlet feedback and logging

Keep existing **Outlet Turned On / Outlet Turned Off** programming connected to third-party tiles' state/display feedback actions. Those actions should update the display without sending another command to the plug. The driver does not automatically connect unrelated tiles.

Keep **Enable Light Polling** enabled for Kasa/Tapo-app or physical-control changes. Outlets use **Light Poll Interval (Seconds)**; an existing 10-second interval can be retained. The driver logging option does not control Home's own property-processing logs. This fix removes redundant notifications; it does not promise an empty Home log or suppress real telemetry changes.

## Validation

The notification changes passed 186 workstation checks, 186 processor checks and nine processor live-device checks. Live plug/strip tests verify confirmed command/external-change feedback and quiet unchanged properties. Final logging-only changes passed another 152 Release lifecycle checks. The exact final package was installed on the lab processor, with all 37 existing identities/placements preserved, and passed isolated Home-programming startup witnesses for Outlet On and Outlet Off. Temporary programming/state and polling settings were restored, leases released, and Demo bulbs read back off.

Earlier light-baseline checks cover white/colour and on/off states. Installed startup witnesses cover driver-tree reloads on Home 4.12.194; a full Home/processor restart was not performed. The lab application log did not append during a bounded observation, so no measured percentage or byte reduction is claimed. The customer's original screenshot identifies the repeated Home property pipeline; confirmation of final log volume on that customer's system remains outstanding.

## Installation and upgrade

GitHub and NuGet use **2.1.14**; the included Crestron driver is **2.1.014.0000** (Home may display **2.001.0014.0**). Import/upload the package, then open the existing **KasaTapoPlatform** driver in Home Setup or Configure Pro and use **Check for Driver Update**. Uploading alone does not update the running driver. Confirm the installed version afterwards. Retain existing children, polling settings and feedback programming.

The Windows ZIP contains a single-file uploader with the identical driver package embedded. Extract it and run it from a Windows PC that can reach the processor; use the processor login. It requires .NET Framework 4.7.2 or later. It does not save processor credentials or reboot the processor.

The NuGet package is a driver distribution wrapper containing the same `.pkg` and community manifest, not a DLL-reference library. Public artifacts contain no Crestron submission PDF or redundant documentation ZIP. Repository documentation and source archives remain available separately. Crestron catalogue publication is separate from this GitHub/NuGet release.

Support: [contact form](https://oznetmaster.github.io/support/). Source and history: [KasaTapoCrestronDriver](https://github.com/oznetmaster/KasaTapoCrestronDriver).

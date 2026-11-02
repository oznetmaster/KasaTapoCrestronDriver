# KasaTapoCrestronDriver 2.1.5

Fix plain-outlet feedback so Crestron Home programming receives confirmed state transitions, including changes made outside Home and detected by polling.

## Changes

- Raise `outletTurnedOn` / `outletTurnedOff` only after device readback confirms a transition. Publish `outletIsOn`, status and icon before the event.
- Detect external state changes through the same polling path. Repeated commands, unchanged polls and acknowledgements without a state change do not repeat transition events.
- Keep the last confirmed state when commands or readbacks fail. A later successful read reports a changed state once. The first successful read establishes the initial state without an on/off event.
- Serialize outlet commands and resolve queued toggles after preceding readback. A retry retains its original target if an acknowledgement or readback is lost.
- Update the README, illustrated help and third-party tile feedback instructions. Preserve existing child identities, property/event/command IDs, light/dimmer paths and dependencies: KasaTapoClient 2.0.1 and DevKit 29.0.10.

## Feedback programming

The driver's own outlet UI uses its state directly. Unrelated tiles need feedback programming: connect the outlet's **Outlet Turned On** and **Outlet Turned Off** events to that tile's on/off state-display actions, where supported. These actions must not command the plug again. The tile driver determines action names and startup synchronization options; publishing `outletIsOn` does not automatically connect other tiles.

Enable **Enable Light Polling** for Kasa/Tapo-app and physical-control changes. Plain outlets share **Light Poll Interval (Seconds)**, whose default is 15 seconds. Feedback follows a successful poll. Initial state is published without replaying transition events.

## Validation

The customer confirmed test driver 2.1.004.0001 working on a CP4-R with 10-second polling, both Ultamation and Control Programming LLC toggle tiles, and event-to-tile feedback programming. Home control and feedback for Kasa-app changes worked. The plug model and a physical-button test were not reported. This is customer-reported hardware evidence, separate from our automated SDK and processor validation.

## Installation and upgrade

GitHub and NuGet use **2.1.5**; the included Crestron driver is **2.1.005.0000**, higher than all registered customer-test allocations, including 2.1.004.0001. Import the package, then update the existing KasaTapoPlatform driver through Home Setup or Configure Pro. Importing alone does not update the running driver. Removing configured children is not a routine upgrade step.

The NuGet package is a driver distribution wrapper containing the final `.pkg` and community `crestron-driver-package.json` manifest, not a DLL-reference library. GitHub's automatic source archives are not installable driver packages.

Support: [contact form](https://oznetmaster.github.io/support/). Source and history: [KasaTapoCrestronDriver](https://github.com/oznetmaster/KasaTapoCrestronDriver).

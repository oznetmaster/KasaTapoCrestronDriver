# Kasa/Tapo outlet-feedback customer test

Driver version: **2.1.004.0001** (Home may display **2.001.0004.1**).
Date: 2 November 2026. This is a customer test build, not a new public GitHub/NuGet release.

- Outlet state, status and icon follow device readback rather than command intent.
- `outletTurnedOn` / `outletTurnedOff` fire once per confirmed transition, including changes detected through polling after Kasa/Tapo-app or physical-button operation.
- Unchanged commands and polls do not repeat events. Failed commands or failed readbacks do not publish an unconfirmed new state. A subsequent successful read can report a transition from the last confirmed state.
- The first successful read publishes initial state without firing a transition event. Third-party tiles need their own initial-state synchronization and event-to-feedback programming.
- Enable **Enable Light Polling** for external changes; plain outlets share **Light Poll Interval (Seconds)**, whose default is 15 seconds. Detection requires a successful poll.
- Existing child identities, property/event/command IDs, and light/dimmer control paths are retained. Update the existing platform driver after uploading; removing configured devices is not a routine update step.

The customer download contains the Windows uploader with this exact package embedded, the standalone package, instructions and dependency notices. The uploader imports the package; it does not update the running driver automatically.

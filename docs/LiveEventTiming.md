# Live event timing tests

The live fixtures run the driver entities with the real Crestron SDK on Windows and inside
the net472 processor test host. They observe SDK property notifications and driver events.
They do not install event programming or certify third-party tile configuration.

## Private inputs

Use the existing private `LiveTestSettings.json`, with `enabled: true`, account credentials
and explicit `devices` entries. Keep it and all raw results outside tracked source.

- `light`: the selected Demo legacy colour bulb.
- `lightSmart`: the selected Demo SMART colour bulb.
- `plug`: the selected Demo outlet.
- `strip`: the selected strip discovery identity and one `childDeviceId`.
- `hub`: the selected hub discovery identity for read-only sensor/button observations.

Each role accepts a stable `deviceId` or a unique discovery alias. SMART discovery may omit
the alias even when authenticated device information contains it; use the verified discovery
ID in that case. `LiveInventory_ReportsConfiguredTargets` performs read-only identity checks.

`allowDemoPowerControl: true` explicitly enables control cases. A controlled standalone
device, or the specifically selected strip socket, must currently have Demo in its name.
Do not rename a device to bypass this guard. Outlet tests restore their original power;
bulb timing tests leave the selected bulbs off. These fixtures do not change other sockets.

## Automated timing checks

`DemoOutlet_EventsFollowActualReadbackAndPropertyPublication` and
`DemoLight_FeedbackWaitsForActualReadback` forward real network traffic through a recording
transport. The transport can withhold an actual device response before returning it to the
client. An independent connection verifies that the physical device has changed while the
entity still retains its previous feedback. No new outlet event or light level notification
may appear until the readback is released.

Outlet cases also check state/status/icon publication before the transition event, no
repetition on unchanged polls, and an external change made through the independent client.
The trace uses sequence numbers and operation names; it does not log protocol payloads.

Run the existing `kasatapodriver-live` manual processor suite with the private input file.
It contains nine cases, including the four timing cases, read-only checks, inventory and
the existing retained-brightness power test. Its filter excludes physical-trigger cases.

For workstation execution, supply private runsettings that enable live tests and set
`TestDataDirectory` to a directory containing the input file and `DriverTestData` resources.
Override the repository's ordinary non-processor filter, and select the required live tests.

## Physical sensor and button checks

`kasatapodriver-physical-events` is a separate manual suite. Its listener reads the selected
hub without changing reporting intervals or double-click preferences. It establishes initial
sensor/log baselines and checks properties, status, icon and availability inside callbacks.

When `LIVE HUB LISTENER READY` appears, operate the button, open/close the contact and trigger
motion, then move away until motion clears. The default window is 300 seconds. The required
observations are `buttonTriggered`, `contactOpened`, `contactClosed`, `motionDetectedEvent`
and `motionCleared`. Unobserved required transitions fail the test; an idle observation window
does not count as a pass. Additional observed button gestures and alarms are recorded.

Workstation runsettings can set `HubObservationSeconds` (15–600) and a comma-separated
`ExpectedPhysicalEvents` to select an explicitly planned observation. Keep the expected set
with the evidence. A successful button/contact/motion pass does not certify physical battery,
leak or comfort-warning transitions. All six sensor event pairs and all five button events
also have repeatable fixture coverage on the workstation and processor.

The normal processor workflow retains exact source/package identity, result files and traces.
It removes its temporary instance and releases the processor lease after confirmed completion.
Failed observation windows retain their failure evidence and package archive.

### Startup baseline

The first confirmed light state publishes power, level and active colour channels once, after awaited processor baseline synchronization and readiness. Off dimmers report level 0 even when the bulb retains brightness. Outlet and sensor startup readings emit their matching state events after complete properties and availability; unknown readings and historical button gestures do not. Later unchanged polls remain quiet. Native Home can additionally emit light events while binding a load, so installed-driver checks verify startup reporting and quiet unchanged polling separately. The physical listener clears initial state events before its operator window; they cannot count as physical transition evidence.

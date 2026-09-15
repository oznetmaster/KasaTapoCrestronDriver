# Read-only installed-driver control probe

Build this .NET 10 console project to observe a selected plug or strip outlet independently of the installed driver. It never sends on/off commands. The shared NUnit workflow sends commands through Home, then invokes this probe for fresh physical observations and restoration verification.

Use the existing private `LiveTestSettings.json` with `credentials.userName`, `credentials.password` and `devices.<role>`. The selected role needs one `deviceId` and, for a strip outlet, `childDeviceId`; a single entry under `hosts` is also supported. Discovery resolves the current address from the stable ID. It does not assume a transport from the product name. Both discovery and the connected device must match that ID.

The runner supplies `--request` and `--response`; configure `--settings <private-file>` and `--role plug` or `--role strip` in its probe arguments. Request/response files follow the [installed-control protocol](https://github.com/oznetmaster/CrestronHomeNUnit/blob/main/docs/InstalledDriverControls.md). Keep settings and generated evidence outside tracked source. Do not publish actual device identities, addresses or credentials.

Outlet entities expose read-only `controlDeviceId` and `controlStatus` diagnostics for this workflow. Activity includes all background work and command retries; completion alone is not proof of success. The probe verifies the actual device state. Existing outlet commands and UI bindings remain unchanged.
The validated installed-driver workflow uses the absolute `outletOn`/`outletOff` commands. The probe only observes; it does not issue either command.

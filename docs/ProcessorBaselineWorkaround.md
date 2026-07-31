# Crestron Home Processor Baseline Workaround

## Status

**Optional, opt-in, and disabled by default.** This workaround exists solely to compensate for a
Crestron Home platform defect in how `lightTunable:mode` is (not) honored during light-load
initialization. Crestron has been made aware of this issue (see [Root Cause](#root-cause) and the
original defect report reproduced below) and has acknowledged the underlying behavior, but as of
this writing **no fix has been shipped**. Once Crestron ships a fix that makes the reflected
`lightTunable:mode` property actually control processor-side light-load initialization, this
workaround will be removed from the driver and the driver will rely on the documented SDK behavior
instead.

This document is self-contained: it can be read on its own to understand the problem and the
workaround. The [README](../README.md) links here and only summarizes the highlights.

---

## Root Cause

Crestron Home's Entity V2 lighting model exposes a `lightTunable:mode` property that a reflected
driver entity is documented to use to tell the processor whether a tunable full-color/full-white
light load should currently be treated as being in **Color** (HSV) mode or **White** (color
temperature) mode. In practice, **setting `lightTunable:mode` has no observable effect** on how the
processor initializes or renders the load's tuning mode.

Instead, the processor's own internal Load layer maintains two *separate* copies of the tuning mode
for each light load:

- A **stored/baseline** tuning mode (`BaselineSetting.TuningMode`), which the processor
  re-applies automatically on every power-off → power-on transition of the load, independent of
  whatever the driver most recently published.
- An **active/live** tuning mode (nested under `TunableChannelStates.TuningMode`), which reflects
  whichever channel (HSV or CT) happens to currently be rendered, and which is what the Crestron
  Home UI actually reads when first displaying the load's tile/detail page.

These two values can disagree with each other, and both can disagree with the actual, live device
state reported by the driver. Concretely:

- A bulb reporting an active, positive color temperature (i.e., unambiguously in white/CT mode)
  can still have its **stored baseline** stuck at `ColorTuning` from whatever mode it was in the
  *first* time the processor learned about the load (for example, right after discovery, before the
  driver ever reported real state).
- Because the baseline is silently re-applied by the processor on every off→on toggle — not just
  at driver/entity creation — the load can revert to displaying (or driving) the wrong mode any
  time the load is power-cycled, even long after the driver has been correctly publishing the
  right state the whole time.
- The driver has no supported way to observe or correct either of these processor-internal values
  through the Entity V2 / Driver DevKit APIs. `lightTunable:mode` is reflected and published
  correctly by the driver, but the processor does not consume it to update either the baseline or
  the active tuning mode.

The practical, user-visible symptom is a **flash of the wrong color, or the wrong mode being shown
in the Crestron Home app**, immediately after a light is turned on — most noticeably right after
adding a new device, after a driver reload, or after any bulb power cycle (including ordinary
on/off use, not just driver-initiated ones).

Because the Crestron Home UI reads the processor's **active** tuning-mode copy (not anything the
driver publishes) the first time it renders a light's tile or detail page, this is not always just
a transient flash — the UI itself can **initialize showing the wrong mode and controls entirely**
(for example, presenting HSV color controls for a bulb that is actually running in white/CT mode,
or vice versa) and remain that way until the mismatch happens to be corrected, such as by the
optional workaround below or a subsequent state change that happens to realign the two copies.

The original defect report filed against this behavior — including exact reproduction steps,
processor console evidence (`ch rpc lights ListAllLoadStates` output showing the internal
inconsistency), and the requested fix — is preserved verbatim in
[`CRESTRON_HOME_TUNING_MODE_DEFECT.md`](../CRESTRON_HOME_TUNING_MODE_DEFECT.md) at the repository
root.

## Why This Can't Be Fixed From the Driver Alone

The Crestron Driver DevKit gives a reflected driver entity no supported mechanism to:

1. Set or query a light load's processor-side `BaselineSetting` or active `TuningMode`.
2. Discover the processor-generated light load ID associated with a given driver entity (this ID
	is assigned by the processor itself, is not exposed to the driver, and changes across driver
	reloads).
3. Receive a callback when the Crestron Home UI or another process changes a load's tuning mode
	out from under the driver.

Only the processor's own console (accessible over SSH, the same interface used for on-device
diagnostics and `ch rpc` commands) exposes read/write access to this internal state, via commands
such as:

```text
ch rpc lights ListAllLoadStates
ch rpc lights SetBaselineSetting <loadId> <hue> <saturation> <ct> false ColorTuning|WhiteTuning
ch rpc lights SetLoadState <loadId>:1:<level>:0
```

There is no DriverKit-level equivalent of these commands.

## The Workaround: `ProcessorBaselineCoordinator`

To avoid requiring end users to manually run console commands after every device change, this
driver includes an **optional** component, `ProcessorBaselineCoordinator`, that automates the same
correction the console commands above perform:

1. **Connects over SSH** to the Crestron Home processor console using credentials supplied in
	configuration (see [Configuration](#configuration) below), reusing a single session across
	calls and tearing it down and reconnecting if a call ever times out or fails.
2. **Resolves the processor-assigned light load ID** for a given light entity by name, since this
	ID is not available through the normal driver APIs and can change across reloads.
3. **Reads back both the stored baseline tuning mode and the currently active tuning mode** (plus,
	experimentally, the stored baseline's Hue/Saturation/ColorTemperature) for that load directly
	from the console (`ch rpc lights ListAllLoadStates`), rather than trusting the driver's own
	last-known state, since the two can diverge independently of anything the driver did.
4. **Compares** those processor-reported values against the mode/color the driver is currently
	trying to publish (derived from the same rules used elsewhere in the driver — see
	`LightTuningDecisions`).
5. If either the baseline or the active tuning mode (or, experimentally, the baseline color) is
	found to be out of sync with what the driver actually wants, it **issues the same
	`SetBaselineSetting` / power-cycle console sequence** a person would type by hand, forcing the
	processor to re-baseline the load with the correct tuning mode and color values.
6. Applies a hard overall timeout around the whole operation, since the underlying SSH library's
	blocking calls do not reliably observe cancellation, and a stuck console session must never be
	allowed to silently disable baseline correction for the rest of the process's lifetime.

This is intentionally a "fairly out there" fix: it drives the processor via its own administrative
console instead of the documented driver SDK surface, because the documented surface does not
expose any way to solve this problem today. It exists only as a stopgap.

## Configuration

The workaround is entirely **opt-in**. It is controlled by these platform driver configuration
items:

| Field | Description |
|---|---|
| **Enable Processor Baseline Workaround** | Master switch. When disabled (the default), none of the console logic runs and the driver behaves exactly as it would if this workaround did not exist. |
| **Processor SSH Host** | Optional. Hostname or IP address of the Crestron Home processor's console/SSH endpoint. Leave blank to have the driver automatically use the processor's primary (non-loopback) IPv4 address instead. |
| **Processor SSH User Name** | SSH login user name for the processor console. |
| **Processor SSH Password** | SSH login password for the processor console. |

If the workaround is disabled, or the SSH user name/password are left blank, the driver simply
skips baseline synchronization and relies solely on the (currently non-functional)
`lightTunable:mode` publication
— i.e., the driver behaves as if this workaround did not exist at all.

## Risks and Limitations

- This relies on an **undocumented, unsupported processor console interface** (`ch rpc lights ...`)
  that is not part of any published Crestron API contract and could change or be removed in a
  future Crestron Home firmware release without notice.
- It requires storing SSH credentials for the processor in the driver's configuration, which is a
  meaningfully larger trust surface than a normal Entity V2 driver needs.
- The blocking SSH.NET calls used to talk to the console do not honor cancellation tokens, so a
  stuck or slow processor console session can still cause a multi-second delay before the driver's
  own hard timeout gives up and tears the connection down.
- Because the workaround has to actively toggle the light load off and on to force the processor
  to re-apply a corrected baseline, there is an inherent brief, additional state transition
  involved in the fix itself, separate from the flash it is trying to prevent.

## Future Removal

If/when Crestron ships a platform fix that makes the processor honor the reflected
`lightTunable:mode` property (or otherwise exposes a supported way to control processor-side light
load tuning-mode/baseline state), `ProcessorBaselineCoordinator` and its associated configuration
items will be removed from this driver, and the driver will go back to relying purely on
`lightTunable:mode` publication as originally intended.

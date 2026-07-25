# Crestron Home full-color light tuning-mode initialization defect

## Summary

Crestron Home initializes the displayed tuning mode of a reflected full-color light inconsistently with the driver's published state. A Kasa KL130 reporting active color temperature is rendered as Color mode, while a bulb reporting HSV color can be rendered as White mode after a UI power cycle.

## Environment

- Processor: MC4-R
- Crestron Device Drivers DevKit: 27.0.24
- Driver entity capabilities: `lightDimmer`, `lightEmulatedColorTemperature`, and `lightColor` (Hue/Saturation)
- Device: TP-Link Kasa KL130(UN)

## Reproduction

1. Configure a full-color bulb through a reflected driver entity exposing:
	- `lightDimmer:level`
	- `lightEmulatedColorTemperature:level`
	- `lightColor:hue`
	- `lightColor:saturation`
2. Put the bulb in CT mode at 4526 K.
3. Reload/recreate the driver entity.
4. Publish the connected bulb state: CT 4526 K, retained HSV values, and no active HSV display mode.
5. Observe the Crestron Home UI initializes the load in Color mode despite the bulb physically displaying CT white.

The inverse is also reproducible: an HSV-active blue bulb can be initialized/latch to White after the Home UI sends an emulated CT preamble before its dimmer power-on command.

## Driver evidence

At processor-log time 06:06:08, the reflected entity definition contained:

- `lightTunable:mode`
- Local data type `lightTunable:TuningMode`

The driver published:

- `lightTunable:mode = white`
- `lightEmulatedColorTemperature:level = 4526`

The UI still initialized to Color mode.

The driver correctly derives CT-active state from a positive device `ColorTemperature` and suppresses retained HSV state while CT is active.

## Processor lighting-state evidence

`ch rpc lights ListAllLoadStates` for the recreated KL130 load showed:

- Active `EmulatedColorTemperature = 4526`
- Active `TuningMode = ColorTuning`
- Baseline `TuningMode = WhiteTuning`

This is internally inconsistent with the device-reported active CT mode.

## Manual processor workaround

The processor can be manually aligned with these console commands:

1. `ch rpc lights SetBaselineSetting <loadId> <hue> <saturation> <ct> false ColorTuning|WhiteTuning`
2. `ch rpc lights SetLoadState <loadId>:1:<level>:0`

The second command applies the baseline to the active `TunableChannelStates` and causes the processor to dispatch matching Hue/Saturation or CT mode commands.

However, this is not a viable driver workaround because:

- The Crestron Driver DevKit has no supported driver API for `ch rpc lights` or processor lighting RPC.
- The processor-generated light load ID changes on driver reload (observed IDs: 52285, 52287, 52288) and is not exposed to the reflected driver entity.
- The baseline does not automatically follow user UI transitions between CT and HSV, so a fixed baseline becomes stale.

## Requested resolution

Please provide one of the following supported mechanisms for reflected full-color driver entities:

1. Honor the documented/reflected `lightTunable:mode` property when initializing and updating the processor light load's active tuning mode and baseline; or
2. Expose a supported Driver DevKit API that lets an entity set/query its associated Crestron Home light-load baseline and active `TuningMode`; or
3. Expose the processor-generated light load ID and a supported lighting-state synchronization callback to the driver.

Without one of these, a driver cannot reliably keep the Crestron Home UI mode synchronized with a device that supports both HSV and color temperature.

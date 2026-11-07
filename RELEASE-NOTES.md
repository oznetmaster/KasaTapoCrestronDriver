# KasaTapoCrestronDriver 2.1.15

Patch release to reduce outlet polling load and energy-notification noise while retaining prompt, confirmed On/Off feedback.

## Changes

- Poll standalone outlet relay state at the configured Light Poll Interval without fetching every energy module. Refresh full state and energy about once a minute during steady operation; startup and reconnect can refresh sooner.
- Stagger background outlet polls and cap them at three concurrent operations per platform. Each physical strip has a shared poll. Legacy strips can refresh socket state through system information; SMART strips retain their full child-list refresh, so their protocol traffic can differ.
- Keep direct command/readback feedback independent of the scheduled poll interval and avoid waiting behind a saturated background polling cap. Failed or ignored commands do not invent a confirmed state change.
- Publish outlet voltage to one decimal place (0.1 V) and notify when that rounded value changes. This is reporting precision, not a requirement for an absolute 0.1 V change between raw readings; other energy readings retain their existing precision.
- Persist verified reconnect protocol metadata only when the connected physical device identity matches the saved identity. Preserve child IDs, room placement and event programming.
- Use public KasaTapoClient 2.2.0, retaining its 2.1.0 lighting and legacy KLAP v2 fixes. DevKit remains 29.0.10. Public builds do not enable the customer-only outlet diagnostic journal.

## Polling and logging

Keep **Enable Light Polling** enabled to detect changes made through the Kasa/Tapo app or physical button. **15 seconds** remains the normal recommendation; an existing **10-second** interval is supported and retained. Commands receive immediate confirmed readback rather than waiting for that interval. Energy displays can lag by about a minute, and delayed/offline devices can extend observed polling gaps.

Keep third-party tile feedback programming connected to **Outlet Turned On / Outlet Turned Off**, using the tile's display-feedback action rather than another plug-control command. Existing startup state/event behavior from 2.1.14 is retained. Release builds keep warning/error diagnostics; Home controls its own property-processing logs and can still log genuine changes.

## Validation

The customer test build 2.1.014.0003 passed mixed-load tests with **two real demo outlets and eighteen simulated TCP endpoints**, on workstation and processor. Each 132-second window completed 264 state reads with background concurrency capped at three; command-feedback timing was checked on a simulator under saturation. The tests did not switch either real outlet. These are mixed-load results, not tests of twenty physical devices.

The customer subsequently confirmed that .0003 remained online on CP4-R, retained working Ultamation tile feedback and substantially reduced log activity. This is attributed customer evidence, without a measured percentage reduction. Fresh validation against public client 2.2.0 passed 34 workstation unit checks, 158 workstation lifecycle checks, 192 processor checks and the mixed-load test on both hosts. The exact public package was then installed on the existing lab platform: all 37 device identities and placements, and confirmed outlet/light levels, were preserved. Processor-stored package bytes matched the frozen candidate. No real-device control commands or reboot were used, and all owned reservations were released.

## Installation

GitHub/NuGet version **2.1.15** contains Crestron driver **2.1.015.0000**; Home may display **2.001.0015.0**. It supersedes public 2.1.14 and the 2.1.014.0001–0003 customer tests.

Extract the Windows uploader ZIP and run its executable on a PC that can reach the processor. Enter the processor address and processor login, select Upload driver and wait for the verified upload result. Then open the existing KasaTapoPlatform driver in Home Setup or Configure Pro and use **Check for Driver Update**. Uploading alone does not update the running driver. Retain the existing platform, children, polling settings and feedback programming.

The Windows uploader, GitHub driver package and NuGet wrapper contain the same .pkg. Public packages contain no submission-only help PDF or redundant documentation ZIP. Crestron catalogue submission/publication is a separate process; previously submitted packages are unchanged.

Support: [contact form](https://oznetmaster.github.io/support/).

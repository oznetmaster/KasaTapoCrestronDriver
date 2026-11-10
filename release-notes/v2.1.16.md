# KasaTapoCrestronDriver 2.1.16

This patch corrects the dependency and lifecycle issues described below.

- Use KasaTapoClient 2.2.1.
- Close background-work admission before shutdown, drain discovery, refresh and cache work, and retain synchronization objects while cancelled observers unwind.
- Preserve cached child identities, pairing/configuration, confirmed feedback events, startup baselines and staggered outlet polling.

## Installation

Use the driver package from [GitHub](https://github.com/oznetmaster/KasaTapoCrestronDriver/releases/tag/v2.1.16) or the matching NuGet distribution. Import the package, then update the existing driver in Home Setup or Configure Pro. Importing alone does not update the running instance. Existing pairing and configuration formats are retained; removal and re-add is not a routine update step.

## Validation

Passed 196 workstation Debug and 196 Release test executions, plus 196 native net472/Mono processor tests. Processor validation used fake transports and synthetic credentials; no new physical-device acceptance or endurance run is claimed. Existing processor configuration and all pairing files were preserved. See [development history](DEVELOPMENT-HISTORY.md) for provenance and limits.

This GitHub/NuGet release does not replace any submitted Crestron package. Submission-specific PDFs and correspondence are excluded from public artifacts.

# KasaTapoCrestronDriver 2.1.17

This patch improves initial discovery for SMART power strips whose firmware times out on a combined child-device and component-list request. KasaTapoClient 2.2.2 retries with individual requests and reads each page of outlets. The driver gives that fallback a bounded window within strip discovery.

## Installation

Use the driver package from [GitHub](https://github.com/oznetmaster/KasaTapoCrestronDriver/releases/tag/v2.1.17) or the matching NuGet distribution. Import it and update the existing KasaTapoPlatform driver in Crestron Home Setup or Configure Pro. Existing device IDs, configuration and pairing formats are retained.

## Validation

The regression uses a simulated P300-style strip. It lets the child-list batch time out, then verifies that the driver deadline allows all outlet pages to be read. A second case verifies the legacy strip deadline. The test uses synthetic device responses.

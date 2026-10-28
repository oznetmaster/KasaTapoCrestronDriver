# KasaTapoCrestronDriver 2.1.4

Fixes on/off control for supported SMART dimmers, including HS220 and KS225. The driver uses KasaTapoClient's dimmer-compatible power API, allowing the load to turn on or off independently of its brightness setting.

- Retains native Crestron Home dimmer behavior: zero turns off, full level from off restores retained brightness, and full level while on sets 100%.
- Keeps existing bulb, strip and non-dimmable switch power commands. KasaTapoClient remains at 2.0.1 and Crestron.DeviceDrivers.DevKit remains at 29.0.10.
- Removes the temporary diagnostic file logger from both Debug and Release builds. The Windows uploader no longer offers diagnostic-log collection; normal Crestron Home logging remains available.

GitHub and NuGet use version **2.1.4**; the included Crestron driver is **2.1.004.0000**, higher than the distributed 2.1.002.0002 and 2.1.002.0004 customer-test builds. NuGet 2.1.3 only updated documentation around the unchanged 2.1.2 driver.

Import the package, then update the existing KasaTapoPlatform driver in Home Setup or Configure Pro. Importing alone does not update the active driver. Routine updates do not require removing children. If children remain offline, contact support before deleting configured devices.

The earlier customer-test installation recovered after the platform was removed and re-added; the customer reports all loads working correctly. The cause of the earlier missing child configuration is unconfirmed. That recovery does not establish a general upgrade repair, and this release does not rewrite saved child configuration. Physical KS225 operation has not been confirmed in this case.

Support: [contact form](https://oznetmaster.github.io/support/).

Source and history: [KasaTapoCrestronDriver](https://github.com/oznetmaster/KasaTapoCrestronDriver).

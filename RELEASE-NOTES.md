# KasaTapoCrestronDriver 2.1.2

Adds packaged user help with illustrated installation and operation instructions, model and firmware details, limitations, licensing and an account-free support contact.

- The package and driver DLL now use the descriptive filename `NeilColvin_Platform_KasaTapo_IP_V2`; the driver identity and existing device controls are unchanged.
- Cached children register during startup and retain separate parent and child identities after reload, without waiting for fresh network discovery.
- Outlet transport timeouts report offline status without stopping subsequent polling and recovery attempts.
- Outlet, sensor and button connection changes now update the standard availability interfaces used by Crestron Home, alongside their existing extension properties.
- The package includes the matching help PDF and third-party license notices. The support website is also included in the driver metadata.
- KasaTapoClient 2.0.1 renews established TPAP sessions rejected after a device restarts, allowing automatic recovery without reloading the driver. Crestron.DeviceDrivers.DevKit remains at 29.0.10; no new physical-device settings are required.

Support: [contact form](https://oznetmaster.github.io/support/), with no GitHub account required.

Source, documentation and history: [KasaTapoCrestronDriver](https://github.com/oznetmaster/KasaTapoCrestronDriver).

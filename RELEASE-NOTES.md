# KasaTapoCrestronDriver 2.1.2

Adds packaged user help with illustrated installation and operation instructions, model and firmware details, limitations, licensing and an account-free support contact.

- The package and driver DLL now use the descriptive filename `NeilColvin_Platform_KasaTapo_IP_V2`; the driver identity and existing device controls are unchanged.
- Cached children register during startup and retain separate parent and child identities after reload, without waiting for fresh network discovery.
- Outlet transport timeouts report offline status without stopping subsequent polling and recovery attempts.
- The package includes the matching help PDF and third-party license notices. The support website is also included in the driver metadata.
- KasaTapoClient remains at 2.0.0 and Crestron.DeviceDrivers.DevKit at 29.0.10. This release does not change the device protocol or require new physical-device settings.

Support: [contact form](https://oznetmaster.github.io/support/), with no GitHub account required.

Source, documentation and history: [KasaTapoCrestronDriver](https://github.com/oznetmaster/KasaTapoCrestronDriver).

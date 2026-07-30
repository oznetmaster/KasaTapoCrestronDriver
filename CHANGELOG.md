# Changelog

All notable changes to this project are documented here. Each entry summarizes the corresponding
[GitHub release](https://github.com/oznetmaster/KasaTapoCrestronDriver/releases), which remains the
authoritative, detailed record (including build assets) for that version. This file exists as a
single, scannable index of the full version history.

The format is loosely based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this
project follows [Semantic Versioning](https://semver.org/).

## [1.1.0]

First general availability release.

- Crestron Home platform driver for TP-Link Kasa and Tapo smart lights: a single driver instance discovers devices on the local network and publishes each as a managed child light entity.
- Local-only device access: devices are discovered and controlled directly over the local network. No Tapo or Kasa cloud account is used or required.
- Includes an optional, SSH-based workaround (`ProcessorBaselineCoordinator`) for a Crestron Home processor defect in which the `lightTunable:mode` property has no effect, which can also cause the UI to initialize incorrectly at startup. See [`docs/ProcessorBaselineWorkaround.md`](docs/ProcessorBaselineWorkaround.md) for details.
- Distributed as a NuGet package containing the driver `.pkg`, installable via the Crestron Home Driver Feed Installer.

## [1.0.0-preview]

Initial public preview release.

- Crestron Home platform driver for TP-Link Kasa and Tapo smart lights: a single driver instance discovers devices on the local network and publishes each as a managed child light entity.
- Local-only device access: devices are discovered and controlled directly over the local network. No Tapo or Kasa cloud account is used or required.
- Includes an optional, SSH-based workaround (`ProcessorBaselineCoordinator`) for a Crestron Home processor defect in which the `lightTunable:mode` property has no effect, which can also cause the UI to initialize incorrectly at startup. See [`docs/ProcessorBaselineWorkaround.md`](docs/ProcessorBaselineWorkaround.md) for details.
- Distributed as a NuGet package containing the driver `.pkg`, installable via the Crestron Home Driver Feed Installer.

## [1.0.001.0001]

- Improved driver reload behavior by making child-device activation nonblocking from Crestron child configuration callbacks.
- Republished cached managed child devices early during startup so installed child devices can reattach after reload.
- Kept physical device connection and state initialization in the background, allowing the platform driver to come online while child devices finish connecting.
- Added logging around discovery, cache seeding, child configuration status, activation, and startup connection timing.

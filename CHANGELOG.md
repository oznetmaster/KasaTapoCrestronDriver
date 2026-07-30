# Changelog

All notable changes to this project are documented here. Each entry summarizes the corresponding
[GitHub release](https://github.com/oznetmaster/KasaTapoCrestronDriver/releases), which remains the
authoritative, detailed record (including build assets) for that version. This file exists as a
single, scannable index of the full version history.

The format is loosely based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this
project follows [Semantic Versioning](https://semver.org/).

## [1.0.0-preview]

- First public preview release, published to GitHub and NuGet.
- Added an MIT + Commons Clause `LICENSE` and standardized copyright/license headers on all first-party source files.
- Rewrote `README.md` to describe the driver as a Crestron Home platform driver (one instance manages many child light devices), documented local-only device discovery/access (no Tapo/Kasa cloud account access), and documented installation via NuGet/the Crestron Home Driver Feed Installer.
- Added [`docs/ProcessorBaselineWorkaround.md`](docs/ProcessorBaselineWorkaround.md), a self-contained explanation of the Crestron `lightTunable:mode` tuning-mode defect (including its effect on UI initialization), and the optional SSH-based `ProcessorBaselineCoordinator` workaround.
- Added this `CHANGELOG.md` as a scannable version-history index, with full details remaining in GitHub Releases.
- Added a GitHub Actions release workflow (`.github/workflows/release-package.yml`) that builds the driver `.pkg`, packs a NuGet wrapper package, publishes it to NuGet.org using Trusted Publishing (OIDC, no long-lived API keys), and attaches the `.pkg` to the GitHub release.
- Vendored a patched build of `Renci.SshNet` (`lib/Renci.SshNet/`) used by `ProcessorBaselineCoordinator`, replacing a machine-local reference so the project builds reproducibly on any machine and in CI.

## [1.0.001.0001]

- Improved driver reload behavior by making child-device activation nonblocking from Crestron child configuration callbacks.
- Republished cached managed child devices early during startup so installed child devices can reattach after reload.
- Kept physical device connection and state initialization in the background, allowing the platform driver to come online while child devices finish connecting.
- Added logging around discovery, cache seeding, child configuration status, activation, and startup connection timing.

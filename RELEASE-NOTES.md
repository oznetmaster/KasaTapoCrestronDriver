# KasaTapoCrestronDriver v2.0.1

Patch release correcting lifecycle, configuration and recovery defects while preserving the public API and intended driver behavior.

## Fixes

- Do not deliver an offline callback to a child registration that an earlier callback removed or replaced during the same notification pass.
- Remove the Debug diagnostic listener when disposing its platform driver.
- Add direct regression coverage for persisted device descriptors, configured-child markers, stable controller identity and SDK controller publication. Cache fixtures use isolated temporary files.

## Tests and build process

- 34 offline tests and 35 SDK lifecycle tests. The current implementation passes on Windows in Debug and Release; both processor suites passed twice in the same host process.
- The shared net472 processor test package is available in the solution and appears under **Utility** in Configure. Its standalone Home tile and Windows NUnit runner select the test suites.
- Driver Debug build versions follow the manifest; three-part release tags select the CI release version. Test builds do not increment or deploy the production driver.
- Processor test packages are not published to NuGet. Private deployment settings, live inputs and desktop SDK runtime dependencies are excluded from source and release assets.

## Installation and documentation

The GitHub release includes the production driver package and a separate processor test package. The test package appears under Utility in Configure and is not included in the driver NuGet package. See [CHANGELOG.md](CHANGELOG.md) for release history and [README.md](README.md) for installation and testing.

The three optional read-only live checks passed on confirmation. An earlier run could not discover the selected light; live results depend on local device/network availability. The repeatable offline/lifecycle suites do not require those devices.

# KasaTapoCrestronDriver Tests

## 1.2.0

- Include the shared outlet command-activity and SDK dispatch regressions. All 34 portable and 37 lifecycle cases passed both on Windows and the processor; three optional read-only live checks also passed.
- Build the tests against the driver's new read-only outlet diagnostics. Running the automatic suites does not operate physical devices; installed-driver control checks are a separate explicitly configured workflow feature.
- Keep private inputs and deployment settings excluded. Install the package from Configure's Utility category. This is a GitHub-only test-package release, with no test-package NuGet publication.

## 1.1.1

- Rebuild with CrestronHomeNUnit 1.2.1. Test execution now participates in the shared processor reservation used by the runner, Test Explorer, CLI and hardware CI.
- The net472 package contains 72 discovered cases, with 69 in automatic suites. Live suites remain optional and require private inputs where documented.
- Use the standalone Utility tile, Windows runner, or the solution's Test Explorer workflow project. Private workflow plans can remove the temporary instance after testing.
- This is an independent processor-test package release on GitHub; it does not publish or update a driver/library NuGet package.

## 1.1.0 — 2026-09-14

- 34 offline tests and 35 SDK lifecycle tests, shared between desktop validation and the net472 processor package.
- Cover persisted light, outlet, sensor and button descriptors, configured-device markers, incomplete cache metadata, and publication through the real SDK registry without duplicate controllers. Each cache fixture uses its own temporary file. Release the Debug diagnostic listener when its platform driver is disposed.
- Install the standalone test package from Configure’s **Utility** category. Select suites using its Home tile or the Windows NUnit runner.
- Processor test packages are GitHub release assets and are not published to NuGet. Private test inputs and deployment settings are excluded.

Install `KasaTapoCrestronDriver.ProcessorTests.pkg`, then add **Utility → Neil Colvin → KasaTapoCrestronDriver Tests** in Crestron Home Configure. All processor test packages use the Utility category. The standalone Home tile runs automatic suites; the [Windows runner](https://github.com/oznetmaster/CrestronHomeNUnit/releases) provides discovery, selection and detailed results. Each package includes its own host; the NUnit self-test package is optional.

The `.sources.json` asset records the exact source revisions. The documentation ZIP includes license notices; SHA256SUMS.txt covers every other asset. Private settings, deployment credentials and live results are excluded. NUnit 4.6.1 is the official NuGet framework, not a private fork.

CI validates desktop tests and discovery from the packaged assembly. Processor runs are a separate hardware validation; the unit and lifecycle suites have passed during local processor testing.
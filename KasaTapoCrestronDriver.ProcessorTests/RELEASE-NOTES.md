# KasaTapoCrestronDriver Tests

## 1.1.0 — 2026-09-14

- 34 offline tests and 35 SDK lifecycle tests, shared between desktop validation and the net472 processor package.
- Cover persisted light, outlet, sensor and button descriptors, configured-device markers, incomplete cache metadata, and publication through the real SDK registry without duplicate controllers. Each cache fixture uses its own temporary file. Release the Debug diagnostic listener when its platform driver is disposed.
- Install the standalone test package from Configure’s **Utility** category. Select suites using its Home tile or the Windows NUnit runner.
- Processor test packages are GitHub release assets and are not published to NuGet. Private test inputs and deployment settings are excluded.

Initial public processor test package, version 1.0.0. This is a test-package release; no library or production driver NuGet package is published.

- 34 ordinary NUnit tests and 20 processor lifecycle cases using simulated device responses.
- The test package contains the driver under test. Its lifecycle suite exercises real net472 driver entities on the processor; the same sources also have desktop validation.
- Production driver version 2.0.0 and its NuGet package remain unchanged.

Install `KasaTapoCrestronDriver.ProcessorTests.pkg`, then add **Utility → Neil Colvin → KasaTapoCrestronDriver Tests** in Crestron Home Configure. All processor test packages use the Utility category. The standalone Home tile runs automatic suites; the [Windows runner](https://github.com/oznetmaster/CrestronHomeNUnit/releases/tag/v1.0.0) provides discovery, selection and detailed results. Each package includes its own host; the NUnit self-test package is optional.

The `.sources.json` asset records the exact source revisions. The documentation ZIP includes license notices; SHA256SUMS.txt covers every other asset. Private settings, deployment credentials and live results are excluded. NUnit 4.6.1 is the official NuGet framework, not a private fork.

CI validates desktop tests and discovery from the packaged assembly. Processor runs are a separate hardware validation; both suites have passed during local processor testing.

# KasaTapo driver processor tests

This project packages the driver's NUnit tests for Crestron Home. It belongs in `KasaTapoCrestronDriver.slnx` beside the production driver and is not a NuGet package.

## Build and deploy in Visual Studio

1. Open `KasaTapoCrestronDriver.slnx`.
2. Build **KasaTapoCrestronDriver.ProcessorTests** in Debug. This project targets only `net472`.
3. Debug builds automatically deploy using this project's private `.csproj.user` settings. It uses the shared test-package SDK's SFTP import script; it does not deploy the production Kasa driver.
4. In Crestron Home Configure, find **Utility → Neil Colvin → KasaTapoCrestronDriver Tests** and add the test host.
5. In the Windows runner, click **Find packages**, select **KasaTapoCrestronDriver Tests**, and connect.
6. Run **Unit Tests** first, then **Processor lifecycle**.

The host uses an automatically assigned TCP port and advertises itself through mDNS. The production Kasa driver and this test package can be installed together; they have distinct package identities. The package also exposes its two suites from the test host's Home tile.

## Suites

| Suite | Cases | Coverage |
| --- | ---: | --- |
| Unit Tests | 34 | Light tuning, dimmable plugs, device classification and simulated light/energy responses |
| Processor lifecycle | 20 | Real `net472` driver entities and Crestron SDK: sensor definitions, child registration, shared parent state, polling, cancellation, disposal and recovery |

Both suites use simulated device responses and do not require live network devices or test credentials. The lifecycle suite exercises the actual driver assembly on the processor. Its sources are shared with the existing desktop lifecycle project, which retains its desktop-compatible SDK target for local validation.

## UI ownership

The package's top-level `UiDefinitions` and `Translations` belong to the NUnit host. The sensor and outlet UI files required by the tested entities are staged under `DriverTestData/sensor/uidefinitions` and `DriverTestData/outlet/uidefinitions`. `DriverTestPaths` passes that isolated data directory to the entities. The production driver's main UI is not installed as the test host UI. The package builder also removes dependency driver manifests from the merged test host and verifies the generated identity before deployment.

## Local configuration

The shared SDK is expected in an adjacent `CrestronHomeNUnit` checkout. Override `ProcessorTestSdkRoot` or other machine paths in `KasaTapoCrestronDriver.ProcessorTests.Local.targets` when needed.

Keep `.csproj.user`, `*.Local.targets`, runner settings and any future live-device settings in `.git/info/exclude`. They must not be committed or included in release assets. The locally created `.csproj.user` copies the existing driver's deployment credentials; it is excluded from Git. Public releases can attach the `.pkg` asset after processor validation.

Building the test reference uses an unmerged production driver with deployment disabled. Design-time builds do not package or deploy it. A normal production-driver build retains its existing deployment behavior.

## Desktop tests

Both existing test projects now use NUnit 4.6.1 and NUnit3TestAdapter for Visual Studio. Select the repository's `.runsettings` in Test Explorer to exclude the `Processor` category on Windows. The test projects also declare this settings file.

```powershell
dotnet test KasaTapoCrestronDriver.Tests/KasaTapoCrestronDriver.Tests.csproj --framework net472 --filter "TestCategory!=Processor" -p:DeployAfterBuild=false
dotnet test KasaTapoCrestronDriver.Lifecycle.Tests/KasaTapoCrestronDriver.Lifecycle.Tests.csproj -p:DeployAfterBuild=false
```

The processor's embedded NUnit runner uses the suite filters in `ProcessorTests.json` and does not inherit the desktop `.runsettings` filter.


## Reproducible releases

`ProcessorTestSdk.lock.json` pins the public Crestron Home NUnit SDK revision used by CI. The test release workflow fetches this exact commit and uses `ProcessorTestSdkRoot` to select it. Local development may use the adjacent checkout. Run **Release processor tests** with the desired test version to publish the package without changing the production driver or publishing NuGet. The release includes the licenses and source revisions used to build it.

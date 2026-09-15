# KasaTapoCrestronDriver processor workflow

This .NET 10 test container integrates the processor workflow with Visual Studio Test Explorer and VSTest. It uses the published **CrestronHomeNUnit.TestAdapter 1.2.1**. The existing NUnit test projects and net472 processor package remain separate.

Before launching Visual Studio, set `CRESTRON_HOME_KASATAPOCRESTRONDRIVER_WORKFLOW_SETTINGS` to the absolute path of your private adapter-settings JSON file. That file contains `planPath`, `userName` and `password`; the referenced private plan specifies your processor, certificate fingerprints, source/build paths, required suites, live inputs and cleanup. Keep these files outside the repository, or use `.git/info/exclude` for any private local file. Never commit them or upload results containing private inputs.

Select **KasaTapoCrestronDriver processor workflow** in Test Explorer. The workflow runs the local tests itself, then builds/deploys the processor package, waits for activation, runs the required processor suites and removes its test instance when `removeTestInstanceAfterRun` is true. Optional live suites and actual-driver updates run only when explicitly included in the private plan. Discovery is offline and never operates the processor. Attempting execution without private settings fails.

Use the existing NUnit projects for ordinary local tests. Select this workflow separately for processor testing; avoid selecting both in the same test cycle, and never put this workflow project into its own plan's `localTests`.

```powershell
dotnet test KasaTapoCrestronDriver.WorkflowTests/KasaTapoCrestronDriver.WorkflowTests.csproj -c Release --list-tests
dotnet test KasaTapoCrestronDriver.WorkflowTests/KasaTapoCrestronDriver.WorkflowTests.csproj -c Release --filter FullyQualifiedName=CrestronHome.Workflows.kasatapocrestrondriver_driver
```

See [Test Explorer setup](https://github.com/oznetmaster/CrestronHomeNUnit/blob/main/docs/VisualStudioTestExplorer.md) and the [private hardware CI template](https://github.com/oznetmaster/CrestronHomeNUnit/blob/main/docs/GitHubHardwareCI.md). Results are retained privately even after a failed or interrupted run. Uncertain execution retains its processor lock for investigation; it is not automatically retried.


Maintainers can publish only the processor test package using the **Release processor tests** workflow and an independent three-part version. This does not publish a driver or library NuGet release.
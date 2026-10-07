# Kasa/Tapo submission tests

This dedicated NUnit 5 project exposes the same phase-two submission tests in Visual Studio, VS Code and CI through the published `CrestronHomeDevTools.SubmissionTests` 1.25.0 package. It does not build or publish a new production driver, prepare documents, sign or send a submission.

Discovery requires only .NET 10 and public NuGet access:

```powershell
dotnet test KasaTapoCrestronDriver.SubmissionTests/KasaTapoCrestronDriver.SubmissionTests.csproj -c Release --list-tests
```

Before execution, configure the existing private submission profile with the exact candidate, test fixtures, device bindings, restoration rules and approved equipment. Set `KASATAPO_SUBMISSION_SETTINGS` to its absolute settings path and `KASATAPO_SUBMISSION_SETTINGS_SHA256` to the independently recorded digest. Keep both out of source control. Without these settings, tests are skipped; this is not a successful submission test run. Do not include this orchestration project in the profile's local test selection, which would recurse. Do not run another worker against the same retained run concurrently.

Run the four initial cases (`CandidateValidation`, `LocalAndProcessorChecks`, `ProcessorEvidence`, `AppAndRecoveryChecks`) in Test Explorer. Their dependencies are native NUnit dependencies. Endurance is `[Explicit]`: deliberately select it after the initial cases pass, then run `PostEnduranceAndRemovalChecks`. Ordinary Run All does not start endurance; final checks remain inconclusive until the required endurance evidence exists. Completed stages verify and reuse their retained evidence. Physical prompts require the configured operator; never substitute a simulated response.

CI uses this same project and fixture through the retained upstream helper:

```powershell
./tools/Invoke-SubmissionSuite.ps1 `
  -Project KasaTapoCrestronDriver.SubmissionTests/KasaTapoCrestronDriver.SubmissionTests.csproj `
  -Fixture KasaTapoCrestronDriver.SubmissionTests.DriverSubmissionTests `
  -ResultsDirectory TestResults/submission-new-run `
  -IncludeEndurance
```

Use a fresh results directory. Omit `-IncludeEndurance` to select only the four initial cases. The helper rejects missing, duplicate, skipped and unsuccessful selected cases and retains separate original TRX files. It comes unchanged from DevTools v1.25.0 (`tools/Invoke-SubmissionSuite.ps1`, MIT licensed). Run live CI on the configured equipment worker with its private settings, not an unconfigured hosted runner. The repository's discovery CI only compiles and discovers tests; it does not operate equipment.

After phase two has a verified complete test assessment and finalization receipt, invoke the generic phase-three document/signing/delivery flow separately. A green discovery job, skipped tests, or an initial-only pass does not authorize phase three. See the [shared native suite contract](https://github.com/oznetmaster/CrestronHomeDevTools/blob/v1.25.0/docs/submission/NativeNUnitSuite.md) and [submission runbook](https://github.com/oznetmaster/CrestronHomeDevTools/blob/v1.25.0/docs/submission/Runbook.md). The current shared fixture represents one endurance stage; identifying any second distinct required endurance case remains unresolved and is not covered by discovery validation.

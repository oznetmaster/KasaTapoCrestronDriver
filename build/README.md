# Build and test

This repository uses [the shared Cake build component](https://github.com/oznetmaster/BuildAutomation/tree/dd6056146da4424c996c0c3a0f98651e16209098) pinned in `component.json`. It is build tooling only; product dependencies and versions are unchanged.

The CI workflow retains its jobs, framework matrix, filters, prerequisites and artifact upload rules. Repository-owned recipes preserve the build/test/package commands. Publication, validation reuse, approvals, runner selection and hardware reservations remain in GitHub Actions.

Run the same task locally with .NET 10, Git and PowerShell:

```powershell
./build/Invoke.ps1 -Task TASK_NAME
```

For a matrix task, set its `BUILD_INPUT_*` environment values as shown in the workflow before invoking it. These are project/framework selections, not credentials. The first local invocation downloads the immutable component into ignored `artifacts/build-components/`.

Available tasks:

- `tests-tests-build-read-only-installed-control-probe`
- `tests-tests-compile-optional-android-app-fixtures`
- `tests-tests-verify-offline-app-evidence-contracts`
- `tests-tests-verify-discovery-coverage-guards`
- `tests-tests-run-discovered-nunit-driver-tests`
- `tests-tests-run-discovered-desktop-sdk-lifecycle-harness`
- `tests-tests-build-processor-package`
- `tests-tests-validate-all-packaged-suites`
- `workflow-discovery-submission-discovery-discover-submission-tests-without-private-settings-or-equipment`
- `workflow-discovery-discover-verify-offline-test-explorer-discovery`

The shared component rejects missing/stale/empty/failed declared TRX results and writes one receipt per invocation beneath `artifacts/build-automation/`. It does not publish packages or invoke live hardware tests. Existing specialized scripts remain authoritative.

## Shared public driver release

The shared path in release-package.yml selects standard_release=true and public_version=2.1.16. It retains the caller workflow identity and publication environment. Versioned notes, driver manifests and build/driver-release.json select the artifacts. The repository release-assets Cake task builds and inspects the production packages; pinned shared actions freeze the exact bytes, publish to NuGet, verify GitHub assets, and then publish the draft. Earlier workflow paths remain available.

Reviewed workstation and native processor results can be reused through the existing explicit flags and provenance fields. Public driver wrappers contain installable .pkg payloads, not referenceable library assemblies; they do not use the library symbol-package policy. No submission PDFs or private test inputs are staged.

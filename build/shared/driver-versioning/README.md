# Driver versioning

Canonical build-only helpers for all maintained Crestron drivers, including RainPoint. Repository paths, manifests, package identities and distribution registers remain repository configuration. Copy these helpers unchanged into `build/shared/driver-versioning/` and record the source revision and SHA-256 hashes in `build/driver-versioning.json`. Thin existing entry points preserve callers and ordinary clone/IDE builds; using these helpers requires no Cake download or sibling checkout.

Public GitHub and NuGet versions have three components. The manifest has four. Debug builds increment the fourth component; local Release builds preserve it; CI Release builds reset it to zero after confirming any explicit release version matches the prepared first three components. Import `DriverVersion.props` only after setting `DriverManifestFile`. The props refresh package metadata after the bump target.

`Assert-DriverPackageVersion.ps1` checks the source manifest and actual root `.dat` in the package. Public checks require a zero fourth component. Only explicit local package smoke checks may use `AllowLocalRevision`; it still requires the same three-part release and exact source/package agreement. Submission document checks remain separate repository wrappers. No operation edits an existing public or submitted package.

Run the standalone suite with:

```powershell
pwsh -NoProfile -File DriverVersioning/Test-SharedDriverVersioning.ps1
```

Consumers also run `Test-DriverVersioning.ps1 -Root <repository>` to exercise their actual imports. Keep repository-specific version/distribution guards in addition to these common regressions.

These files were extracted from existing driver build tooling. Preserve the accompanying Commons Clause notice; this directory is not relicensed by the root MIT licence.

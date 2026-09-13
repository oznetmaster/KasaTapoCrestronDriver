# Driver versioning

The source manifest selects `major.minor.patch.build`. Debug builds increment `build`; release CI normalizes it to zero for the release selected by the manifest and matching three-part GitHub tag. Local Release builds preserve the manifest. MSBuild reads its version from the manifest and refreshes it after the Debug increment. Explicit release `PackageVersion` overrides, including prerelease suffixes, survive the refresh.

| Stage | Example | Check |
|---|---|---|
| Debug source/build | `2.0.001.0006` / `2.0.1.6` | All four numeric components agree |
| GitHub tag / NuGet release | `v2.0.1` / `2.0.1` | Three release components match the prepared manifest |
| CI release driver | `2.0.001.0000` | Built package metadata matches source and release |
| Imported catalogue entry | Exact `.pkg` version | Uploaded bytes identified by hash and version |
| Installed driver instance | Exact selected build | Loaded state and all four version components confirmed |

Before a new release, prepare the manifest's first three components for the intended version, then tag that version. The workflow does not add another patch increment. Select the exact expected package filename, never the newest arbitrary `.pkg` in a directory. Re-running a tagged release must not manufacture the next version. Existing historical release assets and tags are not rewritten by these rules.

Processor test packages retain independent versions. A published release and a later local Debug build need not match. The deployment gate compares the tested artifact with the catalogue and installed instance, not with the latest GitHub tag. Rebuilding changes the artifact and requires testing again.

`tools/Test-DriverVersioning.ps1` runs isolated manifest/MSBuild, tag-selection and package-metadata checks without compiling or deploying a driver. It is also run by the release workflow. Ordinary driver unit/lifecycle and processor tests remain separate checks.
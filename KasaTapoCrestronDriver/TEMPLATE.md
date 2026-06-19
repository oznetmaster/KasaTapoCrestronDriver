# CrestronHome Device Driver Template Maintenance

This repository is the editable source for the standalone `CrestronHome Device Driver` project template. Maintain the scaffold content here, pack it here, and publish it from here.

## Workspace layout

- `templatepack.csproj` - root packaging project that builds the template `.nupkg`
- `Install-CrestronHomeDeviceDriverTemplate.ps1` - local pack/install loop for maintainer validation
- `Install-CrestronHomeDeviceDriverTemplateFromGitHub.ps1` - consumer installer that downloads a GitHub release asset
- `.template.config/template.json` - template identity, parameters, and output definition
- `KasaTapoCrestronDriver.slnx` - workspace solution containing both the pack project and scaffold project
- `KasaTapoCrestronDriver/` - scaffold source that becomes the generated project
- `.github/workflows/publish-template.yml` - GitHub Actions workflow for packing and release publishing

## Local maintenance workflow

1. Edit the scaffold content under `KasaTapoCrestronDriver/`, plus any root files that should appear in generated output.
2. Build the scaffold project as needed:
   - `dotnet build .\KasaTapoCrestronDriver.slnx -c Debug`
3. Pack and reinstall the template locally:
   - `pwsh .\Install-CrestronHomeDeviceDriverTemplate.ps1`
4. Verify the template is registered:
   - `dotnet new list crestronhome-driver`
5. Smoke-test generation into a temp folder:
   - `dotnet new crestronhome-driver -n SmokeTestDriver`

## Packaging model

`templatepack.csproj` is the only project that should be used to create the template package for distribution. The scaffold project remains focused on the generated driver solution and its own Crestron-specific pack behavior.

The package intentionally includes:

- `.template.config/**`
- `KasaTapoCrestronDriver/**`
- `KasaTapoCrestronDriver.slnx`
- `README.md`
- `LICENSE`

The package intentionally excludes build output and local user artifacts such as `bin`, `obj`, and `*.user` files.

## Publisher workflow

### One-time metadata review

Before the first public release, replace placeholder repository values in these files:

- `templatepack.csproj`
- `Install-CrestronHomeDeviceDriverTemplateFromGitHub.ps1`
- any template defaults that should point to the final GitHub repository

### Local pre-release checklist

1. Run `dotnet build .\KasaTapoCrestronDriver.slnx -c Debug`
2. Run `pwsh .\Install-CrestronHomeDeviceDriverTemplate.ps1`
3. Create a smoke-test project with `dotnet new crestronhome-driver`
4. Confirm the generated solution opens and builds as expected in Visual Studio

### GitHub release flow

1. Commit the template changes.
2. Create and push a version tag such as `v1.0.0`.
3. GitHub Actions runs `.github/workflows/publish-template.yml`.
4. The workflow packs `templatepack.csproj`, uploads the `.nupkg` as a workflow artifact, and attaches it to the GitHub release for the tag.

Manual workflow runs are also available when you only need a package artifact without publishing a tagged release.

## Consumer workflow

Consumers can install the template from a published GitHub release asset:

1. Download `Install-CrestronHomeDeviceDriverTemplateFromGitHub.ps1`.
2. Run it against the publishing repository:
   - `pwsh .\Install-CrestronHomeDeviceDriverTemplateFromGitHub.ps1 -Repository owner/repo`
3. Confirm installation:
   - `dotnet new list crestronhome-driver`
4. Create a project:
   - `dotnet new crestronhome-driver -n MyDriver`

If a consumer needs a specific release, they can pass `-Tag v1.0.0`.

## Notes

- Keep the editable template source in this repository only; do not move maintenance back into a driver implementation repo.
- If the package ID or release asset naming changes, update `templatepack.csproj`, the GitHub workflow, and both installer scripts together.

# Copyright (c) 2026 Neil Colvin.
# Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

param(
    [Parameter(Mandatory)][string] $Version,
    [Parameter(Mandatory)][string] $SdkRoot,
    [Parameter(Mandatory)][string] $ManifestUtilExe,
    [string] $Package = 'KasaTapoCrestronDriver.ProcessorTests'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = $PSScriptRoot
if ($Package -ne 'KasaTapoCrestronDriver.ProcessorTests') { throw 'Unsupported release package.' }
$projectDirectory = Join-Path $root $Package
foreach ($project in @('KasaTapoCrestronDriver.Tests/KasaTapoCrestronDriver.Tests.csproj', 'KasaTapoCrestronDriver.Lifecycle.Tests/KasaTapoCrestronDriver.Lifecycle.Tests.csproj')) {
    dotnet test "$root/$project" -c Release --filter 'TestCategory!=Live' -p:DeployAfterBuild=false
    if ($LASTEXITCODE -ne 0) { throw "Desktop tests failed: $project" }
}
dotnet build "$projectDirectory/$Package.csproj" -c Release -p:BuildProcessorTestPackages=true -p:DeployAfterBuild=false "-p:ProcessorTestSdkRoot=$SdkRoot" "-p:ReleaseVersion=$Version" "-p:ManifestUtilExe=$ManifestUtilExe" "-p:LocalCrestronSdkLibDir=$(Split-Path $ManifestUtilExe -Parent)"
if ($LASTEXITCODE -ne 0) { throw 'Processor test package build failed.' }
$output = Join-Path $projectDirectory 'bin/Release/net472'
$release = Join-Path $root 'artifacts/release'
if (Test-Path -LiteralPath $release) { throw 'Use a fresh release staging directory.' }
[IO.Directory]::CreateDirectory($release) | Out-Null
$pkg = Join-Path $output "$Package.pkg"
# Inspect the actual shipped assembly, not just pre-package build output.
$extracted = Join-Path $root ('artifacts/verify-' + [Guid]::NewGuid().ToString('N'))
[IO.Compression.ZipFile]::ExtractToDirectory($pkg, $extracted)
$suites = (Get-Content "$projectDirectory/ProcessorTests.json" -Raw | ConvertFrom-Json).Suites
if (@($suites | Where-Object { $_.ExpectedCount -le 0 }).Count) { throw 'Every suite needs an expected discovery count.' }
$expectedTests = ($suites | Measure-Object -Property ExpectedCount -Sum).Sum
& "$SdkRoot/ProcessorTestPackage.Validation/bin/Release/net472/ProcessorTestPackage.Validation.exe" "$extracted/$Package.dll" "$root/artifacts/validation" $expectedTests
if ($LASTEXITCODE -ne 0) { throw 'Packaged test discovery failed.' }
$manifest = Get-Content "$projectDirectory/$Package.json" -Raw | ConvertFrom-Json
if ($manifest.GeneralInformation.DeviceType -ne 'Utility') { throw 'Processor test packages must use the Utility category.' }
$revision = git -C $root rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot record package revision.' }
$sdkRevision = git -C $SdkRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot record SDK revision.' }
$record = [ordered]@{ package=$Package; version=$Version; packageRevision=$revision; sdkRevision=$sdkRevision; configuration='Release' }
[IO.File]::WriteAllText("$release/$Package.sources.json", ($record | ConvertTo-Json -Depth 6))
Copy-Item -LiteralPath $pkg -Destination $release
$docs = Join-Path $root 'artifacts/release-documentation'
[IO.Directory]::CreateDirectory($docs) | Out-Null
foreach ($file in @('README.md', 'LICENSE')) { Copy-Item -LiteralPath "$root/$file" -Destination $docs }
Copy-Item -LiteralPath "$projectDirectory/README.md" -Destination "$docs/Package-Guide.md"
Copy-Item -LiteralPath "$projectDirectory/RELEASE-NOTES.md" -Destination $docs
Copy-Item -LiteralPath "$extracted/Licenses" -Destination $docs -Recurse
Copy-Item -LiteralPath "$root/ProcessorTestSdk.lock.json" -Destination $docs
[IO.Compression.ZipFile]::CreateFromDirectory($docs, "$release/$Package-Documentation.zip")
foreach ($archive in @(Get-ChildItem $release -File | Where-Object Extension -In '.pkg', '.zip')) {
    $zip = [IO.Compression.ZipFile]::OpenRead($archive.FullName)
    try {
        $private = @($zip.Entries | Where-Object FullName -Match '(?i)(\.local\.json$|\.csproj\.user$|\.Local\.targets$|(^|/)LiveTestSettings\.json$|ProcessorKeys\.dat$|\.pfx$|TestResults/)')
        if ($private.Count) { throw "Private file found in $($archive.Name)." }
    } finally { $zip.Dispose() }
}
$hashes = @(Get-ChildItem $release -File | Sort-Object Name | ForEach-Object { (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Name })
[IO.File]::WriteAllLines("$release/SHA256SUMS.txt", $hashes)
git -C $root diff --exit-code
if ($LASTEXITCODE -ne 0) { throw 'Build unexpectedly changed tracked files.' }
# Copyright (c) 2026 Neil Colvin.
# Licensed under the MIT License with Commons Clause. See NOTICE.md in this directory.
$ErrorActionPreference = 'Stop'
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$fixture = Join-Path $temporaryRoot ('SharedVersionChecks-' + [guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory((Join-Path $fixture 'Driver'))
try {
    $import = [Security.SecurityElement]::Escape((Join-Path $PSScriptRoot 'DriverVersion.props'))
    @"
<Project>
  <PropertyGroup><DriverManifestFile Condition="'`$(DriverManifestFile)' == ''">`$(MSBuildThisFileDirectory)manifest.json</DriverManifestFile></PropertyGroup>
  <Import Project="$import" />
</Project>
"@ | Set-Content (Join-Path $fixture 'Driver/DriverVersion.props')
    Copy-Item (Join-Path $PSScriptRoot 'BumpDriverVersion.ps1') (Join-Path $fixture 'Driver/BumpDriverVersion.ps1')
    & "$PSScriptRoot/Test-DriverVersioning.ps1" -Root $fixture
    $manifest = Join-Path $fixture 'manifest.json'
    @{GeneralInformation=@{DriverVersion='2.0.001.0005'}} | ConvertTo-Json -Depth 4 | Set-Content $manifest
    $package = Join-Path $fixture 'Example.pkg'
    $archive = [IO.Compression.ZipFile]::Open($package,[IO.Compression.ZipArchiveMode]::Create)
    try {
        $writer = [IO.StreamWriter]::new($archive.CreateEntry('Example.dat').Open())
        try { $writer.Write('{"driverVersion":"2.0.1.5"}') } finally { $writer.Dispose() }
    } finally { $archive.Dispose() }
    $rejected = $false
    try { & "$PSScriptRoot/Assert-DriverPackageVersion.ps1" -PackagePath $package -ManifestPath $manifest -ReleaseVersion '2.0.1' }
    catch { $rejected = $true }
    if (!$rejected) { throw 'Public release accepted a nonzero test revision.' }
    & "$PSScriptRoot/Assert-DriverPackageVersion.ps1" -PackagePath $package -ManifestPath $manifest -ReleaseVersion '2.0.1' -AllowLocalRevision
    $rejected = $false
    try { & "$PSScriptRoot/Assert-DriverPackageVersion.ps1" -PackagePath $package -ManifestPath $manifest -ReleaseVersion '2.0.2' -AllowLocalRevision }
    catch { $rejected = $true }
    if (!$rejected) { throw 'Local revision mode accepted the wrong release.' }
    Write-Host 'Passed 3 additional public/local artifact-version checks.'
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    if ([IO.Path]::GetDirectoryName($resolved).TrimEnd('\','/') -ne $temporaryRoot.TrimEnd('\','/') -or [IO.Path]::GetFileName($resolved) -notlike 'SharedVersionChecks-*') { throw 'Unexpected fixture cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

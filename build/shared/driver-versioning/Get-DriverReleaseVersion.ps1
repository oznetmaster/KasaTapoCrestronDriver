# Copyright (c) 2026 Neil Colvin.
# Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

param(
    [Parameter(Mandatory)][string[]] $ManifestPath,
    [string] $Tag,
    [switch] $RequireTag
)
$ErrorActionPreference = 'Stop'
$versions = @($ManifestPath | ForEach-Object {
    $manifest = Get-Content -LiteralPath $_ -Raw | ConvertFrom-Json
    $info = if ($manifest.CrestronSerialDeviceApi) { $manifest.CrestronSerialDeviceApi.GeneralInformation } else { $manifest.GeneralInformation }
    $version = [version]$info.DriverVersion
    if ($version.Revision -lt 0) { throw 'DriverVersion must have four numeric components.' }
    $version.ToString(3)
} | Select-Object -Unique)
if ($versions.Count -ne 1) { throw 'Driver manifests in one release must share their three release components.' }
$release = $versions[0]
$package = $release
if ($Tag -match '^v?(?<base>\d+\.\d+\.\d+)(?<suffix>-[0-9A-Za-z.-]+)?$') {
    if ($Matches['base'] -ne $release) { throw "Release tag '$Tag' does not match manifest release '$release'." }
    $package = $Matches['base'] + $Matches['suffix']
} elseif ($RequireTag -or $Tag -match '^v?\d') {
    throw 'Release tags must use vMAJOR.MINOR.PATCH with an optional prerelease suffix.'
}
[pscustomobject]@{ PackageVersion = $package; DriverReleaseVersion = $release; Tag = 'v' + $package }
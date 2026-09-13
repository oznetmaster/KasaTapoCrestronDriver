# Copyright (c) 2026 Neil Colvin.
# Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

param(
    [Parameter(Mandatory)][string] $PackagePath,
    [Parameter(Mandatory)][string] $ManifestPath,
    [Parameter(Mandatory)][string] $ReleaseVersion
)
$ErrorActionPreference = 'Stop'
if ($ReleaseVersion -notmatch '^(?<base>\d+\.\d+\.\d+)(?:-[0-9A-Za-z.-]+)?$') {
    throw 'ReleaseVersion must be a three-component release, optionally with a prerelease suffix.'
}
$expected = [version]($Matches['base'] + '.0')
$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
$info = if ($manifest.CrestronSerialDeviceApi) { $manifest.CrestronSerialDeviceApi.GeneralInformation } else { $manifest.GeneralInformation }
$manifestVersion = [version]$info.DriverVersion
$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $PackagePath))
try {
    $entries = @($zip.Entries | Where-Object { $_.FullName -notmatch '[/\\]' -and $_.Name.EndsWith('.dat', [StringComparison]::OrdinalIgnoreCase) })
    if ($entries.Count -ne 1 -or $entries[0].Length -gt 1MB) { throw 'Expected one root package manifest, at most 1 MiB.' }
    $reader = [IO.StreamReader]::new($entries[0].Open())
    try { $metadata = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    $packageVersion = [version]$metadata.driverVersion
    if ($packageVersion -ne $expected -or $manifestVersion -ne $expected) {
        throw "Release '$ReleaseVersion' requires driver '$expected'; source manifest '$manifestVersion', built package '$packageVersion'."
    }
    Write-Host "Verified release $ReleaseVersion and built driver $packageVersion."
} finally { $zip.Dispose() }
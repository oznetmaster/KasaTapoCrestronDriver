# Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactDirectory,
    [string]$PlanPath = (Join-Path $PSScriptRoot '../FrozenRelease.json')
)
$ErrorActionPreference = 'Stop'
$plan = Get-Content -LiteralPath $PlanPath -Raw | ConvertFrom-Json
if ($plan.schemaVersion -ne 1 -or $plan.publicVersion -notmatch '^\d+\.\d+\.\d+$' -or !$plan.files.Count) { throw 'Invalid frozen release plan.' }
& "$PSScriptRoot/Assert-PublicDistributionVersion.ps1" -ReleaseVersion $plan.publicVersion
$names = @{}
foreach ($file in $plan.files) {
    if ($file.name -notmatch '^[A-Za-z0-9_.-]+$' -or $file.sha256 -notmatch '^[a-f0-9]{64}$' -or $file.bytes -le 0 -or $names.ContainsKey($file.name)) { throw 'Invalid or duplicate frozen asset.' }
    $names[$file.name] = $true
    $path = Join-Path $ArtifactDirectory $file.name
    if ((Get-Item -LiteralPath $path).Length -ne $file.bytes -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) { throw "Frozen asset differs: $($file.name)" }
}
$pkgName = 'NeilColvin_Platform_KasaTapo_IP_V2.pkg'
$nugetName = "CrestronHomeDriver.TpLink.KasaTapoPlatform.$($plan.publicVersion).nupkg"
if (!$names.ContainsKey($pkgName) -or !$names.ContainsKey($nugetName)) { throw 'Both driver and NuGet payloads must be frozen.' }
$pkg = Join-Path $ArtifactDirectory $pkgName
$nuget = Join-Path $ArtifactDirectory $nugetName
& "$PSScriptRoot/Assert-DriverPackageVersion.ps1" -PackagePath $pkg -ManifestPath "$PSScriptRoot/../KasaTapoCrestronDriver/KasaTapoCrestronDriver.json" -ReleaseVersion $plan.publicVersion
& "$PSScriptRoot/Test-NuGetDocumentation.ps1" -PackagePath $nuget
$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $nuget))
try {
    $spec = @($zip.Entries | Where-Object FullName -like '*.nuspec')
    if ($spec.Count -ne 1) { throw 'Expected one NuGet manifest.' }
    $reader = [IO.StreamReader]::new($spec[0].Open())
    try { [xml]$metadata = $reader.ReadToEnd() } finally { $reader.Dispose() }
    if ($metadata.package.metadata.id -ne 'CrestronHomeDriver.TpLink.KasaTapoPlatform' -or $metadata.package.metadata.version -ne $plan.publicVersion) { throw 'Wrong NuGet package identity.' }
    $entry = $zip.GetEntry($pkgName)
    if (!$entry) { throw 'NuGet driver payload is missing.' }
    $stream = $entry.Open()
    try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() } finally { $stream.Dispose() }
    if ($hash -ne (Get-FileHash -LiteralPath $pkg).Hash.ToLowerInvariant()) { throw 'NuGet contains different driver bytes.' }
    $readme = $zip.GetEntry('README.md')
    if (!$readme) { throw 'NuGet README missing.' }
    $stream = $readme.Open()
    try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() } finally { $stream.Dispose() }
    if ($hash -ne (Get-FileHash -LiteralPath "$PSScriptRoot/../README.md").Hash.ToLowerInvariant()) { throw 'Packaged README does not match this source.' }
} finally { $zip.Dispose() }
Write-Host "Verified all $($plan.files.Count) frozen release assets and embedded driver bytes."

# Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$NoticesPath,
    [string]$RegisterPath = (Join-Path $PSScriptRoot '../driver-distributions.json')
)
$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$notices = (Resolve-Path -LiteralPath $NoticesPath).Path
$hash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
$zip = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $root = @($zip.Entries | Where-Object { $_.FullName -match '^[^/\\]+\.dat$' })
    if ($root.Count -ne 1 -or $root[0].Length -gt 1MB) { throw 'Invalid package manifest.' }
    $reader = [IO.StreamReader]::new($root[0].Open())
    try { $info = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
} finally { $zip.Dispose() }
$register = Get-Content -LiteralPath $RegisterPath -Raw | ConvertFrom-Json
$entry = @($register.distributions | Where-Object { [version]$_.driverVersion -eq [version]$info.driverVersion })
if ($entry.Count -ne 1 -or $entry[0].channel -ne 'test' -or $entry[0].status -notin @('reserved','prepared','distributed') -or $entry[0].packageSha256 -ne $hash) {
    throw 'Reserve this exact test version and package checksum in driver-distributions.json before building a sideloader.'
}
if ($info.driverId -ne $register.driverId -or $info.baseModel -ne $register.product) { throw 'The package belongs to another product.' }
$public = @($register.distributions | Where-Object channel -eq public | ForEach-Object { [version]$_.driverVersion } | Sort-Object -Descending)
if ($public.Count -gt 0 -and [version]$info.driverVersion -le $public[0]) { throw 'The test package must be higher than the latest recorded public version.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($output) | Out-Null
$metadataPath = Join-Path $output 'embedded-payload.json'
@{ FileName = [IO.Path]::GetFileName($package); Model = $info.baseModel; DriverId = $info.driverId; Version = $info.driverVersion; Sha256 = $hash } |
    ConvertTo-Json | Set-Content -LiteralPath $metadataPath -Encoding utf8NoBOM
$project = Join-Path $PSScriptRoot 'TestDriverSideload/TestDriverSideload.csproj'
dotnet build $project -c Release "-p:PayloadPath=$package" "-p:PayloadMetadataPath=$metadataPath" "-p:NoticesPath=$notices" -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw 'Sideloader build failed.' }
$built = Join-Path $PSScriptRoot 'TestDriverSideload/bin/Release/net472/KasaTapo-Test-Installer.exe'
$destination = Join-Path $output ("KasaTapo-Test-" + $info.driverVersion + '.exe')
Copy-Item -LiteralPath $built -Destination $destination
Write-Output $destination

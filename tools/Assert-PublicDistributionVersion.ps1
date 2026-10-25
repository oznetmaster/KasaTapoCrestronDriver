# Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
param(
    [Parameter(Mandatory)][string]$ReleaseVersion,
    [string]$RegisterPath = (Join-Path $PSScriptRoot '../driver-distributions.json')
)
$ErrorActionPreference = 'Stop'
if ($ReleaseVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Public GitHub/NuGet versions must have exactly three numeric components.' }
$version = [version]($ReleaseVersion + '.0')
$register = Get-Content -LiteralPath $RegisterPath -Raw | ConvertFrom-Json
if ($register.schemaVersion -ne 1 -or -not $register.distributions) { throw 'Invalid driver distribution register.' }
$seen = @{}
foreach ($entry in $register.distributions) {
    if ($entry.driverVersion -notmatch '^\d+\.\d+\.\d+\.\d+$' -or $entry.channel -notin @('public','test')) { throw 'Invalid distribution version or channel.' }
    $known = [version]$entry.driverVersion
    if ($seen.ContainsKey($known.ToString())) { throw 'A driver distribution version was allocated more than once.' }
    $seen[$known.ToString()] = $true
    if ($entry.channel -eq 'public') {
        if ($entry.publicVersion -notmatch '^\d+\.\d+\.\d+$' -or [version]($entry.publicVersion + '.0') -ne $known) { throw 'Public distribution and package versions disagree.' }
    } elseif ($entry.publicVersion) { throw 'Test distributions must not allocate a GitHub/NuGet public version.' }
    # Reserved/withdrawn versions still count: never reuse a number or rely on a
    # test package having reached only its intended recipient.
    if ($entry.channel -eq 'test' -and $version -le $known) {
        throw "Public $ReleaseVersion creates package $version, which does not exceed test $($entry.driverVersion) ($($entry.id)). Choose a higher three-part public version."
    }
    if ($entry.channel -eq 'public' -and $version -lt $known) { throw 'The proposed public version is older than a recorded public release.' }
}
Write-Host "Public $ReleaseVersion / package $version exceeds every recorded test distribution."

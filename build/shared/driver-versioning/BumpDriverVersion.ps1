# Copyright (c) 2026 Neil Colvin.
# Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

param(
    [Parameter(Mandatory)][string] $ManifestPath,
    [string] $Configuration = 'Debug',
    [string] $ReleaseVersion
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $ManifestPath)) { throw 'Driver manifest was not found.' }
$content = Get-Content -LiteralPath $ManifestPath -Raw
$matches = [regex]::Matches($content, '(?<="DriverVersion":\s*")(?<major>\d+)\.(?<minor>\d+)\.(?<release>\d+)\.(?<build>\d+)(?=")')
if ($matches.Count -ne 1) { throw 'The manifest must contain exactly one four-component DriverVersion.' }
$match = $matches[0]
$current = [version]$match.Value
if (@($current.Major, $current.Minor, $current.Build, $current.Revision | Where-Object { $_ -gt 65535 }).Count) {
    throw 'Driver version components must be between 0 and 65535.'
}
$isCiBuild = @($env:CI, $env:TF_BUILD, $env:GITHUB_ACTIONS | Where-Object { $_ -in @('true', '1') }).Count -gt 0
switch ($Configuration) {
    'Debug' {
        if ($current.Revision -eq 65535) { throw 'Debug build component is exhausted; prepare the next release version.' }
        $next = [version]::new($current.Major, $current.Minor, $current.Build, $current.Revision + 1)
    }
    'Release' {
        if (-not $isCiBuild) {
            Write-Host 'BumpDriverVersion: local Release build preserves the manifest.'
            exit 0
        }
        $selected = $current.ToString(3)
        if ($ReleaseVersion) {
            if ($ReleaseVersion -notmatch '^\d+\.\d+\.\d+$' -or [version]$ReleaseVersion -ne [version]$selected) {
                throw 'ReleaseVersion must match the three release components prepared in the manifest.'
            }
            $selected = $ReleaseVersion
        }
        # Release preparation/tagging selects the release components. CI resets only the build.
        $next = [version]($selected + '.0')
    }
    default { exit 0 }
}
$newVersion = '{0}.{1}.{2}.{3}' -f $next.Major, $next.Minor, $next.Build.ToString('000'), $next.Revision.ToString('0000')
if ($match.Value -eq $newVersion) { exit 0 }
$content = [regex]::Replace($content, '(?<="DriverVersion":\s*")[^"]+(?=")', $newVersion, 1)
$content = [regex]::Replace($content, '(?<="VersionDate":\s*")[^"]+(?=")', (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff'), 1)
Set-Content -LiteralPath $ManifestPath -Value $content -NoNewline
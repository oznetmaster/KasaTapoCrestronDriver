# Copyright (c) 2026 Neil Colvin. MIT License; see LICENSE.
[CmdletBinding()]
param([string]$GuardPath = (Join-Path $PSScriptRoot 'Assert-PublicDriverArtifacts.ps1'))
$ErrorActionPreference = 'Stop'
$guard = (Resolve-Path -LiteralPath $GuardPath).Path
$base = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$root = Join-Path $base ('public-driver-packaging-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
$passed = 0
function Archive([string]$Name, [hashtable]$Entries) {
    $path = Join-Path $root $Name
    $stream = [IO.File]::Create($path)
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $Entries.Keys) {
            $entry = $zip.CreateEntry($name); $target = $entry.Open()
            try {
                $bytes = if ($Entries[$name] -is [byte[]]) { $Entries[$name] } else { [Text.Encoding]::UTF8.GetBytes([string]$Entries[$name]) }
                $target.Write($bytes, 0, $bytes.Length)
            } finally { $target.Dispose() }
        }
    } finally { $zip.Dispose(); $stream.Dispose() }
    return $path
}
function Case([string]$Name, [string[]]$Paths, [bool]$ExpectPass) {
    $failed = $false
    try { & $guard -Path $Paths | Out-Null } catch { $failed = $true }
    if ($failed -eq $ExpectPass) { throw "Packaging regression failed: $Name" }
    $script:passed++
}
try {
    $clean = Archive 'clean.pkg' @{'driver.dat'='{}'; 'driver.dll'='runtime'; 'uidefinitions/Ui.xml'='<ui/>'; 'THIRD-PARTY-NOTICES.txt'='license'}
    $pdf = Archive 'help.pkg' @{'driver.dat'='{}'; 'Help.pdf'='%PDF-1.7'}
    $mixed = Archive 'mixed.pkg' @{'Docs\HELP.PDF'='%PDF-1.7'}
    Case 'runtime/UI/notices remain valid' @($clean) $true
    Case 'embedded help rejected' @($pdf) $false
    Case 'case-insensitive PDF and backslash path rejected' @($mixed) $false
    $nuget = Archive 'clean.nupkg' @{'driver.pkg'=[IO.File]::ReadAllBytes($clean); 'README.md'='public docs'; 'package.nuspec'='<package/>'}
    Case 'NuGet with clean driver and README accepted' @($nuget) $true
    Case 'PDF inside NuGet driver rejected' @((Archive 'nested.nupkg' @{'driver.pkg'=[IO.File]::ReadAllBytes($pdf)})) $false
    Case 'loose PDF in NuGet rejected' @((Archive 'loose.nupkg' @{'help.PdF'='%PDF'})) $false
    Case 'documentation ZIP with README and HTML accepted' @((Archive 'docs.zip' @{'README.md'='public'; 'docs/index.html'='<html/>'; 'docs/api.xml'='<doc/>'})) $true
    Case 'submission PDF in documentation ZIP rejected' @((Archive 'bad-docs.zip' @{'submission/help.pdf'='%PDF'})) $false
    Case 'nested distribution inspected recursively' @((Archive 'bundle.zip' @{'distribution.nupkg'=[IO.File]::ReadAllBytes((Join-Path $root 'nested.nupkg'))})) $false
    $loose = Join-Path $root 'standalone.pdf'; [IO.File]::WriteAllText($loose,'%PDF')
    Case 'standalone PDF asset rejected' @($loose) $false
    $broken = Join-Path $root 'broken.pkg'; [IO.File]::WriteAllText($broken,'not a ZIP')
    Case 'malformed package fails closed' @($broken) $false
    Case 'missing asset fails closed' @((Join-Path $root 'missing.pkg')) $false
    $empty = Join-Path $root 'empty'; [void][IO.Directory]::CreateDirectory($empty)
    Case 'empty asset directory rejected' @($empty) $false
    Case 'multiple archives all inspected' @($clean,$pdf) $false
    $directory = Join-Path $root 'assets'; [void][IO.Directory]::CreateDirectory($directory); Copy-Item -LiteralPath $clean -Destination $directory
    Case 'asset directory accepted' @($directory) $true
    Copy-Item -LiteralPath $loose -Destination $directory
    Case 'loose PDF in asset directory rejected' @($directory) $false
    Write-Output "Public driver packaging: $passed regression cases passed."
} finally {
    $resolved = [IO.Path]::GetFullPath($root)
    if ([IO.Path]::GetDirectoryName($resolved) -ne $base.TrimEnd([IO.Path]::DirectorySeparatorChar) -or [IO.Path]::GetFileName($resolved) -notlike 'public-driver-packaging-*') { throw 'Refusing unexpected test cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

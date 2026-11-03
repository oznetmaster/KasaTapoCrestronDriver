# Copyright (c) 2026 Neil Colvin. MIT License with Commons Clause; see LICENSE in this directory.
$ErrorActionPreference='Stop'
. "$PSScriptRoot/Coverage.ps1"
$checks=0
function Reject([scriptblock]$Action) {
    $failed=$false
    try { & $Action } catch { $failed=$true }
    if (!$failed) { throw 'Invalid coverage evidence was accepted.' }
    $script:checks++
}
Assert-CoverageTestNames @('A','B') @('B','A') 'Reordered';$checks++
Reject { Assert-CoverageTestNames @() @() 'Empty' }
Reject { Assert-CoverageTestNames @('A','B') @('A') 'Missing' }
Reject { Assert-CoverageTestNames @('A','B') @('A','A') 'Duplicate' }
Reject { Assert-CoverageTestNames @('A','B') @('A','C') 'Same count' }
Reject { Assert-CoverageTestNames @('A') @('a') 'Case changed' }
$case=[pscustomobject]@{Name='A';Processor=$false;Result='Passed';Reason=$null}
Assert-CoverageTestOutcome $case $false;$checks++
$case.Result='Failed';Reject { Assert-CoverageTestOutcome $case $false }
$case.Result='NotExecuted';Reject { Assert-CoverageTestOutcome $case $true }
[xml]$reason='<reason><message>Requires the approved processor harness.</message></reason>'
$case.Reason=$reason.SelectSingleNode('/reason/message')
Reject { Assert-CoverageTestOutcome $case $true -AllowedSkipReasons @('Requires the approved processor harness.') }
$case.Processor=$true
Reject { Assert-CoverageTestOutcome $case $false -AllowedSkipReasons @('Requires the approved processor harness.') }
Reject { Assert-CoverageTestOutcome $case $true }
Assert-CoverageTestOutcome $case $true -AllowedSkipReasons @('Requires the approved processor harness.');$checks++
Reject { Assert-CoverageTestOutcome $case $true -AllowedSkipReasons @('different policy') }
$path=Join-Path ([IO.Path]::GetTempPath()) ('shared-coverage-'+[guid]::NewGuid().ToString('N')+'.xml')
try {
    [IO.File]::WriteAllText($path,'<test-run><test-suite runstate="NotRunnable"><test-case fullname="A" /></test-suite></test-run>')
    Reject { Read-CoverageTestTree $path }
    [IO.File]::WriteAllText($path,'<test-run />');Reject { Read-CoverageTestTree $path }
    [IO.File]::WriteAllText($path,'<test-run><test-suite><properties><property name="Category" value="Processor"/><property name="Category" value="Live"/><property name="Category" value="LocalLive"/></properties><test-case fullname="Prefix.A" result="Passed" /></test-suite></test-run>')
    $case=Read-CoverageTestTree $path -ExtraCategories LocalLive
    if (!$case.Live -or !$case.Processor -or !$case.LocalLive -or $case.Name -cne 'Prefix.A') { throw 'Inherited categories or identity changed.' };$checks++
    $case=Read-CoverageTestTree $path -NormalizeName {param($Name) $Name.Replace('Prefix.','')}
    if($case.Name -cne 'A'){throw 'Caller normalization was not applied.'};$checks++
    [IO.File]::WriteAllText($path,'wrapper <test-run><test-case fullname="A" /></test-run> tail')
    if((Read-CoverageTestTree $path -AdapterDump).Name -cne 'A'){throw 'Adapter wrapper parsing failed.'};$checks++
    [IO.File]::WriteAllText($path,'<test-run/><test-run/>');Reject { Read-CoverageTestTree $path -AdapterDump }
    [IO.File]::WriteAllText($path,'malformed');Reject { Read-CoverageTestTree $path }
} finally { Remove-Item -LiteralPath $path -ErrorAction SilentlyContinue }
Write-Host "Passed $checks shared coverage evidence checks. No tests or devices were executed."

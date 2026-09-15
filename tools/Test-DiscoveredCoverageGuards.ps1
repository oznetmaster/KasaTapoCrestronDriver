# Copyright (c) 2026 Neil Colvin.
# Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Test-DiscoveredCoverage.ps1"
$checks = 0
function Reject([scriptblock]$Action) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    if (!$rejected) { throw 'An invalid coverage result was accepted.' }
    $script:checks++
}
Assert-SameTests @('Fixture.A','Fixture.B') @('Fixture.B','Fixture.A') 'Reordered results'
$checks++
Reject { Assert-SameTests @('Fixture.A','Fixture.B') @('Fixture.A') 'Missing result' }
Reject { Assert-SameTests @('Fixture.A','Fixture.B') @('Fixture.A','Fixture.A') 'Duplicate replaces missing result' }
Reject { Assert-SameTests @('Fixture.A','Fixture.B') @('Fixture.A','Fixture.C') 'Changed identity with same total' }
Reject { Assert-SameTests @() @() 'Empty suite' }
$case = [pscustomobject]@{Name='Fixture.A';Processor=$false;Result='Passed';Reason=$null}
Assert-TestOutcome $case $false
$checks++
$case.Result='Failed'
Reject { Assert-TestOutcome $case $false }
$case.Result='NotExecuted'
Reject { Assert-TestOutcome $case $true }
[xml]$reason='<reason><message>Requires the SDK desktop harness or the processor runtime.</message></reason>'
$case.Reason=$reason.reason.message
# XML properties return string values; use the node explicitly, as the coverage reader does.
$case.Reason=$reason.SelectSingleNode('/reason/message')
Reject { Assert-TestOutcome $case $true }
$case.Processor=$true
Reject { Assert-TestOutcome $case $false }
Assert-TestOutcome $case $true
$checks++
$case.Reason.InnerText='Requires the SDK desktop harness or processor runtime.'
Assert-TestOutcome $case $true
$checks++
$case.Reason.InnerText='Ignored because the test unexpectedly failed.'
Reject { Assert-TestOutcome $case $true }
$temporary=Join-Path ([IO.Path]::GetTempPath()) ('coverage-guard-'+[Guid]::NewGuid().ToString('N')+'.xml')
try {
    [IO.File]::WriteAllText($temporary,'<test-run><test-suite runstate="NotRunnable"><test-case fullname="Fixture.A" /></test-suite></test-run>')
    Reject { Read-TestTree $temporary }
    [IO.File]::WriteAllText($temporary,'<test-run />')
    Reject { Read-TestTree $temporary }
} finally { Remove-Item -LiteralPath $temporary -ErrorAction SilentlyContinue }
Write-Host "$checks discovery coverage guard checks passed."

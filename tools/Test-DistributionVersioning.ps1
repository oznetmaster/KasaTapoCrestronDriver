# Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
$ErrorActionPreference = 'Stop'
$check = Join-Path $PSScriptRoot 'Assert-PublicDistributionVersion.ps1'
$checks = 0
$fixture = [IO.Path]::GetTempFileName()
try {
@'
{"schemaVersion":1,"distributions":[
{"id":"public","channel":"public","publicVersion":"2.1.2","driverVersion":"2.1.002.0000","status":"released"},
{"id":"test","channel":"test","publicVersion":null,"driverVersion":"2.1.002.0002","status":"reserved"}
]}
'@ | Set-Content -LiteralPath $fixture -Encoding utf8
foreach ($case in @(
    @{ Version = '2.1.2'; Reject = $true },
    @{ Version = '2.1.1'; Reject = $true },
    @{ Version = '2.1.3'; Reject = $false },
    @{ Version = '2.2.0'; Reject = $false },
    @{ Version = '3.0.0'; Reject = $false },
    @{ Version = '2.1.2.3'; Reject = $true },
    @{ Version = '2.1.3-preview.1'; Reject = $true },
    @{ Version = 'v2.1.3'; Reject = $true }
)) {
    $failed = $false
    try { & $check -ReleaseVersion $case.Version -RegisterPath $fixture } catch { $failed = $true }
    if ($failed -ne $case.Reject) { throw "Unexpected distribution gate result for $($case.Version)." }
    $checks++
}
Write-Host "Passed $checks distribution version checks."
} finally { Remove-Item -LiteralPath $fixture -Force }

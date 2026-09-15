# Copyright (c) 2026 Neil Colvin.
# Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

# Shared build deployment entry point. Keep credentials and machine paths in .csproj.user.
#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string] $PkgFile,
    [Parameter(Mandatory)][string] $ProjectUserFile,
    [string] $DevToolsPath
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $PkgFile -PathType Leaf)) { throw 'The driver package does not exist.' }
[xml]$settings = Get-Content -LiteralPath $ProjectUserFile -Raw
function Get-Setting([string] $Name, [switch] $Required) {
    $node = $settings.SelectSingleNode("/Project/PropertyGroup/$Name")
    if ($Required -and (-not $node -or [string]::IsNullOrWhiteSpace($node.InnerText))) {
        throw "Set $Name in the private project .csproj.user file."
    }
    if ($node) { return $node.InnerText }
    return $null
}
if (-not $DevToolsPath) { $DevToolsPath = Get-Setting 'CrestronHomeDevToolsPath' }
if (-not $DevToolsPath) { $DevToolsPath = $env:CRESTRON_HOME_DEVTOOLS_PATH }
if (-not $DevToolsPath -or -not (Test-Path -LiteralPath $DevToolsPath -PathType Leaf)) {
    throw 'Set CrestronHomeDevToolsPath in .csproj.user to the DevTools console .exe or .dll supporting shared processor leases.'
}
$connection = @{
    CRESTRON_HOME_HOST = Get-Setting 'CrestronHomeIP' -Required
    CRESTRON_HOME_USER = Get-Setting 'CrestronHomeFtpUser' -Required
    CRESTRON_HOME_PASSWORD = Get-Setting 'CrestronHomeSftpPassword' -Required
    CRESTRON_HOME_SSH_FINGERPRINT = Get-Setting 'CrestronHomeSshFingerprint' -Required
    CRESTRON_HOME_CERT_SHA256 = Get-Setting 'CrestronHomeCertificateSha256' -Required
}
function Invoke-DevTools([string[]] $Arguments, [switch] $WithConnection) {
    $start = [System.Diagnostics.ProcessStartInfo]::new()
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    if ([IO.Path]::GetExtension($DevToolsPath) -eq '.dll') {
        $start.FileName = 'dotnet'
        $start.ArgumentList.Add([IO.Path]::GetFullPath($DevToolsPath))
    } else {
        $start.FileName = [IO.Path]::GetFullPath($DevToolsPath)
    }
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    if ($WithConnection) {
        foreach ($entry in $connection.GetEnumerator()) { $start.Environment[$entry.Key] = $entry.Value }
    }
    $process = [System.Diagnostics.Process]::Start($start)
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $diagnostic = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $text = $output.GetAwaiter().GetResult()
        $errorText = $diagnostic.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            if ($WithConnection -and $errorText) { Write-Host $errorText.Trim() }
            throw "DevTools exited with code $($process.ExitCode). Deployment was not confirmed; no success stamp should be written."
        }
        return $text
    } finally { $process.Dispose() }
}
$capabilities = Invoke-DevTools -Arguments @('capabilities') | ConvertFrom-Json
if ($capabilities.SharedProcessorLease -ne 1 -or -not $capabilities.VerifiedPackageImport) {
    throw 'This DevTools console lacks the required processor lease/import support. Update it before deploying.'
}
$result = Invoke-DevTools -Arguments @('deploy', '--package', [IO.Path]::GetFullPath($PkgFile), '--timeout', '180') -WithConnection | ConvertFrom-Json
if (-not $result.Available) { throw 'The imported package version was not confirmed available.' }
Write-Host "Package imported and verified: $($result.Package.Model) $($result.Package.Version). Installed instances can now be updated."

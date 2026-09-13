# Copyright (c) 2026 Neil Colvin.
# Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$projects = @(Get-ChildItem -LiteralPath $repo -Directory | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'DriverVersion.props') })
if ($projects.Count -eq 0) { throw 'No driver version properties were found.' }
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$fixture = Join-Path $temporaryRoot ('DriverVersionChecks-' + [guid]::NewGuid().ToString('N'))
New-Item -Path $fixture -ItemType Directory | Out-Null
$checks = 0

function Invoke-Tool {
    param([string] $Tool, [string[]] $Arguments, [bool] $Ci = $false, [bool] $ShouldFail = $false)
    $start = [Diagnostics.ProcessStartInfo]::new($Tool)
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    foreach ($key in @('CI', 'TF_BUILD', 'GITHUB_ACTIONS')) { $start.Environment[$key] = if ($Ci) { 'true' } else { 'false' } }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) { $process.Kill($true); throw 'Version check timed out.' }
        $output = $stdout.GetAwaiter().GetResult()
        $errors = $stderr.GetAwaiter().GetResult()
        if (($process.ExitCode -ne 0) -ne $ShouldFail) { throw "Unexpected version-check exit code $($process.ExitCode): $output $errors" }
        return $output
    } finally { $process.Dispose() }
}

function Set-Manifest {
    param([string] $Version, [switch] $V1)
    $general = @{ GeneralInformation = @{ DriverVersion = $Version; VersionDate = '2000-01-01' } }
    $json = if ($V1) { @{ CrestronSerialDeviceApi = $general } } else { $general }
    $json | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $fixture 'manifest.json') -NoNewline
}

function Check-Build {
    param([string] $Configuration, [bool] $Ci, [string] $Initial, [string] $Expected, [string] $ReleaseVersion = '', [bool] $ShouldFail = $false, [string] $PackageVersion = '')
    Set-Manifest $Initial
    $arguments = @('msbuild', (Join-Path $fixture 'check.proj'), '-nologo', '-target:BumpDriverVersion', '-getProperty:DriverManifestVersion,Version,PackageVersion', "-p:Configuration=$Configuration", "-p:DriverReleaseVersion=$ReleaseVersion")
    if ($PackageVersion) { $arguments += "-p:PackageVersion=$PackageVersion" }
    $output = Invoke-Tool 'dotnet' $arguments -Ci $Ci -ShouldFail $ShouldFail
    if (-not $ShouldFail) {
        $result = ($output.Substring($output.IndexOf('{')) | ConvertFrom-Json).Properties
        $actual = (Get-Content -LiteralPath (Join-Path $fixture 'manifest.json') -Raw | ConvertFrom-Json).GeneralInformation.DriverVersion
        $expectedProject = ([version]$Expected).ToString($(if ($Configuration -eq 'Debug') { 4 } else { 3 }))
        $expectedPackage = if ($PackageVersion) { $PackageVersion } else { $expectedProject }
        if ($actual -ne $Expected -or $result.DriverManifestVersion -ne $Expected -or $result.Version -ne $expectedProject -or $result.PackageVersion -ne $expectedPackage) {
            throw "Version inconsistency: manifest $actual; properties $($result | ConvertTo-Json -Compress); expected $Expected/$expectedProject/$expectedPackage."
        }
    }
    $script:checks++
}

try {
    foreach ($project in $projects) {
        $properties = [Security.SecurityElement]::Escape((Join-Path $project.FullName 'DriverVersion.props'))
        $scriptPath = [Security.SecurityElement]::Escape((Join-Path $project.FullName 'BumpDriverVersion.ps1'))
        $manifest = [Security.SecurityElement]::Escape((Join-Path $fixture 'manifest.json'))
        @"
<Project>
  <PropertyGroup><DriverManifestFile>$manifest</DriverManifestFile></PropertyGroup>
  <Import Project="$properties" />
  <Target Name="BumpDriverVersion">
    <Exec Command="pwsh -NoProfile -NonInteractive -File &amp;quot;$scriptPath&amp;quot; -ManifestPath &amp;quot;$manifest&amp;quot; -Configuration &amp;quot;`$(Configuration)&amp;quot; -ReleaseVersion &amp;quot;`$(DriverReleaseVersion)&amp;quot;" />
  </Target>
</Project>
"@.Replace('&amp;quot;', '&quot;') | Set-Content -LiteralPath (Join-Path $fixture 'check.proj') -NoNewline
        Check-Build 'Debug' $false '2.0.001.0005' '2.0.001.0006'
        Check-Build 'Debug' $true '2.0.001.0005' '2.0.001.0006'
        Check-Build 'Release' $false '2.0.001.0005' '2.0.001.0005'
        Check-Build 'Release' $true '2.0.001.0005' '2.0.001.0000' '2.0.1'
        Check-Build 'Release' $true '2.0.001.0000' '2.0.001.0000' '2.0.1'
        Check-Build 'Release' $true '2.0.001.0005' '2.0.001.0000'
        Check-Build 'Release' $true '2.0.001.0005' '' '2.0.2' -ShouldFail $true
        Check-Build 'Release' $true '2.0.001.0005' '2.0.001.0000' '2.0.1' -PackageVersion '2.0.1-preview.1'
        Check-Build 'Debug' $false '2.0.001.65535' '' -ShouldFail $true
        Write-Host "$($project.Name): Debug increment and project metadata, unchanged local Release, exact/repeated CI release, mismatched release rejection, prerelease override, build overflow passed."
    }
    $manifestPath = Join-Path $fixture 'manifest.json'
    foreach ($v1 in @($false, $true)) {
        Set-Manifest '2.0.001.0005' -V1:$v1
        $selected = & (Join-Path $PSScriptRoot 'Get-DriverReleaseVersion.ps1') -ManifestPath $manifestPath -Tag 'v2.0.1' -RequireTag
        if ($selected.PackageVersion -ne '2.0.1' -or $selected.DriverReleaseVersion -ne '2.0.1') { throw 'Three-part release selection failed.' }
        $checks++
    }
    foreach ($tag in @('v2.0.2', 'v2.0.1.5', 'master')) {
        Invoke-Tool 'pwsh' @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'Get-DriverReleaseVersion.ps1'), '-ManifestPath', $manifestPath, '-Tag', $tag, '-RequireTag') -ShouldFail $true | Out-Null
        $checks++
    }
    $selected = & (Join-Path $PSScriptRoot 'Get-DriverReleaseVersion.ps1') -ManifestPath $manifestPath -Tag 'v2.0.1-preview.1' -RequireTag
    if ($selected.PackageVersion -ne '2.0.1-preview.1') { throw 'Prerelease selection failed.' }
    $checks++
    # Verify the artifact check reads the actual package, not just source settings.
    $packagePath = Join-Path $fixture 'Example.pkg'
    $zip = [IO.Compression.ZipFile]::Open($packagePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $writer = [IO.StreamWriter]::new($zip.CreateEntry('Example.dat').Open())
        try { $writer.Write('{"driverVersion":"2.0.1.0"}') } finally { $writer.Dispose() }
    } finally { $zip.Dispose() }
    foreach ($v1 in @($false, $true)) {
        Set-Manifest '2.0.001.0000' -V1:$v1
        & (Join-Path $PSScriptRoot 'Assert-DriverPackageVersion.ps1') -PackagePath $packagePath -ManifestPath $manifestPath -ReleaseVersion '2.0.1'
        $checks++
    }
    Invoke-Tool 'pwsh' @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'Assert-DriverPackageVersion.ps1'), '-PackagePath', $packagePath, '-ManifestPath', $manifestPath, '-ReleaseVersion', '2.0.2') -ShouldFail $true | Out-Null
    $checks++
    Write-Host "Passed $checks driver-version checks. No working manifest was changed."
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    if ([IO.Path]::GetDirectoryName($resolved).TrimEnd('\') -ne $temporaryRoot.TrimEnd('\') -or [IO.Path]::GetFileName($resolved) -notlike 'DriverVersionChecks-*') {
        throw 'Unexpected test cleanup path.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

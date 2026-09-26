# Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
param([Parameter(Mandatory)][string]$PackagePath, [string]$ToolArchivePath)
$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$version = '1.19.0'
$expectedHash = '5BC1A8313E21A9D20B1712160A952F6D54EABAC6E0541591E21911A31ACF5DBF'
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$work = Join-Path $temporaryRoot ('kasa-package-tools-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($work) | Out-Null
try {
    $archive = Join-Path $work 'tools.zip'
    if ($ToolArchivePath) {
        Copy-Item -LiteralPath (Resolve-Path -LiteralPath $ToolArchivePath).Path -Destination $archive
    } else {
        Invoke-WebRequest -Uri "https://github.com/oznetmaster/CrestronHomeDevTools/releases/download/v$version/CrestronHomeDevTools.Console-win-x64.zip" -OutFile $archive
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expectedHash) {
        throw 'Package normalization tool checksum mismatch.'
    }
    $tools = Join-Path $work 'tools'
    Expand-Archive -LiteralPath $archive -DestinationPath $tools
    $console = Join-Path $tools 'CrestronHomeDevTools.Console.exe'
    $reports = Join-Path ([IO.Path]::GetDirectoryName($package)) ('package-validation-' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($reports) | Out-Null
    & $console submission normalize-package --package $package --report (Join-Path $reports 'normalization.json')
    if ($LASTEXITCODE -ne 0) { throw 'Driver package normalization failed.' }
    # Verify the checked-in notices against this build's actual merged DLL bytes.
    # Updating dependencies requires reviewing the inventory and license texts.
    $inventory = Join-Path $PSScriptRoot '../dependency-notices.json'
    $build = [IO.Path]::GetDirectoryName($package)
    $driver = Join-Path $build ([IO.Path]::GetFileNameWithoutExtension($package) + '.dll')
    $include = Join-Path $work 'notices'
    [IO.Directory]::CreateDirectory($include) | Out-Null
    $receipt = Join-Path $reports 'notices-stage.json'
    & $console submission dependency-notices stage --manifest $inventory --merge-inputs (Join-Path $build 'merge_inputs.txt') --driver-assembly $driver --include-directory $include --receipt $receipt
    if ($LASTEXITCODE -ne 0) { throw 'Merged dependency notice inventory does not match this build.' }
    & $console submission dependency-notices verify --manifest $inventory --merge-inputs (Join-Path $build 'merge_inputs.txt') --driver-assembly $driver --receipt $receipt --package $package --report (Join-Path $reports 'packaged-notices.json')
    if ($LASTEXITCODE -ne 0) { throw 'Packaged dependency notices do not match the reviewed inventory.' }
    Write-Output "Package validation reports: $reports"
}
finally {
    $resolved = [IO.Path]::GetFullPath($work)
    if ([IO.Path]::GetDirectoryName($resolved).TrimEnd('\') -ne $temporaryRoot.TrimEnd('\') -or
        [IO.Path]::GetFileName($resolved) -notmatch '^kasa-package-tools-[a-f0-9]{32}$') {
        throw 'Temporary package-tool directory could not be verified for cleanup.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

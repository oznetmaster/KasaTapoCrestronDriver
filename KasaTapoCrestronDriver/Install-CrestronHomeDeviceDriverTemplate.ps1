[CmdletBinding()]
param(
	[ValidateSet('Debug', 'Release')]
	[string]$Configuration = 'Release',

	[string]$Version,

	[switch]$SkipPack,

	[switch]$SkipList,

	[string]$PackageId = 'CrestronHome.DeviceDriver.Template'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$templatePackProject = Join-Path $repoRoot 'templatepack.csproj'
$packageOutputDirectory = Join-Path $repoRoot 'artifacts\packages'

if (-not $SkipPack) {
	$packArguments = @(
		'pack',
		$templatePackProject,
		'--configuration', $Configuration,
		'--output', $packageOutputDirectory
	)

	if ($Version) {
		$packArguments += '/p:Version=' + $Version
	}

	Write-Host "Packing template from $templatePackProject"
	& dotnet @packArguments
	if ($LASTEXITCODE -ne 0) {
		throw 'dotnet pack failed.'
	}
}

if (-not (Test-Path $packageOutputDirectory)) {
	throw "Package output directory was not found: $packageOutputDirectory"
}

$package = Get-ChildItem -Path $packageOutputDirectory -Filter '*.nupkg' |
	Where-Object { $_.Name -notlike '*.symbols.nupkg' } |
	Sort-Object LastWriteTimeUtc -Descending |
	Select-Object -First 1

if (-not $package) {
	throw "No template package was found in $packageOutputDirectory"
}

Write-Host "Installing template package $($package.FullName)"
& dotnet new uninstall $PackageId | Out-Null
& dotnet new install $package.FullName --force
if ($LASTEXITCODE -ne 0) {
	throw 'dotnet new install failed.'
}

if (-not $SkipList) {
	& dotnet new list crestronhome-driver
}

Write-Host 'Template installation complete.'

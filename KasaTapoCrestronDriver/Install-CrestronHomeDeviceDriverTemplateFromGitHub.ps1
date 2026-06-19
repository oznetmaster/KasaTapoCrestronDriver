[CmdletBinding()]
param(
	[string]$Repository = 'username/reponame',

	[string]$Tag,

	[string]$AssetPattern = 'CrestronHome.DeviceDriver.Template*.nupkg',

	[string]$PackageId = 'CrestronHome.DeviceDriver.Template',

	[string]$DestinationDirectory = (Join-Path ([System.IO.Path]::GetTempPath()) 'CrestronHomeDeviceDriverTemplate'),

	[switch]$KeepPackage
)

$ErrorActionPreference = 'Stop'

if ($Repository -eq 'username/reponame') {
	throw 'Set -Repository to the GitHub owner/repository that publishes this template package, or update the default value in this script before distributing it.'
}

$releaseApiUrl = if ($Tag) {
	"https://api.github.com/repos/$Repository/releases/tags/$Tag"
}
else {
	"https://api.github.com/repos/$Repository/releases/latest"
}

$headers = @{
	'User-Agent' = 'CrestronHomeDeviceDriverTemplateInstaller'
	'Accept' = 'application/vnd.github+json'
}

Write-Host "Querying $releaseApiUrl"
$release = Invoke-RestMethod -Uri $releaseApiUrl -Headers $headers
$asset = $release.assets | Where-Object { $_.name -like $AssetPattern } | Select-Object -First 1

if (-not $asset) {
	throw "No release asset matched '$AssetPattern' in $Repository."
}

New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null
$packagePath = Join-Path $DestinationDirectory $asset.name

Write-Host "Downloading $($asset.name)"
Invoke-WebRequest -Uri $asset.browser_download_url -Headers $headers -OutFile $packagePath

Write-Host 'Installing template package'
& dotnet new uninstall $PackageId | Out-Null
& dotnet new install $packagePath --force
if ($LASTEXITCODE -ne 0) {
	throw 'dotnet new install failed.'
}

& dotnet new list crestronhome-driver

if (-not $KeepPackage) {
	Remove-Item $packagePath -Force -ErrorAction SilentlyContinue
}

Write-Host 'Template installation complete.'

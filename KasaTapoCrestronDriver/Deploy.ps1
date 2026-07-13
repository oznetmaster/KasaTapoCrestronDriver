param(
	[Parameter(Mandatory)][string] $PkgFile,
	[Parameter(Mandatory)][string] $ProcessorIP,
	[Parameter(Mandatory)][string] $User,
	[Parameter(Mandatory)][string] $Password,
	[switch] $Clean
)

Import-Module Posh-SSH -ErrorAction Stop

$credential = [System.Management.Automation.PSCredential]::new($User, (ConvertTo-SecureString $Password -AsPlainText -Force))
$importPath = '/user/ThirdPartyDrivers/Import'
$usedPath = '/user/Data/UsedThirdPartyDrivers'
$deviceManifest = '/user/Data/PyngDeviceManifest/DeviceManifest.cfg'
$localManifest = "$env:TEMP\DeviceManifest.cfg"

$driverManifestPath = Join-Path $PSScriptRoot 'KasaTapoCrestronDriver.json'
$driverManifest = Get-Content $driverManifestPath -Raw | ConvertFrom-Json
$manufacturer = ($driverManifest.GeneralInformation.Manufacturer -replace '\s', '').ToLower()
$model = ($driverManifest.GeneralInformation.BaseModel -replace '\s', '').ToLower()
$company = ($driverManifest.GeneralInformation.Developer.Company -replace '\s', '').ToLower()
$newVersion = $driverManifest.GeneralInformation.DriverVersion
$usedFolderPattern = "$manufacturer.$model*$company"
$pkgName = Split-Path $PkgFile -Leaf

Write-Host "Deploying $pkgName v$newVersion to $ProcessorIP..."

function Remove-SftpDirectory {
	param([int] $SessionId, [string] $Path)

	$children = Get-SFTPChildItem -SessionId $SessionId -Path $Path -ErrorAction SilentlyContinue
	foreach ($child in $children) {
		$childPath = "$Path/$($child.Name)"
		if ($child.IsDirectory) {
			Remove-SftpDirectory -SessionId $SessionId -Path $childPath
		}
		else {
			Remove-SFTPItem -SessionId $SessionId -Path $childPath -Force -ErrorAction SilentlyContinue
		}
	}

	Remove-SFTPItem -SessionId $SessionId -Path $Path -Force -ErrorAction SilentlyContinue
}

$sftpSession = New-SFTPSession -ComputerName $ProcessorIP -Credential $credential -AcceptKey -ErrorAction Stop
try {
	if ($Clean) {
		$stale = Get-SFTPChildItem -SessionId $sftpSession.SessionId -Path $usedPath -ErrorAction SilentlyContinue |
			Where-Object { $_.Name -like $usedFolderPattern }
		foreach ($directory in $stale) {
			Remove-SftpDirectory -SessionId $sftpSession.SessionId -Path "$usedPath/$($directory.Name)"
		}
	}

	Get-SFTPItem -SessionId $sftpSession.SessionId -Path $deviceManifest -Destination $env:TEMP -Force -ErrorAction Stop
	$cfgRaw = Get-Content $localManifest -Raw
	$driverGuidBase = "$manufacturer.$model"
	$cfgRaw = $cfgRaw -replace "(?<=`"DriverPath`":`"$driverGuidBase[^/]+/)[^/]+(?=/)", $newVersion
	$cfgRaw = $cfgRaw -replace "(?<=`"DriverFolderPath`":`"$driverGuidBase[^/]+/)[^`"]+", $newVersion
	$cfgRaw = $cfgRaw -replace "(?<=`"DriverVersion`":`")[^`"]+(?=`",`"DriverGuid`":`"$driverGuidBase)", $newVersion

	Set-Content -Path $localManifest -Value $cfgRaw -NoNewline -Encoding UTF8
	Set-SFTPItem -SessionId $sftpSession.SessionId -Path $localManifest -Destination ($deviceManifest.Substring(0, $deviceManifest.LastIndexOf('/'))) -Force -ErrorAction Stop
	Set-SFTPItem -SessionId $sftpSession.SessionId -Path $PkgFile -Destination $importPath -Force -ErrorAction Stop
}
finally {
	Remove-SFTPSession -SessionId $sftpSession.SessionId | Out-Null
}

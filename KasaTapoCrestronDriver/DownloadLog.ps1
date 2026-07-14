param(
	[string] $ProjectUserFile = "$PSScriptRoot\KasaTapoCrestronDriver.csproj.user",
	[string] $RemotePath = "/rm/SeawolfDiagnostic/$(Get-Date -Format 'yyyy-MM-dd').log",
	[string] $LocalPath = "$PSScriptRoot\DownloadedLog.log"
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ProjectUserFile)) {
	throw "Project user file not found: $ProjectUserFile"
}

[xml] $projectXml = Get-Content $ProjectUserFile
$ip = $projectXml.Project.PropertyGroup.CrestronHomeIP
$user = $projectXml.Project.PropertyGroup.CrestronHomeFtpUser
$password = $projectXml.Project.PropertyGroup.CrestronHomeSftpPassword

Import-Module Posh-SSH -ErrorAction Stop

$secure = ConvertTo-SecureString $password -AsPlainText -Force
$credential = [System.Management.Automation.PSCredential]::new($user, $secure)

$session = New-SFTPSession -ComputerName $ip -Credential $credential -AcceptKey -ErrorAction Stop
try {
	Get-SFTPItem -SessionId $session.SessionId -Path $RemotePath -Destination $PSScriptRoot -Force
	$downloadedName = Join-Path $PSScriptRoot (Split-Path $RemotePath -Leaf)
	if ($downloadedName -ne $LocalPath) {
		Move-Item -Path $downloadedName -Destination $LocalPath -Force
	}
	Write-Host "Downloaded to $LocalPath"
}
finally {
	Remove-SFTPSession -SessionId $session.SessionId | Out-Null
}

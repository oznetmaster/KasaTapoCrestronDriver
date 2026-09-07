param(
	[string] $ProjectUserFile = "$PSScriptRoot\KasaTapoCrestronDriver.csproj.user",
	[Parameter(Mandatory)][string] $LocalFile,
	[Parameter(Mandatory)][string] $RemoteDestinationDir,
	[string] $RemoteFileName
)

$ErrorActionPreference = 'Stop'

[xml] $projectXml = Get-Content $ProjectUserFile
$ip = $projectXml.Project.PropertyGroup.CrestronHomeIP
$user = $projectXml.Project.PropertyGroup.CrestronHomeFtpUser
$password = $projectXml.Project.PropertyGroup.CrestronHomeSftpPassword

Import-Module Posh-SSH -ErrorAction Stop

$secure = ConvertTo-SecureString $password -AsPlainText -Force
$credential = [System.Management.Automation.PSCredential]::new($user, $secure)
$sftp = New-SFTPSession -ComputerName $ip -Credential $credential -Force -ErrorAction Stop
try {
	if ($RemoteFileName) {
		$tempLocal = Join-Path ([System.IO.Path]::GetTempPath()) $RemoteFileName
		Copy-Item -Path $LocalFile -Destination $tempLocal -Force
		Set-SFTPItem -SessionId $sftp.SessionId -Path $tempLocal -Destination $RemoteDestinationDir -Force -ErrorAction Stop
		Remove-Item $tempLocal -Force
	}
	else {
		Set-SFTPItem -SessionId $sftp.SessionId -Path $LocalFile -Destination $RemoteDestinationDir -Force -ErrorAction Stop
	}
	Write-Host "Uploaded $LocalFile to $RemoteDestinationDir"
}
finally {
	Remove-SFTPSession -SessionId $sftp.SessionId | Out-Null
}

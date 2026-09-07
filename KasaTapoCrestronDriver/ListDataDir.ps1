param(
	[string] $ProjectUserFile = "$PSScriptRoot\KasaTapoCrestronDriver.csproj.user",
	[string] $Path = '/user/Data'
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
	Get-SFTPChildItem -SessionId $sftp.SessionId -Path $Path -ErrorAction Stop | Select-Object Name, IsDirectory, Length, LastWriteTime | Format-Table -AutoSize
}
finally {
	Remove-SFTPSession -SessionId $sftp.SessionId | Out-Null
}

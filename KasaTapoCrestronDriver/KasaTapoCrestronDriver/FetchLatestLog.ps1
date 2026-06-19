param(
	[string] $ProjectUserFile = "$PSScriptRoot\KasaTapoCrestronDriver.csproj.user",
	[string] $OutputDirectory = 'C:\Temp'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ProjectUserFile)) {
	throw "Project user file not found: $ProjectUserFile"
}

[xml] $projectXml = Get-Content $ProjectUserFile
$ip = $projectXml.Project.PropertyGroup.CrestronHomeIP
$user = $projectXml.Project.PropertyGroup.CrestronHomeFtpUser
$password = $projectXml.Project.PropertyGroup.CrestronHomeSftpPassword

if ([string]::IsNullOrWhiteSpace($ip) -or [string]::IsNullOrWhiteSpace($user) -or [string]::IsNullOrWhiteSpace($password)) {
	throw "Missing CrestronHomeIP / CrestronHomeFtpUser / CrestronHomeSftpPassword in $ProjectUserFile"
}

Import-Module Posh-SSH -ErrorAction Stop

$secure = ConvertTo-SecureString $password -AsPlainText -Force
$credential = [System.Management.Automation.PSCredential]::new($user, $secure)
if (-not (Test-Path $OutputDirectory)) {
	New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
}

$sftp = New-SFTPSession -ComputerName $ip -Credential $credential -AcceptKey -ErrorAction Stop
try {
	$remote = "/rm/SeawolfDiagnostic/$((Get-Date -Format 'yyyy-MM-dd')).log"
	Get-SFTPItem -SessionId $sftp.SessionId -Path $remote -Destination $OutputDirectory -Force -ErrorAction Stop
	Get-ChildItem $OutputDirectory | Sort-Object LastWriteTime -Descending | Select-Object -First 1 FullName, Length, LastWriteTime | Format-List
}
finally {
	Remove-SFTPSession -SessionId $sftp.SessionId | Out-Null
}

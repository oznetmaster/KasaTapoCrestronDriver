param(
	[string] $ProjectUserFile = "$PSScriptRoot\KasaTapoCrestronDriver.csproj.user",
	[string] $Command = "grep -a '8012184b2d44b892681bbba81fc6331f1d932836' /rm/SeawolfDiagnostic/$(Get-Date -Format 'yyyy-MM-dd').log | tail -n 300"
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

$session = New-SSHSession -ComputerName $ip -Credential $credential -Force -ErrorAction Stop
try {
	$result = Invoke-SSHCommand -SSHSession $session -Command $Command -TimeOut 30
	Write-Output $result.Output
}
finally {
	Remove-SSHSession -SSHSession $session | Out-Null
}

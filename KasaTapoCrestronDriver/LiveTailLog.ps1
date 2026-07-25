param(
	[string] $ProjectUserFile = "$PSScriptRoot\KasaTapoCrestronDriver.csproj.user"
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
	$stream = New-SSHShellStream -SSHSession $session
	Start-Sleep -Seconds 2
	Write-Host "Streaming the processor diagnostic console on $ip. Press Ctrl+C to stop."
	while ($true) {
		Start-Sleep -Seconds 2
		$output = $stream.Read()
		if (-not [string]::IsNullOrEmpty($output)) {
			Write-Host $output -NoNewline
		}
	}
}
finally {
	Remove-SSHSession -SSHSession $session | Out-Null
}

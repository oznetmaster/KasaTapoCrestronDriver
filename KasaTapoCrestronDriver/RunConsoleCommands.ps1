param(
	[string] $ProjectUserFile = "$PSScriptRoot\KasaTapoCrestronDriver.csproj.user",
	[string[]] $Commands,
	[int] $ResponseWaitMilliseconds = 2000
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
	Start-Sleep -Milliseconds 500
	$null = $stream.Read()

	foreach ($command in $Commands) {
		$stream.WriteLine($command)
		Start-Sleep -Milliseconds $ResponseWaitMilliseconds
		Write-Output "`$ $command"
		$output = $stream.Read()
		if (-not [string]::IsNullOrWhiteSpace($output)) {
			Write-Output $output
		}
	}
}
finally {
	Remove-SSHSession -SSHSession $session | Out-Null
}

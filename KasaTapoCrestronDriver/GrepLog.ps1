param(
	[string] $ProjectUserFile = "$PSScriptRoot\KasaTapoCrestronDriver.csproj.user",
	[string] $Pattern = "startup connect"
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

$session = New-SSHSession -ComputerName $ip -Credential $credential -AcceptKey -ErrorAction Stop
try {
	$stream = New-SSHShellStream -SSHSession $session
	$logPath = "/rm/SeawolfDiagnostic/$(Get-Date -Format 'yyyy-MM-dd').log"
	$stream.WriteLine("")
	Start-Sleep -Seconds 1
	$stream.Read() | Out-Null
	$stream.WriteLine("grep -a `"$Pattern`" $logPath > /tmp/out.txt 2>&1; wc -l < /tmp/out.txt")
	Start-Sleep -Seconds 10
	$output = $stream.Read()
	Write-Host $output
	Write-Host "----LAST 200----"
	$stream.WriteLine("tail -n 200 /tmp/out.txt")
	Start-Sleep -Seconds 6
	$output2 = $stream.Read()
	Write-Host $output2
}
finally {
	Remove-SSHSession -SSHSession $session | Out-Null
}

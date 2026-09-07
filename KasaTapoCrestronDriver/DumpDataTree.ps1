param(
	[string] $ProjectUserFile = "$PSScriptRoot\KasaTapoCrestronDriver.csproj.user",
	[string] $RemoteRoot = '/user/Data',
	[string] $LocalRoot = 'C:\Temp\DataDump'
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

if (Test-Path $LocalRoot) { Remove-Item $LocalRoot -Recurse -Force }
New-Item -ItemType Directory -Path $LocalRoot | Out-Null

function Copy-RemoteTree {
	param([string] $RemotePath, [string] $LocalPath)

	$items = Get-SFTPChildItem -SessionId $sftp.SessionId -Path $RemotePath -ErrorAction SilentlyContinue
	foreach ($item in $items) {
		$remoteChild = "$RemotePath/$($item.Name)"
		$localChild = Join-Path $LocalPath $item.Name
		if ($item.IsDirectory) {
			New-Item -ItemType Directory -Path $localChild -Force | Out-Null
			Copy-RemoteTree -RemotePath $remoteChild -LocalPath $localChild
		}
		else {
			try {
				Get-SFTPItem -SessionId $sftp.SessionId -Path $remoteChild -Destination $LocalPath -Force -ErrorAction Stop
			}
			catch {
				Write-Warning "Failed to download $remoteChild : $_"
			}
		}
	}
}

try {
	Copy-RemoteTree -RemotePath $RemoteRoot -LocalPath $LocalRoot
	Write-Host "Done. Files under $LocalRoot"
}
finally {
	Remove-SFTPSession -SessionId $sftp.SessionId | Out-Null
}

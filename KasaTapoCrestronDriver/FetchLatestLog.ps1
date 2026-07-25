param(
	[string] $ProjectUserFile = "$PSScriptRoot\KasaTapoCrestronDriver.csproj.user",
	[string] $OutputDirectory = 'C:\Temp',
	[int] $WaitForNewFlushSeconds = 0,
	[int] $PollIntervalSeconds = 15
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

function Get-RemoteLogMetadata {
	param(
		[int] $SessionId,
		[string] $RemotePath
	)

	$remoteDirectory = [System.IO.Path]::GetDirectoryName($RemotePath).Replace('\', '/')
	$remoteFileName = [System.IO.Path]::GetFileName($RemotePath)
	$remoteFile = Get-SFTPChildItem -SessionId $SessionId -Path $remoteDirectory -ErrorAction Stop | Where-Object { $_.Name -eq $remoteFileName } | Select-Object -First 1
	if ($null -eq $remoteFile) {
		throw "Remote log file not found: $RemotePath"
	}

	return $remoteFile
}

$sftp = New-SFTPSession -ComputerName $ip -Credential $credential -Force -ErrorAction Stop
try {
	$remote = "/rm/SeawolfDiagnostic/$((Get-Date -Format 'yyyy-MM-dd')).log"
	$baselineWriteTimeUtc = $null

	if ($WaitForNewFlushSeconds -gt 0) {
		$existingRemoteFile = Get-RemoteLogMetadata -SessionId $sftp.SessionId -RemotePath $remote
		$baselineWriteTimeUtc = $existingRemoteFile.LastWriteTimeUtc
		$deadlineUtc = [DateTime]::UtcNow.AddSeconds($WaitForNewFlushSeconds)

		while ([DateTime]::UtcNow -lt $deadlineUtc) {
			Start-Sleep -Seconds $PollIntervalSeconds
			$currentRemoteFile = Get-RemoteLogMetadata -SessionId $sftp.SessionId -RemotePath $remote
			if ($currentRemoteFile.LastWriteTimeUtc -gt $baselineWriteTimeUtc) {
				break
			}
		}
	}

	Get-SFTPItem -SessionId $sftp.SessionId -Path $remote -Destination $OutputDirectory -Force -ErrorAction Stop
	$downloadedFile = Get-Item (Join-Path $OutputDirectory ([System.IO.Path]::GetFileName($remote)))
	$currentRemoteMetadata = Get-RemoteLogMetadata -SessionId $sftp.SessionId -RemotePath $remote
	[pscustomobject]@{
		FullName = $downloadedFile.FullName
		Length = $downloadedFile.Length
		LastWriteTime = $downloadedFile.LastWriteTime
		RemoteLastWriteTime = $currentRemoteMetadata.LastWriteTime
		WaitedForNewFlush = $WaitForNewFlushSeconds -gt 0
		FlushAdvanced = $null -eq $baselineWriteTimeUtc -or $currentRemoteMetadata.LastWriteTimeUtc -gt $baselineWriteTimeUtc
	} | Format-List
}
finally {
	Remove-SFTPSession -SessionId $sftp.SessionId | Out-Null
}

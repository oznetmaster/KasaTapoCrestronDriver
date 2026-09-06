param(
	[string] $ProjectUserFile = "$PSScriptRoot\KasaTapoCrestronDriver.csproj.user",
	[string[]] $Include = @(),
	[string[]] $Exclude = @('LG', 'LGTV', 'LG TV'),
	[string] $OutputFile,
	[int] $KeepAliveSeconds = 240
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
	if ($OutputFile) {
		$outDir = Split-Path -Parent $OutputFile
		if ($outDir -and -not (Test-Path $outDir)) {
			New-Item -ItemType Directory -Path $outDir -Force | Out-Null
		}
		Write-Host "Also writing to $OutputFile"
	}

	$pending = ''
	$lastKeepAlive = Get-Date

	while ($true) {
		Start-Sleep -Milliseconds 500

		$pending += $stream.Read()
		if ($pending.Contains("`n")) {
			$lines = $pending -split "`r?`n"
			$pending = $lines[-1]
			foreach ($line in $lines[0..($lines.Count - 2)]) {
				if ([string]::IsNullOrWhiteSpace($line)) { continue }
				if ($Exclude.Count -gt 0 -and ($Exclude | Where-Object { $line -match [regex]::Escape($_) })) { continue }
				if ($Include.Count -gt 0 -and -not ($Include | Where-Object { $line -match [regex]::Escape($_) })) { continue }

				$stamped = "{0:HH:mm:ss} {1}" -f (Get-Date), $line
				Write-Host $stamped
				if ($OutputFile) {
					Add-Content -Path $OutputFile -Value $stamped
				}
			}
		}

		# The processor logs off idle SSH sessions (~20 minutes), which silently
		# kills the tail. Send a harmless newline periodically to keep it alive.
		if (((Get-Date) - $lastKeepAlive).TotalSeconds -ge $KeepAliveSeconds) {
			$stream.WriteLine('')
			$lastKeepAlive = Get-Date
		}
	}
}
finally {
	Remove-SSHSession -SSHSession $session | Out-Null
}

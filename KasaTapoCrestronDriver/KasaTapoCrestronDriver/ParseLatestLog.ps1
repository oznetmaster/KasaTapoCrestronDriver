param(
	[Parameter(Mandatory)][string] $LogPath,
	[int] $TailLines = 200
)

if (-not (Test-Path $LogPath)) {
	throw "Log file not found: $LogPath"
}

Get-Content $LogPath -Tail $TailLines |
	Select-String -Pattern 'error|warning|exception|fail' -CaseSensitive:$false

param(
	[int] $Seconds = 15
)

Write-Host "Waiting $Seconds second(s) before the next manual step..."
Start-Sleep -Seconds $Seconds

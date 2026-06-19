param(
	[Parameter(Mandatory)][string] $ManifestPath,
	[string] $Configuration = 'Debug'
)

if (-not (Test-Path $ManifestPath)) {
	exit 0
}

$content = Get-Content $ManifestPath -Raw
$match = [regex]::Match($content, '(?<="DriverVersion":\s*")(?<major>\d+)\.(?<minor>\d+)\.(?<release>\d+)\.(?<build>\d+)(?=")')
if (-not $match.Success) {
	Write-Warning 'DriverVersion must contain four numeric components.'
	exit 0
}

$major = $match.Groups['major'].Value
$minor = $match.Groups['minor'].Value
$release = [int]$match.Groups['release'].Value
$build = [int]$match.Groups['build'].Value

switch ($Configuration) {
	'Debug' {
		$newVersion = '{0}.{1}.{2}.{3}' -f $major, $minor, $release.ToString('000'), ($build + 1).ToString('0000')
	}
	'Release' {
		$newVersion = '{0}.{1}.{2}.0000' -f $major, $minor, ($release + 1).ToString('000')
	}
	default {
		exit 0
	}
}

$content = [regex]::Replace($content, '(?<="DriverVersion":\s*")[^"]+(?=")', $newVersion, 1)
$content = [regex]::Replace($content, '(?<="VersionDate":\s*")[^"]+(?=")', (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff'), 1)
Set-Content -Path $ManifestPath -Value $content -NoNewline

param(
	[Parameter(Mandatory)][string] $ManifestPath,
	[string] $Configuration = 'Debug'
)

if (-not (Test-Path $ManifestPath)) {
	exit 0
}

# Release version bumps (which advance the release digit and reset build) are reserved for CI,
# so local Release builds used for validation/deployment don't churn the manifest version.
# Debug builds always bump the build digit, since those are used for iterative local deployment.
$isCiBuild = $env:CI -or $env:TF_BUILD -or $env:GITHUB_ACTIONS
if ($Configuration -eq 'Release' -and -not $isCiBuild) {
	Write-Host 'BumpDriverVersion: skipping Release version bump outside CI.'
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

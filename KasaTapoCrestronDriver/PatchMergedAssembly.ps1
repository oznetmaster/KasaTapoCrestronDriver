param(
	[Parameter(Mandatory)][string] $AssemblyPath,
	[string] $OutputPath = ''
)

if (-not $OutputPath) {
	$dir = [System.IO.Path]::GetDirectoryName($AssemblyPath)
	$stem = [System.IO.Path]::GetFileNameWithoutExtension($AssemblyPath)
	$OutputPath = [System.IO.Path]::Combine($dir, $stem + '_patched.dll')
}

$cecilPath = Get-ChildItem "$env:USERPROFILE\.dotnet\tools\.store\dotnet-ilrepack" -Recurse -Filter 'Mono.Cecil.dll' -ErrorAction SilentlyContinue |
	Select-Object -First 1 -ExpandProperty FullName
if (-not $cecilPath) {
	Write-Warning 'PatchMergedAssembly: Mono.Cecil.dll not found - skipping patch.'
	exit 0
}

[System.Reflection.Assembly]::LoadFrom($cecilPath) | Out-Null

function ShouldRename([Mono.Cecil.TypeDefinition] $typeDefinition) {
	return ($typeDefinition.Namespace -eq 'System' -or $typeDefinition.Namespace.StartsWith('System.'))
}

$assemblyBytes = [System.IO.File]::ReadAllBytes($AssemblyPath)
$assemblyStream = [System.IO.MemoryStream]::new($assemblyBytes)
$readerParameters = [Mono.Cecil.ReaderParameters]::new()
$assemblyDefinition = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($assemblyStream, $readerParameters)
$module = $assemblyDefinition.MainModule
$count = 0

foreach ($typeDefinition in $module.Types) {
	if (ShouldRename $typeDefinition) {
		$oldName = $typeDefinition.FullName
		$typeDefinition.Namespace = '_Stripped.' + $typeDefinition.Namespace
		$count++
		Write-Host "  Renamed: $oldName -> $($typeDefinition.FullName)"
	}
}

$outDir = [System.IO.Path]::GetDirectoryName($OutputPath)
if ($outDir -and -not (Test-Path $outDir)) {
	New-Item -ItemType Directory -Path $outDir | Out-Null
}

$tempPath = $OutputPath + '.tmp'
try {
	$fileStream = [System.IO.File]::Open($tempPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
	try {
		$assemblyDefinition.Write($fileStream)
	}
	finally {
		$fileStream.Dispose()
	}

	$assemblyDefinition.Dispose()
	[System.IO.File]::Copy($tempPath, $OutputPath, $true)
	Remove-Item $tempPath -Force
	Write-Host "PatchMergedAssembly: $count type(s) renamed -> $OutputPath"
	exit 0
}
catch {
	$assemblyDefinition.Dispose()
	if (Test-Path $tempPath) {
		Remove-Item $tempPath -Force
	}

	Write-Error "PatchMergedAssembly: Write failed - $_"
	exit 1
}

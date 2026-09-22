param(
	[Parameter(Mandatory)][string] $AssemblyPath,
	[string] $OutputPath = '',
	[string] $ReferenceAssemblyDirectory = $env:NET472_REFERENCE_ASSEMBLIES,
	[string] $SdkLibDir = $env:CRESTRON_DRIVER_SDK_LIBRARIES
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
$resolver  = [Mono.Cecil.DefaultAssemblyResolver]::new()
foreach ($directory in @([System.IO.Path]::GetDirectoryName($AssemblyPath), $ReferenceAssemblyDirectory, $SdkLibDir)) {
	if (-not [string]::IsNullOrWhiteSpace($directory) -and (Test-Path -LiteralPath $directory)) {
		$resolver.AddSearchDirectory($directory)
	}
}
$readerParameters.AssemblyResolver = $resolver
$assemblyDefinition    = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($assemblyStream, $readerParameters)
$module    = $assemblyDefinition.MainModule
$count     = 0

# Cecil reads custom-attribute blobs lazily. Decode them before changing type names:
# enum argument types and typeof values then refer to the original definitions and
# are re-encoded with their new names when the module is written. Otherwise Cecil
# can copy an untouched blob containing a now-invalid serialized enum type name.
function ResolveAttributes($provider) {
	foreach ($attribute in $provider.CustomAttributes) {
		$null = $attribute.ConstructorArguments.Count
		$null = $attribute.Properties.Count
		$null = $attribute.Fields.Count
	}
}

function ResolveTypeAttributes($type) {
	ResolveAttributes $type
	foreach ($parameter in $type.GenericParameters) { ResolveAttributes $parameter }
	foreach ($field in $type.Fields) { ResolveAttributes $field }
	foreach ($property in $type.Properties) { ResolveAttributes $property }
	foreach ($event in $type.Events) { ResolveAttributes $event }
	foreach ($method in $type.Methods) {
		ResolveAttributes $method
		ResolveAttributes $method.MethodReturnType
		foreach ($parameter in $method.Parameters) { ResolveAttributes $parameter }
		foreach ($parameter in $method.GenericParameters) { ResolveAttributes $parameter }
	}
	foreach ($nested in $type.NestedTypes) { ResolveTypeAttributes $nested }
}

ResolveAttributes $assemblyDefinition
ResolveAttributes $module
foreach ($type in $module.Types) { ResolveTypeAttributes $type }

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
	$resolver.Dispose()
	$assemblyStream.Dispose()
	[System.IO.File]::Copy($tempPath, $OutputPath, $true)
	Remove-Item $tempPath -Force
	Write-Host "PatchMergedAssembly: $count type(s) renamed -> $OutputPath"
	exit 0
}
catch {
	$assemblyDefinition.Dispose()
	$resolver.Dispose()
	$assemblyStream.Dispose()
	if (Test-Path $tempPath) {
		Remove-Item $tempPath -Force
	}

	Write-Error "PatchMergedAssembly: Write failed - $_"
	exit 1
}

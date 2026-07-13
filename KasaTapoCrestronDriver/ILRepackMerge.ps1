param(
	[Parameter(Mandatory)][string] $TargetPath,
	[Parameter(Mandatory)][string] $InputListFile,
	[Parameter(Mandatory)][string] $LibDir,
	[string] $SdkLibDir = $env:CRESTRON_DRIVER_SDK_LIBRARIES,
	[string] $FxRefDir = $env:NET472_REFERENCE_ASSEMBLIES,
	[string] $FxRuntimeDir = $env:NETFX_RUNTIME_DIR
)

$inputs = Get-Content $InputListFile | Where-Object { $_ -ne '' } | Where-Object { Test-Path $_ }
if ($inputs.Count -eq 0) {
	Write-Error "No valid input files found in $InputListFile"
	exit 1
}

$libArgs = @("/lib:`"$LibDir`"")
if (-not [string]::IsNullOrWhiteSpace($SdkLibDir) -and (Test-Path $SdkLibDir)) {
	$libArgs += "/lib:`"$SdkLibDir`""
}
if (-not [string]::IsNullOrWhiteSpace($FxRefDir) -and (Test-Path $FxRefDir)) {
	$libArgs += "/lib:`"$FxRefDir`""
}
if (-not [string]::IsNullOrWhiteSpace($FxRuntimeDir) -and (Test-Path $FxRuntimeDir)) {
	$libArgs += "/lib:`"$FxRuntimeDir`""
}

$inputArgs = $inputs | ForEach-Object { "`"$_`"" }
$allArgs = @('/internalize', '/allowdup', '/allowduplicateresources', "/out:`"$TargetPath`"") + $libArgs + $inputArgs
$cmd = 'ilrepack ' + ($allArgs -join ' ')
Write-Host $cmd
Invoke-Expression $cmd

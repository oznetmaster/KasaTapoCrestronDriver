param([Parameter(Mandatory)][string] $AssemblyPath)
$ErrorActionPreference = 'Stop'
# Run in a fresh Windows PowerShell process against the final net472 patched DLL.
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $AssemblyPath).Path)
$flags = [Reflection.BindingFlags]'Static,NonPublic,Public'
$wireJson = $assembly.GetType('KasaTapoClient.Internal.WireJson', $true)
$serialize = $wireJson.GetMethod('Serialize', $flags)
$requestType = $assembly.GetType('KasaTapoClient.Internal.WireRequest`1', $true).MakeGenericType([object])
$request = [Activator]::CreateInstance($requestType)
$requestType.GetProperty('Method').SetValue($request, 'get_device_info', $null)
$json = $serialize.MakeGenericMethod($requestType).Invoke($null, @($request))
if ($json -ne '{"method":"get_device_info"}') { throw "Merged property/ignore attributes failed: $json" }

$baseType = $assembly.GetType('KasaTapoClient.Internal.TpapLoginParametersDto', $true)
$derivedType = $assembly.GetType('KasaTapoClient.Internal.TpapRegisterParametersDto', $true)
$register = [Activator]::CreateInstance($derivedType)
$json = $serialize.MakeGenericMethod($baseType).Invoke($null, @($register))
$decoded = ConvertFrom-Json $json
if ('stok' -notin $decoded.PSObject.Properties.Name -or $null -ne $decoded.stok) {
    throw 'Merged derived-type metadata lost the required null stok property.'
}
if ($assembly.GetReferencedAssemblies().Name -match '^(Newtonsoft[.]Json|log4net)$') {
    throw 'Obsolete JSON or logging assembly reference remains.'
}
foreach ($name in @('Newtonsoft.Json.JsonConvert', 'log4net.ILog')) {
    if ($null -ne $assembly.GetType($name, $false)) { throw "Obsolete merged type remains: $name" }
}
Write-Host 'Merged net472 JSON contracts passed: property names, null omission, derived authentication model and dependency removal.'

param(
	[string] $LocalManifestPath = 'C:\Temp\DataDump\PyngDeviceManifest\DeviceManifest.cfg',
	[int] $DeviceId = 52758,
	[string] $OutputPath = 'C:\Temp\DeviceManifest.fixed.cfg'
)

$ErrorActionPreference = 'Stop'

$raw = Get-Content $LocalManifestPath -Raw
$obj = $raw | ConvertFrom-Json

$key = $DeviceId.ToString()
$removed = $false

if ($obj.Devices.PSObject.Properties[$key]) {
	Write-Host "Removing top-level device entry: $($obj.Devices.$key.Name) (Id=$DeviceId)"
	$obj.Devices.PSObject.Properties.Remove($key)
	$removed = $true
}
else {
	foreach ($deviceProp in $obj.Devices.PSObject.Properties) {
		if ($deviceProp.Name -eq '$type') { continue }
		$device = $deviceProp.Value
		if ($device.ChildDevices) {
			$match = $device.ChildDevices | Where-Object { $_.Id -eq $DeviceId }
			if ($match) {
				Write-Host "Removing child device entry: $($match.Name) (Id=$DeviceId) from parent $($device.Name) (Id=$($device.Id))"
				$device.ChildDevices = @($device.ChildDevices | Where-Object { $_.Id -ne $DeviceId })
				$removed = $true
				break
			}
		}
	}
}

if (-not $removed) {
	throw "Device id $DeviceId not found in manifest at $LocalManifestPath (checked top-level and ChildDevices)"
}

# Re-serialize preserving compact single-line style similar to original (Crestron's JSON.NET output is not indented)
$json = $obj | ConvertTo-Json -Depth 100 -Compress

Set-Content -Path $OutputPath -Value $json -NoNewline -Encoding UTF8
Write-Host "Wrote corrected manifest to $OutputPath"

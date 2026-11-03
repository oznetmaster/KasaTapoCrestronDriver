# Copyright (c) 2026 Neil Colvin. MIT License; see LICENSE.
# Shared source: oznetmaster/BuildAutomation/PublicDriverPackaging.
[CmdletBinding()]
param([Parameter(Mandatory)][string[]]$Path)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
$script:archiveCount = 0
$script:driverPackageCount = 0
$limit = 256MB

function Test-Archive([IO.Stream]$Stream, [string]$Label, [int]$Depth) {
    if ($Depth -gt 6) { throw "Public artifact nesting exceeds the inspection limit: $Label" }
    $archive = [IO.Compression.ZipArchive]::new($Stream, [IO.Compression.ZipArchiveMode]::Read, $true)
    try {
        $script:archiveCount++
        if ($archive.Entries.Count -gt 100000) { throw "Public artifact has too many entries: $Label" }
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            if ($name.EndsWith('/')) { continue }
            $extension = [IO.Path]::GetExtension($name).ToLowerInvariant()
            if ($extension -eq '.pdf') { throw "Submission-only PDF in public artifact: $Label!$name" }
            if ($extension -notin @('.pkg', '.nupkg', '.snupkg', '.zip')) { continue }
            if ($extension -eq '.pkg') { $script:driverPackageCount++ }
            if ($entry.Length -gt $limit) { throw "Nested public archive exceeds the inspection limit: $Label!$name" }
            $source = $entry.Open()
            $memory = [IO.MemoryStream]::new()
            try {
                $buffer = [byte[]]::new(65536)
                while (($count = $source.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    if ($memory.Length + $count -gt $limit) { throw "Nested public archive exceeds the inspection limit: $Label!$name" }
                    $memory.Write($buffer, 0, $count)
                }
                $memory.Position = 0
                Test-Archive $memory "$Label!$name" ($Depth + 1)
            } finally { $memory.Dispose(); $source.Dispose() }
        }
    } finally { $archive.Dispose() }
}

$pathComparison = if ($IsWindows) { [StringComparer]::OrdinalIgnoreCase } else { [StringComparer]::Ordinal }
$files = [Collections.Generic.HashSet[string]]::new($pathComparison)
foreach ($selected in $Path) {
    $item = Get-Item -LiteralPath $selected -ErrorAction Stop
    if ($item.PSIsContainer) {
        foreach ($file in Get-ChildItem -LiteralPath $item.FullName -File -Recurse) { [void]$files.Add($file.FullName) }
    } else { [void]$files.Add($item.FullName) }
}
foreach ($file in $files) {
    $extension = [IO.Path]::GetExtension($file).ToLowerInvariant()
    if ($extension -eq '.pdf') { throw "Submission-only PDF among public release assets: $file" }
    if ($extension -notin @('.pkg', '.nupkg', '.snupkg', '.zip')) { continue }
    if ($extension -eq '.pkg') { $script:driverPackageCount++ }
    $stream = [IO.File]::OpenRead($file)
    try { Test-Archive $stream ([IO.Path]::GetFileName($file)) 0 }
    finally { $stream.Dispose() }
}
if ($script:archiveCount -eq 0) { throw 'No public package or archive was inspected. Select the actual release assets.' }
Write-Output "Public packaging verified: $($files.Count) files, $script:archiveCount archives, $script:driverPackageCount driver payloads; no submission PDFs."

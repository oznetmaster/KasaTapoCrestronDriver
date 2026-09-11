# Copyright (c) 2026 Neil Colvin.
# Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

param([ValidateSet('Debug','Release')][string]$Configuration='Debug')
$project = Join-Path $PSScriptRoot 'KasaTapoCrestronDriver.ProcessorTests.csproj'
[xml]$xml = Get-Content -LiteralPath $project -Raw
$sdk = $xml.SelectSingleNode("/Project/PropertyGroup/ProcessorTestSdkRoot").InnerText
$sdk = $sdk.Replace('$(MSBuildProjectDirectory)', $PSScriptRoot)
& (Join-Path $sdk 'PrepareDesktopRunner.ps1') -ProjectUserFile ($project + '.user') -Configuration $Configuration -Port 0
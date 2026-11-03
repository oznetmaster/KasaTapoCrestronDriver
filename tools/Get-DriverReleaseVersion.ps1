# Copyright (c) 2026 Neil Colvin.
# Licensed under the MIT License with Commons Clause; see repository LICENSE.
param([Parameter(Mandatory)][string[]]$ManifestPath,[string]$Tag,[switch]$RequireTag)
$ErrorActionPreference = 'Stop'
& "$PSScriptRoot/../build/shared/driver-versioning/Get-DriverReleaseVersion.ps1" @PSBoundParameters

# Copyright (c) 2026 Neil Colvin.
# Licensed under the MIT License with Commons Clause; see repository LICENSE.
param([Parameter(Mandatory)][string]$ManifestPath,[string]$Configuration='Debug',[string]$ReleaseVersion)
$ErrorActionPreference = 'Stop'
& "$PSScriptRoot/../build/shared/driver-versioning/BumpDriverVersion.ps1" @PSBoundParameters

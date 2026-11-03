# Copyright (c) 2026 Neil Colvin.
# Licensed under the MIT License with Commons Clause; see repository LICENSE.
param([Parameter(Mandatory)][string]$PackagePath,[Parameter(Mandatory)][string]$ManifestPath,[Parameter(Mandatory)][string]$ReleaseVersion)
$ErrorActionPreference = 'Stop'
& "$PSScriptRoot/../build/shared/driver-versioning/Assert-DriverPackageVersion.ps1" @PSBoundParameters

# Copyright (c) 2026 Neil Colvin.
# Licensed under the MIT License with Commons Clause; see repository LICENSE.
$ErrorActionPreference = 'Stop'
& "$PSScriptRoot/../build/shared/driver-versioning/Test-DriverVersioning.ps1" -Root (Split-Path $PSScriptRoot -Parent)

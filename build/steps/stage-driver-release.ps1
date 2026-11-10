# Copyright (c) 2026 Neil Colvin. MIT with Commons Clause; see LICENSE.
& ./build/Stage-DriverRelease.ps1 -Version $env:RELEASE_VERSION
if ($LASTEXITCODE -ne 0) { throw 'Driver release staging failed.' }

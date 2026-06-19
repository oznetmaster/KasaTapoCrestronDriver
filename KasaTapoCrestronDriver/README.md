# KasaTapoCrestronDriver

`KasaTapoCrestronDriver` is a starter scaffold for building a **Crestron Home SDK V2** device driver targeting **.NET Framework 4.7.2**.

## What this template gives you

- SDK-style `net472` project structure
- Crestron Home driver manifest and NuGet wrapper manifest
- ILRepack merge and post-merge assembly patch pipeline
- Optional deploy and log helper scripts
- Sample platform and managed-child entity structure
- Starter UI definition and translation assets

## Immediate follow-up after project creation

1. Update the placeholder metadata in the driver manifest and package manifest.
2. Add your device SDK or API package references to the project.
3. Replace the sample platform and child entity logic with your device-specific implementation.
4. Update the UI definition XML and translations to match your properties and commands.
5. Set the local SDK paths in `*.Local.targets`.
6. Set optional deployment credentials in `*.csproj.user`.

## Build

```powershell
dotnet build .\KasaTapoCrestronDriver\KasaTapoCrestronDriver.csproj -c Debug
```

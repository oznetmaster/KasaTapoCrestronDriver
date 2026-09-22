# Driver tests

Both test projects use NUnit 4.6.1, NUnit3TestAdapter 6.3.0, Microsoft.NET.Test.Sdk 18.10.1 and NUnit.Analyzers 4.15.0. The workflow project uses CrestronHomeNUnit.TestAdapter 1.12.1.

- The production build and 34 ordinary tests target .NET Framework 4.7.2.
- The desktop lifecycle project runs 38 offline cases and targets .NET 10 and compiles the same driver sources against the desktop SDK. The shared lifecycle fixtures also run against the production SDK on the net472 processor.
- The processor package contains the ordinary, lifecycle and opt-in live suites. Desktop adapters and runners are excluded from the merged package.

```powershell
dotnet test KasaTapoCrestronDriver.Tests/KasaTapoCrestronDriver.Tests.csproj --filter "TestCategory!=Processor&TestCategory!=Live"
dotnet test KasaTapoCrestronDriver.Lifecycle.Tests/KasaTapoCrestronDriver.Lifecycle.Tests.csproj --filter "TestCategory!=Live"
```

The desktop SDK requires its separate Newtonsoft.Json.Compact runtime dependency through the private `CompactJsonPath` property. This SDK dependency is not part of the production driver package. Application fixtures use System.Text.Json.

Three live tests refresh a selected light, plug and strip socket and verify the actual SDK entity state. They do not send device-control commands. Select the Live category explicitly and supply private `LiveTestSettings.json` through NUnit's `TestDataDirectory` parameter together with `EnableLiveTests=true`. The desktop lifecycle project and processor live suite share the same fixtures. Settings use stable device IDs or unique aliases, credentials and one selected strip child ID; keep them outside source control and release artifacts.

Use the existing solution and [processor test instructions](../KasaTapoCrestronDriver.ProcessorTests/README.md) for processor validation. Local desktop success alone does not establish processor compatibility.

After building the production Release package, run the merged-assembly contract check in a fresh .NET Framework PowerShell process:

```powershell
powershell.exe -NoProfile -File tools/Test-MergedJson.ps1 -AssemblyPath KasaTapoCrestronDriver/bin/Release/net472/patched/KasaTapoCrestronDriver.dll
```

This checks actual serialization from the packaged assembly, including renamed serializer attributes and the required TPAP registration field, and rejects merged Newtonsoft.Json/log4net code.

$output = Join-Path $PWD 'KasaTapoCrestronDriver.ProcessorTests/bin/Release/net472'
$extracted = Join-Path $env:RUNNER_TEMP 'driver-package-tests'
[IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $output 'KasaTapoCrestronDriver.ProcessorTests.pkg'), $extracted)
./tools/Test-DiscoveredCoverage.ps1 -Stage Package -SdkRoot ../CrestronHomeNUnit -PackageAssembly (Join-Path $extracted 'KasaTapoCrestronDriver.ProcessorTests.dll') -SourceInventory TestResults/KasaTapoCrestronDriver.Tests/inventory.json -SuiteCategories @{'kasatapodriver'='unit';'kasatapodriver-lifecycle'='processor';'kasatapodriver-live'='live'} -DiscoveryOnly -ResultsDirectory TestResults

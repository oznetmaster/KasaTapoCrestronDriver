dotnet build KasaTapoCrestronDriver.ProcessorTests/KasaTapoCrestronDriver.ProcessorTests.csproj -c Release -p:BuildProcessorTestPackages=true -p:ReleaseVersion=1.3.0 -p:DeployAfterBuild=false "-p:ManifestUtilExe=$env:MANIFEST_UTIL_EXE"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

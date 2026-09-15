# Copyright (c) 2026 Neil Colvin.
# Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

param(
    [ValidateSet('Desktop', 'Package')][string]$Stage,
    [string]$Project,
    [string]$Framework,
    [string]$Configuration = 'Release',
    [string]$ResultsDirectory = 'TestResults',
    [switch]$AllowProcessorSkips,
    [string]$SdkRoot,
    [string]$PackageAssembly,
    [string]$SourceInventory,
    [string[]]$RequiredCategories = @('unit','processor','live'),
    [hashtable]$SuiteCategories = @{unit='unit';lifecycle='processor';live='live'},
    [string]$ValidatorPath,
    [switch]$ExcludeProcessor,
    [string]$CompareProcessorInventory,
    [switch]$DiscoveryOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-SameTests($Expected, $Actual, [string]$Context) {
    $left = @($Expected | Sort-Object -CaseSensitive)
    $right = @($Actual | Sort-Object -CaseSensitive)
    if (!$left.Count -or $left.Count -ne $right.Count) { throw "$Context has empty or missing test results." }
    for ($index = 0; $index -lt $left.Count; $index++) {
        if ($left[$index] -cne $right[$index]) { throw "$Context test identities differ: $($left[$index]) / $($right[$index])" }
    }
}
function Read-TestTree([string]$Path, [switch]$AdapterDump) {
    $text = [IO.File]::ReadAllText($Path)
    if ($AdapterDump) {
        # The adapter's diagnostic wrapper is not XML; its NUnit test-run element is.
        $matches = [regex]::Matches($text, '(?s)<test-run\b.*?</test-run>')
        if ($matches.Count -ne 1) { throw "Expected one discovery tree in $Path" }
        $text = $matches[0].Value
    }
    $xml = [Xml.XmlDocument]::new()
    $xml.XmlResolver = $null
    $xml.LoadXml($text)
    if ($xml.SelectNodes("//*[@runstate='NotRunnable']").Count) { throw "Invalid tests in $Path" }
    $cases = @($xml.SelectNodes('//test-case'))
    if (!$cases.Count) { throw "Empty suite in $Path" }
    foreach ($case in $cases) {
        $categories = @($case.SelectNodes("ancestor-or-self::*/properties/property[@name='Category']") | ForEach-Object { $_.GetAttribute('value') })
        [pscustomobject]@{ Name=$case.GetAttribute('fullname'); Live=('Live' -in $categories); Processor=('Processor' -in $categories); Result=$case.GetAttribute('result'); Label=$case.GetAttribute('label'); Reason=$case.SelectSingleNode('reason/message') }
    }
}
function Assert-TestOutcome($Case, [bool]$AllowSkip) {
    if ($Case.Result -eq 'Passed') { return }
    if ($AllowSkip -and $Case.Processor -and $Case.Result -in @('Skipped','NotExecuted') -and
        $null -ne $Case.Reason -and ($Case.Reason.InnerText.Contains('Requires the SDK desktop harness or the processor runtime.') -or
        $Case.Reason.InnerText.Contains('Requires the SDK desktop harness or processor runtime.') -or
        $Case.Reason.InnerText.Contains('Requires the Crestron processor runtime; run the Processor Lifecycle suite on the processor.'))) { return }
    throw "Unexpected result for $($Case.Name): $($Case.Result)"
}

if ($MyInvocation.InvocationName -eq '.') { return }
$results = [IO.Path]::GetFullPath($ResultsDirectory)
[IO.Directory]::CreateDirectory($results) | Out-Null
if ($Stage -eq 'Desktop') {
    $projectPath = [IO.Path]::GetFullPath($Project)
    $name = [IO.Path]::GetFileNameWithoutExtension($projectPath)
    $properties = @(& dotnet msbuild $projectPath -nologo "-p:Configuration=$Configuration" "-p:TargetFramework=$Framework" '-getProperty:TargetPath,AssemblyName') -join "`n"
    if ($LASTEXITCODE) { throw 'Cannot resolve test assembly output.' }
    $properties = ($properties | ConvertFrom-Json).Properties
    $output = Split-Path $properties.TargetPath
    $assemblyName = $properties.AssemblyName
    $evidence = Join-Path $results $name
    if (Test-Path $evidence) { throw 'Use a fresh results directory to avoid stale evidence.' }
    [IO.Directory]::CreateDirectory($evidence) | Out-Null
    $started = [DateTime]::UtcNow
    dotnet test $projectPath -c $Configuration --list-tests --filter 'TestCategory=Processor|TestCategory!=Processor' -p:DeployAfterBuild=false -- NUnit.DumpXmlTestDiscovery=true
    if ($LASTEXITCODE) { throw 'Desktop discovery failed.' }
    $dump = Get-Item "$output/Dump/D_$assemblyName.dll.dump"
    if ($dump.LastWriteTimeUtc -lt $started) { throw 'Discovery output is stale.' }
    Copy-Item $dump.FullName "$evidence/discovery.dump"
    $inventory = @(Read-TestTree "$evidence/discovery.dump" -AdapterDump)
    foreach ($category in $RequiredCategories) {
        $selected = @($inventory | Where-Object { if ($category -eq 'live') { $_.Live } elseif ($category -eq 'processor') { $_.Processor -and !$_.Live } else { !$_.Processor -and !$_.Live } })
        if (!$selected.Count) { throw "Expected a nonempty $category suite." }
    }
    $filter = if ($ExcludeProcessor) { 'TestCategory!=Live&TestCategory!=Processor' } else { 'TestCategory!=Live' }
    dotnet test $projectPath -c $Configuration --no-build --filter $filter --logger 'trx;LogFileName=tests.trx' --results-directory $evidence
    if ($LASTEXITCODE) { throw 'Desktop execution failed.' }
    [xml]$trx = Get-Content "$evidence/tests.trx" -Raw
    $definitions = @{}
    foreach ($definition in $trx.TestRun.TestDefinitions.UnitTest) { $definitions[$definition.GetAttribute('id')] = $definition }
    $observed = @()
    foreach ($result in $trx.TestRun.Results.UnitTestResult) {
        $definition = $definitions[$result.GetAttribute('testId')]
        if ($null -eq $definition) { throw 'Result has no test definition.' }
        $identity = $definition.TestMethod.GetAttribute('className') + '.' + $definition.GetAttribute('name')
        $match = @($inventory | Where-Object Name -CEQ $identity)
        if ($match.Count -ne 1) { throw "Unknown or ambiguous result: $identity" }
        $match[0].Result = $result.GetAttribute('outcome')
        $match[0].Reason = $result.SelectSingleNode("*[local-name()='Output']/*[local-name()='ErrorInfo']/*[local-name()='Message']")
        Assert-TestOutcome $match[0] $AllowProcessorSkips.IsPresent
        $observed += $identity
    }
    Assert-SameTests @($inventory | Where-Object { !$_.Live -and (!$ExcludeProcessor -or !$_.Processor) } | ForEach-Object Name) $observed 'Desktop execution'
    if ($CompareProcessorInventory) {
        $sourceCases = @(Get-Content $CompareProcessorInventory -Raw | ConvertFrom-Json)
        Assert-SameTests @($sourceCases | Where-Object { $_.Processor -and !$_.Live } | ForEach-Object Name) $observed 'Desktop processor harness'
    }
    $inventory | Select-Object Name,Live,Processor | ConvertTo-Json -Depth 4 | Set-Content "$evidence/inventory.json" -Encoding utf8
    Write-Host "Verified $($observed.Count) test results against discovery; $(@($inventory | Where-Object Live).Count) live tests discovered only."
} elseif ($Stage -eq 'Package') {
    $inventory = @(Get-Content $SourceInventory -Raw | ConvertFrom-Json)
    $validation = Join-Path $results ('package-' + [Guid]::NewGuid().ToString('N'))
    $validator = if ($ValidatorPath) { $ValidatorPath } else { "$SdkRoot/ProcessorTestPackage.Validation/bin/Release/net472/ProcessorTestPackage.Validation.exe" }
    & $validator $PackageAssembly "$validation/discovery" 0
    if ($LASTEXITCODE) { throw 'Package discovery failed.' }
    $trees = @(Get-ChildItem "$validation/discovery" -Recurse -Filter TestTree.xml)
    $suites = @($SuiteCategories.Keys)
    Assert-SameTests $suites @($trees | ForEach-Object { $_.Directory.Name }) 'Package suites'
    foreach ($tree in $trees) {
        $category = $SuiteCategories[$tree.Directory.Name]
        if ($category -notin @('unit','processor','live')) { throw 'Unknown suite category.' }
        $expected = @($inventory | Where-Object { $candidate = $_; switch ($category) { 'unit' { !$candidate.Live -and !$candidate.Processor } 'processor' { !$candidate.Live -and $candidate.Processor } 'live' { $candidate.Live } } } | ForEach-Object Name)
        Assert-SameTests $expected @((Read-TestTree $tree.FullName).Name) "Packaged $($tree.Directory.Name) discovery"
    }
    if ($DiscoveryOnly) { Write-Host 'Packaged discovery matches source identities; execution is validated separately by the desktop harness and processor CI.'; return }
    & $validator $PackageAssembly "$validation/execution" 0 --run-twice
    if ($LASTEXITCODE) { throw 'Package execution failed.' }
    $runs = @(Get-ChildItem "$validation/execution" -Recurse -Directory | Where-Object Name -Match '^run-[12]$')
    if ($runs.Count -ne 2) { throw 'Both package executions are required.' }
    foreach ($run in $runs) {
        $files = @(Get-ChildItem $run.FullName -Recurse -Filter TestResult.xml)
        Assert-SameTests @($SuiteCategories.Keys | Where-Object { $SuiteCategories[$_] -ne 'live' }) @($files | ForEach-Object { $_.Directory.Name }) 'Executed package suites'
        $executed = @()
        foreach ($file in $files) {
            $cases = @(Read-TestTree $file.FullName)
            foreach ($case in $cases) { Assert-TestOutcome $case $true }
            $executed += $cases.Name
        }
        Assert-SameTests @($inventory | Where-Object { !$_.Live } | ForEach-Object Name) $executed 'Packaged execution'
    }
    Write-Host 'Packaged discovery matches source test identities; both executions are complete.'
} else { throw 'Choose Desktop or Package validation.' }

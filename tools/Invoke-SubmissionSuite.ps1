# Copyright (c) 2026 Neil Colvin. MIT licensed.
[CmdletBinding()]
param(
 [Parameter(Mandatory)][string]$Project,
 [Parameter(Mandatory)][ValidatePattern('^[A-Za-z_][A-Za-z0-9_.+]*$')][string]$Fixture,
 [Parameter(Mandatory)][string]$ResultsDirectory,
 [switch]$IncludeEndurance
)
$ErrorActionPreference='Stop'
$projectPath=(Resolve-Path -LiteralPath $Project).Path
if(-not (Test-Path -LiteralPath $projectPath -PathType Leaf) -or [IO.Path]::GetExtension($projectPath) -ne '.csproj'){throw 'Select the dedicated submission test project'}
$resultsPath=[IO.Path]::GetFullPath($ResultsDirectory)
if(Test-Path -LiteralPath $resultsPath){throw 'Use a fresh result directory; prior runs must remain intact'}
[IO.Directory]::CreateDirectory($resultsPath)|Out-Null
function Invoke-SelectedTests([string]$phase,[string[]]$tests) {
 $filter=($tests|ForEach-Object{"FullyQualifiedName=$Fixture.$_"}) -join '|'
 $phaseResults=Join-Path $resultsPath $phase
 & dotnet test $projectPath --configuration Release --filter $filter --logger 'trx;LogFileName=submission.trx' --results-directory $phaseResults -- NUnit.ExplicitMode=Strict
 if($LASTEXITCODE -ne 0){throw 'The selected submission tests did not complete successfully; inspect their retained results'}
 # A green process with no matching tests, skips or a misspelled fixture is not a complete selected run.
 $readerSettings=[Xml.XmlReaderSettings]::new()
 $readerSettings.DtdProcessing=[Xml.DtdProcessing]::Prohibit
 $readerSettings.XmlResolver=$null
 $reader=[Xml.XmlReader]::Create((Join-Path $phaseResults 'submission.trx'),$readerSettings)
 try{$document=[Xml.XmlDocument]::new();$document.XmlResolver=$null;$document.Load($reader)}finally{$reader.Dispose()}
 $results=@($document.SelectNodes("//*[local-name()='UnitTestResult']"))
 if($results.Count -ne $tests.Count -or @($results|Where-Object{$_.outcome -ne 'Passed'}).Count -ne 0){throw 'Required selected tests are missing, skipped or unsuccessful'}
 foreach($name in $tests) {
  if(@($results|Where-Object{$_.testName -eq $name -or $_.testName -eq "$Fixture.$name"}).Count -ne 1){throw "Missing or ambiguous selected test: $name"}
 }
}
# Keep NUnit Strict mode: an explicit test is selected alone, just as in the IDE.
Invoke-SelectedTests 'initial' @('CandidateValidation','LocalAndProcessorChecks','ProcessorEvidence','AppAndRecoveryChecks')
if($IncludeEndurance) {
 Invoke-SelectedTests 'endurance' @('Endurance')
 Invoke-SelectedTests 'final' @('PostEnduranceAndRemovalChecks')
}
Write-Output "Selected submission tests passed. Original detailed results remain in the pinned run. Phase three is a separate invocation."

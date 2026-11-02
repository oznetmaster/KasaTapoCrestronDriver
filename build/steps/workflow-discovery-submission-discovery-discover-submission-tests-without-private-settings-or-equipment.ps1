$output = @(dotnet test KasaTapoCrestronDriver.SubmissionTests/KasaTapoCrestronDriver.SubmissionTests.csproj -c Release --list-tests 2>&1)
$output | Write-Output
if ($LASTEXITCODE -ne 0) { throw 'Submission fixture build/discovery failed.' }
foreach ($name in @('CandidateValidation','LocalAndProcessorChecks','ProcessorEvidence','AppAndRecoveryChecks','Endurance','PostEnduranceAndRemovalChecks')) {
  if (@($output | Where-Object { $_.ToString().Trim() -ceq $name }).Count -ne 1) { throw "Expected exactly one $name test." }
}

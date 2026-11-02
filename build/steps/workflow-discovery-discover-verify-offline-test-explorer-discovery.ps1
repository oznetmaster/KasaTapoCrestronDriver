$output = @(dotnet test $env:WORKFLOW_PROJECT -c Release --list-tests 2>&1)
$output | Write-Output
if ($LASTEXITCODE -ne 0) { throw 'Workflow container build/discovery failed.' }
if (@($output | Where-Object { $_.ToString().Trim() -eq $env:EXPECTED_WORKFLOW }).Count -ne 1) { throw 'Expected exactly one named processor workflow.' }

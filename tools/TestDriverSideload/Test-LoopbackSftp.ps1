# Test only: run against the isolated Paramiko server started by loopback_sftp.py.
param([string]$Executable, [int]$Port, [string]$ReportDirectory)
$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFile([IO.Path]::GetFullPath($Executable))
$program = $assembly.GetType('TestDriverSideload.Program')
$main = $program.GetMethod('Main', [Reflection.BindingFlags]'Static,NonPublic')
$verifyArgs = [object[]]::new(1)
$verifyArgs[0] = [string[]]@('--verify',(Join-Path $ReportDirectory 'loopback-verify.json'))
if ($main.Invoke($null,$verifyArgs) -ne 0) { throw 'Standalone assembly verification failed.' }
$payload = $assembly.GetType('TestDriverSideload.Payload').GetMethod('Load').Invoke($null,@())
$remoteType = $assembly.GetType('TestDriverSideload.SftpFiles')
$fingerprint = $remoteType.GetMethod('Probe').Invoke($null,@('127.0.0.1',$Port))
$refused = $false
try { $bad = [Activator]::CreateInstance($remoteType,@('127.0.0.1',$Port,'test-uploader','test-only','incorrect-key')); $bad.Dispose() } catch { $refused = $true }
if (-not $refused) { throw 'A changed SSH host key was accepted.' }
Add-Type -TypeDefinition 'public static class SftpTestCallback { public static void Progress(ulong value) {} }'
$progress = [Delegate]::CreateDelegate([Action[UInt64]],[SftpTestCallback].GetMethod('Progress'))
$remote = [Activator]::CreateInstance($remoteType,@('127.0.0.1',$Port,'test-uploader','test-only',$fingerprint))
try {
    $upload = $assembly.GetType('TestDriverSideload.Upload').GetMethod('Run')
    if (-not $upload.Invoke($null,@($payload,$remote,$progress))) { throw 'Initial transfer did not upload.' }
    if ($upload.Invoke($null,@($payload,$remote,$progress))) { throw 'Repeated transfer rewrote the package.' }
} finally { $remote.Dispose() }
@{ Passed = 4; Checks = @('SSH identity probe before authentication','Changed SSH key refused','Real SFTP upload and read-back verification','Duplicate upload preserved'); Fingerprint = $fingerprint } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ReportDirectory 'loopback-result.json') -Encoding utf8

# Copyright (c) 2026 Neil Colvin. MIT with Commons Clause; see LICENSE.
[CmdletBinding()]
param([string]$Version = $env:RELEASE_VERSION)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$config=Get-Content -Raw (Join-Path $root 'build/driver-release.json') | ConvertFrom-Json
if($Version -cne $config.version){throw 'Requested version differs from the reviewed driver release'}
$out=Join-Path $root 'artifacts/release'
if(Test-Path $out){if(@(Get-ChildItem $out -Force).Count){throw 'Existing frozen assets must not be overwritten'}}
New-Item -ItemType Directory -Force $out | Out-Null
$toolRoot=Join-Path $HOME '.nuget/packages/crestron.devicedrivers.manifestutil/29.0.10/tools/net8.0/any'
if(-not(Test-Path (Join-Path $toolRoot 'ManifestUtil.dll'))){
 $tools=Join-Path $root 'artifacts/manifestutil-29.0.10'
 if(Test-Path $tools){throw 'Inspect incomplete ManifestUtil tool cache before retrying'}
 New-Item -ItemType Directory -Force $tools | Out-Null
 $archive=Join-Path $tools 'tool.zip'
 Invoke-WebRequest 'https://api.nuget.org/v3-flatcontainer/crestron.devicedrivers.manifestutil/29.0.10/crestron.devicedrivers.manifestutil.29.0.10.nupkg' -OutFile $archive
 [IO.Compression.ZipFile]::ExtractToDirectory($archive,(Join-Path $tools 'package'))
 $toolRoot=Join-Path $tools 'package/tools/net8.0/any'
}
$manifestUtil=Join-Path $toolRoot 'ManifestUtil.exe'
foreach($file in @('ManifestUtil.exe','ManifestUtil.dll','Newtonsoft.Json.Compact.dll')){if(-not(Test-Path (Join-Path $toolRoot $file))){throw 'Incomplete pinned packaging tool'}}
# Older RAD projects resolve this platform-provided build reference beside the solution.
Copy-Item (Join-Path $toolRoot 'Newtonsoft.Json.Compact.dll') (Join-Path $root 'Newtonsoft.Json.Compact.dll') -Force
Push-Location $root
try {
 foreach($package in $config.packages){
  $manifest=Get-Content -Raw $package.manifest | ConvertFrom-Json
  $versions=@([regex]::Matches((Get-Content -Raw $package.manifest),'"DriverVersion"\s*:\s*"([^"]+)"') | ForEach-Object {$_.Groups[1].Value})
  if($versions.Count -ne 1 -or ([Version]$versions[0]).ToString(3) -cne $Version -or ([Version]$versions[0]).Revision -ne 0){throw 'Driver manifest version differs'}
  $arguments=@('build',$package.project,'-c','Release','-p:BuildForTests=false','-p:DeployAfterBuild=false','-p:SkipDriverPackaging=false','-p:SkipDriverVersionBump=true','-p:MergeDependenciesContinueOnError=false','-p:PackageDriverContinueOnError=false','-p:PackageDriverIgnoreExitCode=false',"-p:ManifestUtilExe=$manifestUtil","-p:CompactJsonPath=$toolRoot/Newtonsoft.Json.Compact.dll","-p:DriverReleaseVersion=$Version")
  & dotnet @arguments
  if($LASTEXITCODE){throw 'Production driver packaging failed'}
  $payload=Join-Path $root (Join-Path (Split-Path $package.project) ('bin/Release/net472/'+$package.payload))
  if(-not(Test-Path $payload)){throw 'Expected public driver payload is missing'}
  $guard=if(Test-Path 'tools/Assert-DriverPackageVersion.ps1'){'tools/Assert-DriverPackageVersion.ps1'}else{'scripts/Assert-DriverPackageVersion.ps1'}
  & $guard -PackagePath $payload -ManifestPath $package.manifest -ReleaseVersion $Version
  $delivery=Join-Path $root ('artifacts/'+$package.id+'.delivery.json')
  @{standardVersion='1.0';packageType='CrestronHomeDriver';driverPlatform='Crestron Home';payloadType='pkg';payloadFile=$package.payload;containsManagedDllPayload=$false;packageId=$package.id;packageVersion=$Version;displayName=$package.displayName;vendor=$package.vendor;driverCategory=$package.category}|ConvertTo-Json|Set-Content $delivery
  & dotnet pack $package.project -c Release --no-build --output $out "-p:PackageVersion=$Version" "-p:PackageDriverPkgPath=$payload" "-p:PackageDriverManifestPath=$delivery" "-p:PackageReleaseNotesFile=$root/RELEASE-NOTES.md"
  if($LASTEXITCODE){throw 'Driver NuGet wrapper packaging failed'}
  Copy-Item -LiteralPath $payload -Destination $out
 }
 foreach($file in @('README.md','CHANGELOG.md','RELEASE-NOTES.md','LICENSE')){Copy-Item -LiteralPath (Join-Path $root $file) -Destination $out}
 if($config.name -eq 'TeslaPowerwallCrestronDriver'){
  & ./tools/Build-LocalSetupRelease.ps1 -OutputDirectory (Join-Path $root 'artifacts/local-provisioning-release')
  if($LASTEXITCODE){throw 'Local provisioning application packaging failed'}
  Copy-Item (Join-Path $root 'artifacts/local-provisioning-release/*.zip') $out
 }
 $publicGuard=if(Test-Path 'tools/Assert-PublicDriverArtifacts.ps1'){'tools/Assert-PublicDriverArtifacts.ps1'}else{'scripts/Assert-PublicDriverArtifacts.ps1'}
 & $publicGuard -Path @($out)
 # Frozen publication also verifies each wrapper's identity and exact payload bytes.
 foreach($package in $config.packages){
  $zip=[IO.Compression.ZipFile]::OpenRead((Join-Path $out ($package.id+'.'+$Version+'.nupkg')))
  try{
   $entry=$zip.GetEntry($package.payload)
   if(-not $entry){throw 'NuGet wrapper is missing the root driver package'}
   $stream=$entry.Open()
   try{$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))}finally{$stream.Dispose()}
   if($hash -cne (Get-FileHash (Join-Path $out $package.payload)).Hash){throw 'NuGet/GitHub driver payload mismatch'}
   if(@($zip.Entries | Where-Object FullName -match '^(lib|runtimes)/').Count){throw 'Driver wrapper exposes application libraries'}
  }finally{$zip.Dispose()}
 }
} finally {Pop-Location}

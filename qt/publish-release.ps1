#requires -Version 7.0
param(
    [Parameter(Mandatory)][ValidateRange(1,65535)][int]$BuildNumber,
    [string]$VersionSuffix='alpha.8',
    [ValidateSet('preview','stable')][string]$Channel='preview',
    [string]$QtSdk='',
    [string]$CurseForgeApiKeyPath='',
    [switch]$SkipSigning,
    [string]$SigningKeyPath=(Join-Path $env:USERPROFILE '.97launch-private/update-signing-private.pem')
)
$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
if ($SkipSigning -and !$VersionSuffix.EndsWith('-local')) { throw 'Unsigned development packages must use a -local version suffix.' }
if (!$SkipSigning -and !(Test-Path -LiteralPath $SigningKeyPath)) { throw 'Missing private update signing key.' }
if (!$QtSdk) { $QtSdk=Join-Path (Split-Path $PSScriptRoot -Parent) '_toolchains/Qt/6.6.3/msvc2019_64' }
if (!$CurseForgeApiKeyPath) { $CurseForgeApiKeyPath=Join-Path $env:USERPROFILE '.97launch-private/curseforge-api-key.txt' }
if (!(Test-Path -LiteralPath $CurseForgeApiKeyPath -PathType Leaf) -or [string]::IsNullOrWhiteSpace([IO.File]::ReadAllText($CurseForgeApiKeyPath))) { throw 'Provide a private CurseForge API key file for this release.' }
./build.ps1 -BuildNumber $BuildNumber -VersionSuffix $VersionSuffix -QtSdk $QtSdk -CurseForgeApiKeyPath $CurseForgeApiKeyPath
$version=(Get-Item -LiteralPath '97LaunchQt.exe').VersionInfo.ProductVersion
if ($Channel -eq 'stable' -and $version.Contains('-')) { throw 'Stable releases cannot contain a prerelease suffix.' }
$output=Join-Path $PSScriptRoot "artifacts/releases/$version-build.$BuildNumber"
if (Test-Path -LiteralPath $output) { throw 'Release output already exists; preserve immutable release artifacts.' }
$stage=Join-Path $output 'runtime'
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item -LiteralPath '97LaunchQt.exe' -Destination $stage
& (Join-Path $QtSdk 'bin/windeployqt.exe') --release --qmldir (Join-Path $PSScriptRoot 'build-basic/lite-qml') --no-translations --no-opengl-sw --compiler-runtime --dir $stage (Join-Path $stage '97LaunchQt.exe')
if ($LASTEXITCODE) { throw 'Runtime deployment failed.' }
$vsWhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vsPath=& $vsWhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$redist=Get-ChildItem -LiteralPath ($vsPath+'/VC/Redist/MSVC') -Directory -Filter '14.*' | Sort-Object Name -Descending | Select-Object -First 1
if(!$redist){throw 'Missing MSVC runtime deployment directory.'}
Copy-Item -Path ($redist.FullName+'/x64/Microsoft.VC143.CRT/*.dll') -Destination $stage
New-Item -ItemType Directory -Path ($stage+'/worker'),($stage+'/imageformats'),($stage+'/licenses') -Force | Out-Null
Copy-Item -LiteralPath 'worker/97Launch.InstanceWorker.exe' -Destination ($stage+'/worker')
Copy-Item -LiteralPath (Join-Path $QtSdk 'plugins/imageformats/qwebp.dll') -Destination ($stage+'/imageformats')
Copy-Item -LiteralPath 'third_party/WebView2-1.0.3719.77/LICENSE.txt','third_party/WebView2-1.0.3719.77/NOTICE.txt' -Destination ($stage+'/licenses')
Copy-Item -LiteralPath 'licenses/Qt' -Destination ($stage+'/licenses/Qt') -Recurse
if(Test-Path -LiteralPath (Join-Path $QtSdk '../../Licenses')){Copy-Item -LiteralPath (Join-Path $QtSdk '../../Licenses') -Destination ($stage+'/licenses/Qt') -Recurse}
@'
[Paths]
Plugins=.
Qml2Imports=qml
'@ | Set-Content -LiteralPath ($stage+'/qt.conf') -Encoding utf8
$cab=Join-Path $output 'payload.cab'
$ddf=Join-Path $output 'payload.ddf'
$lines=[Collections.Generic.List[string]]::new()
foreach($line in @('.OPTION EXPLICIT','.Set Cabinet=on','.Set Compress=on','.Set CompressionType=LZX','.Set CompressionMemory=21','.Set MaxDiskSize=0','.Set MaxCabinetSize=0',('.Set DiskDirectoryTemplate="'+$output+'"'),'.Set CabinetNameTemplate=payload.cab')){$lines.Add($line)}
foreach($file in Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName){
    $relative=[IO.Path]::GetRelativePath($stage,$file.FullName)
    if($relative.Contains('"')){throw 'Invalid package file name.'}
    $lines.Add('"'+$file.FullName+'" "'+$relative+'"')
}
[IO.File]::WriteAllLines($ddf,$lines,[Text.Encoding]::Default)
& makecab.exe /F $ddf
if($LASTEXITCODE -or !(Test-Path -LiteralPath $cab)){throw 'CAB packaging failed.'}
$cmake=Join-Path $vsPath 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
& $cmake -S . -B build-basic "-DLAUNCH_PAYLOAD_CAB=$($cab.Replace('\','/'))"
if($LASTEXITCODE){throw 'Bootstrap configuration failed.'}
& $cmake --build build-basic --config Release --target 97Launch --parallel 6
if($LASTEXITCODE){throw 'Bootstrap build failed.'}
$exe=Join-Path $output '97Launch.exe'
Copy-Item -LiteralPath 'build-basic/bin/97Launch.exe' -Destination $exe
$file=Get-Item -LiteralPath $exe
if($file.VersionInfo.FilePrivatePart -ne $BuildNumber -or $file.VersionInfo.ProductVersion -ne $version -or $file.VersionInfo.ProductName -ne '97Launch Qt'){throw 'Release metadata mismatch.'}
if($SkipSigning){Write-Output "Local single-EXE package prepared: $exe";return}
Add-Type -TypeDefinition @'
using System;using System.IO;using System.Security.Cryptography;
public static class QtReleaseSigning {
 public static string[] Sign(string keyPath,byte[] payload){using var rsa=RSA.Create();rsa.ImportFromPem(File.ReadAllText(keyPath));return new[]{Convert.ToBase64String(rsa.SignData(payload,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1)),Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant().Substring(0,16)};}
}
'@
$payload=[ordered]@{format=1;app='97Launch';version=$version;build=$BuildNumber;channel=$Channel;architecture='x64';size=$file.Length;sha256=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant();issued_at=[DateTimeOffset]::UtcNow.ToUnixTimeSeconds()}
$bytes=[Text.Encoding]::UTF8.GetBytes(($payload|ConvertTo-Json -Compress))
$signature=[QtReleaseSigning]::Sign($SigningKeyPath,$bytes)
$manifest=[ordered]@{payload=[Convert]::ToBase64String($bytes);signature=$signature[0];key_id=$signature[1]}
[IO.File]::WriteAllText((Join-Path $output 'manifest.json'),($manifest|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
Write-Output "Signed single-EXE release prepared: $output"

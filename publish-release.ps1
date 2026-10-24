#requires -Version 7.0
param(
    [Parameter(Mandatory)][ValidateRange(1, 65535)][int]$BuildNumber,
    [ValidateSet('preview', 'stable')][string]$Channel = 'preview',
    [string]$SigningKeyPath = (Join-Path $env:USERPROFILE '.97launch-private\update-signing-private.pem'),
    [switch]$Restart
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $SigningKeyPath)) { throw 'The private update signing key is missing.' }
& (Join-Path $PSScriptRoot 'build.ps1') -BuildNumber $BuildNumber -Restart:$Restart
$taskExe = Join-Path $PSScriptRoot '97Launch.exe'
$taskFile = Get-Item -LiteralPath $taskExe
$taskVersion = $taskFile.VersionInfo.ProductVersion.Split('+')[0]
if ($taskFile.VersionInfo.FilePrivatePart -ne $BuildNumber) { throw 'The EXE build number does not match.' }
if ($Channel -eq 'stable' -and $taskVersion.Contains('-')) { throw 'Stable releases cannot use a prerelease version.' }

if (-not ('ReleaseSigning' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Security.Cryptography;
public static class ReleaseSigning {
    public static string[] Sign(string keyPath, byte[] payload) {
        using (var rsa = RSA.Create()) {
            rsa.ImportFromPem(File.ReadAllText(keyPath));
            var id = Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant().Substring(0, 16);
            return new[] { Convert.ToBase64String(rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)), id };
        }
    }
}
'@
}
$taskPayload = [ordered]@{
    format = 1; app = '97Launch'; version = $taskVersion; build = $BuildNumber
    channel = $Channel; architecture = 'x64'; size = $taskFile.Length
    sha256 = (Get-FileHash -LiteralPath $taskExe -Algorithm SHA256).Hash.ToLowerInvariant()
    issued_at = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
}
$taskBytes = [Text.Encoding]::UTF8.GetBytes(($taskPayload | ConvertTo-Json -Compress))
$taskSignature = [ReleaseSigning]::Sign($SigningKeyPath, $taskBytes)
$taskOutput = Join-Path $PSScriptRoot "artifacts\releases\$taskVersion-build.$BuildNumber"
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
Copy-Item -LiteralPath $taskExe -Destination (Join-Path $taskOutput '97Launch.exe')
$taskManifest = [ordered]@{payload = [Convert]::ToBase64String($taskBytes); signature = $taskSignature[0]; key_id = $taskSignature[1]}
[IO.File]::WriteAllText((Join-Path $taskOutput 'manifest.json'), ($taskManifest | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
Write-Host "Signed release prepared: $taskOutput"

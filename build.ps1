param(
    [switch]$Restart,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateRange(0, 65535)][int]$BuildNumber = 0
)

$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$taskExecutable = Join-Path $taskRoot '97Launch.exe'
$taskProject = Join-Path $taskRoot 'src\Launcher.App\Launcher.App.csproj'

if (-not (Test-Path -LiteralPath (Join-Path $taskRoot 'authorization-server.json'))) {
    throw 'Create authorization-server.json from authorization-server.example.json before publishing.'
}

if ($Restart) {
    Get-Process -Name '97Launch', 'Launcher.App' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($taskRoot + '\', [StringComparison]::OrdinalIgnoreCase) } |
        Stop-Process
}

& dotnet publish $taskProject -c $Configuration -r win-x64 -p:Platform=x64 "-p:BuildNumber=$BuildNumber" --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit $LASTEXITCODE)." }
if (-not (Test-Path -LiteralPath $taskExecutable)) { throw 'Portable executable was not generated.' }

$taskFile = Get-Item -LiteralPath $taskExecutable
Write-Host ('Portable application: {0} ({1:N1} MiB)' -f $taskFile.FullName, ($taskFile.Length / 1MB))
if ($Restart) { Start-Process -FilePath $taskExecutable -WorkingDirectory $taskRoot }

# ============================================================================
#  WhSnap - Focal - focal-build.ps1
#  Serialized solution build. Every parallel worker (agents / humans) must
#  build through this script: MSBuild writes shared obj/ + bin/ state, and two
#  concurrent builds of the same solution corrupt each other's outputs.
#
#  The lock is advisory and cross-process (exclusive FileStream on a temp file).
#  Usage:
#      pwsh -File build/tools/focal-build.ps1
#      pwsh -File build/tools/focal-build.ps1 -Configuration Debug
#      pwsh -File build/tools/focal-build.ps1 -Project src/WhSnap.App/WhSnap.App.csproj
#  NOTE: ASCII-only on purpose (Windows PowerShell 5.1 reads .ps1 as ANSI).
# ============================================================================
param(
    [string]$Configuration = 'Release',
    [string]$Project = 'WhSnap.sln',
    [int]$LockTimeoutSeconds = 900,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$target = Join-Path $root $Project
if (-not (Test-Path $target)) { throw "project not found: $target" }

$lockPath = Join-Path $env:TEMP 'whsnap-focal-build.lock'
$deadline = (Get-Date).AddSeconds($LockTimeoutSeconds)
$stream = $null
$waited = $false

while ($null -eq $stream) {
    try {
        $stream = [System.IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None')
    } catch [System.IO.IOException] {
        if ((Get-Date) -gt $deadline) { throw "build lock timeout after $LockTimeoutSeconds s ($lockPath)" }
        if (-not $waited) { Write-Host "[focal-build] waiting for another build to finish..."; $waited = $true }
        Start-Sleep -Seconds 2
    }
}

try {
    Write-Host "[focal-build] $Project ($Configuration)"
    $output = & dotnet build $target -c $Configuration -v m 2>&1
    $code = $LASTEXITCODE
    if ($Quiet) {
        $output | Select-Object -Last 8 | ForEach-Object { Write-Host $_ }
    } else {
        $output | ForEach-Object { Write-Host $_ }
    }
    exit $code
} finally {
    $stream.Dispose()
    Remove-Item $lockPath -Force -ErrorAction SilentlyContinue
}

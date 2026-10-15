<#
.SYNOPSIS
    Signs WhSnap binaries with a (self-signed) code-signing certificate, with an
    RFC3161 timestamp.

.DESCRIPTION
    The timestamp matters even for a self-signed certificate: without it the
    signature becomes invalid once the certificate expires.

    The private key can be supplied three ways:
      * -PfxFile      local .pfx path
      * -PfxBase64    base64 of the .pfx (CI secret; written to a temp file)
      * (neither)     falls back to a certificate already in Cert:\CurrentUser\My
                      whose thumbprint matches -Thumbprint

    ASCII-only on purpose: Windows PowerShell 5.1 reads .ps1 files without a BOM as ANSI.

.EXAMPLE
    powershell -File build/tools/sign.ps1 -Path artifacts\installer\WhSnap-0.1.0-setup-x64.exe -PfxFile artifacts\signing\WhSnap.pfx
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]]$Path,

    [string]$PfxFile,
    [string]$PfxBase64,
    [string]$PfxPassword,
    [string]$Thumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [string]$SigntoolPath
)

$ErrorActionPreference = 'Stop'

function Resolve-Signtool {
    param([string]$Explicit)

    if ($Explicit) {
        if (-not (Test-Path $Explicit)) { throw "signtool not found at $Explicit." }
        return $Explicit
    }

    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path $kits) {
        $found = Get-ChildItem $kits -Recurse -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -like '*\x64\*' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($found) { return $found.FullName }
    }

    $onPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    throw @'
signtool.exe not found. Signing needs the Windows SDK signing tools:
  * Visual Studio Installer -> Individual components -> "Windows 10/11 SDK"
  * or the standalone Windows SDK (winget install Microsoft.WindowsSDK.10.0.22621)
GitHub Actions windows-latest runners already include it.
'@
}

$signtool = Resolve-Signtool -Explicit $SigntoolPath
Write-Host "signtool: $signtool"

$tempPfx = $null
try {
    $signArgs = @('sign', '/fd', 'SHA256', '/td', 'SHA256', '/tr', $TimestampUrl, '/d', 'WhSnap')

    if ($PfxBase64) {
        $tempPfx = Join-Path ([System.IO.Path]::GetTempPath()) ("whsnap-sign-{0}.pfx" -f ([guid]::NewGuid().ToString('N')))
        [System.IO.File]::WriteAllBytes($tempPfx, [Convert]::FromBase64String($PfxBase64))
        $signArgs += @('/f', $tempPfx)
        Write-Host "Using decoded CI certificate ($tempPfx)"
    }
    elseif ($PfxFile) {
        if (-not (Test-Path $PfxFile)) { throw "pfx not found: $PfxFile" }
        $signArgs += @('/f', (Resolve-Path $PfxFile).Path)
        Write-Host "Using certificate file: $PfxFile"
    }
    elseif ($Thumbprint) {
        $signArgs += @('/sha1', $Thumbprint)
        Write-Host "Using certificate in store: $Thumbprint"
    }
    else {
        throw 'Provide -PfxFile, -PfxBase64 or -Thumbprint.'
    }

    if ($PfxPassword) { $signArgs += @('/p', $PfxPassword) }

    foreach ($file in $Path) {
        $resolved = (Resolve-Path $file).Path
        Write-Host "Signing $resolved"
        & $signtool @signArgs $resolved
        if ($LASTEXITCODE -ne 0) { throw "signtool failed for $resolved (exit code $LASTEXITCODE)." }
    }

    Write-Host ''
    Write-Host 'Verifying signatures...'
    foreach ($file in $Path) {
        & $signtool verify /pa /v (Resolve-Path $file).Path | Select-Object -Last 4
    }
}
finally {
    if ($tempPfx -and (Test-Path $tempPfx)) {
        Remove-Item $tempPfx -Force
        Write-Host 'Temporary certificate removed.'
    }
}

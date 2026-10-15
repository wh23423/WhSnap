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

    -CerOut additionally exports the PUBLIC certificate (.cer) next to the build
    output: the GitHub Release tells recipients to run install-trust.ps1, so the
    .cer has to be shipped with the installer (CI uploads artifacts/signing/*.cer).
    A .pfx that only lives in a CI secret would otherwise never produce a .cer.

    Verification is honest about the self-signed case: Get-AuthenticodeSignature is
    the same WinVerifyTrust check signtool runs, and "signed but the root is not
    trusted on this machine" is reported as OK-with-a-note (that is exactly what
    install-trust.ps1 fixes), while unsigned / tampered files still fail the script.

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
    [string]$CerOut,
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

function Get-SigningCertificate {
    param([string]$CerPathFromPfx, [string]$PlainPassword)

    if ($CerPathFromPfx) {
        return New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($CerPathFromPfx, $PlainPassword)
    }

    if ($Thumbprint) {
        return Get-Item ("Cert:\CurrentUser\My\{0}" -f $Thumbprint)
    }

    return $null
}

# Verdict per file: 'trusted' (chain OK) or 'untrusted' (signature valid, self-signed root
# not installed on this machine). Throws when the file is unsigned or the hash does not match.
function Test-SignedFile {
    param([string]$File, [string]$ExpectedThumbprint)

    $sig = Get-AuthenticodeSignature -LiteralPath $File
    $status = [string]$sig.Status
    $signer = $sig.SignerCertificate

    if ($null -eq $signer) {
        throw "Not signed: $File (status $status)"
    }

    if ($ExpectedThumbprint -and ($signer.Thumbprint -ne $ExpectedThumbprint)) {
        throw ("Signed by an unexpected certificate: {0} (expected {1})" -f $signer.Thumbprint, $ExpectedThumbprint)
    }

    if ($status -eq 'HashMismatch') {
        throw "Signature does not match the file content (tampered after signing?): $File"
    }

    if ($status -eq 'Valid') {
        Write-Host ("    [OK] {0} - signed by {1} (chain trusted)" -f (Split-Path -Leaf $File), $signer.Subject)
        return 'trusted'
    }

    Write-Host ("    [OK] {0} - signed by {1}; chain not trusted on this machine yet ({2})" -f `
        (Split-Path -Leaf $File), $signer.Subject, $status)
    return 'untrusted'
}

$tempPfx = $null
try {
    $signArgs = @('sign', '/fd', 'SHA256', '/td', 'SHA256', '/tr', $TimestampUrl, '/d', 'WhSnap')
    $verifyPfx = $null

    if ($PfxBase64) {
        $tempPfx = Join-Path ([System.IO.Path]::GetTempPath()) ("whsnap-sign-{0}.pfx" -f ([guid]::NewGuid().ToString('N')))
        [System.IO.File]::WriteAllBytes($tempPfx, [Convert]::FromBase64String($PfxBase64))
        $signArgs += @('/f', $tempPfx)
        $verifyPfx = $tempPfx
        Write-Host "Using decoded CI certificate ($tempPfx)"
    }
    elseif ($PfxFile) {
        if (-not (Test-Path $PfxFile)) { throw "pfx not found: $PfxFile" }
        $verifyPfx = (Resolve-Path $PfxFile).Path
        $signArgs += @('/f', $verifyPfx)
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

    $signingCert = Get-SigningCertificate -CerPathFromPfx $verifyPfx -PlainPassword $PfxPassword
    $expectedThumbprint = if ($signingCert) { $signingCert.Thumbprint } else { $Thumbprint }

    if ($CerOut) {
        if (-not $signingCert) {
            throw '-CerOut needs the certificate, so pass -PfxFile, -PfxBase64 or -Thumbprint.'
        }

        $cerDir = Split-Path -Parent $CerOut
        if ($cerDir -and -not (Test-Path $cerDir)) { New-Item -ItemType Directory -Path $cerDir -Force | Out-Null }
        [System.IO.File]::WriteAllBytes($CerOut, $signingCert.Export(
            [System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
        Write-Host ("Exported public certificate: {0} ({1})" -f $CerOut, $signingCert.Thumbprint)
    }

    foreach ($file in $Path) {
        $resolved = (Resolve-Path $file).Path
        Write-Host "Signing $resolved"
        & $signtool @signArgs $resolved
        if ($LASTEXITCODE -ne 0) { throw "signtool failed for $resolved (exit code $LASTEXITCODE)." }
    }

    Write-Host ''
    Write-Host 'Verifying signatures...'
    $verdicts = @()

    foreach ($file in $Path) {
        $verdicts += (Test-SignedFile -File (Resolve-Path $file).Path -ExpectedThumbprint $expectedThumbprint)
    }

    if ($verdicts -contains 'untrusted') {
        Write-Host ''
        Write-Host 'NOTE: signature is valid, but the self-signed root is not trusted here.' -ForegroundColor Yellow
        Write-Host '      Recipients run install-trust.ps1 once (as admin) to drop the "Unknown publisher" prompt.'
    }
}
finally {
    if ($tempPfx -and (Test-Path $tempPfx)) {
        Remove-Item $tempPfx -Force
        Write-Host 'Temporary certificate removed.'
    }
}

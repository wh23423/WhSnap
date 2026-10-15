<#
.SYNOPSIS
    Imports the WhSnap self-signed certificate into the local machine trust stores
    so Windows stops reporting "Unknown publisher".

.DESCRIPTION
    Run this ONCE, as administrator, on each machine that installs WhSnap.
    It adds the public certificate to:
      * Trusted Root Certification Authorities  (chain validation)
      * Trusted Publishers                      (no "Unknown publisher" prompt)

    It does NOT bypass SmartScreen for downloads from the internet -- that warning
    is reputation based and cannot be satisfied by a self-signed certificate.

    ASCII-only on purpose: Windows PowerShell 5.1 reads .ps1 files without a BOM as ANSI.

.EXAMPLE
    # right-click PowerShell -> Run as administrator
    powershell -ExecutionPolicy Bypass -File install-trust.ps1 -CerPath .\WhSnap.cer
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CerPath
)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
$elevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $elevated) {
    Write-Host 'This script must run as administrator.' -ForegroundColor Red
    Write-Host 'Right-click PowerShell -> "Run as administrator", then run it again.'
    exit 1
}

if (-not (Test-Path -LiteralPath $CerPath)) {
    throw "Certificate not found: $CerPath"
}

$resolved = (Resolve-Path -LiteralPath $CerPath).Path
Write-Host "Importing $resolved"

$cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($resolved)
Write-Host ("  Subject    : {0}" -f $cert.Subject)
Write-Host ("  Thumbprint : {0}" -f $cert.Thumbprint)
Write-Host ("  Valid until: {0:yyyy-MM-dd}" -f $cert.NotAfter)

Import-Certificate -FilePath $resolved -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
Write-Host '  -> added to Trusted Root Certification Authorities'

Import-Certificate -FilePath $resolved -CertStoreLocation 'Cert:\LocalMachine\TrustedPublisher' | Out-Null
Write-Host '  -> added to Trusted Publishers'

Write-Host ''
Write-Host 'Done. WhSnap installers signed with this certificate now show the real publisher.' -ForegroundColor Green
Write-Host 'To undo:'
Write-Host ('  Get-ChildItem Cert:\LocalMachine\Root | Where-Object Thumbprint -eq {0} | Remove-Item' -f $cert.Thumbprint)
Write-Host ('  Get-ChildItem Cert:\LocalMachine\TrustedPublisher | Where-Object Thumbprint -eq {0} | Remove-Item' -f $cert.Thumbprint)

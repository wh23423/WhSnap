<#
.SYNOPSIS
    Creates a self-signed code-signing certificate for WhSnap (zero cost).

.DESCRIPTION
    Honest expectations -- a self-signed certificate:
      * does NOT remove the SmartScreen "Windows protected your PC" prompt
      * does NOT reduce antivirus false positives
      * DOES show your name in the file's Digital Signatures tab
      * DOES stop "Unknown publisher" once the recipient imports the .cer into
        Trusted Root Certification Authorities (see install-trust.ps1)
    That trade-off is fine for distributing to yourself and a small circle.

    Outputs into artifacts/signing:
      WhSnap.cer  -- public certificate, ship this to recipients
      WhSnap.pfx  -- private key, NEVER commit it (already covered by .gitignore)

    ASCII-only on purpose: Windows PowerShell 5.1 reads .ps1 files without a BOM as ANSI.

.EXAMPLE
    powershell -File build/tools/make-cert.ps1
    powershell -File build/tools/make-cert.ps1 -Subject 'CN=wh23423' -Years 3
#>
[CmdletBinding()]
param(
    [string]$Subject = 'CN=WhSnap',
    [int]$Years = 5,
    [string]$OutDir,
    [securestring]$Password,
    [string]$PfxPassword
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $OutDir) { $OutDir = Join-Path $repoRoot 'artifacts\signing' }

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

# Plain-text password is accepted for scripting/CI; otherwise prompt securely.
if ($PfxPassword) {
    $secure = ConvertTo-SecureString -String $PfxPassword -AsPlainText -Force
}
elseif ($Password) {
    $secure = $Password
}
else {
    $secure = Read-Host -AsSecureString -Prompt 'Password to protect the .pfx private key'
}

Write-Host "Creating self-signed code-signing certificate: $Subject"
$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $Subject `
    -KeyUsage DigitalSignature `
    -KeyAlgorithm RSA `
    -KeyLength 3072 `
    -HashAlgorithm SHA256 `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -NotAfter (Get-Date).AddYears($Years)

$cerPath = Join-Path $OutDir 'WhSnap.cer'
$pfxPath = Join-Path $OutDir 'WhSnap.pfx'

Export-Certificate -Cert $cert -FilePath $cerPath -Type CERT | Out-Null
Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $secure | Out-Null

Write-Host ''
Write-Host 'Done.'
Write-Host ("  Subject    : {0}" -f $cert.Subject)
Write-Host ("  Thumbprint : {0}" -f $cert.Thumbprint)
Write-Host ("  Valid until: {0:yyyy-MM-dd}" -f $cert.NotAfter)
Write-Host ("  Public cert: {0}" -f $cerPath)
Write-Host ("  Private key: {0}" -f $pfxPath)
Write-Host ''
Write-Host 'Next steps:'
Write-Host '  1. Keep WhSnap.pfx private. Do not commit it (covered by .gitignore).'
Write-Host '  2. Sign builds:  powershell -File build/tools/sign.ps1 -Path <files> -PfxFile <pfx>'
Write-Host '  3. For CI, store the base64 of the .pfx in the SIGNING_PFX_BASE64 secret:'
Write-Host '       [Convert]::ToBase64String([IO.File]::ReadAllBytes("WhSnap.pfx"))'
Write-Host '  4. Ship WhSnap.cer plus install-trust.ps1 to recipients.'

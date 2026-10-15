<#
.SYNOPSIS
    End-to-end acceptance check for the SIGNED WhSnap preview package.

.DESCRIPTION
    Installs the built setup exe silently, records the evidence that matters for a signed
    release, runs the installed self-check, uninstalls, and then proves the machine is
    clean again:

      * the account that is signed in gets WhSnap.exe and the setup exe both signed
        (Authenticode status + signer + RFC3161 timestamp)
      * the installed payload reports the expected version (ProductVersion, which carries
        the +commit suffix so you can see which revision was packaged)
      * unins000.exe is reported honestly (Inno generates it at install time, so it is
        normally unsigned)
      * user manual / Run entry / uninstall entry / Start menu entries are all present
      * the installed --selftest exits 0 with 0 FAIL
      * silent uninstall removes the app dir, Start menu, Run entry and uninstall entry
        while leaving %APPDATA%\WhSnap untouched (settings.json hash unchanged)

    Exit code 0 means every check passed; 1 means at least one did not (the report lists
    them). Nothing is left installed either way.

    Note: on a machine where the self-signed .cer has not been imported into the trust
    stores, Authenticode status is expected to be UnknownError ("A certificate chain
    processed, but terminated in a root certificate which is not trusted"). That is
    reported as OK-with-note, not as a failure; HashMismatch / NotSigned are failures.

    ASCII-only on purpose: Windows PowerShell 5.1 reads .ps1 files without a BOM as ANSI.

.EXAMPLE
    powershell -File build/tools/verify-signed-install.ps1 -Version 1.0.0-preview.1
    powershell -File build/tools/verify-signed-install.ps1 -SetupPath artifacts\installer\WhSnap-1.0.0-preview.1-setup-x64.exe
#>
[CmdletBinding()]
param(
    [string]$Version = '1.0.0-preview.1',
    [string]$SetupPath,
    [string]$ReportPath
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $SetupPath) {
    $SetupPath = Join-Path $repoRoot ("artifacts\installer\WhSnap-{0}-setup-x64.exe" -f $Version)
}
if (-not $ReportPath) {
    $ReportPath = Join-Path ([System.IO.Path]::GetTempPath()) 'whsnap-signed-install-evidence.txt'
}

$appDir = Join-Path $env:LOCALAPPDATA 'Programs\WhSnap'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{B7A3F1C2-6D4E-4A19-9C3B-8E2F5A7D1B44}_is1'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\WhSnap'
$configFile = Join-Path $env:APPDATA 'WhSnap\settings.json'

$lines = New-Object System.Collections.Generic.List[string]
$problems = New-Object System.Collections.Generic.List[string]

function Say([string]$text) {
    Write-Host $text
    $lines.Add($text)
}

function Test-True([string]$name, [bool]$condition, [string]$detail) {
    if ($condition) { Say ("    [OK]   {0}: {1}" -f $name, $detail) }
    else { Say ("    [FAIL] {0}: {1}" -f $name, $detail); $problems.Add($name) }
}

if (-not (Test-Path -LiteralPath $SetupPath)) {
    throw "installer not found: $SetupPath (build it first: build/tools/build-installer.ps1 -Version $Version -Sign ...)"
}

$setup = Get-Item -LiteralPath $SetupPath
$beforeHash = if (Test-Path $configFile) { (Get-FileHash $configFile -Algorithm SHA256).Hash } else { '(absent)' }

Say ("=== WhSnap signed package verification  {0} ===" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
Say ("setup      : {0} ({1:N1} MB)" -f $setup.Name, ($setup.Length / 1MB))
Say ("sha256     : {0}" -f (Get-FileHash -LiteralPath $setup.FullName -Algorithm SHA256).Hash.ToLowerInvariant())
Say ("settings   : {0} (before)" -f $beforeHash)

# --- 1) the setup exe itself -------------------------------------------------
$setupSig = Get-AuthenticodeSignature -LiteralPath $setup.FullName
Say '[1] setup exe signature'
if ($null -eq $setupSig.SignerCertificate) {
    Test-True 'setup signed' $false ("status {0} - not signed" -f $setupSig.Status)
}
else {
    Test-True 'setup signed' ($setupSig.Status -ne 'HashMismatch') `
        ("{0}; signer {1}; thumb {2}; timestamp {3}" -f `
            $setupSig.Status, $setupSig.SignerCertificate.Subject,
            $setupSig.SignerCertificate.Thumbprint.Substring(0, 8),
            $(if ($setupSig.TimeStamperCertificate) { $setupSig.TimeStamperCertificate.Subject } else { '(none)' }))
}

# --- 2) silent install -------------------------------------------------------
$p = Start-Process -FilePath $setup.FullName -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru
Say ("[2] silent install exit={0}" -f $p.ExitCode)
Test-True 'install exit code' ($p.ExitCode -eq 0) ("exit {0}" -f $p.ExitCode)
Test-True 'app dir created' (Test-Path $appDir) $appDir

$exe = Join-Path $appDir 'WhSnap.exe'
$uninst = Join-Path $appDir 'unins000.exe'

if (Test-Path $exe) {
    $info = (Get-Item $exe).VersionInfo
    Say ("    FileVersion    : {0}" -f $info.FileVersion)
    Say ("    ProductVersion : {0}" -f $info.ProductVersion)
    Test-True 'installed version' ($info.ProductVersion -like "$Version*") ("ProductVersion $($info.ProductVersion)")

    $appSig = Get-AuthenticodeSignature -LiteralPath $exe
    if ($null -eq $appSig.SignerCertificate) {
        Test-True 'installed WhSnap.exe signed' $false ("status {0} - not signed (was the payload signed BEFORE ISCC?)" -f $appSig.Status)
    }
    else {
        Test-True 'installed WhSnap.exe signed' ($appSig.Status -ne 'HashMismatch') `
            ("{0}; signer {1}; timestamp {2}" -f `
                $appSig.Status, $appSig.SignerCertificate.Subject,
                $(if ($appSig.TimeStamperCertificate) { 'yes' } else { 'none' }))
    }
}

if (Test-Path $uninst) {
    $uninstSig = Get-AuthenticodeSignature -LiteralPath $uninst
    Say ("    unins000.exe   : {0} (Inno generates it at install time; unsigned is expected)" -f $uninstSig.Status)
}

$manual = @(Get-ChildItem $appDir -Filter 'whsnap-*.md' -ErrorAction SilentlyContinue)
Test-True 'user manual in app dir' ($manual.Count -ge 1) `
    $(if ($manual.Count -ge 1) { "{0} ({1} bytes)" -f $manual[0].Name, $manual[0].Length } else { 'MISSING' })
Test-True 'Run entry' ($null -ne (Get-ItemProperty $runKey -Name WhSnap -ErrorAction SilentlyContinue)) 'HKCU\...\Run\WhSnap'
Test-True 'uninstall entry' (Test-Path $uninstallKey) 'Control Panel entry'
Test-True 'Start menu entries' ((Test-Path $startMenu) -and (@(Get-ChildItem $startMenu -ErrorAction SilentlyContinue).Count -ge 3)) `
    ("{0} items" -f @(Get-ChildItem $startMenu -ErrorAction SilentlyContinue).Count)

# --- 3) self-check from the installed copy -----------------------------------
$selftest = Start-Process -FilePath $exe -ArgumentList '--selftest' -Wait -PassThru
$selftestReport = Join-Path $env:TEMP 'whsnap-selftest.txt'
$pass = @(Select-String -Path $selftestReport -Pattern '^\[PASS\]' -Encoding UTF8 -ErrorAction SilentlyContinue).Count
$fail = @(Select-String -Path $selftestReport -Pattern '^\[FAIL\]' -Encoding UTF8 -ErrorAction SilentlyContinue).Count
Say ("[3] installed --selftest exit={0}  PASS={1} FAIL={2}" -f $selftest.ExitCode, $pass, $fail)
Test-True 'installed selftest' ($selftest.ExitCode -eq 0 -and $fail -eq 0) ("exit {0}, {1} FAIL" -f $selftest.ExitCode, $fail)

# --- 4) silent uninstall -----------------------------------------------------
$p = Start-Process -FilePath $uninst -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES' -Wait -PassThru
Say ("[4] silent uninstall exit={0}" -f $p.ExitCode)

$deadline = (Get-Date).AddSeconds(120)
while ((Test-Path $appDir) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }

Test-True 'app dir removed' (-not (Test-Path $appDir)) 'no leftovers'
Test-True 'Run entry removed' ($null -eq (Get-ItemProperty $runKey -Name WhSnap -ErrorAction SilentlyContinue)) 'clean'
Test-True 'uninstall entry removed' (-not (Test-Path $uninstallKey)) 'clean'
Test-True 'Start menu removed' (-not (Test-Path $startMenu)) 'clean'

$afterHash = if (Test-Path $configFile) { (Get-FileHash $configFile -Algorithm SHA256).Hash } else { '(absent)' }
Say ("    settings.json after: {0}" -f $afterHash)
Test-True 'user data preserved' ($beforeHash -eq $afterHash) 'settings.json hash unchanged'

$verdict = if ($problems.Count -eq 0) { 'CONCLUSION: PASS' } else { "CONCLUSION: FAIL -> $($problems -join ', ')" }
Say $verdict

[System.IO.File]::WriteAllLines($ReportPath, $lines, (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("evidence written: {0}" -f $ReportPath)

if ($problems.Count -gt 0) { exit 1 }

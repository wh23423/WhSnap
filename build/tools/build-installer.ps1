<#
.SYNOPSIS
    Publishes WhSnap and builds the Inno Setup installer.

.DESCRIPTION
    Steps:
      1. dotnet publish (self-contained win-x64) into artifacts/publish
      2. ensure the .iss file is UTF-8 with BOM (Inno Setup 6 needs the BOM to read
         the Chinese UI strings correctly; an editor that strips the BOM would
         otherwise silently garble the installer)
      3. locate ISCC.exe and compile the installer into artifacts/installer
      4. generate update.json (self-update manifest) next to the installer, with the
         installer's SHA-256 and size; -ReleaseBaseUrl turns the URL into the release
         asset URL, without it the local absolute path is used (offline distribution)

    ASCII-only on purpose: Windows PowerShell 5.1 reads .ps1 files without a BOM as ANSI.

.EXAMPLE
    powershell -File build/tools/build-installer.ps1
    powershell -File build/tools/build-installer.ps1 -SkipPublish     # reuse last publish
    powershell -File build/tools/build-installer.ps1 -Version 1.0.0 -ReleaseBaseUrl https://github.com/wh23423/WhSnap/releases/download/v1.0.0
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$Version = '0.1.0',
    [switch]$SkipPublish,
    [string]$IsccPath,
    # self-update manifest (update.json)
    [string]$Channel,
    [string]$ReleaseBaseUrl,
    [string]$Notes,
    [string]$MinimumVersion = ''
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $repoRoot 'src\WhSnap.App\WhSnap.App.csproj'
$issFile = Join-Path $repoRoot 'build\installer\WhSnap.iss'
$publishDir = Join-Path $repoRoot 'artifacts\publish'
$installerDir = Join-Path $repoRoot 'artifacts\installer'

function Write-Step([string]$text) {
    Write-Host ''
    Write-Host "==> $text" -ForegroundColor Cyan
}

# --- 1) publish ---------------------------------------------------------------
if (-not $SkipPublish) {
    Write-Step "Publishing $Configuration / $Runtime (self-contained)"
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

    & dotnet publish $project -c $Configuration -r $Runtime --self-contained true `
        -p:PublishTrimmed=false -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
}
elseif (-not (Test-Path $publishDir)) {
    throw "Publish directory not found: $publishDir (run without -SkipPublish first)."
}

$exe = Join-Path $publishDir 'WhSnap.exe'
if (-not (Test-Path $exe)) { throw "WhSnap.exe not found in $publishDir." }
Write-Host ("    payload: {0} files, {1:N1} MB" -f `
    (Get-ChildItem $publishDir -Recurse -File).Count, `
    ((Get-ChildItem $publishDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB))

# --- 2) make sure the .iss is UTF-8 with BOM ---------------------------------
Write-Step 'Ensuring WhSnap.iss is UTF-8 with BOM'
$bytes = [System.IO.File]::ReadAllBytes($issFile)
$hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
if ($hasBom) {
    Write-Host '    already has BOM.'
}
else {
    $text = [System.IO.File]::ReadAllText($issFile, (New-Object System.Text.UTF8Encoding($false)))
    [System.IO.File]::WriteAllText($issFile, $text, (New-Object System.Text.UTF8Encoding($true)))
    Write-Host '    BOM added.'
}

# --- 3) compile --------------------------------------------------------------
Write-Step 'Compiling installer'
if (-not $IsccPath) {
    # NOTE: wrap the pipeline in @() -- a single surviving candidate would otherwise
    # be returned as a bare string and $candidates[0] would yield its first character.
    $candidates = @(
        @(
            (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:LOCALAPPDATA 'Inno Setup 6\ISCC.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 5\ISCC.exe')
        ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }
    )

    if ($candidates.Count -eq 0) {
        # winget installs Inno Setup per-user by default; search before giving up
        $searchRoots = @(
            (Join-Path $env:LOCALAPPDATA 'Programs'),
            ${env:ProgramFiles(x86)},
            $env:ProgramFiles
        ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }

        $found = Get-ChildItem -Path $searchRoots -Filter 'ISCC.exe' -Recurse -Depth 3 -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($found) { $candidates = @($found.FullName) }
    }

    if ($candidates.Count -eq 0) {
        throw "ISCC.exe not found. Install Inno Setup 6 (winget install JRSoftware.InnoSetup) or pass -IsccPath."
    }

    $IsccPath = $candidates[0]
}

Write-Host "    using $IsccPath (version $Version)"
if (-not (Test-Path $installerDir)) { New-Item -ItemType Directory -Path $installerDir -Force | Out-Null }

# User manual is optional: the .iss includes it (and adds a Start-menu entry) only when
# the file exists. Build the Chinese file name from code points so this script stays ASCII-only.
$manualName = 'whsnap-' + [char]0x7528 + [char]0x6237 + [char]0x624B + [char]0x518C + '.md'   # whsnap-<user manual>.md
$manualPath = Join-Path $repoRoot ('docs\' + $manualName)
if (Test-Path -LiteralPath $manualPath) {
    Write-Host ("    user manual: {0} ({1:N0} KB) -> installed into the app dir" -f $manualName, ((Get-Item -LiteralPath $manualPath).Length / 1KB))
}
else {
    Write-Host ("    user manual not found ({0}); installer will skip it" -f $manualName) -ForegroundColor Yellow
}

# 先清掉上一次的安装包：artifacts/installer 里混着旧版本时，分发很容易拿错文件
Get-ChildItem $installerDir -Filter '*.exe' -ErrorAction SilentlyContinue | Remove-Item -Force

& $IsccPath "/DMyAppVersion=$Version" $issFile
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE." }

# --- 4) self-update manifest (update.json) -----------------------------------
Write-Step 'Generating update.json'

# NOTE: @() -- a single match would otherwise be returned bare and .Name would be null.
$installer = @(Get-ChildItem $installerDir -Filter '*.exe' -ErrorAction SilentlyContinue) | Select-Object -First 1
if (-not $installer) { throw "installer exe not found in $installerDir." }

# Preview versions go to the beta channel (the app's stable channel rejects them on purpose).
if (-not $Channel) { $Channel = if ($Version -match '-') { 'beta' } else { 'stable' } }
if ($Channel -notin @('stable', 'beta')) { throw "Channel must be stable or beta (got '$Channel')." }
if (-not $Notes) { $Notes = "WhSnap $Version" }

# Without -ReleaseBaseUrl the manifest points at the local file, which is exactly what
# offline / shared-folder distribution and --update-test style checks need.
$url = if ($ReleaseBaseUrl) { $ReleaseBaseUrl.TrimEnd('/') + '/' + $installer.Name } else { $installer.FullName }

# Field names must match WhSnap.Core.UpdateManifest (System.Text.Json is case-sensitive here).
$manifest = [ordered]@{
    SchemaVersion  = 1
    Version        = $Version
    Channel        = $Channel
    Url            = $url
    Sha256         = (Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    Size           = $installer.Length
    PublishedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    Notes          = $Notes
    MinimumVersion = $MinimumVersion
}

$manifestPath = Join-Path $installerDir 'update.json'
[System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))

Write-Host ("    channel: {0}   version: {1}" -f $Channel, $Version)
Write-Host ("    url    : {0}" -f $url)
Write-Host ("    sha256 : {0}...   size: {1:N0} bytes" -f $manifest.Sha256.Substring(0, 16), $manifest.Size)

Write-Step 'Result'
Get-ChildItem $installerDir -Filter '*.exe' | ForEach-Object {
    Write-Host ("    {0}  ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB))
}
Get-ChildItem $installerDir -Filter 'update.json' | ForEach-Object {
    Write-Host ("    {0}  ({1:N0} bytes)" -f $_.Name, $_.Length)
}

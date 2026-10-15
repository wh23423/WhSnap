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

    ASCII-only on purpose: Windows PowerShell 5.1 reads .ps1 files without a BOM as ANSI.

.EXAMPLE
    powershell -File build/tools/build-installer.ps1
    powershell -File build/tools/build-installer.ps1 -SkipPublish     # reuse last publish
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$Version = '0.1.0',
    [switch]$SkipPublish,
    [string]$IsccPath
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

& $IsccPath "/DMyAppVersion=$Version" $issFile
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE." }

Write-Step 'Result'
Get-ChildItem $installerDir -Filter '*.exe' | ForEach-Object {
    Write-Host ("    {0}  ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB))
}

<#
.SYNOPSIS
    Checks that the user manual still mentions every configurable setting and every bindable action.

.DESCRIPTION
    The manual must not drift away from the implementation: every label in SettingsSchema.cs and
    every display name in HotkeyCatalog.cs has to appear verbatim in the user manual.
    Exits 1 (and lists what is missing) when something is not covered.

    ASCII-only on purpose: Windows PowerShell 5.1 reads .ps1 files without a BOM as ANSI.
    The Chinese file name of the manual is built from code points for the same reason;
    the compared strings come from the source files, so no Chinese literal is needed here.

.EXAMPLE
    powershell -File build/tools/check-manual-coverage.ps1
#>
[CmdletBinding()]
param(
    [string]$ReportPath = (Join-Path $env:TEMP 'whsnap-manual-coverage.txt')
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$schemaPath = Join-Path $repoRoot 'src\WhSnap.Core\SettingsSchema.cs'
$catalogPath = Join-Path $repoRoot 'src\WhSnap.Core\HotkeyCatalog.cs'

# docs\whsnap-<user manual>.md  (user manual = U+7528 U+6237 U+624B U+518C)
$manualName = 'whsnap-' + [char]0x7528 + [char]0x6237 + [char]0x624B + [char]0x518C + '.md'
$manualPath = Join-Path $repoRoot ('docs\' + $manualName)

if (-not (Test-Path -LiteralPath $manualPath)) { throw "user manual not found: $manualPath" }

$utf8 = New-Object System.Text.UTF8Encoding($false)
$schemaText = [System.IO.File]::ReadAllText($schemaPath, $utf8)
$catalogText = [System.IO.File]::ReadAllText($catalogPath, $utf8)
$manualText = [System.IO.File]::ReadAllText($manualPath, $utf8)

# new("Key", "Group", "Label", SettingKind   -> label is group 3
$settingMatches = [regex]::Matches($schemaText, 'new\(\s*"([A-Za-z0-9]+)"\s*,\s*"([^"]+)"\s*,\s*"([^"]+)"\s*,\s*SettingKind')
# new("id", "DisplayName", HotkeyScope       -> name is group 2
$actionMatches = [regex]::Matches($catalogText, 'new\(\s*"([A-Za-z0-9._]+)"\s*,\s*"([^"]+)"\s*,\s*HotkeyScope')

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("manual: $manualName")
$lines.Add("settings checked: $($settingMatches.Count)   actions checked: $($actionMatches.Count)")
$missing = 0

foreach ($m in $settingMatches) {
    $key = $m.Groups[1].Value
    $group = $m.Groups[2].Value
    $label = $m.Groups[3].Value
    if (-not $manualText.Contains($label)) {
        $missing++
        $lines.Add("  MISSING setting label [$group] $label  ($key)")
    }
}

foreach ($m in $actionMatches) {
    $id = $m.Groups[1].Value
    $name = $m.Groups[2].Value
    if (-not $manualText.Contains($name)) {
        $missing++
        $lines.Add("  MISSING action name $name  ($id)")
    }
}

$lines.Add("missing total: $missing")
[System.IO.File]::WriteAllLines($ReportPath, $lines, $utf8)

Write-Host ("settings checked: {0}, actions checked: {1}, missing: {2}" -f $settingMatches.Count, $actionMatches.Count, $missing)

if ($missing -gt 0) {
    Write-Host ("see {0}" -f $ReportPath) -ForegroundColor Yellow
    exit 1
}

Write-Host 'manual covers every setting label and every action name.' -ForegroundColor Green
exit 0

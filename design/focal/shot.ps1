# ============================================================================
#  WhSnap - Focal - shot.ps1
#  Renders every surface to PNG with headless Chrome, for visual self-review.
#  Usage:  powershell -File shot.ps1            # all surfaces
#          powershell -File shot.ps1 overlay    # one surface
#  NOTE: kept ASCII-only on purpose - Windows PowerShell 5.1 reads .ps1 as ANSI.
# ============================================================================
param([string]$Only = "")

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$chrome = 'C:\Program Files\Google\Chrome\Application\chrome.exe'
if (-not (Test-Path $chrome)) { throw "Chrome not found: $chrome" }

$root  = Split-Path -Parent $MyInvocation.MyCommand.Path
$shots = Join-Path $root 'shots'
New-Item -ItemType Directory -Force -Path $shots | Out-Null
# PID-suffixed profile: several instances may render in parallel without fighting.
$chromeProfile = Join-Path $env:TEMP "focal-shot-$PID"

# Width x height must match the surface's own design canvas (fixed, not responsive).
# index uses height 0 = auto (tall window, then trimmed to real content height).
$targets = [ordered]@{
  'index'      = @(1280, 0)
  'overlay'    = @(1440, 900)
  'annotate'   = @(1280, 800)
  'settings'   = @(980,  680)
  'pin-panel'  = @(760,  560)
  'tray'       = @(700,  760)
  'onboarding' = @(880,  620)
}

function Test-RowHasContent($bmp, $y, $bg) {
  for ($x = 2; $x -lt $bmp.Width; $x += 4) {
    $c = $bmp.GetPixel($x, $y)
    if ([math]::Abs($c.R - $bg.R) -gt 4 -or [math]::Abs($c.G - $bg.G) -gt 4 -or [math]::Abs($c.B - $bg.B) -gt 4) { return $true }
  }
  return $false
}

# Trim the blank tail of an auto-height page, then crop to content + 48px margin.
function Trim-Bottom($path) {
  $src = [System.Drawing.Bitmap]::FromFile($path)
  try {
    $bg = $src.GetPixel(2, 2)
    $found = -1
    for ($y = $src.Height - 1; $y -gt 0; $y -= 4) {
      if (Test-RowHasContent $src $y $bg) { $found = $y; break }
    }
    if ($found -lt 0) { return 0 }
    for ($y = [math]::Min($found + 3, $src.Height - 1); $y -gt $found - 4; $y--) {
      if (Test-RowHasContent $src $y $bg) { $found = $y; break }
    }
    $h = [math]::Min($src.Height, $found + 49)
    $dst = New-Object System.Drawing.Bitmap($src.Width, $h)
    $g = [System.Drawing.Graphics]::FromImage($dst)
    $g.DrawImage($src, 0, 0, (New-Object System.Drawing.Rectangle(0, 0, $src.Width, $h)), 'Pixel')
    $g.Dispose()
    $tmp = "$path.tmp.png"
    $dst.Save($tmp, [System.Drawing.Imaging.ImageFormat]::Png)
    $dst.Dispose()
    $src.Dispose()
    Move-Item -Force $tmp $path
    return $h
  } catch {
    if ($src) { $src.Dispose() }
    throw
  }
}

foreach ($name in $targets.Keys) {
  if ($Only -and $Only -ne $name) { continue }
  $src = if ($name -eq 'index') { Join-Path $root 'index.html' } else { Join-Path $root "surfaces\$name.html" }
  if (-not (Test-Path $src)) { Write-Host "SKIP $name (no page yet)"; continue }

  $w, $h = $targets[$name]
  $out = Join-Path $shots "$name.png"
  $url = 'file:///' + ($src -replace '\\', '/')
  $size = if ($h -eq 0) { "$w,9000" } else { "$w,$h" }

  $chromeArgs = @(
    '--headless=new', '--disable-gpu', '--no-sandbox', '--no-first-run', '--disable-extensions',
    "--user-data-dir=$chromeProfile", '--hide-scrollbars', '--force-device-scale-factor=1',
    '--virtual-time-budget=3000', "--window-size=$size", "--screenshot=$out", $url
  )
  # Chrome is a GUI binary: a bare call operator can return before the PNG lands.
  # Start-Process -Wait is reliable here; the retry loop covers a cold profile dir.
  for ($attempt = 1; $attempt -le 3; $attempt++) {
    if (Test-Path $out) { Remove-Item $out -Force }
    Start-Process -FilePath $chrome -ArgumentList $chromeArgs -Wait -NoNewWindow | Out-Null
    if (Test-Path $out) { break }
    Write-Host "     retry $name ($attempt)"
    Start-Sleep -Milliseconds 500
  }

  if (-not (Test-Path $out)) { Write-Host "FAIL $name"; continue }
  if ($h -eq 0) { $h = Trim-Bottom $out }
  $kb = [math]::Round((Get-Item $out).Length / 1kb, 1)
  Write-Host ("OK   {0,-11} {1}x{2}  {3} KB" -f $name, $w, $h, $kb)
}

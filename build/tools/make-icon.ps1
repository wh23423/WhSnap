<#
.SYNOPSIS
    Generates the WhSnap application icon (multi-size ICO with BMP payload, zero dependencies).

.DESCRIPTION
    Design: blue rounded square with a white viewfinder frame in the corners.
    Pure byte construction -- no System.Drawing -- so it runs on any PowerShell 7 or
    Windows PowerShell 5.1 host. The ICO is used for the exe icon, tray icon and
    installer icon.

    NOTE: this script is intentionally ASCII-only. Windows PowerShell 5.1 reads .ps1
    files without a BOM as ANSI, which garbles non-ASCII literals.

.EXAMPLE
    powershell -File build/tools/make-icon.ps1
#>
[CmdletBinding()]
param(
    [string]$OutPath,
    [int[]]$Sizes = @(16, 24, 32, 48, 64, 128, 256)
)

$ErrorActionPreference = 'Stop'

if (-not $OutPath) {
    $OutPath = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'src\WhSnap.App\Assets\whsnap.ico'
}

function New-IconImageData {
    param([int]$Size)

    # BITMAPINFOHEADER: biHeight is doubled (XOR bitmap + AND mask)
    $header = New-Object byte[] 40
    [BitConverter]::GetBytes([int]40).CopyTo($header, 0)
    [BitConverter]::GetBytes([int]$Size).CopyTo($header, 4)
    [BitConverter]::GetBytes([int]($Size * 2)).CopyTo($header, 8)
    [BitConverter]::GetBytes([int16]1).CopyTo($header, 12)
    [BitConverter]::GetBytes([int16]32).CopyTo($header, 14)

    $xor = New-Object byte[] ($Size * $Size * 4)
    $rowBytes = [int]([math]::Ceiling($Size / 32.0) * 4)
    $and = New-Object byte[] ($rowBytes * $Size)

    # geometry
    $margin = $Size * 0.04
    $left = $margin
    $right = $Size - $margin
    $top = $margin
    $bottom = $Size - $margin
    $radius = $Size * 0.22

    $inset = $Size * 0.24
    $arm = $Size * 0.24
    $thick = [math]::Max(1.0, $Size * 0.085)
    $half = $Size / 2.0

    $cornerXs = @($inset, ($Size - $inset))
    $cornerYs = @($inset, ($Size - $inset))

    # colors
    $bgB = 0xEB; $bgG = 0x6F; $bgR = 0x1F   # #1F6FEB
    $fgB = 0xFF; $fgG = 0xFF; $fgR = 0xFF   # white

    for ($y = 0; $y -lt $Size; $y++) {
        $rowBase = (($Size - 1 - $y) * $Size) * 4
        $py = $y + 0.5

        for ($x = 0; $x -lt $Size; $x++) {
            $px = $x + 0.5
            $a = 0; $b = 0; $g = 0; $r = 0

            $inside = $false
            if (($px -ge $left) -and ($px -le $right) -and ($py -ge $top) -and ($py -le $bottom)) {
                $inside = $true
                $cx = -1.0; $cy = -1.0
                if ($px -lt ($left + $radius)) { $cx = $left + $radius }
                elseif ($px -gt ($right - $radius)) { $cx = $right - $radius }
                if ($py -lt ($top + $radius)) { $cy = $top + $radius }
                elseif ($py -gt ($bottom - $radius)) { $cy = $bottom - $radius }
                if (($cx -ge 0) -and ($cy -ge 0)) {
                    $dx = $px - $cx
                    $dy = $py - $cy
                    if (($dx * $dx + $dy * $dy) -gt ($radius * $radius)) { $inside = $false }
                }
            }

            if ($inside) {
                $a = 255; $b = $bgB; $g = $bgG; $r = $bgR
                $onFrame = $false

                foreach ($cornerX in $cornerXs) {
                    $dirX = 1.0
                    if ($cornerX -ge $half) { $dirX = -1.0 }
                    foreach ($cornerY in $cornerYs) {
                        $dirY = 1.0
                        if ($cornerY -ge $half) { $dirY = -1.0 }

                        $hx1 = [math]::Min($cornerX, $cornerX + ($dirX * $arm))
                        $hx2 = [math]::Max($cornerX, $cornerX + ($dirX * $arm))
                        $hy1 = [math]::Min($cornerY, $cornerY + ($dirY * $thick))
                        $hy2 = [math]::Max($cornerY, $cornerY + ($dirY * $thick))

                        if (($px -ge $hx1) -and ($px -le $hx2) -and ($py -ge $hy1) -and ($py -le $hy2)) {
                            $onFrame = $true
                            break
                        }

                        $vx1 = [math]::Min($cornerX, $cornerX + ($dirX * $thick))
                        $vx2 = [math]::Max($cornerX, $cornerX + ($dirX * $thick))
                        $vy1 = [math]::Min($cornerY, $cornerY + ($dirY * $arm))
                        $vy2 = [math]::Max($cornerY, $cornerY + ($dirY * $arm))

                        if (($px -ge $vx1) -and ($px -le $vx2) -and ($py -ge $vy1) -and ($py -le $vy2)) {
                            $onFrame = $true
                            break
                        }
                    }

                    if ($onFrame) { break }
                }

                if ($onFrame) { $b = $fgB; $g = $fgG; $r = $fgR }
            }

            $offset = $rowBase + ($x * 4)
            $xor[$offset] = [byte]$b
            $xor[$offset + 1] = [byte]$g
            $xor[$offset + 2] = [byte]$r
            $xor[$offset + 3] = [byte]$a
        }
    }

    $result = New-Object byte[] ($header.Length + $xor.Length + $and.Length)
    $header.CopyTo($result, 0)
    $xor.CopyTo($result, $header.Length)
    $and.CopyTo($result, $header.Length + $xor.Length)
    return $result
}

Write-Host "Generating icon: $OutPath"
$images = @()
foreach ($size in $Sizes) {
    $data = New-IconImageData -Size $size
    $images += , $data
    Write-Host ("  - {0}x{0}: {1} bytes" -f $size, $data.Length)
}

$stream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($stream)
$writer.Write([int16]0)
$writer.Write([int16]1)
$writer.Write([int16]$Sizes.Count)

$offset = 6 + (16 * $Sizes.Count)
for ($i = 0; $i -lt $Sizes.Count; $i++) {
    $size = $Sizes[$i]
    $data = $images[$i]
    $dim = $size
    if ($size -ge 256) { $dim = 0 }

    $writer.Write([byte]$dim)
    $writer.Write([byte]$dim)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([int16]1)
    $writer.Write([int16]32)
    $writer.Write([int]$data.Length)
    $writer.Write([int]$offset)
    $offset += $data.Length
}

$writer.Flush()

# Write payload with the explicit 3-argument overload: $writer.Write($byteArray)
# is ambiguous under PowerShell overload resolution and silently writes one byte.
foreach ($data in $images) {
    $stream.Write($data, 0, $data.Length)
}

$directory = Split-Path -Parent $OutPath
if (-not (Test-Path -LiteralPath $directory)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

[System.IO.File]::WriteAllBytes($OutPath, $stream.ToArray())
$writer.Dispose()
$stream.Dispose()

$info = Get-Item -LiteralPath $OutPath
Write-Host ("Done: {0} ({1:N1} KB, {2} sizes)" -f $info.FullName, ($info.Length / 1KB), $Sizes.Count)

# Builds branding\icon.ico (multi-size, PNG-in-ICO) from the source D2RR logo PNG.
# Run from any directory; paths are resolved relative to this script.
# The caller must supply -Source pointing at the master logo PNG, e.g.:
#   .\build-icon.ps1 -Source "C:\path\to\D2RR_RAWR_LOGO.png"
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Source
)

$ErrorActionPreference = 'Stop'

$root  = Split-Path -Parent $PSCommandPath
$dest  = Join-Path $root 'icon.ico'
$copy  = Join-Path $root 'D2RR_RAWR_LOGO.png'

if (-not (Test-Path $Source)) { throw "Source PNG not found: $Source" }
Copy-Item -Force $Source $copy

Add-Type -AssemblyName System.Drawing
$srcImg = [System.Drawing.Image]::FromFile($Source)
$sizes  = @(16, 24, 32, 48, 64, 128, 256)
$pngs   = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($srcImg, 0, 0, $s, $s)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $pngs += ,@($s, $ms.ToArray())
}
$srcImg.Dispose()

$fs = [System.IO.File]::Open($dest, [System.IO.FileMode]::Create)
$bw = New-Object System.IO.BinaryWriter $fs
$count = $pngs.Count

# ICONDIR
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$count)

$headerSize = 6 + ($count * 16)
$offset = $headerSize
$entries = @()
foreach ($p in $pngs) {
    $entries += ,@($p[0], $p[1].Length, $offset)
    $offset += $p[1].Length
}

# ICONDIRENTRY x N (PNG-in-ICO; size byte 0 == 256)
foreach ($e in $entries) {
    $sz  = $e[0]; $len = $e[1]; $off = $e[2]
    $dim = if ($sz -eq 256) { [byte]0 } else { [byte]$sz }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim)
    $bw.Write([byte]0);    $bw.Write([byte]0)
    $bw.Write([uint16]1);  $bw.Write([uint16]32)
    $bw.Write([uint32]$len); $bw.Write([uint32]$off)
}

# Payloads
foreach ($p in $pngs) { $bw.Write($p[1]) }
$bw.Flush(); $bw.Dispose(); $fs.Dispose()

Write-Host ("Wrote {0} ({1} bytes, {2} sizes)" -f $dest, (Get-Item $dest).Length, $count)

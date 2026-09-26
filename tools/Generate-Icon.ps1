<#
.SYNOPSIS
  RawAnalyzerのアプリケーションアイコン(.ico)を生成する。
  Bayer配列(RGGB)の2x2モザイクをモチーフにしたデザイン。
#>
param(
    [string]$OutputPath = "$PSScriptRoot\..\RawAnalyzer.App\Assets\RawAnalyzer.ico"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

function New-RoundedRectPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Draw-Icon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # 背景: ダークの角丸スクエア
    $bgPath = New-RoundedRectPath 0 0 $size $size ($size * 0.19)
    $bg = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0x23, 0x23, 0x2A))
    $g.FillPath($bg, $bgPath)

    # 2x2 Bayerモザイク (RGGB)
    $gap = [single]($size * 0.10)
    $cell = [single](($size - 3 * $gap) / 2)
    $cr = [single]($cell * 0.24)
    $colors = @(
        [System.Drawing.Color]::FromArgb(255, 0xE0, 0x5A, 0x4E),  # R
        [System.Drawing.Color]::FromArgb(255, 0x7E, 0xCB, 0x72),  # Gr
        [System.Drawing.Color]::FromArgb(255, 0x5C, 0xA9, 0x5C),  # Gb
        [System.Drawing.Color]::FromArgb(255, 0x5B, 0x9D, 0xD9)   # B
    )
    $second = [single]($gap * 2 + $cell)
    $positions = @(
        @($gap, $gap), @($second, $gap),
        @($gap, $second), @($second, $second)
    )
    for ($i = 0; $i -lt 4; $i++) {
        $p = New-RoundedRectPath $positions[$i][0] $positions[$i][1] $cell $cell $cr
        $brush = New-Object System.Drawing.SolidBrush($colors[$i])
        $g.FillPath($brush, $p)
        $brush.Dispose(); $p.Dispose()
    }

    $g.Dispose(); $bg.Dispose(); $bgPath.Dispose()
    return $bmp
}

# 32bpp BGRA(BMP形式)エントリを生成(互換性重視。256pxのみPNG)
function Get-BmpIconEntry([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    # BITMAPINFOHEADER (高さはXORマスク+ANDマスクで2倍)
    $bw.Write([uint32]40); $bw.Write([int]$w); $bw.Write([int]($h * 2))
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]0)
    $bw.Write([uint32]($w * $h * 4)); $bw.Write([int]0); $bw.Write([int]0)
    $bw.Write([uint32]0); $bw.Write([uint32]0)
    # XORマスク: BGRA ボトムアップ
    for ($y = $h - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $w; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G)
            $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }
    # ANDマスク: 全0(アルファ使用)。行は32bit境界へパディング
    $maskRowBytes = [Math]::Ceiling($w / 32.0) * 4
    $zeros = New-Object byte[] ($maskRowBytes * $h)
    $bw.Write($zeros)
    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Close(); $ms.Dispose()
    # 配列の列挙展開を防ぐため単一要素として返す
    return , $bytes
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngs = @()
foreach ($s in $sizes) {
    $bmp = Draw-Icon $s
    if ($s -ge 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngs += , $ms.ToArray()
        $ms.Dispose()
    }
    else {
        $pngs += , [byte[]](Get-BmpIconEntry $bmp)
    }
    $bmp.Dispose()
}

$dir = Split-Path -Parent $OutputPath
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }

$fs = [System.IO.File]::Create($OutputPath)
$bw = New-Object System.IO.BinaryWriter($fs)
# ICONDIR
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
# ICONDIRENTRY
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))  # width
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))  # height
    $bw.Write([byte]0)      # colors
    $bw.Write([byte]0)      # reserved
    $bw.Write([uint16]1)    # planes
    $bw.Write([uint16]32)   # bpp
    $bw.Write([uint32]$pngs[$i].Length)
    $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($png in $pngs) { $bw.Write([byte[]]$png) }
$bw.Close(); $fs.Close()

Write-Host "生成完了: $OutputPath ($([Math]::Round((Get-Item $OutputPath).Length / 1KB, 1)) KB)"

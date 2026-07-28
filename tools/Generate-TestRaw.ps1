<#
.SYNOPSIS
  RawAnalyzer動作確認用の合成Rawファイルを生成する。

.DESCRIPTION
  既定で 32768x32768 (約10億画素・2GB) の12bit下詰め・リトルエンディアンRawを生成する。
  パターン: 対角グラデーション + 同心円リング + 1024px間隔のグリッド線 + 擬似ノイズ。
  RawAnalyzerのインポートダイアログでは以下を指定すること:
    幅 32768 / 高さ 32768 / 12bit / 下詰め(LSB) / Little / ヘッダ 0 / フレーム 1

.EXAMPLE
  .\Generate-TestRaw.ps1
  .\Generate-TestRaw.ps1 -Width 4000 -Height 3000 -OutputPath D:\eval\test_4000x3000_12bit.raw
#>
param(
    [string]$OutputPath = "$env:LOCALAPPDATA\RawAnalyzer\testdata\gigapixel_32768x32768_12bit.raw",
    [int]$Width = 32768,
    [int]$Height = 32768
)

$ErrorActionPreference = "Stop"

Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.Threading.Tasks;

public static class RawTestGen
{
    // 12bit値のパターン生成(0..4095)
    static int Pattern(int x, int y, int w, int h)
    {
        // 対角グラデーション
        int v = (int)(((long)x + y) * 4095 / (w + h - 2));

        // 中心からの同心円リング
        double dx = x - w * 0.5, dy = y - h * 0.5;
        double r = Math.Sqrt(dx * dx + dy * dy);
        v += (int)(700.0 * Math.Sin(r * 0.002));

        // 決定的な擬似ノイズ ±16
        uint n = (uint)(x * 374761393 + y * 668265263);
        n = (n ^ (n >> 13)) * 1274126177u;
        v += (int)(n >> 27) - 16;

        if (v < 0) v = 0;
        if (v > 4095) v = 4095;

        // 1024px間隔のグリッド線(パン位置の目印)。ノイズより後に置き必ず最大値にする
        if (x % 1024 < 2 || y % 1024 < 2) v = 4095;
        return v;
    }

    public static void Generate(string path, int width, int height)
    {
        const int chunkRows = 256;
        byte[] buffer = new byte[(long)width * 2 * chunkRows];
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 22))
        {
            for (int y0 = 0; y0 < height; y0 += chunkRows)
            {
                int rows = Math.Min(chunkRows, height - y0);
                Parallel.For(0, rows, r =>
                {
                    int y = y0 + r;
                    long offset = (long)r * width * 2;
                    for (int x = 0; x < width; x++)
                    {
                        int v = Pattern(x, y, width, height);
                        buffer[offset + 2 * x] = (byte)(v & 0xFF);
                        buffer[offset + 2 * x + 1] = (byte)(v >> 8);
                    }
                });
                fs.Write(buffer, 0, rows * width * 2);
                Console.Write("\r{0} / {1} 行 ({2:F0}%)   ", y0 + rows, height, (y0 + rows) * 100.0 / height);
            }
        }
        Console.WriteLine();
    }
}
"@

$dir = Split-Path -Parent $OutputPath
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }

$sizeGb = [double]$Width * $Height * 2 / 1GB
Write-Host ("生成中: {0} ({1}x{2}, 12bit, {3:F2} GB)" -f $OutputPath, $Width, $Height, $sizeGb)
$sw = [System.Diagnostics.Stopwatch]::StartNew()
[RawTestGen]::Generate($OutputPath, $Width, $Height)
$sw.Stop()
Write-Host ("完了: {0:F1} 秒" -f $sw.Elapsed.TotalSeconds)
Write-Host "インポート設定: 幅 $Width / 高さ $Height / 12bit / 下詰め(LSB) / Little / ヘッダ 0"

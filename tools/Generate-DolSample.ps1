<#
.SYNOPSIS
  RawAnalyzerのHDR機能確認用の合成DOL 2段(ライン交互)rawを生成する。

.DESCRIPTION
  シーン輝度は横方向ウェッジ(0〜16×4095)+同心円リング。
  長秒 = clip(シーン, 4095) / 短秒 = シーン/16 を偶奇行に交互格納する。
  既定で 1920×2160 (シーン1080行×2) の12bit下詰め・リトルエンディアン。
  RawAnalyzerのインポートダイアログでは以下を指定すること:
    幅 1920 / 高さ 2160 / 12bit / 下詰め(LSB) / Little / ヘッダ 0 /
    フレーム数 1 / HDR方式 "DOL 2段" / 露光比 16

.EXAMPLE
  .\Generate-DolSample.ps1
#>
param(
    [string]$OutputPath = "$env:LOCALAPPDATA\RawAnalyzer\testdata\dol_2frame_1920x2160_12bit.raw",
    [int]$Width = 1920,
    [int]$SceneHeight = 1080,
    [int]$Ratio = 16
)

$ErrorActionPreference = "Stop"

Add-Type -TypeDefinition @"
using System;
using System.IO;

public static class DolSampleGen
{
    // HDRシーン輝度 (0 .. ratio*4095)
    static double Scene(int x, int y, int w, int h, int ratio)
    {
        double wedge = (double)x / (w - 1) * ratio * 4095.0;
        double dx = x - w * 0.5, dy = y - h * 0.5;
        double r = Math.Sqrt(dx * dx + dy * dy);
        double rings = (Math.Sin(r * 0.02) + 1.0) * 0.5;  // 0..1
        double v = wedge * (0.6 + 0.4 * rings);
        // 中央に高輝度スポット(短秒でしか写らない)
        if (r < 60) v = ratio * 4095.0;
        return v;
    }

    public static void Generate(string path, int width, int sceneHeight, int ratio)
    {
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            byte[] row = new byte[width * 2];
            for (int sy = 0; sy < sceneHeight; sy++)
            {
                // 偶数行 = 長秒, 奇数行 = 短秒 (ライン交互)
                for (int stage = 0; stage < 2; stage++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        double s = Scene(x, sy, width, sceneHeight, ratio);
                        int v = stage == 0
                            ? (int)Math.Min(4095.0, s)
                            : (int)Math.Min(4095.0, s / ratio);
                        row[2 * x] = (byte)(v & 0xFF);
                        row[2 * x + 1] = (byte)(v >> 8);
                    }
                    fs.Write(row, 0, row.Length);
                }
            }
        }
    }
}
"@

$dir = Split-Path -Parent $OutputPath
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }

Write-Host ("生成中: {0} ({1}x{2}, DOL 2段ライン交互, 露光比 {3})" -f $OutputPath, $Width, ($SceneHeight * 2), $Ratio)
[DolSampleGen]::Generate($OutputPath, $Width, $SceneHeight, $Ratio)
Write-Host "完了"
Write-Host "インポート設定: 幅 $Width / 高さ $($SceneHeight*2) / 12bit / 下詰め(LSB) / Little / HDR方式 'DOL 2段' / 露光比 $Ratio"

<#
.SYNOPSIS
  READMEのスクリーンショットや動作確認に使う合成Bayer Rawを生成する。

.DESCRIPTION
  実写を使わずに一通りの機能を試せるように、以下を1枚に詰めたシーンを生成する。
    上段  : 24色のカラーチャート(ホワイトバランス・カラーマトリクスの確認用)
    中段  : 16段のグレーウェッジ(ヒストグラム・トーンカーブの確認用)
    下段左: シーメンススター(解像感・デモザイクの確認用)
    下段右: 水平ランプ(ラインプロファイル・射影の確認用)
  さらに周辺減光・ガウスノイズ・白キズ/黒キズ・欠陥列を重ねてあるので、
  欠陥画素検出やノイズ測定もそのまま試せる。

  既定で 1920x1080 の12bit下詰め・リトルエンディアン・RGGB。
  RawAnalyzerのインポートダイアログでは以下を指定すること:
    幅 1920 / 高さ 1080 / 12bit / 下詰め(LSB) / Little / ヘッダ 0 /
    フレーム数 1 / Bayer RGGB

  -PairIndex を変えると同一シーンでノイズだけが異なる2枚を作れる。
  ノイズ/DR測定は2枚差分を使うので、0と1の2回実行して使う。

  -Dark を付けると暗時フレーム(黒レベル+FPN+時間ノイズのみ)を生成する。
  ノイズ/DR測定は本来こうした一様面で行うもので、絵柄のあるフレームで測ると
  絵柄の濃淡がσ_FPNに乗ってしまい意味のある値にならない。

.EXAMPLE
  .\Generate-DemoScene.ps1
  .\Generate-DemoScene.ps1 -PairIndex 1 -OutputPath demo_b.raw
  .\Generate-DemoScene.ps1 -Dark -OutputPath dark_a.raw
  .\Generate-DemoScene.ps1 -Dark -PairIndex 1 -OutputPath dark_b.raw
#>
param(
    [string]$OutputPath = "$env:LOCALAPPDATA\RawAnalyzer\testdata\demo_scene_1920x1080_12bit_rggb.raw",
    [int]$Width = 1920,
    [int]$Height = 1080,
    [int]$PairIndex = 0,
    [switch]$Dark
)

$ErrorActionPreference = "Stop"

Add-Type -TypeDefinition @"
using System;
using System.IO;

public static class DemoSceneGen
{
    // ColorChecker相当の24色(sRGB 8bit)。表示ガンマが1.0なのでそのまま12bitへ伸ばす
    static readonly int[,] Patches = new int[24, 3] {
        {115, 82, 68}, {194,150,130}, { 98,122,157}, { 87,108, 67},
        {133,128,177}, {103,189,170}, {214,126, 44}, { 80, 91,166},
        {193, 90, 99}, { 94, 60,108}, {157,188, 64}, {224,163, 46},
        { 56, 61,150}, { 70,148, 73}, {175, 54, 60}, {231,199, 31},
        {187, 86,149}, { 8,133,161}, {243,243,242}, {200,200,200},
        {160,160,160}, {122,122,121}, { 85, 85, 85}, { 52, 52, 52}
    };

    // 決定的な擬似乱数(seedを変えるとノイズだけが変わる)
    static uint Hash(uint a)
    {
        a ^= a >> 16; a *= 0x7feb352d;
        a ^= a >> 15; a *= 0x846ca68b;
        a ^= a >> 16;
        return a;
    }

    static double Uniform(int x, int y, int seed)
    {
        return Hash((uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(seed * 83492791))
               / 4294967296.0;
    }

    // Box-Muller。片側だけ使う
    static double Normal(int x, int y, int seed)
    {
        double u1 = Math.Max(1e-9, Uniform(x, y, seed));
        double u2 = Uniform(x, y, seed + 1);
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    // シーンのRGB(0..1)を返す
    static void Scene(int x, int y, int w, int h, out double r, out double g, out double b)
    {
        // 帯の境界は偶数行に合わせる(奇数行だとBayer位相がまたがって1行だけ色が付く)
        int yChart = ((int)(h * 0.46)) & ~1;
        int yWedge = ((int)(h * 0.60)) & ~1;

        if (y < yChart)
        {
            // カラーチャート: 6x4。周囲と枠は暗いグレー
            double bg = 0.09;
            r = g = b = bg;
            int cols = 6, rows = 4;
            double marginX = w * 0.06, marginY = h * 0.03;
            double cellW = (w - 2 * marginX) / cols;
            double cellH = (yChart - 2 * marginY) / rows;
            int cx = (int)Math.Floor((x - marginX) / cellW);
            int cy = (int)Math.Floor((y - marginY) / cellH);
            if (cx >= 0 && cx < cols && cy >= 0 && cy < rows)
            {
                // パッチ間に隙間を空ける
                double ix = (x - marginX) - cx * cellW;
                double iy = (y - marginY) - cy * cellH;
                if (ix > cellW * 0.06 && ix < cellW * 0.94 &&
                    iy > cellH * 0.08 && iy < cellH * 0.92)
                {
                    int p = cy * cols + cx;
                    r = Patches[p, 0] / 255.0;
                    g = Patches[p, 1] / 255.0;
                    b = Patches[p, 2] / 255.0;
                }
            }
            return;
        }

        if (y < yWedge)
        {
            // グレーウェッジ16段
            int step = (int)(16.0 * x / w);
            double v = step / 15.0;
            r = g = b = v;
            return;
        }

        if (x < w * 0.5)
        {
            // シーメンススター(36本)
            double cx0 = w * 0.25, cy0 = h * 0.80;
            double dx = x - cx0, dy = y - cy0;
            double rad = Math.Sqrt(dx * dx + dy * dy);
            double lim = Math.Min(w * 0.22, h * 0.19);
            if (rad > lim)
            {
                r = g = b = 0.12;
            }
            else
            {
                double th = Math.Atan2(dy, dx);
                double s = Math.Sin(th * 18.0);
                double v = s > 0 ? 0.92 : 0.05;
                // 中心の細かすぎる部分は潰す
                if (rad < lim * 0.06) v = 0.5;
                r = g = b = v;
            }
            return;
        }

        // 水平ランプ + 細かいリング(プロファイル観察用)
        double t = (x - w * 0.5) / (w * 0.5);
        double ring = 0.06 * Math.Sin((x + y) * 0.08);
        double val = Math.Max(0.0, Math.Min(1.0, t + ring));
        r = val; g = val; b = val;
    }

    public static void Generate(string path, int w, int h, int pairIndex, bool dark)
    {
        int seed = 1000 + pairIndex * 7919;
        const int Max = 4095;
        const int BlackLevel = 240;

        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            byte[] row = new byte[w * 2];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (dark)
                    {
                        // 黒レベル + 固定パターン(pairIndexに依らない) + 時間ノイズ
                        double fpn = (Uniform(x, y, 555) - 0.5) * 24.0;
                        double tmp = Normal(x, y, seed) * 5.5;
                        int dv = (int)Math.Round(BlackLevel + fpn + tmp);
                        double dd = Uniform(x, y, 424242);
                        if (dd > 0.99997) dv = Max;
                        dv = Math.Max(0, Math.Min(Max, dv));
                        row[2 * x] = (byte)(dv & 0xFF);
                        row[2 * x + 1] = (byte)(dv >> 8);
                        continue;
                    }

                    double r, g, b;
                    Scene(x, y, w, h, out r, out g, out b);

                    // RGGB。偶数行=R,Gr / 奇数行=Gb,B
                    double v = ((y & 1) == 0)
                        ? (((x & 1) == 0) ? r : g)
                        : (((x & 1) == 0) ? g : b);

                    // 周辺減光(中心1.0 → 四隅0.72)
                    double nx = (x - w * 0.5) / (w * 0.5);
                    double ny = (y - h * 0.5) / (h * 0.5);
                    v *= 1.0 - 0.14 * (nx * nx + ny * ny);

                    int code = (int)Math.Round(v * Max);

                    // 読み出しノイズ + ショットノイズ相当
                    code += (int)Math.Round(Normal(x, y, seed) * (6.0 + 0.7 * Math.Sqrt(Math.Max(0, code))));

                    // 欠陥列(暗電流の乗った1列)
                    if (x == 1234) code += 260;

                    // 白キズ/黒キズ。位置はpairIndexに依らず固定(実センサと同じ振る舞い)
                    double d = Uniform(x, y, 424242);
                    if (d > 0.99997) code = Max;
                    else if (d < 0.00001) code = 0;

                    code = Math.Max(0, Math.Min(Max, code));
                    row[2 * x] = (byte)(code & 0xFF);
                    row[2 * x + 1] = (byte)(code >> 8);
                }
                fs.Write(row, 0, row.Length);
            }
        }
    }
}
"@

$dir = Split-Path -Parent $OutputPath
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }

$kind = if ($Dark) { "暗時フレーム" } else { "評価シーン" }
Write-Host ("生成中: {0} ({1}x{2}, 12bit RGGB, {3}, ペア {4})" -f $OutputPath, $Width, $Height, $kind, $PairIndex)
[DemoSceneGen]::Generate($OutputPath, $Width, $Height, $PairIndex, $Dark.IsPresent)
Write-Host "完了"
Write-Host "インポート設定: 幅 $Width / 高さ $Height / 12bit / 下詰め(LSB) / Little / ヘッダ 0 / Bayer RGGB"

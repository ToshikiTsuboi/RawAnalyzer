using RawAnalyzer.App.Rendering;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class ColorPipelineTests
{
    private static readonly BayerPattern[] AllPatterns =
    {
        BayerPattern.Rggb, BayerPattern.Bggr, BayerPattern.Grbg, BayerPattern.Gbrg,
    };

    /// <summary>チャネルごとに一定値のモザイクを生成する。</summary>
    internal static ushort[] BuildConstantMosaic(
        int width, int height, BayerPattern pattern, ushort r, ushort g, ushort b)
    {
        var mosaic = new ushort[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                mosaic[y * width + x] = BayerHelper.GetChannel(pattern, x, y) switch
                {
                    BayerChannel.R => r,
                    BayerChannel.B => b,
                    _ => g,
                };
            }
        }

        return mosaic;
    }

    public static IEnumerable<object[]> Patterns()
    {
        foreach (BayerPattern p in AllPatterns)
        {
            yield return new object[] { p };
        }
    }

    public static IEnumerable<object[]> InvalidDevelopParameters()
    {
        // 範囲外(Gamma 0・負のゲイン・負のコントラスト)と非有限(NaN・∞)は
        // Create 冒頭の同じ入口で弾かれる。行列の NaN だけは ColorMatrix.Validate の ArgumentException
        yield return new object[] { new DevelopParameters(Gamma: 0), typeof(ArgumentOutOfRangeException) };
        yield return new object[] { new DevelopParameters(Gain: -1), typeof(ArgumentOutOfRangeException) };
        yield return new object[] { new DevelopParameters(Contrast: -1), typeof(ArgumentOutOfRangeException) };
        yield return new object[] { new DevelopParameters(Gamma: double.NaN), typeof(ArgumentOutOfRangeException) };
        yield return new object[]
        {
            new DevelopParameters(Gain: double.PositiveInfinity), typeof(ArgumentOutOfRangeException),
        };
        yield return new object[]
        {
            new DevelopParameters(Gamma: 1.0, Matrix: new ColorMatrix(1, 0, 0, 0, double.NaN, 0, 0, 0, 1)),
            typeof(ArgumentException),
        };
    }

    [Theory]
    [MemberData(nameof(Patterns))]
    public void BlockToRgb_ConstantChannels_RecoversValues(BayerPattern pattern)
    {
        ushort[] mosaic = BuildConstantMosaic(2, 2, pattern, 1000, 2000, 3000);
        ColorPipeline.BlockToRgb(
            pattern, mosaic[0], mosaic[1], mosaic[2], mosaic[3],
            out ushort r, out ushort g, out ushort b);
        Assert.Equal(1000, r);
        Assert.Equal(2000, g);
        Assert.Equal(3000, b);
    }

    [Theory]
    [InlineData(BayerPattern.Rggb)]
    [InlineData(BayerPattern.Bggr)]   // pattern 引数が無視・固定されていないことを見る
    public void DemosaicBilinear_ConstantChannels_ExactEverywhere(BayerPattern pattern)
    {
        const int size = 8;
        ushort[] mosaic = BuildConstantMosaic(size, size, pattern, 1000, 2000, 3000);
        var rgb = new ushort[size * size * 3];

        ColorPipeline.DemosaicBilinear(mosaic, size, size, 0, 0, pattern, rgb);

        for (int i = 0; i < size * size; i++)
        {
            Assert.Equal(1000, rgb[i * 3]);
            Assert.Equal(2000, rgb[i * 3 + 1]);
            Assert.Equal(3000, rgb[i * 3 + 2]);
        }
    }

    [Fact]
    public void DemosaicBilinear_LinearRamp_ExactInInterior()
    {
        // 全チャネル同一の線形ランプ: バイリニア補間は内部で厳密に元値を復元する
        // (入力・期待値・通る分岐がパターンによらず同じなので Rggb のみ)
        const int size = 10;
        var mosaic = new ushort[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                mosaic[y * size + x] = (ushort)(100 * x + 40 * y);
            }
        }

        var rgb = new ushort[size * size * 3];
        ColorPipeline.DemosaicBilinear(mosaic, size, size, 0, 0, BayerPattern.Rggb, rgb);

        for (int y = 1; y < size - 1; y++)
        {
            for (int x = 1; x < size - 1; x++)
            {
                int expected = 100 * x + 40 * y;
                int index = (y * size + x) * 3;
                Assert.Equal(expected, rgb[index]);
                Assert.Equal(expected, rgb[index + 1]);
                Assert.Equal(expected, rgb[index + 2]);
            }
        }
    }

    [Fact]
    public void DemosaicBilinear_NonZeroOrigin_UsesAbsoluteParity()
    {
        // origin(1,1)から切り出した領域: パリティが反転する
        const int size = 6;
        ushort[] full = BuildConstantMosaic(size + 2, size + 2, BayerPattern.Rggb, 1000, 2000, 3000);
        var region = new ushort[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                region[y * size + x] = full[(y + 1) * (size + 2) + (x + 1)];
            }
        }

        var rgb = new ushort[size * size * 3];
        ColorPipeline.DemosaicBilinear(region, size, size, 1, 1, BayerPattern.Rggb, rgb);

        for (int i = 0; i < size * size; i++)
        {
            Assert.Equal(1000, rgb[i * 3]);
            Assert.Equal(2000, rgb[i * 3 + 1]);
            Assert.Equal(3000, rgb[i * 3 + 2]);
        }
    }

    [Fact]
    public void DevelopLuts_LinearParameters_MapEndpoints()
    {
        var luts = DevelopLuts.Create(new DevelopParameters(BlackLevel: 0, Gamma: 1.0));
        Assert.Equal(0, luts.G[0]);
        Assert.Equal(255, luts.G[65535]);
        Assert.InRange(luts.G[32768], (byte)127, (byte)128);
    }

    [Fact]
    public void DevelopLuts_WbGains_ScaleTheirOwnChannels()
    {
        // GainR/GainG/GainB を1回の Create で同時に与え、
        // 各ゲインが自分のチャネルにだけ配線されていることを見る
        var luts = DevelopLuts.Create(
            new DevelopParameters(GainR: 2.0, GainG: 1.0, GainB: 0.5, Gamma: 1.0));

        Assert.Equal(255, luts.R[32768]);                       // ×2: 半分の入力で飽和
        Assert.InRange(luts.R[16384], (byte)127, (byte)128);
        Assert.InRange(luts.G[32768], (byte)127, (byte)128);   // ×1: 等倍のまま
        Assert.Equal(255, luts.G[65535]);
        Assert.InRange(luts.B[65535], (byte)127, (byte)128);   // ×0.5: 最大入力でも半分
        Assert.InRange(luts.B[32768], (byte)63, (byte)64);
    }

    [Fact]
    public void DevelopLuts_BlackAndWhitePoint_ClipBothEnds()
    {
        // 黒1000・白33768(幅32768): 黒以下は0、白以上は255、中点は半分
        var luts = DevelopLuts.Create(
            new DevelopParameters(BlackLevel: 1000, WhitePoint: 33768, Gamma: 1.0));

        Assert.Equal(0, luts.G[0]);
        Assert.Equal(0, luts.G[1000]);
        Assert.InRange(luts.G[1000 + 16384], (byte)127, (byte)128);
        Assert.Equal(255, luts.G[33768]);
        Assert.Equal(255, luts.G[65535]);
    }

    [Fact]
    public void DevelopLuts_Gain_ScalesAllChannels()
    {
        // ColorDevelop表示でゲインスライダーが完全に無反応だった問題の回帰テスト
        var luts = DevelopLuts.Create(new DevelopParameters(Gamma: 1.0, Gain: 2.0));

        Assert.Equal(255, luts.R[32768]);
        Assert.Equal(255, luts.G[32768]);
        Assert.Equal(255, luts.B[32768]);
        Assert.InRange(luts.G[16384], (byte)127, (byte)128);
    }

    [Fact]
    public void DevelopLuts_Contrast_SteepensAroundMidpoint()
    {
        var flat = DevelopLuts.Create(new DevelopParameters(Gamma: 1.0));
        var steep = DevelopLuts.Create(new DevelopParameters(Gamma: 1.0, Contrast: 2.0));

        // 中心(0.5)は動かず、上下は開く
        Assert.InRange(steep.G[32768], (byte)127, (byte)128);
        Assert.True(steep.G[49152] > flat.G[49152], "上側が明るくなること");
        Assert.True(steep.G[16384] < flat.G[16384], "下側が暗くなること");
        Assert.Equal(0, steep.G[16384]);
    }

    [Theory]
    [MemberData(nameof(InvalidDevelopParameters))]
    public void DevelopLuts_InvalidParameters_Throw(DevelopParameters parameters, Type expectedException)
    {
        Assert.Throws(expectedException, () => DevelopLuts.Create(parameters));
    }

    [Fact]
    public void DevelopLuts_SwapMatrix_ExchangesChannels()
    {
        // R↔B入替行列 + 全体ゲイン2: ゲインは行列の前(線形段)で掛かる
        var swap = new ColorMatrix(0, 0, 1, 0, 1, 0, 1, 0, 0);
        var luts = DevelopLuts.Create(
            new DevelopParameters(Gamma: 1.0, Matrix: swap, Gain: 2.0));
        Assert.True(luts.HasMatrix);

        luts.Convert(20000, 10000, 5000, out byte r, out byte g, out byte b);

        // ガンマ1なので出力 ≈ 入力×2/65535×255(R/B入替): 5000→39, 10000→78, 20000→156
        Assert.InRange(r, (byte)38, (byte)40);
        Assert.InRange(g, (byte)77, (byte)79);
        Assert.InRange(b, (byte)155, (byte)157);
    }

    [Fact]
    public void DevelopLuts_MatrixClampsNegativeAndOverflow()
    {
        // 大きな係数は255、負の係数は0にクランプされ、0.5倍はそのまま半分になる
        var extreme = new ColorMatrix(3, 0, 0, 0, -1, 0, 0, 0, 0.5);
        var luts = DevelopLuts.Create(new DevelopParameters(Gamma: 1.0, Matrix: extreme));

        luts.Convert(65535, 65535, 65535, out byte r, out byte g, out byte b);

        Assert.Equal(255, r);
        Assert.Equal(0, g);
        Assert.InRange(b, (byte)127, (byte)128);
    }

    public static IEnumerable<object[]> DiagonalMatrixCases()
    {
        // 対角行列 diag(dR,dG,dB) を掛ける行列経路は、WBゲインへ同じ倍率を
        // 掛けた非行列経路(クリップは最終段の1回だけ)と同じ出力になるはず。
        // 1. レビュー指摘#5の再現値: WB R=2 で1を超えた値が行列0.5倍で範囲内へ戻る
        yield return new object[] { new DevelopParameters(GainR: 2.0, Gamma: 1.0), 0.5, 1.0, 1.0 };

        // 2. 行列で1を超えた値・黒レベル未満の負の値を、コントラスト<1が範囲内へ戻す
        yield return new object[]
        {
            new DevelopParameters(BlackLevel: 8192, Gamma: 1.0, Contrast: 0.5), 1.5, 1.0, 1.0,
        };

        // 3. 黒/白点・全体ゲイン・コントラスト>1・ガンマ2.2 を含む一般の組み合わせ
        yield return new object[]
        {
            new DevelopParameters(
                BlackLevel: 4096, GainR: 1.8, GainB: 1.4, Gamma: 2.2,
                WhitePoint: 60000, Gain: 1.5, Contrast: 1.3),
            0.7, 1.1, 0.6,
        };
    }

    [Theory]
    [MemberData(nameof(DiagonalMatrixCases))]
    public void DevelopLuts_DiagonalMatrix_MatchesEquivalentWbGains(
        DevelopParameters parameters, double dR, double dG, double dB)
    {
        // 行列前(WB・ゲイン後)や行列後・コントラスト前で0〜1へ切り詰めると、
        // 行列やコントラストで範囲内へ戻るはずの値が失われて非行列経路とずれる
        var withMatrix = DevelopLuts.Create(
            parameters with { Matrix = new ColorMatrix(dR, 0, 0, 0, dG, 0, 0, 0, dB) });
        var reference = DevelopLuts.Create(parameters with
        {
            GainR = parameters.GainR * dR,
            GainG = parameters.GainG * dG,
            GainB = parameters.GainB * dB,
        });
        Assert.True(withMatrix.HasMatrix);
        Assert.False(reference.HasMatrix);

        for (int v = 0; v < 65536; v++)
        {
            var code = (ushort)v;
            withMatrix.Convert(code, code, code, out byte r, out byte g, out byte b);

            // 行列経路はガンマLUTの入力量子化(65536段)ぶんだけ丸めが違い得る
            Assert.InRange(r - reference.R[v], -1, 1);
            Assert.InRange(g - reference.G[v], -1, 1);
            Assert.InRange(b - reference.B[v], -1, 1);
        }
    }

    [Fact]
    public void DevelopLuts_MatrixAfterWbOverflow_DisplayAndExportKeepHighlight()
    {
        // レビュー指摘#5: Gamma=1、WB R=2、行列diag(0.5,1,1)、R=49151 は
        // 0.75×2=1.5 → 行列で0.75 → 191 になるはず(行列前に1へ切り詰めると128)。
        // 表示(ViewportRenderer)と現像保存(ImageExport)は同じ DevelopLuts.Convert を通る
        const int size = 8;
        ushort[] mosaic = BuildConstantMosaic(size, size, BayerPattern.Rggb, 49151, 32768, 16384);
        using RawImage image = TestImages.FromCodes(mosaic, size, size, bayer: BayerPattern.Rggb);
        var luts = DevelopLuts.Create(new DevelopParameters(
            GainR: 2.0, Gamma: 1.0, Matrix: new ColorMatrix(0.5, 0, 0, 0, 1, 0, 0, 0, 1)));

        byte[] exported = ImageExport.DevelopRgb24(image, 0, BayerPattern.Rggb, luts);

        var request = new RenderRequest
        {
            Source = new RawImageRenderSource(image, 0),
            Lut = DisplayLut.Create(new DisplayParameters()),
            Mode = ViewportDisplayMode.ColorDevelop,
            Pattern = BayerPattern.Rggb,
            DevelopLuts = luts,
        };
        var displayed = new byte[size * size * 4];
        ViewportRenderer.Render(
            request, zoom: 1.0, originX: 0, originY: 0, size, size, displayed, default);

        for (int i = 0; i < size * size; i++)
        {
            Assert.Equal(191, exported[i * 3]);
            Assert.Equal(128, exported[i * 3 + 1]);
            Assert.Equal(64, exported[i * 3 + 2]);

            // 表示バッファは BGRA
            Assert.Equal(64, displayed[i * 4]);
            Assert.Equal(128, displayed[i * 4 + 1]);
            Assert.Equal(191, displayed[i * 4 + 2]);
        }
    }
}

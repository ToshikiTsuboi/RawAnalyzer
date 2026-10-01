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
        yield return new object[] { new DevelopParameters(SourceBitDepth: 0), typeof(ArgumentOutOfRangeException) };
        yield return new object[] { new DevelopParameters(SourceBitDepth: 17), typeof(ArgumentOutOfRangeException) };
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
    public void DemosaicBilinear_Canceled_ThrowsInsteadOfReturningUnfilledRows()
    {
        // 残課題 2026-10-02 A6。取り消されると例外を出さずに途中で戻り、処理されなかった行(確保直後の0や前の
        // 内容)を正常な結果として呼び出し側へ返していた(書き出し・描画はそれぞれ後で確かめて補っていた)。
        // 取り消しは OperationCanceledException で知らせる
        const int size = 64;
        ushort[] mosaic = BuildConstantMosaic(size, size, BayerPattern.Rggb, 1000, 2000, 3000);
        var rgb = new ushort[size * size * 3];
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => ColorPipeline.DemosaicBilinear(
            mosaic, size, size, 0, 0, BayerPattern.Rggb, rgb, cts.Token));
    }

    [Fact]
    public void DevelopLuts_WbGainsScaleTheirOwnChannels_GainScalesAll()
    {
        // GainR/GainG/GainB と全体の Gain を1回の Create で同時に与え、各WBゲインが自分のチャネルにだけ、
        // 全体の Gain が全チャネルに配線されていることを見る(実効倍率は R×4・G×2・B×1)。
        // 全体の Gain は、カラー現像でゲインスライダーが完全に無反応だった問題(1fba66b 重大#14)の回帰の確認を兼ねる
        var luts = DevelopLuts.Create(
            new DevelopParameters(GainR: 2.0, GainG: 1.0, GainB: 0.5, Gamma: 1.0, Gain: 2.0));

        Assert.Equal(255, luts.R[16384]);                       // ×4: 1/4の入力で飽和
        Assert.InRange(luts.R[8192], (byte)127, (byte)128);
        Assert.Equal(255, luts.G[32768]);                       // ×2: 半分の入力で飽和
        Assert.InRange(luts.G[16384], (byte)127, (byte)128);
        Assert.InRange(luts.B[32768], (byte)127, (byte)128);   // ×1: 等倍のまま
        Assert.Equal(255, luts.B[65535]);
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

    /// <summary>行の和が1(白→白)で非対角が負の、典型的なカメラRGB→表示RGBの行列。</summary>
    private static readonly ColorMatrix TypicalCcm = new(
        1.6, -0.4, -0.2,
        -0.2, 1.4, -0.2,
        0.0, -0.5, 1.5);

    [Theory]
    [InlineData(1.0)]
    public void DevelopLuts_SaturatedPixel_TypicalCcm_StaysWhite(double gamma)
    {
        // 全チャネルが白点(65535)に達した画素を WB(2,1,1.5)・典型的なCCMで現像する。
        // WB後の (2,1,1.5) をそのまま行列へ通すと (2.5,0.7,1.75) → クリップで (1,0.7,1) の
        // マゼンタになる。飽和画素は行列の前で0〜1へ切り詰めて (1,1,1) とし、白のまま保つ
        var luts = DevelopLuts.Create(new DevelopParameters(
            GainR: 2.0, GainB: 1.5, Gamma: gamma, Matrix: TypicalCcm));

        luts.Convert(65535, 65535, 65535, out byte r, out byte g, out byte b);

        Assert.Equal(255, r);
        Assert.Equal(255, g);
        Assert.Equal(255, b);
    }

    [Theory]
    [InlineData(65535, 4095)]   // 既定の白レベル(最大code): 白点65535、画素は 4095<<4 = 65520
    [InlineData(64015, 4000)]   // 白レベル code 4000: 白点 = 4000<<4 | 15
    public void DevelopLuts_SaturatedPixel_ComparesInCodesOfSourceBitDepth(
        int whitePoint, int whiteCode)
    {
        // アプリは Nbit 素材の白点を白レベルcodeの上端(下位ビットを1で埋めた値)に置き、
        // 画素は code<<(16−N) で下位ビットが0。16bit値のまま比べると白レベルのcodeの画素が
        // 白点未満になり、12bit等の白飛びがマゼンタのまま残る。code で比べて白に保つ
        var luts = DevelopLuts.Create(new DevelopParameters(
            GainR: 2.0, GainB: 1.5, Gamma: 1.0, Matrix: TypicalCcm,
            WhitePoint: (ushort)whitePoint, SourceBitDepth: 12));

        var saturated = (ushort)(whiteCode << 4);
        luts.Convert(saturated, saturated, saturated, out byte r, out byte g, out byte b);
        Assert.Equal(255, r);
        Assert.Equal(255, g);
        Assert.Equal(255, b);

        // 1code下は飽和していない: 行列の前では切り詰めず、行列の後に1回だけクリップする
        // (G = (−0.2·2 + 1.4 − 0.2·1.5)·x = 0.7x、R・B は1を超えて255)
        var below = (ushort)((whiteCode - 1) << 4);
        luts.Convert(below, below, below, out r, out g, out b);
        int expectedG = (int)Math.Round(255 * 0.7 * below / whitePoint);
        Assert.Equal(255, r);
        Assert.InRange(g, expectedG - 1, expectedG + 1);
        Assert.Equal(255, b);
    }

    [Fact]
    public void DevelopLuts_DegenerateWhitePoint_TreatsAboveBlackAsSaturated()
    {
        // 白点 ≤ 黒点の縮退時、線形値は x = (v > 黒点 ? 1 : 0)。飽和の判定も同じ定義に合わせ、
        // 黒点を超える画素を白点に達した画素とみなす
        var luts = DevelopLuts.Create(new DevelopParameters(
            BlackLevel: 30000, WhitePoint: 20000, GainR: 2.0, GainB: 1.5, Gamma: 1.0,
            Matrix: TypicalCcm));

        luts.Convert(40000, 40000, 40000, out byte r, out byte g, out byte b);
        Assert.Equal(255, r);
        Assert.Equal(255, g);
        Assert.Equal(255, b);

        luts.Convert(30000, 30000, 30000, out r, out g, out b);
        Assert.Equal(0, r);
        Assert.Equal(0, g);
        Assert.Equal(0, b);
    }

    public static IEnumerable<object[]> DiagonalMatrixCases()
    {
        // 対角行列 diag(dR,dG,dB) を掛ける行列経路は、WBゲインへ同じ倍率を
        // 掛けた非行列経路(クリップは最終段の1回だけ)と同じ出力になるはず。
        // (レビュー指摘#5の再現値 WB R=2・diag(0.5,1,1) は DevelopLuts_MatrixAfterWbOverflow_DisplayAndExportKeepHighlight
        // が手計算の値で見る)
        // 1. 行列で1を超えた値・黒レベル未満の負の値を、コントラスト<1が範囲内へ戻す
        yield return new object[]
        {
            new DevelopParameters(BlackLevel: 8192, Gamma: 1.0, Contrast: 0.5), 1.5, 1.0, 1.0,
        };

        // 2. 黒/白点・全体ゲイン・コントラスト>1・ガンマ2.2 を含む一般の組み合わせ
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

        // 白点以上の入力(情報が失われた飽和画素)は除外する。行列経路は白飛びを中性に保つため
        // 飽和画素だけ行列の前で0〜1へ切り詰めるが、非行列経路はチャネル別LUTで他チャネルの
        // 飽和を知り得ず最終段でしかクリップしないので、そこでは一致しない
        // (この比較が確かめたいのは、飽和していない値のクリップ順序)
        for (int v = 0; v < parameters.WhitePoint; v++)
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

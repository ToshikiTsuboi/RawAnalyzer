using RawViewer.Core;
using Xunit;

namespace RawViewer.Tests;

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
    [MemberData(nameof(Patterns))]
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

    [Theory]
    [MemberData(nameof(Patterns))]
    public void DemosaicBilinear_LinearRamp_ExactInInterior(BayerPattern pattern)
    {
        // 全チャネル同一の線形ランプ: バイリニア補間は内部で厳密に元値を復元する
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
        ColorPipeline.DemosaicBilinear(mosaic, size, size, 0, 0, pattern, rgb);

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
    public void DevelopLuts_GainR_ScalesRedChannel()
    {
        var luts = DevelopLuts.Create(new DevelopParameters(GainR: 2.0, Gamma: 1.0));
        Assert.Equal(255, luts.R[32768]);
        Assert.InRange(luts.R[16384], (byte)127, (byte)128);
        Assert.InRange(luts.G[32768], (byte)127, (byte)128);
    }

    [Fact]
    public void DevelopLuts_BlackLevel_ClipsBelow()
    {
        var luts = DevelopLuts.Create(new DevelopParameters(BlackLevel: 1000, Gamma: 1.0));
        Assert.Equal(0, luts.G[0]);
        Assert.Equal(0, luts.G[1000]);
        Assert.Equal(255, luts.G[65535]);
    }

    [Fact]
    public void DevelopLuts_InvalidGamma_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DevelopLuts.Create(new DevelopParameters(Gamma: 0)));
    }

    [Fact]
    public void DevelopLuts_IdentityMatrix_MatchesChannelLuts()
    {
        var withMatrix = DevelopLuts.Create(new DevelopParameters(
            Gamma: 2.2, Matrix: ColorMatrix.Identity));
        Assert.False(withMatrix.HasMatrix);

        withMatrix.Convert(10000, 20000, 30000, out byte r, out byte g, out byte b);
        Assert.Equal(withMatrix.R[10000], r);
        Assert.Equal(withMatrix.G[20000], g);
        Assert.Equal(withMatrix.B[30000], b);
    }

    [Fact]
    public void DevelopLuts_SwapMatrix_ExchangesChannels()
    {
        // R↔B入替行列
        var swap = new ColorMatrix(0, 0, 1, 0, 1, 0, 1, 0, 0);
        var luts = DevelopLuts.Create(new DevelopParameters(Gamma: 1.0, Matrix: swap));
        Assert.True(luts.HasMatrix);

        luts.Convert(40000, 20000, 10000, out byte r, out byte g, out byte b);

        // ガンマ1・ゲイン1なので出力 ≈ 入力/65535*255(R/B入替)
        Assert.InRange(r, (byte)(10000 * 255 / 65535 - 1), (byte)(10000 * 255 / 65535 + 1));
        Assert.InRange(g, (byte)(20000 * 255 / 65535 - 1), (byte)(20000 * 255 / 65535 + 1));
        Assert.InRange(b, (byte)(40000 * 255 / 65535 - 1), (byte)(40000 * 255 / 65535 + 1));
    }

    [Fact]
    public void DevelopLuts_ScaleMatrix_HalvesOutput()
    {
        var half = new ColorMatrix(0.5, 0, 0, 0, 0.5, 0, 0, 0, 0.5);
        var luts = DevelopLuts.Create(new DevelopParameters(Gamma: 1.0, Matrix: half));

        luts.Convert(65535, 65535, 65535, out byte r, out byte g, out byte b);

        Assert.InRange(r, (byte)127, (byte)128);
        Assert.InRange(g, (byte)127, (byte)128);
        Assert.InRange(b, (byte)127, (byte)128);
    }

    [Fact]
    public void DevelopLuts_MatrixClampsNegativeAndOverflow()
    {
        // 大きな係数と負の係数でも0..255にクランプされる
        var extreme = new ColorMatrix(3, 0, 0, 0, -1, 0, 0, 0, 1);
        var luts = DevelopLuts.Create(new DevelopParameters(Gamma: 1.0, Matrix: extreme));

        luts.Convert(65535, 65535, 65535, out byte r, out byte g, out byte b);

        Assert.Equal(255, r);
        Assert.Equal(0, g);
        Assert.Equal(255, b);
    }
}

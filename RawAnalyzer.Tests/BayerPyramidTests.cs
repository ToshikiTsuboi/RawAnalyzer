using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class BayerPyramidTests
{
    private static RawImage LoadImage(ushort[] codes, RawFormat format)
    {
        string path = TestData.WriteTempFile(TestData.EncodeRawFile(codes, format));
        try
        {
            return RawLoader.Load(path, format);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static RawFormat MakeFormat(int width, int height) => new()
    {
        Width = width,
        Height = height,
        BitDepth = 16,
        Bayer = BayerPattern.Rggb,
    };

    [Fact]
    public void Create_NoBayer_ProducesNoLevels()
    {
        var format = new RawFormat { Width = 32, Height = 32, BitDepth = 16 };
        using RawImage image = LoadImage(new ushort[32 * 32], format);

        using var pyramid = BayerPyramid.Create(image, format);

        Assert.Equal(0, pyramid.LevelCount);
        Assert.Equal(1, pyramid.SelectFactor(0.1));
    }

    [Fact]
    public void Create_ConstantMosaic_KeepsChannelValues()
    {
        // 同色画素だけを平均するので、各チャネルが一定なら値は変わらない
        const int size = 32;
        ushort[] mosaic = ColorPipelineTests.BuildConstantMosaic(
            size, size, BayerPattern.Rggb, r: 1000, g: 2000, b: 3000);
        RawFormat format = MakeFormat(size, size);
        using RawImage image = LoadImage(mosaic, format);

        using var pyramid = BayerPyramid.Create(image, format);

        Assert.True(pyramid.LevelCount >= 2);
        foreach (int factor in new[] { 2, 4 })
        {
            RawImage? level = pyramid.GetLevel(factor);
            Assert.NotNull(level);
            Assert.Equal(size / factor, level!.Width);
            Assert.Equal(size / factor, level.Height);
            Assert.Equal(BayerPattern.Rggb, level.Format.Bayer);

            for (int y = 0; y < level.Height; y++)
            {
                for (int x = 0; x < level.Width; x++)
                {
                    ushort expected = (y & 1) == 0
                        ? (ushort)((x & 1) == 0 ? 1000 : 2000)
                        : (ushort)((x & 1) == 0 ? 2000 : 3000);
                    Assert.Equal(expected, level.GetPixel(x, y));
                }
            }
        }
    }

    [Fact]
    public void Create_Factor2_AveragesSamePhasePixelsOnly()
    {
        // 位相ごとに異なる勾配を入れ、混ざらないことを確認する
        const int size = 8;
        var codes = new ushort[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                // 位相ベース + 位置による微小変化
                int phase = ((y & 1) * 2 + (x & 1)) * 10000;
                codes[y * size + x] = (ushort)(phase + (y / 2) * 100 + (x / 2) * 10);
            }
        }

        RawFormat format = MakeFormat(size, size);
        using RawImage image = LoadImage(codes, format);

        using var pyramid = BayerPyramid.Create(image, format);
        RawImage? level = pyramid.GetLevel(2);

        Assert.NotNull(level);
        Assert.Equal(4, level!.Width);
        Assert.Equal(4, level.Height);

        for (int destY = 0; destY < 4; destY++)
        {
            for (int destX = 0; destX < 4; destX++)
            {
                int px = destX & 1;
                int py = destY & 1;
                int bx = destX >> 1;
                int by = destY >> 1;
                int sum = 0;
                for (int j = 0; j < 2; j++)
                {
                    for (int i = 0; i < 2; i++)
                    {
                        int sx = 2 * (bx * 2 + i) + px;
                        int sy = 2 * (by * 2 + j) + py;
                        sum += codes[sy * size + sx];
                    }
                }

                Assert.Equal((ushort)(sum / 4), level.GetPixel(destX, destY));
            }
        }
    }

    [Fact]
    public void SelectFactor_PicksLargestNotExceedingInverseZoom()
    {
        const int size = 64;
        RawFormat format = MakeFormat(size, size);
        using RawImage image = LoadImage(new ushort[size * size], format);

        using var pyramid = BayerPyramid.Create(image, format);

        Assert.Equal(1, pyramid.SelectFactor(1.0));
        Assert.Equal(1, pyramid.SelectFactor(2.0));
        Assert.Equal(2, pyramid.SelectFactor(0.5));
        Assert.Equal(4, pyramid.SelectFactor(0.25));
        Assert.Equal(4, pyramid.SelectFactor(0.2));
    }

    [Fact]
    public void Create_OddSize_TruncatesToWholeBlocks()
    {
        const int width = 11;
        const int height = 9;
        RawFormat format = MakeFormat(width, height);
        using RawImage image = LoadImage(new ushort[width * height], format);

        using var pyramid = BayerPyramid.Create(image, format);
        RawImage? level = pyramid.GetLevel(2);

        Assert.NotNull(level);

        // 11/4*2 = 4, 9/4*2 = 4(2x2ブロック単位で切り捨て)
        Assert.Equal(4, level!.Width);
        Assert.Equal(4, level.Height);
        Assert.Equal(0, level.Width & 1);
        Assert.Equal(0, level.Height & 1);
    }

    [Fact]
    public void Create_CancelledToken_ThrowsOperationCanceled()
    {
        const int size = 64;
        RawFormat format = MakeFormat(size, size);
        using RawImage image = LoadImage(new ushort[size * size], format);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => BayerPyramid.Create(image, format, cancellationToken: cts.Token));
    }
}

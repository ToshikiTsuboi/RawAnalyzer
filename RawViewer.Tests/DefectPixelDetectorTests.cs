using RawViewer.Core;
using Xunit;

namespace RawViewer.Tests;

public class DefectPixelDetectorTests
{
    private static RawImage LoadImage(ushort[] codes, int width, int height, int bitDepth = 12)
    {
        var format = new RawFormat { Width = width, Height = height, BitDepth = bitDepth };
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

    /// <summary>ほぼフラット(±1のディザ)な画面に欠陥を埋め込む。</summary>
    private static ushort[] MakeFlatWithDefects(
        int width, int height, ushort baseCode, params (int X, int Y, ushort Code)[] defects)
    {
        var codes = new ushort[width * height];
        for (int i = 0; i < codes.Length; i++)
        {
            codes[i] = (ushort)(baseCode + (i % 2)); // σ>0にするためのディザ
        }

        foreach ((int x, int y, ushort code) in defects)
        {
            codes[y * width + x] = code;
        }

        return codes;
    }

    [Fact]
    public void Detect_HotAndDeadPixels_FindsBoth()
    {
        ushort[] codes = MakeFlatWithDefects(32, 32, 1000, (5, 7, 4000), (20, 15, 10));
        using RawImage image = LoadImage(codes, 32, 32);

        DefectDetectionResult result = DefectPixelDetector.Detect(image, sigmaFactor: 6.0);

        Assert.Equal(2, result.Defects.Count);
        Assert.Contains(new DefectPixel(5, 7, 4000, DefectType.Hot), result.Defects);
        Assert.Contains(new DefectPixel(20, 15, 10, DefectType.Dead), result.Defects);
        Assert.Equal(1, result.HotCount);
        Assert.Equal(1, result.DeadCount);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Detect_HotOnly_IgnoresDeadPixels()
    {
        ushort[] codes = MakeFlatWithDefects(16, 16, 1000, (3, 3, 4000), (8, 8, 10));
        using RawImage image = LoadImage(codes, 16, 16);

        DefectDetectionResult result = DefectPixelDetector.Detect(
            image, detectHot: true, detectDead: false);

        Assert.Single(result.Defects);
        Assert.Equal(DefectType.Hot, result.Defects[0].Type);
    }

    [Fact]
    public void Detect_CleanImage_FindsNothing()
    {
        ushort[] codes = MakeFlatWithDefects(16, 16, 1000);
        using RawImage image = LoadImage(codes, 16, 16);

        DefectDetectionResult result = DefectPixelDetector.Detect(image);

        Assert.Empty(result.Defects);
    }

    [Fact]
    public void Detect_MaxResults_TruncatesAndFlags()
    {
        // 多数の白点を埋め込む
        var defects = new (int, int, ushort)[20];
        for (int i = 0; i < 20; i++)
        {
            defects[i] = (i, i, 4000);
        }

        ushort[] codes = MakeFlatWithDefects(32, 32, 1000, defects);
        using RawImage image = LoadImage(codes, 32, 32);

        DefectDetectionResult result = DefectPixelDetector.Detect(image, maxResults: 5);

        Assert.True(result.Truncated);
        Assert.True(result.Defects.Count <= 5);
    }

    [Fact]
    public void Detect_ResultsSortedByRowThenColumn()
    {
        ushort[] codes = MakeFlatWithDefects(
            32, 32, 1000, (20, 5, 4000), (3, 5, 4000), (10, 2, 4000));
        using RawImage image = LoadImage(codes, 32, 32);

        DefectDetectionResult result = DefectPixelDetector.Detect(image);

        Assert.Equal(3, result.Defects.Count);
        Assert.Equal((10, 2), (result.Defects[0].X, result.Defects[0].Y));
        Assert.Equal((3, 5), (result.Defects[1].X, result.Defects[1].Y));
        Assert.Equal((20, 5), (result.Defects[2].X, result.Defects[2].Y));
    }

    [Fact]
    public void Detect_InvalidSigma_Throws()
    {
        ushort[] codes = MakeFlatWithDefects(4, 4, 100);
        using RawImage image = LoadImage(codes, 4, 4);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DefectPixelDetector.Detect(image, sigmaFactor: 0));
    }
}

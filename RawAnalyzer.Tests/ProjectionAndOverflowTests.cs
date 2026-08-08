using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class ProjectionAndOverflowTests
{
    private static RawImage LoadImage(ushort[] codes, int width, int height, int bitDepth = 16)
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

    [Fact]
    public void ComputeProjections_MatchesIndividualFunctions()
    {
        const int width = 12;
        const int height = 9;
        var codes = new ushort[width * height];
        for (int i = 0; i < codes.Length; i++)
        {
            codes[i] = (ushort)(i * 7 % 60000);
        }

        using RawImage image = LoadImage(codes, width, height);
        var roi = new RegionOfInterest(2, 1, 8, 6);

        (double[] horizontal, double[] vertical) =
            ImageAnalysis.ComputeProjections(image, 0, roi);

        Assert.Equal(ImageAnalysis.ComputeHorizontalProjection(image, 0, roi), horizontal);
        Assert.Equal(ImageAnalysis.ComputeVerticalProjection(image, 0, roi), vertical);
    }

    [Fact]
    public void ComputeProjections_Canceled_Throws()
    {
        using RawImage image = LoadImage(new ushort[64 * 64], 64, 64);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => ImageAnalysis.ComputeProjections(
            image, 0, new RegionOfInterest(0, 0, 64, 64), cts.Token));
    }

    [Fact]
    public void ComputeProjections_EmptyRoi_ReturnsEmpty()
    {
        using RawImage image = LoadImage(new ushort[16], 4, 4);

        (double[] horizontal, double[] vertical) =
            ImageAnalysis.ComputeProjections(image, 0, new RegionOfInterest(0, 0, 0, 0));

        Assert.Empty(horizontal);
        Assert.Empty(vertical);
    }

    [Fact]
    public void RequiredBytes_HugeFrameCount_ThrowsInsteadOfOverflowing()
    {
        // 掛け算が桁あふれすると負のサイズになり、ファイル長の検証を素通りする
        var format = new RawFormat
        {
            Width = 60000,
            Height = 60000,
            BitDepth = 16,
            FrameCount = 2_000_000_000,
        };

        Assert.Throws<ArgumentException>(() => format.RequiredBytes());
    }

    [Fact]
    public void Validate_PixelCountOverLimit_Throws()
    {
        var format = new RawFormat { Width = 2_000_000_000, Height = 2_000_000_000 };

        ArgumentException ex = Assert.Throws<ArgumentException>(() => format.Validate());
        Assert.Contains("画素数", ex.Message);
    }

    [Fact]
    public void RequiredBytes_NormalFormat_IsExact()
    {
        var format = new RawFormat
        {
            Width = 1920, Height = 1080, BitDepth = 12, HeaderOffset = 1024, FrameCount = 3,
        };

        Assert.Equal(1024 + 1920L * 1080 * 2 * 3, format.RequiredBytes());
    }

    [Fact]
    public void Load_OverflowingFormat_IsRejected()
    {
        // 実際の読み込み経路でも桁あふれで検証を回避できないこと
        string path = TestData.WriteTempFile(new byte[16]);
        try
        {
            var format = new RawFormat
            {
                Width = 60000, Height = 60000, BitDepth = 16, FrameCount = 2_000_000_000,
            };

            Assert.ThrowsAny<ArgumentException>(() => RawLoader.Load(path, format));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

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
    public void ComputeProjections_Canceled_Throws()
    {
        using RawImage image = LoadImage(new ushort[64 * 64], 64, 64);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => ImageAnalysis.ComputeProjections(
            image, 0, new RegionOfInterest(0, 0, 64, 64), cts.Token));
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
}

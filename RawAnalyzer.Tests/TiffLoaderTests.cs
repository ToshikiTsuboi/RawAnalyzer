using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class TiffLoaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Load_8Bit_NormalizesTo16Bit(bool bigEndian)
    {
        const int width = 5;
        const int height = 3;
        ushort[] pixels = TestData.MakePattern(width * height, 8);
        byte[] tiff = TestData.BuildTiff(pixels, width, height, 8, bigEndian);

        using RawImage image = TiffLoader.Load(tiff);
        Assert.Equal(width, image.Width);
        Assert.Equal(height, image.Height);
        Assert.Equal(8, image.Format.BitDepth);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                Assert.Equal((ushort)(pixels[y * width + x] << 8), image.GetPixel(x, y));
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Load_16Bit_ReadsValuesAsIs(bool bigEndian)
    {
        const int width = 4;
        const int height = 4;
        ushort[] pixels = TestData.MakePattern(width * height, 16);
        byte[] tiff = TestData.BuildTiff(pixels, width, height, 16, bigEndian);

        using RawImage image = TiffLoader.Load(tiff);
        Assert.Equal(16, image.Format.BitDepth);
        Assert.Equal(bigEndian ? Endianness.Big : Endianness.Little, image.Format.Endianness);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                Assert.Equal(pixels[y * width + x], image.GetPixel(x, y));
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Load_MultiStrip_ReassemblesAllRows(bool bigEndian)
    {
        const int width = 6;
        const int height = 5;
        ushort[] pixels = TestData.MakePattern(width * height, 16);
        byte[] tiff = TestData.BuildTiff(pixels, width, height, 16, bigEndian, rowsPerStrip: 2);

        using RawImage image = TiffLoader.Load(tiff);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                Assert.Equal(pixels[y * width + x], image.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public void Load_FromFile_Works()
    {
        const int width = 3;
        const int height = 3;
        ushort[] pixels = TestData.MakePattern(width * height, 16);
        string path = TestData.WriteTempFile(TestData.BuildTiff(pixels, width, height, 16, false));
        try
        {
            using RawImage image = TiffLoader.Load(path);
            Assert.Equal(pixels[4], image.GetPixel(1, 1));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_Compressed_Throws()
    {
        byte[] tiff = TestData.BuildTiff(new ushort[4], 2, 2, 16, false, compression: 5);
        Assert.Throws<InvalidDataException>(() => TiffLoader.Load(tiff));
    }

    [Fact]
    public void Load_BadByteOrderMark_Throws()
    {
        byte[] tiff = TestData.BuildTiff(new ushort[4], 2, 2, 16, false);
        tiff[0] = (byte)'X';
        tiff[1] = (byte)'X';
        Assert.Throws<InvalidDataException>(() => TiffLoader.Load(tiff));
    }

    [Fact]
    public void Load_BadMagicNumber_Throws()
    {
        byte[] tiff = TestData.BuildTiff(new ushort[4], 2, 2, 16, false);
        tiff[2] = 99;
        Assert.Throws<InvalidDataException>(() => TiffLoader.Load(tiff));
    }

    [Fact]
    public void Load_TruncatedHeader_Throws()
    {
        Assert.Throws<InvalidDataException>(() => TiffLoader.Load(new byte[] { (byte)'I', (byte)'I', 42 }));
    }
}

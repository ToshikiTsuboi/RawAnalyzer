using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// TiffLoader.Load / LoadCore(Core内の非圧縮TIFF復号)の単体テスト。
/// </summary>
/// <remarks>
/// 本番の読込経路(ImageFileLoader → TryProbePixelLayout / RawLoader → 自前復号 → WIC)は
/// TiffSpecTests / TiffStackTests / SaveTests が担う。ここは将来のCore内デコーダの土台として、
/// 8→16bit正規化・両エンディアンの16bit・複数ストリップ・ヘッダ検査の最小限だけを見る。
/// </remarks>
public class TiffLoaderTests
{
    [Fact]
    public void Load_8Bit_NormalizesTo16Bit()
    {
        const int width = 5;
        const int height = 3;
        ushort[] pixels = TestData.MakePattern(width * height, 8);
        byte[] tiff = TestData.BuildTiff(pixels, width, height, 8, bigEndian: false);

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

    [Fact]
    public void Load_MultiStrip_ReassemblesAllRows()
    {
        const int width = 6;
        const int height = 5;
        ushort[] pixels = TestData.MakePattern(width * height, 16);
        byte[] tiff = TestData.BuildTiff(pixels, width, height, 16, bigEndian: false, rowsPerStrip: 2);

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
    public void Load_Compressed_Throws()
    {
        byte[] tiff = TestData.BuildTiff(new ushort[4], 2, 2, 16, false, compression: 5);
        Assert.Throws<InvalidDataException>(() => TiffLoader.Load(tiff));
    }

    [Fact]
    public void Load_BadByteOrderMarkOrMagicNumber_Throws()
    {
        byte[] badByteOrder = TestData.BuildTiff(new ushort[4], 2, 2, 16, false);
        badByteOrder[0] = (byte)'X';
        badByteOrder[1] = (byte)'X';
        Assert.Throws<InvalidDataException>(() => TiffLoader.Load(badByteOrder));

        byte[] badMagic = TestData.BuildTiff(new ushort[4], 2, 2, 16, false);
        badMagic[2] = 99;
        Assert.Throws<InvalidDataException>(() => TiffLoader.Load(badMagic));
    }
}

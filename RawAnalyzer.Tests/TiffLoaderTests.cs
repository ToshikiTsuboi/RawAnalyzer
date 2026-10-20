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
        var page = TiffBuilder.GrayPage(width, height, 8, TiffBuilder.SampleBytes(pixels, 8));
        byte[] tiff = new TiffBuilder().Build(page);

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
        var page = TiffBuilder.GrayPage(width, height, 16, TiffBuilder.SampleBytes(pixels, 16, bigEndian));
        byte[] tiff = new TiffBuilder(bigEndian).Build(page);

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
        var page = TiffBuilder.GrayPage(width, height, 16, TiffBuilder.SampleBytes(pixels, 16), rowsPerStrip: 2);
        byte[] tiff = new TiffBuilder().Build(page);

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
        byte[] tiff = new TiffBuilder().Build(TiffBuilder.GrayPage(2, 2, 16, new byte[8], compression: 5));
        Assert.Throws<InvalidDataException>(() => TiffLoader.Load(tiff));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void Load_FillOrder2_Throws(int bits)
    {
        // 回帰テスト: Load は FillOrder タグを読まず、バイト内のビット順を反転しないまま FillOrder=1 と同じ値を
        // 黙って返していた。プローブ(「直接読み出せません」)・自前復号(「未対応です」)と同じく断る
        ushort[] pixels = { 1, 2, 3, 0x80, 0xF0, 0x0F };
        var page = TiffBuilder.GrayPage(3, 2, bits, TiffBuilder.SampleBytes(pixels, bits));
        page.Tags[266] = (3, new long[] { 2 });
        byte[] tiff = new TiffBuilder().Build(page);

        var ex = Assert.Throws<InvalidDataException>(() => TiffLoader.Load(tiff));
        Assert.Contains("FillOrder", ex.Message);
    }

    [Fact]
    public void Load_FillOrder1Explicit_ReadsValues()
    {
        // FillOrder=1(既定値)を明示したファイルは従来どおり読む
        ushort[] pixels = { 1, 2, 3, 0x80, 0xF0, 0x0F };
        var page = TiffBuilder.GrayPage(3, 2, 16, TiffBuilder.SampleBytes(pixels, 16));
        page.Tags[266] = (3, new long[] { 1 });
        byte[] tiff = new TiffBuilder().Build(page);

        using RawImage image = TiffLoader.Load(tiff);
        for (int i = 0; i < pixels.Length; i++)
        {
            Assert.Equal(pixels[i], image.GetPixel(i % 3, i / 3));
        }
    }

    [Fact]
    public void Load_BadByteOrderMarkOrMagicNumber_Throws()
    {
        byte[] badByteOrder = new TiffBuilder().Build(TiffBuilder.GrayPage(2, 2, 16, new byte[8]));
        badByteOrder[0] = (byte)'X';
        badByteOrder[1] = (byte)'X';
        Assert.Throws<InvalidDataException>(() => TiffLoader.Load(badByteOrder));

        byte[] badMagic = new TiffBuilder().Build(TiffBuilder.GrayPage(2, 2, 16, new byte[8]));
        badMagic[2] = 99;
        Assert.Throws<InvalidDataException>(() => TiffLoader.Load(badMagic));
    }
}

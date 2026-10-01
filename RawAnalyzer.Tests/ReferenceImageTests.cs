using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 画像演算の参照画像(B)・ノイズ測定の2枚目を、表示中の対象(A)と組み合わせて読む規約の検証。
/// </summary>
public class ReferenceImageTests
{
    private const int Width = 8;
    private const int Height = 4;

    private static RawFormat Raw12(long headerOffset = 0, Endianness endianness = Endianness.Little) => new()
    {
        Width = Width,
        Height = Height,
        BitDepth = 12,
        Packing = BitPacking.Lsb,
        Endianness = endianness,
        HeaderOffset = headerOffset,
        Bayer = BayerPattern.Rggb,
    };

    private static ushort[] Fill(ushort code) => Enumerable.Repeat(code, Width * Height).ToArray();

    [Theory]
    [InlineData(0, Endianness.Little)]
    [InlineData(512, Endianness.Big)]
    public void AfterFilter_DarkRawIsReadInFileFormat_AndSubtractsFullBlackLevel(
        long headerOffset, Endianness endianness)
    {
        // 12bit 下詰めの raw をフィルタした結果は 16bit・下詰め・リトルエンディアン・ヘッダ0 の形式になる。
        // 同じ形式で撮ったダークをその処理結果の形式で読むと、正規化(<<4)されず 1/16 の値のまま引かれ、
        // ビッグエンディアンならバイトが入れ替わる。以前はヘッダ0の下詰めではサイズも一致して警告も出なかった
        RawFormat opened = Raw12(headerOffset, endianness);
        using RawImage source = TestImages.FromCodes(Fill(1000), opened);
        using RawImage filtered = ImageFilters.Apply(
            source, new ImageFilterOptions(ImageFilterKind.Median), 0, BayerPattern.Rggb);
        Assert.Equal(16, filtered.Format.BitDepth);

        string darkPath = TestData.WriteTempFile(TestData.EncodeRawFile(Fill(64), opened));
        try
        {
            RawFormat readFormat = ReferenceImage.RawReadFormat(filtered.Format, opened);
            Assert.Equal(new FileInfo(darkPath).Length, ReferenceImage.ExpectedRawSize(readFormat));

            using RawImage dark = RawLoader.Load(darkPath, readFormat);
            using RawImage result = ImageCalculator.Apply(
                filtered, dark, ImageOperation.Subtract, 0, 0, BayerPattern.Rggb);

            // 黒レベル 64 code(内部値 1024)を引く。処理結果の形式は保つ(16bit)
            Assert.Equal((1000 - 64) << 4, result.GetPixel(3, 2));
            Assert.Equal(16, result.Format.BitDepth);
        }
        finally
        {
            File.Delete(darkPath);
        }
    }

    [Fact]
    public void AfterBinning_DarkRawOfFileSize_IsRejectedAsSizeMismatch()
    {
        // ビニングで寸法が縮んだ処理結果の形式で読むと、ファイルの先頭だけを縮んだ寸法として読み、
        // 別の画素同士を引いた結果を黙って出していた。ファイルの形式で読めば寸法の違いを明示的に断る
        RawFormat opened = Raw12();
        using RawImage source = TestImages.FromCodes(Fill(1000), opened);
        using RawImage binned = ImageBinning.Apply(source, 2, BinningMode.Average, 0, BayerPattern.Rggb);

        string darkPath = TestData.WriteTempFile(TestData.EncodeRawFile(Fill(64), opened));
        try
        {
            RawFormat readFormat = ReferenceImage.RawReadFormat(binned.Format, opened);
            Assert.Equal(new FileInfo(darkPath).Length, ReferenceImage.ExpectedRawSize(readFormat));

            using RawImage dark = RawLoader.Load(darkPath, readFormat);
            Assert.Throws<ArgumentException>(() => ImageCalculator.Apply(
                binned, dark, ImageOperation.Subtract, 0, 0, BayerPattern.Rggb));
        }
        finally
        {
            File.Delete(darkPath);
        }
    }

    [Fact]
    public void Unprocessed_ReadsFirstFrameWithCurrentBayer()
    {
        // 処理していない raw では開いたフォーマットと同じ(先頭の1フレームだけを読む)。右パネルで変えた
        // Bayer は表示中のフォーマットのものを使う
        RawFormat opened = Raw12() with { FrameCount = 4 };
        RawFormat current = opened with { Bayer = BayerPattern.Bggr };

        RawFormat readFormat = ReferenceImage.RawReadFormat(current, opened);

        Assert.Equal(opened with { FrameCount = 1, Bayer = BayerPattern.Bggr }, readFormat);
    }

    [Fact]
    public void ImageFileTarget_UsesCurrentFormat()
    {
        // TIFF 等の画像ファイルを表示中(raw のフォーマットがない)なら、従来どおり表示中の形式で読む
        RawFormat current = new() { Width = Width, Height = Height, BitDepth = 16 };

        Assert.Equal(current, ReferenceImage.RawReadFormat(current, openedRaw: null));
    }
}

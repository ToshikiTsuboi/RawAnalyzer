using System.Buffers.Binary;
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

    private static TiffBuilder.Page FloatPage(float minimum, float maximum)
    {
        // 左上が最小、右下が最大の傾斜(値域が min〜max になる)
        var bytes = new byte[Width * Height * 4];
        for (int i = 0; i < Width * Height; i++)
        {
            float value = minimum + ((maximum - minimum) * i / ((Width * Height) - 1));
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), value);
        }

        return TiffBuilder.GrayPage(Width, Height, 32, bytes, sampleFormat: 3);
    }

    private static TempTiff FloatTiff(float minimum, float maximum) =>
        TempTiff.Write(new TiffBuilder().Build(FloatPage(minimum, maximum)));

    private static SampleScaling? ScalingOf(string path, int pageIndex = 0)
    {
        DecodedImage decoded = ImageFileLoader.Load(path, pageIndex: pageIndex);
        decoded.Luminance.Dispose();
        return decoded.Scaling;
    }

    [Fact]
    public void FloatTiffReference_WithDifferentRange_IsRejected()
    {
        // 32bit実数の TIFF は1枚ごとの値域で16bitへ写す。対象 0〜4000 ADU と、ダーク 95〜110 ADU は
        // 別の係数で写され(100 ADU が対象では 1638 code、ダークでは 59577 code)、コードのまま引くと
        // ほぼ全画素が0に張り付く。以前は何の警告もなく結果を出していた
        using TempTiff target = FloatTiff(0, 4000);
        using TempTiff dark = FloatTiff(95, 110);
        SampleScaling? targetScaling = ScalingOf(target.Path);
        Assert.NotNull(targetScaling);

        var ex = Assert.Throws<InvalidOperationException>(() => ReferenceImage.Load(
            dark.Path, isRaw: false, Raw12(), targetScaling, CancellationToken.None));
        Assert.Contains("値の対応", ex.Message);
    }

    [Fact]
    public void FloatTiffReference_WithSameRange_IsLoaded()
    {
        // 同じ係数で写された2枚(値域の上限が同じ非負のデータ)は、コード同士の演算が元の値の演算になる
        using TempTiff target = FloatTiff(0, 4000);
        using TempTiff reference = FloatTiff(10, 4000);
        SampleScaling? targetScaling = ScalingOf(target.Path);

        using RawImage loaded = ReferenceImage.Load(
            reference.Path, isRaw: false, Raw12(), targetScaling, CancellationToken.None);

        Assert.Equal(Width, loaded.Width);
    }

    [Fact]
    public void OtherPageOfSameTiffStack_WithDifferentRange_IsRejected()
    {
        // TIFF スタックの2ページ目を表示して同じファイルを指定すると、参照は先頭ページを読む。
        // ページごとに値域が違えば係数も違う
        byte[] stack = new TiffBuilder().Build(FloatPage(0, 4000), FloatPage(0, 60000));
        using TempTiff file = TempTiff.Write(stack);
        SampleScaling? page1 = ScalingOf(file.Path, pageIndex: 1);

        Assert.Throws<InvalidOperationException>(() => ReferenceImage.Load(
            file.Path, isRaw: false, Raw12(), page1, CancellationToken.None));
    }

    [Fact]
    public void RawReference_ForScaledTarget_IsRejected()
    {
        // 対象が値域換算した TIFF で、参照が raw(等倍のコード)でも、コードは同じ値を表さない
        using TempTiff target = FloatTiff(0, 4000);
        SampleScaling? targetScaling = ScalingOf(target.Path);
        string rawPath = TestData.WriteTempFile(TestData.EncodeRawFile(Fill(64), Raw12()));
        try
        {
            Assert.Throws<InvalidOperationException>(() => ReferenceImage.Load(
                rawPath, isRaw: true, Raw12(), targetScaling, CancellationToken.None));
        }
        finally
        {
            File.Delete(rawPath);
        }
    }

    [Fact]
    public void RawReference_ForUnscaledTarget_IsLoaded()
    {
        // raw・16bit 整数の TIFF 同士(等倍)は従来どおり組み合わせられる
        string rawPath = TestData.WriteTempFile(TestData.EncodeRawFile(Fill(64), Raw12()));
        try
        {
            using RawImage loaded = ReferenceImage.Load(
                rawPath, isRaw: true, Raw12(), targetScaling: null, CancellationToken.None);
            Assert.Equal(64 << 4, loaded.GetPixel(0, 0));
        }
        finally
        {
            File.Delete(rawPath);
        }
    }

    [Fact]
    public void ScalingMismatch_ComparesCodeToValueMappingOnly()
    {
        var range = new SampleRange(0, 4000, 0);
        var sameMappingOtherRange = new SampleRange(5, 4000, 3);

        Assert.Null(ReferenceImage.ScalingMismatch(null, null));
        Assert.Null(ReferenceImage.ScalingMismatch(
            SampleScaling.FromRange(range), SampleScaling.FromRange(sameMappingOtherRange)));
        Assert.NotNull(ReferenceImage.ScalingMismatch(SampleScaling.FromRange(range), null));
        Assert.NotNull(ReferenceImage.ScalingMismatch(null, SampleScaling.FromRange(range)));
        Assert.NotNull(ReferenceImage.ScalingMismatch(
            SampleScaling.FromRange(range), SampleScaling.FromRange(new SampleRange(0, 60000, 0))));
    }

    [Fact]
    public void MultiFrameReference_ShapedLikeTargetFile_IsExpectedSize()
    {
        // 4フレームの raw を開いてフレーム2を表示し、2枚目に同じファイル(または同じ形の連写ファイル)を
        // 指定した。2枚目は先頭フレームだけを読むので正しく測れるのに、以前は1フレーム分と一致しないため
        // 毎回「サイズ不一致・測定値が正しくない可能性」の警告が出ていた(同一ファイルの断り文はこの手順を勧める)
        RawFormat opened = Raw12(headerOffset: 64) with { FrameCount = 4 };
        long oneFrame = ReferenceImage.ExpectedRawSize(ReferenceImage.RawReadFormat(opened, opened));
        long file = opened.RequiredBytes();

        Assert.True(ReferenceImage.IsExpectedRawSize(file, oneFrame, targetFileSize: file));
        Assert.True(ReferenceImage.IsExpectedRawSize(oneFrame, oneFrame, targetFileSize: file));
    }

    [Theory]
    [InlineData(2)]  // 2フレーム分(幅2倍・同じ高さの別フォーマットとも区別できない)
    [InlineData(3)]
    public void OtherSizes_AreStillWarned(int frames)
    {
        // 1フレームの倍数を一律に許すと、幅2倍などの別フォーマットのファイルを無警告で誤読する。
        // 許すのは1フレーム分と、対象のファイルと同じ大きさ(同じ形のファイル)だけ
        RawFormat opened = Raw12(headerOffset: 64) with { FrameCount = 4 };
        long oneFrame = ReferenceImage.ExpectedRawSize(ReferenceImage.RawReadFormat(opened, opened));
        long other = 64 + (opened.FrameSizeInBytes * frames);

        Assert.False(ReferenceImage.IsExpectedRawSize(other, oneFrame, targetFileSize: opened.RequiredBytes()));
        Assert.False(ReferenceImage.IsExpectedRawSize(oneFrame * 4, oneFrame, targetFileSize: 0));
    }

    [Fact]
    public void ImageFileTarget_UsesCurrentFormat()
    {
        // TIFF 等の画像ファイルを表示中(raw のフォーマットがない)なら、従来どおり表示中の形式で読む
        RawFormat current = new() { Width = Width, Height = Height, BitDepth = 16 };

        Assert.Equal(current, ReferenceImage.RawReadFormat(current, openedRaw: null));
    }
}

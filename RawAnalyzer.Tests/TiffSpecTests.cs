using System.Buffers.Binary;
using System.Text;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// TIFF仕様の対応状況(2026-09-07調査)に基づく回帰テスト。
/// </summary>
/// <remarks>
/// WICが黙って誤った値を返す形式(未知の圧縮=全画素0、CFAの勝手な現像、8bit符号ありの生ビット)を
/// 弾く/自前で読むこと、WICが開けない形式(BigTIFF、10/14/24/64bit、ImageJの連続スタック、
/// SubIFDのDNG本体)を自前で読めること、ページ数の数え方が経路によらず一致することを確かめる。
/// </remarks>
public class TiffSpecTests
{
    private const int W = 6;
    private const int H = 4;

    // ------------------------------------------------------------------ P1: 黙って壊れる形式

    [Theory]
    [InlineData(34925, "LZMA")]
    [InlineData(9999, "不明")]
    public void UnknownCompression_IsRejectedBeforeWic(int compression, string name)
    {
        // WICはこれらをエラーにせず全画素0の画像として返す。未知の圧縮は EnsureWicCapable の同じ throw で
        // 弾かれ、差は表示名の表だけなので、表にある値と無い値を1つずつ見る
        var page = TiffBuilder.GrayPage(W, H, 16, Ramp16(), compression: compression);
        using var file = TempTiff.Write(new TiffBuilder().Build(page));

        var ex = Assert.Throws<InvalidDataException>(() => ImageFileLoader.Load(file.Path));
        Assert.Contains(name, ex.Message);
        Assert.Contains(compression.ToString(), ex.Message);
    }

    [Fact]
    public void SignedInt8_IsScaledNotRawBits()
    {
        var samples = new byte[W * H];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (byte)(sbyte)(-128 + (i * 255 / (samples.Length - 1)));
        }

        var page = TiffBuilder.GrayPage(W, H, 8, samples, sampleFormat: 2);
        using var file = TempTiff.Write(new TiffBuilder().Build(page));

        DecodedImage decoded = ImageFileLoader.Load(file.Path);
        using RawImage image = decoded.Luminance;
        Assert.Equal(0, image.GetPixel(0, 0));                 // -128 → 0
        Assert.Equal(65535, image.GetPixel(W - 1, H - 1));     // 127 → 65535
        Assert.Contains("8bit値 -128〜127", decoded.ValueNote);
    }

    [Theory]
    [InlineData(new byte[] { 1, 0, 2, 1 }, BayerPattern.Grbg)]
    [InlineData(new byte[] { 1, 2, 0, 1 }, BayerPattern.Gbrg)]
    public void Cfa_IsReadAsBayerRaw(byte[] cfaPattern, BayerPattern expected)
    {
        // Photometric=CFA(32803) をWICは勝手に現像してRGBにする。生値のままBayerとして読む
        // (Rggb は Dng_MainImageInSubIfd、Bggr は Cfa8Bit_IsDecodedNativelyWithBayer が同じ表を通す)
        var page = TiffBuilder.GrayPage(W, H, 16, Ramp16(), photometric: TiffLoader.PhotometricCfa);
        page.Tags[33421] = (3, new long[] { 2, 2 });
        page.Tags[33422] = (1, cfaPattern);
        using var file = TempTiff.Write(new TiffBuilder().Build(page));

        Assert.True(TiffLoader.TryProbePixelLayout(file.Path, out TiffPixelLayout? layout, out _));
        Assert.Equal(expected, layout!.Bayer);

        DecodedImage decoded = ImageFileLoader.Load(file.Path);
        using RawImage image = decoded.Luminance;
        Assert.Null(decoded.Color);
        Assert.Equal(expected, image.Format.Bayer);
        Assert.Equal(16, image.Format.BitDepth);
        AssertRamp16(image);
    }

    [Fact]
    public void Cfa8Bit_IsDecodedNativelyWithBayer()
    {
        byte[] samples = Enumerable.Range(0, W * H).Select(i => (byte)(i * 10)).ToArray();
        var page = TiffBuilder.GrayPage(W, H, 8, samples, photometric: TiffLoader.PhotometricCfa);
        page.Tags[33422] = (1, new byte[] { 2, 1, 1, 0 });
        using var file = TempTiff.Write(new TiffBuilder().Build(page));

        DecodedImage decoded = ImageFileLoader.Load(file.Path);
        using RawImage image = decoded.Luminance;
        Assert.Equal(BayerPattern.Bggr, image.Format.Bayer);
        Assert.Equal(8, image.Format.BitDepth);
        Assert.Equal(30 << 8, image.GetPixel(3, 0));
    }

    [Fact]
    public void Dng_MainImageInSubIfd_IsUsedInsteadOfThumbnail()
    {
        // 典型的なDNG: IFD0は縮小サムネイル(NewSubfileType=1)、本体はSubIFD
        var main = TiffBuilder.GrayPage(W, H, 16, Ramp16(), photometric: TiffLoader.PhotometricCfa);
        main.Tags[33422] = (1, new byte[] { 0, 1, 1, 2 });
        var thumb = TiffBuilder.RgbPage(2, 2, new byte[12]);
        thumb.Tags[254] = (4, new long[] { 1 });
        thumb.SubIfds.Add(main);
        thumb.Tags[50706] = (1, new byte[] { 1, 4, 0, 0 });
        using var file = TempTiff.Write(new TiffBuilder().Build(thumb), ".dng");

        Assert.True(ImageFileLoader.IsSupported(file.Path));
        DecodedImage decoded = ImageFileLoader.Load(file.Path);
        using RawImage image = decoded.Luminance;
        Assert.Equal(1, decoded.PageCount);
        Assert.Null(decoded.Color);
        Assert.Equal(BayerPattern.Rggb, image.Format.Bayer);
        Assert.Equal(W, image.Width);
        AssertRamp16(image);
    }

    [Fact]
    public void CompressedCfa_IsRejectedWithReason()
    {
        var page = TiffBuilder.GrayPage(W, H, 16, Ramp16(), photometric: TiffLoader.PhotometricCfa, compression: 7);
        using var file = TempTiff.Write(new TiffBuilder().Build(page));

        var ex = Assert.Throws<InvalidDataException>(() => ImageFileLoader.Load(file.Path));
        Assert.Contains("CFA", ex.Message);
    }

    // ------------------------------------------------------------------ P2: 開けない・情報が落ちる

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void BigTiff_Uncompressed16Bit_UsesDirectPath(bool bigEndian, bool tiled)
    {
        var builder = new TiffBuilder(bigEndian, bigTiff: true);
        var page = tiled
            ? TiffBuilder.TiledGrayPage(W, H, 16, Ramp16(bigEndian), 4, 2)
            : TiffBuilder.GrayPage(W, H, 16, Ramp16(bigEndian));
        using var file = TempTiff.Write(builder.Build(page));

        Assert.True(TiffLoader.TryReadSampleInfo(file.Path, out TiffSampleInfo? info));
        Assert.True(info!.IsBigTiff);
        Assert.Equal(tiled ? false : true,
            TiffLoader.TryProbePixelLayout(file.Path, out _, out _));

        DecodedImage decoded = ImageFileLoader.Load(file.Path);
        using RawImage image = decoded.Luminance;
        Assert.Equal(16, image.Format.BitDepth);
        AssertRamp16(image);
    }

    [Fact]
    public void BigTiff_Compressed_IsRejectedWithClearMessage()
    {
        var page = TiffBuilder.GrayPage(W, H, 16, Ramp16(), compression: 8);
        using var file = TempTiff.Write(new TiffBuilder(bigTiff: true).Build(page));

        var ex = Assert.Throws<InvalidDataException>(() => ImageFileLoader.Load(file.Path));
        Assert.Contains("BigTIFF", ex.Message);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(12)]
    [InlineData(14)]
    public void PackedBits_KeepBitDepth(int bits)
    {
        int max = (1 << bits) - 1;
        var values = new int[W * H];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = i * max / (values.Length - 1);
        }

        var page = TiffBuilder.GrayPage(W, H, bits, TiffBuilder.PackRows(values, W, bits));
        using var file = TempTiff.Write(new TiffBuilder().Build(page));

        DecodedImage decoded = ImageFileLoader.Load(file.Path);
        using RawImage image = decoded.Luminance;
        Assert.Equal(bits, image.Format.BitDepth);
        Assert.Null(decoded.ValueNote);
        for (int i = 0; i < values.Length; i++)
        {
            Assert.Equal(values[i] << (16 - bits), image.GetPixel(i % W, i / W));
        }
    }

    [Fact]
    public void Packed12Bit_WhiteIsZero_IsInverted()
    {
        int[] values = Enumerable.Range(0, W * H).Select(i => i * 4095 / (W * H - 1)).ToArray();
        var page = TiffBuilder.GrayPage(W, H, 12, TiffBuilder.PackRows(values, W, 12), photometric: 0);
        using var file = TempTiff.Write(new TiffBuilder().Build(page));

        using RawImage image = ImageFileLoader.Load(file.Path).Luminance;
        Assert.Equal(12, image.Format.BitDepth);
        Assert.Equal(4095 << 4, image.GetPixel(0, 0));
        Assert.Equal(0, image.GetPixel(W - 1, H - 1));
    }

    [Fact]
    public void Float64_IsScaledLikeFloat32()
    {
        var bytes = new byte[W * H * 8];
        for (int i = 0; i < W * H; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(i * 8), i / (double)(W * H - 1));
        }

        var page = TiffBuilder.GrayPage(W, H, 64, bytes, sampleFormat: 3);
        using var file = TempTiff.Write(new TiffBuilder().Build(page));

        DecodedImage decoded = ImageFileLoader.Load(file.Path);
        using RawImage image = decoded.Luminance;
        Assert.Equal("64bit実数 0〜1 → 16bit", decoded.ValueNote);
        Assert.Equal(0, image.GetPixel(0, 0));
        Assert.Equal(65535, image.GetPixel(W - 1, H - 1));
        Assert.Equal(Math.Round(65535.0 * 7 / (W * H - 1)), image.GetPixel(1, 1));
    }

    [Fact]
    public void Uint24_IsScaledWithNote()
    {
        var bytes = new byte[W * H * 3];
        for (int i = 0; i < W * H; i++)
        {
            int v = i * 1000;
            bytes[i * 3] = (byte)v;
            bytes[(i * 3) + 1] = (byte)(v >> 8);
            bytes[(i * 3) + 2] = (byte)(v >> 16);
        }

        var page = TiffBuilder.GrayPage(W, H, 24, bytes);
        using var file = TempTiff.Write(new TiffBuilder().Build(page));

        DecodedImage decoded = ImageFileLoader.Load(file.Path);
        using RawImage image = decoded.Luminance;
        Assert.StartsWith("24bit値 0〜23000", decoded.ValueNote);
        Assert.Equal(65535, image.GetPixel(W - 1, H - 1));
        Assert.Equal(0, image.GetPixel(0, 0));
    }

    [Fact]
    public void ImageJTruncatedStack_ExposesVirtualPages()
    {
        // ImageJが4GB超スタックを書く形式: IFDは1つ、画素は先頭フレームから連続
        const int frames = 3;
        var data = new byte[frames * W * H * 2];
        for (int k = 0; k < frames; k++)
        {
            for (int i = 0; i < W * H; i++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(((k * W * H) + i) * 2), (ushort)((k * 1000) + i));
            }
        }

        var page = TiffBuilder.GrayPage(W, H, 16, data.AsSpan(0, W * H * 2).ToArray());
        page.Tags[270] = (2, Encoding.ASCII.GetBytes($"ImageJ=1.53t\nimages={frames}\nslices={frames}\nloop=false\n\0"));
        page.Trailer = data.AsSpan(W * H * 2).ToArray();
        using var file = TempTiff.Write(new TiffBuilder().Build(page));

        Assert.True(TiffLoader.TryReadSampleInfo(file.Path, out TiffSampleInfo? info));
        Assert.Equal(frames, info!.PageCount);
        for (int k = 0; k < frames; k++)
        {
            DecodedImage decoded = ImageFileLoader.Load(file.Path, pageIndex: k);
            using RawImage image = decoded.Luminance;
            Assert.Equal(frames, decoded.PageCount);
            Assert.Equal(k, decoded.PageIndex);
            Assert.Equal((k * 1000) + 7, image.GetPixel(1, 1));
        }
    }

    [Fact]
    public void ImageJTruncatedStack_ClampsToAvailableFrames()
    {
        var page = TiffBuilder.GrayPage(W, H, 16, Ramp16());
        page.Tags[270] = (2, Encoding.ASCII.GetBytes("ImageJ=1.53t\nimages=50\n\0"));
        page.Trailer = new byte[W * H * 2]; // 実際には2フレーム分しかない
        using var file = TempTiff.Write(new TiffBuilder().Build(page));

        // ファイル末尾までに収まるフレーム数に切り詰める(このビルダはIFDを画素の後ろに置くので
        // その分も1フレームに数え得る。宣言の50より小さく、2以上であればよい)
        Assert.True(TiffLoader.TryReadSampleInfo(file.Path, out TiffSampleInfo? info));
        Assert.InRange(info!.PageCount, 2, 49);
        Assert.False(TiffLoader.TryReadSampleInfo(file.Path, out _, pageIndex: info.PageCount));
    }

    [Fact]
    public void ReducedResolutionPages_AreNotCountedOnAnyRoute()
    {
        // WICは NewSubfileType bit0 のIFDをフレームに数えない。ページ表(TryReadSampleInfo)も
        // 直接経路の読込結果も同じ数え方にする(以前はページ数が経路で食い違い、ページ送りが例外になった)
        var full = TiffBuilder.GrayPage(W, H, 16, Ramp16());
        var reduced = TiffBuilder.GrayPage(3, 2, 16, new byte[12]);
        reduced.Tags[254] = (4, new long[] { 1 });
        using var file = TempTiff.Write(new TiffBuilder().Build(full, reduced));

        Assert.True(TiffLoader.TryReadSampleInfo(file.Path, out TiffSampleInfo? info));
        Assert.Equal(1, info!.PageCount);
        Assert.False(TiffLoader.TryReadSampleInfo(file.Path, out _, pageIndex: 1));

        DecodedImage decoded = ImageFileLoader.Load(file.Path);
        using RawImage image = decoded.Luminance;
        Assert.Equal(1, decoded.PageCount);
        AssertRamp16(image);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void GrayWithAlpha_IsGrayNotColor(int bits)
    {
        // WICはグレー+アルファをBgra32/Rgba64で返す。先頭チャネルをグレーとして取り込む
        int bytesPer = bits / 8;
        var samples = new byte[W * H * 2 * bytesPer];
        for (int i = 0; i < W * H; i++)
        {
            int v = i * 1000;
            if (bits == 8)
            {
                samples[i * 2] = (byte)i;
                samples[(i * 2) + 1] = 200;
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(samples.AsSpan(i * 4), (ushort)v);
                BinaryPrimitives.WriteUInt16LittleEndian(samples.AsSpan((i * 4) + 2), 50000);
            }
        }

        var page = TiffBuilder.GrayPage(W, H, bits, samples, samplesPerPixel: 2);
        page.Tags[338] = (3, new long[] { 2 }); // ExtraSamples = unassociated alpha
        using var file = TempTiff.Write(new TiffBuilder().Build(page));

        DecodedImage decoded = ImageFileLoader.Load(file.Path);
        using RawImage image = decoded.Luminance;
        Assert.Null(decoded.Color);
        Assert.Equal(bits, image.Format.BitDepth);
        Assert.Equal(bits == 8 ? 5 * 257 : 5000, image.GetPixel(5, 0));
    }

    [Fact]
    public void StripsOutOfOrder_BigTiff_AreReassembled()
    {
        var page = TiffBuilder.GrayPage(W, H, 16, Ramp16(), rowsPerStrip: 1);
        page.BlockOrder = new[] { 3, 1, 2, 0 };
        using var file = TempTiff.Write(new TiffBuilder(bigTiff: true).Build(page));

        Assert.False(TiffLoader.TryProbePixelLayout(file.Path, out _, out string reason));
        Assert.Contains("連続", reason);
        using RawImage image = ImageFileLoader.Load(file.Path).Luminance;
        AssertRamp16(image);
    }

    // ------------------------------------------------------------------ P3: 表示・明記

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void LowBitGray_IsGrayNotColor(int bits)
    {
        int max = (1 << bits) - 1;
        int[] values = Enumerable.Range(0, W * H).Select(i => i % (max + 1)).ToArray();
        var page = TiffBuilder.GrayPage(W, H, bits, TiffBuilder.PackRows(values, W, bits));
        if (bits == 1)
        {
            page.Tags.Remove(258); // 2値画像はBitsPerSampleを省略することが多い
        }

        using var file = TempTiff.Write(new TiffBuilder().Build(page));

        DecodedImage decoded = ImageFileLoader.Load(file.Path);
        using RawImage image = decoded.Luminance;
        Assert.Null(decoded.Color);
        Assert.Equal(8, image.Format.BitDepth);
        Assert.Equal(0, image.GetPixel(0, 0));
        Assert.Equal(65535, image.GetPixel(max % W, max / W));
    }

    [Fact]
    public void UncompressedYCbCr_IsRejectedWithReason()
    {
        var page = TiffBuilder.GrayPage(W, H, 8, new byte[W * H * 3], photometric: 6, samplesPerPixel: 3);
        using var file = TempTiff.Write(new TiffBuilder().Build(page));

        var ex = Assert.Throws<InvalidDataException>(() => ImageFileLoader.Load(file.Path));
        Assert.Contains("YCbCr", ex.Message);
    }

    [Fact]
    public void Predictor2With32Bit_IsRejectedWithReason()
    {
        var page = TiffBuilder.GrayPage(W, H, 32, new byte[W * H * 4], compression: 8);
        page.Tags[317] = (3, new long[] { 2 });
        using var file = TempTiff.Write(new TiffBuilder().Build(page));

        var ex = Assert.Throws<InvalidDataException>(() => ImageFileLoader.Load(file.Path));
        Assert.Contains("Predictor=2", ex.Message);
    }

    [Fact]
    public async Task ComparePane_LabelsFirstPageAndShowsValueNoteInFileName()
    {
        // 2ページの32bit実数TIFF。先頭ページは昇順、2ページ目は降順にして、
        // 表示名(ページ表記と値域換算メモ)と画素が先頭ページのものであることを1本で見る
        var ascending = new byte[W * H * 4];
        var descending = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++)
        {
            float v = i / (float)(W * H - 1);
            BinaryPrimitives.WriteSingleLittleEndian(ascending.AsSpan(i * 4), v);
            BinaryPrimitives.WriteSingleLittleEndian(descending.AsSpan(i * 4), 1f - v);
        }

        var first = TiffBuilder.GrayPage(W, H, 32, ascending, sampleFormat: 3);
        var second = TiffBuilder.GrayPage(W, H, 32, descending, sampleFormat: 3);
        using var file = TempTiff.Write(new TiffBuilder().Build(first, second));

        using RawAnalyzer.App.Compare.ComparePane pane =
            await RawAnalyzer.App.Compare.ComparePane.LoadAsync(file.Path, rawFormat: null);
        Assert.Equal(2, pane.PageCount);
        Assert.Equal("32bit実数 0〜1 → 16bit", pane.ValueNote);
        Assert.Contains("TIFFページ 1/2", pane.FileName);
        Assert.EndsWith(" · 32bit実数 0〜1 → 16bit", pane.FileName);
        Assert.Equal(0, pane.Image.GetPixel(0, 0));
        Assert.Equal(65535, pane.Image.GetPixel(W - 1, H - 1));
    }

    // ------------------------------------------------------------------ 補助

    private static byte[] Ramp16(bool bigEndian = false)
    {
        var bytes = new byte[W * H * 2];
        for (int i = 0; i < W * H; i++)
        {
            ushort v = (ushort)(i * 65535 / (W * H - 1));
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(i * 2), v);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), v);
            }
        }

        return bytes;
    }

    private static void AssertRamp16(RawImage image)
    {
        Assert.Equal(W, image.Width);
        Assert.Equal(H, image.Height);
        for (int i = 0; i < W * H; i++)
        {
            Assert.Equal((ushort)(i * 65535 / (W * H - 1)), image.GetPixel(i % W, i / W));
        }
    }
}

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
/// WICが黙って誤った値を返す形式(未知の圧縮=全画素0、CFAの勝手な現像)を
/// 弾く/自前で読むこと、WICが開けない形式(BigTIFF、10/14/24/64bit、ImageJの連続スタック、
/// SubIFDのDNG本体)を自前で読めること、ページ数の数え方が経路によらず一致することを確かめる。
/// 8bit符号ありの生ビットは WideSampleTiffTests.SignedWhiteIsZero_IsInvertedAtAnyWidth(8) と
/// ThreeSampleGray8Signed_OpensFirstChannelAsGray が確かめる。
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
    public void Cfa_IsReadAsBayerRaw()
    {
        // Photometric=CFA(32803) をWICは勝手に現像してRGBにする。生値のままBayerとして読む
        // (Rggb は Dng_MainImageInSubIfd、Bggr は Cfa8Bit_IsDecodedNativelyWithBayer、Grbg は
        // ImageFileBayerTests.CfaTiff_DesignationTakesPrecedenceOverCfaPattern の CfaPage が同じ表を通す)
        var page = TiffBuilder.GrayPage(W, H, 16, Ramp16(), photometric: TiffLoader.PhotometricCfa);
        page.Tags[33421] = (3, new long[] { 2, 2 });
        page.Tags[33422] = (1, new byte[] { 1, 2, 0, 1 }); // G B / R G
        using var file = TempTiff.Write(new TiffBuilder().Build(page));

        Assert.True(TiffLoader.TryProbePixelLayout(file.Path, out TiffPixelLayout? layout, out _));
        Assert.Equal(BayerPattern.Gbrg, layout!.Bayer);

        DecodedImage decoded = ImageFileLoader.Load(file.Path);
        using RawImage image = decoded.Luminance;
        Assert.Null(decoded.Color);
        Assert.Equal(BayerPattern.Gbrg, image.Format.Bayer);
        Assert.Equal(16, image.Format.BitDepth);
        AssertRamp16(image);
    }

    [Theory]
    [InlineData(0, 0, BayerPattern.Rggb)]
    [InlineData(1, 0, BayerPattern.Gbrg)]
    [InlineData(0, 1, BayerPattern.Grbg)]
    [InlineData(1, 1, BayerPattern.Bggr)]
    [InlineData(2, 4, BayerPattern.Rggb)]
    public void Cfa_PatternOriginIsActiveAreaTopLeft(int top, int left, BayerPattern expected)
    {
        // DNG の CFAPattern は ActiveArea の左上を起点とする(DNG SDK・dcraw/LibRaw・rawspeed の解釈)。画像全体の
        // (0,0) 起点で当てると、ActiveArea の上端・左端が奇数のとき R↔Gb・Gr↔B が入れ替わっていた
        // (全体レビュー 2026-10-01 B34)。このアプリは遮光域を含む全体を開くので、(0,0) 起点の配列に直して設定する
        foreach (int bits in new[] { 16, 12 })
        {
            int max = (1 << bits) - 1;
            int[] values = Enumerable.Range(0, W * H).Select(i => i * max / ((W * H) - 1)).ToArray();
            byte[] samples = bits == 16 ? Ramp16() : TiffBuilder.PackRows(values, W, bits);
            var page = TiffBuilder.GrayPage(W, H, bits, samples, photometric: TiffLoader.PhotometricCfa);
            page.Tags[33422] = (1, new byte[] { 0, 1, 1, 2 }); // ActiveArea 起点で RGGB
            page.Tags[50829] = (4, new long[] { top, left, H, W });
            using var file = TempTiff.Write(new TiffBuilder().Build(page), ".dng");

            Assert.True(TiffLoader.TryReadSampleInfo(file.Path, out TiffSampleInfo? info));
            Assert.Equal(expected, info!.Bayer);
            DecodedImage decoded = ImageFileLoader.Load(file.Path);
            using RawImage image = decoded.Luminance;
            Assert.Equal(expected, image.Format.Bayer);
        }
    }

    [Fact]
    public void Cfa_BrokenActiveArea_KeepsPatternAtImageOrigin()
    {
        // ActiveArea が読めない(この読み手が扱わない型)ときは従来どおり (0,0) 起点で当て、Bayer の自動設定自体は止めない
        var page = TiffBuilder.GrayPage(W, H, 16, Ramp16(), photometric: TiffLoader.PhotometricCfa);
        page.Tags[33422] = (1, new byte[] { 0, 1, 1, 2 });
        page.Tags[50829] = (5, new byte[] { 1, 0, 0, 0, 1, 0, 0, 0 }); // RATIONAL
        using var file = TempTiff.Write(new TiffBuilder().Build(page), ".dng");

        Assert.True(TiffLoader.TryProbePixelLayout(file.Path, out TiffPixelLayout? layout, out _));
        Assert.Equal(BayerPattern.Rggb, layout!.Bayer);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(12)]
    [InlineData(8)]
    public void SampleFormatUndefined_IsReadAsUnsignedLikeSampleFormat1(int bits)
    {
        // SampleFormat=4(未定義)はTIFF 6.0の勧めどおり、タグがないとき(符号なし整数)と同じに読む。16bitは
        // 値域換算の経路に入って 0〜最大値 → 0〜65535 に引き伸ばされ、SampleFormat=1 と違う値で開いていた
        // (全体レビュー 2026-10-01 B97)。8bit はもとから ×257 のまま
        int max = bits == 8 ? 255 : 4095;
        int[] values = Enumerable.Range(0, W * H).Select(i => i * max / ((W * H) - 1)).ToArray();
        byte[] samples = bits switch
        {
            16 => TiffBuilder.SampleBytes(values.Select(v => (ushort)v).ToArray(), 16),
            8 => TiffBuilder.SampleBytes(values.Select(v => (ushort)v).ToArray(), 8),
            _ => TiffBuilder.PackRows(values, W, bits),
        };
        using var unsigned = TempTiff.Write(new TiffBuilder().Build(TiffBuilder.GrayPage(W, H, bits, samples)));
        using var undefined = TempTiff.Write(new TiffBuilder().Build(TiffBuilder.GrayPage(W, H, bits, samples, sampleFormat: 4)));

        DecodedImage expected = ImageFileLoader.Load(unsigned.Path);
        using RawImage expectedImage = expected.Luminance;
        DecodedImage actual = ImageFileLoader.Load(undefined.Path);
        using RawImage actualImage = actual.Luminance;

        Assert.Null(actual.ValueNote);
        Assert.Equal(expectedImage.Format.BitDepth, actualImage.Format.BitDepth);
        for (int i = 0; i < W * H; i++)
        {
            Assert.Equal(expectedImage.GetPixel(i % W, i / W), actualImage.GetPixel(i % W, i / W));
        }

        // 非圧縮16bitは SampleFormat=1 と同じく直接読み出せる(1億画素超でもオンデマンド読み出し)
        Assert.Equal(
            TiffLoader.TryProbePixelLayout(unsigned.Path, out _, out _),
            TiffLoader.TryProbePixelLayout(undefined.Path, out _, out _));
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
    public void Float64_ExtremeFiniteRange_IsNotCollapsed()
    {
        // 値はすべて有限だが最大−最小が double を超える。以前は幅1へ潰れて [0,65535,65535]、
        // 換算表示も「-1E+308〜-1E+308」になっていた
        var bytes = new byte[3 * 8];
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(0), -1e308);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(8), 0);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(16), 1e308);
        var page = TiffBuilder.GrayPage(3, 1, 64, bytes, sampleFormat: 3);
        using var file = TempTiff.Write(new TiffBuilder().Build(page));

        DecodedImage decoded = ImageFileLoader.Load(file.Path);
        using RawImage image = decoded.Luminance;
        Assert.Equal(0, image.GetPixel(0, 0));
        Assert.Equal(32768, image.GetPixel(1, 0));
        Assert.Equal(65535, image.GetPixel(2, 0));
        Assert.Equal("64bit値 -1E+308〜1E+308 → 16bit (1code≈3.05E+303)", decoded.ValueNote);
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

    [Fact]
    public void BigTiff_GrayWithAlpha_OpensFirstChannel()
    {
        // WICはBigTIFFを開けない。非圧縮のグレー+アルファも自前で先頭チャネルを読む
        // (以前は非圧縮のグレーなのに「BigTIFFは非圧縮のグレースケール/CFAページのみ対応しています」だった)
        byte[] ramp = Ramp16();
        var samples = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++)
        {
            ramp.AsSpan(i * 2, 2).CopyTo(samples.AsSpan(i * 4));
            BinaryPrimitives.WriteUInt16LittleEndian(samples.AsSpan((i * 4) + 2), 50000);
        }

        var page = TiffBuilder.GrayPage(W, H, 16, samples, samplesPerPixel: 2);
        page.Tags[338] = (3, new long[] { 2 }); // ExtraSamples = unassociated alpha
        using var file = TempTiff.Write(new TiffBuilder(bigTiff: true).Build(page));

        DecodedImage decoded = ImageFileLoader.Load(file.Path);
        using RawImage image = decoded.Luminance;
        Assert.Null(decoded.Color);
        Assert.Equal(16, image.Format.BitDepth);
        AssertRamp16(image);
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
    public void UncompressedHint_IsGivenOnlyWhenUncompressedPageOpens()
    {
        // 以前は非圧縮の64bit実数RGBに「64bit 実数の非圧縮(1)TIFFは未対応です(非圧縮であれば読めます)」、
        // 5サンプルの半精度RGBに「非圧縮のRGBのみ対応しています」と、非圧縮のページに非圧縮を案内する
        // 矛盾した文を出していた。グレー(BlackIsZero/WhiteIsZero、1〜5サンプル)とRGB(3〜5サンプル)の
        // 全サンプル形式×ビット幅で、(1) 非圧縮のページのエラー文は非圧縮を案内しない、
        // (2) 圧縮ページのエラー文が非圧縮を案内するなら、同じ構成の非圧縮ページは実際に開ける、を確かめる
        var failures = new List<string>();
        foreach (int photometric in new[] { 1, 0, 2 })
        {
            foreach (int spp in photometric == 2 ? new[] { 3, 4, 5 } : new[] { 1, 2, 3, 4, 5 })
            {
                foreach (int bits in new[] { 8, 16, 24, 32, 64 })
                {
                    foreach (int sampleFormat in new[] { 1, 2, 3 })
                    {
                        string layout = $"Photometric={photometric} {bits}bit×{spp} SampleFormat={sampleFormat}";
                        string? uncompressed = LoadError(SamplePage(photometric, spp, bits, sampleFormat, compress: false));
                        string? compressed = LoadError(SamplePage(photometric, spp, bits, sampleFormat, compress: true));
                        CheckUncompressedHint(layout, uncompressed, compressed, failures);
                    }
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void UncompressedHint_ContainerAndPhotometricAxes_AreConsistent()
    {
        // BigTIFF・SubIFD・CFA/LinearRaw・YCbCr の拒否文は、以前は「BigTIFFは非圧縮のグレースケール/CFAページのみ」
        // 「CFA/LinearRaw(DNG)は非圧縮のみ」「このページ(SubIFD…)は非圧縮の1サンプル/画素のみ」「非圧縮のYCbCr」と、
        // 開けない非圧縮ページやDeflate圧縮のページにも固定の「非圧縮」を出していた。格納形式(クラシック/
        // BigTIFF/SubIFD)× Photometric(グレー/RGB/CFA/LinearRaw/YCbCr)で、上のテストと同じ2点を確かめる
        var layouts = new List<(int Photometric, int Spp, int Bits, int Format)> { (6, 3, 8, 1) };
        foreach ((int bits, int format) in new[] { (8, 1), (16, 1), (16, 2), (24, 2), (32, 3), (64, 3) })
        {
            layouts.Add((1, 1, bits, format));
            layouts.Add((1, 2, bits, format));
            layouts.Add((2, 3, bits, format));
            layouts.Add((2, 4, bits, format));
            layouts.Add((TiffLoader.PhotometricCfa, 1, bits, format));
            layouts.Add((TiffLoader.PhotometricLinearRaw, 1, bits, format));
            layouts.Add((TiffLoader.PhotometricLinearRaw, 3, bits, format));
        }

        string[] containers = { "クラシック", "BigTIFF", "SubIFD" };
        var failures = new List<string>();
        for (int container = 0; container < containers.Length; container++)
        {
            foreach ((int photometric, int spp, int bits, int format) in layouts)
            {
                if (container == 0 && photometric is 1 or 2)
                {
                    // クラシックのグレー/RGBは UncompressedHint_IsGivenOnlyWhenUncompressedPageOpens が同一の
                    // バイト列(全 Photometric 1/0/2 × サンプル数 × ビット幅 × SampleFormat)で見る。
                    // 向こうの行列を絞るときは、この除外を戻す
                    continue;
                }

                string layout = $"{containers[container]} Photometric={photometric} {bits}bit×{spp} SampleFormat={format}";
                string? uncompressed = LoadError(
                    ContainerTiff(container, SamplePage(photometric, spp, bits, format, compress: false)));
                string? compressed = LoadError(
                    ContainerTiff(container, SamplePage(photometric, spp, bits, format, compress: true)));
                CheckUncompressedHint(layout, uncompressed, compressed, failures);
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void BigTiffOrSubIfd_Float32Rgb_IsDecodedNatively(int container)
    {
        // WICはBigTIFFとSubIFDのページを読めない。自前で復号できる32bit実数RGBは自前で開き、
        // クラシックTIFF(WIC経由)と同じ結果にする。以前は「BigTIFFは非圧縮のグレースケール/CFAページのみ
        // 対応しています」「このページ(SubIFD…)は非圧縮の1サンプル/画素のみ対応しています」で開けなかった
        var rgb = new byte[2 * 3 * 4];
        float[] values = { 0f, 0.5f, 2f, 4f, -1f, 1f };
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(rgb.AsSpan(i * 4), values[i]);
        }

        TiffBuilder.Page Page() => TiffBuilder.GrayPage(2, 1, 32, rgb, photometric: 2, sampleFormat: 3, samplesPerPixel: 3);
        using var classicFile = TempTiff.Write(new TiffBuilder().Build(Page()));
        using var file = TempTiff.Write(ContainerTiff(container, Page()));
        DecodedImage expected = ImageFileLoader.Load(classicFile.Path);
        using RawImage expectedOwned = expected.Luminance;
        DecodedImage actual = ImageFileLoader.Load(file.Path);
        using RawImage actualOwned = actual.Luminance;

        Assert.NotNull(actual.Color);
        for (int x = 0; x < 2; x++)
        {
            expected.Color!.GetPixel(x, 0, out ushort er, out ushort eg, out ushort eb);
            actual.Color!.GetPixel(x, 0, out ushort ar, out ushort ag, out ushort ab);
            Assert.Equal((er, eg, eb), (ar, ag, ab));
        }

        Assert.Equal("32bit値 -1〜4 → 16bit (1code≈7.63E-05)", actual.ValueNote);
        Assert.Equal(expected.ValueNote, actual.ValueNote);

        // 圧縮ページは自前では読めないが、非圧縮なら読めることを案内する
        var compressed = Page();
        TiffBuilder.Deflate(compressed);
        using var compressedFile = TempTiff.Write(ContainerTiff(container, compressed));
        var ex = Assert.Throws<InvalidDataException>(() => ImageFileLoader.Load(compressedFile.Path));
        Assert.Contains("非圧縮であれば読めます", ex.Message);
    }

    [Fact]
    public void LinearRaw3Samples_Uncompressed_DoesNotOfferUncompressed()
    {
        // 3サンプルのLinearRaw(linear DNG)は未対応。以前は非圧縮のページに「非圧縮のみ対応しています」と出ていた
        var page = TiffBuilder.GrayPage(W, H, 16, new byte[W * H * 6], photometric: TiffLoader.PhotometricLinearRaw,
            samplesPerPixel: 3);
        using var file = TempTiff.Write(new TiffBuilder().Build(page));

        var ex = Assert.Throws<InvalidDataException>(() => ImageFileLoader.Load(file.Path));
        Assert.Contains("LinearRaw", ex.Message);
        Assert.DoesNotContain("非圧縮", ex.Message.Replace("非圧縮(1)", ""));
    }

    [Fact]
    public void YCbCr_LzwCompressed_NamesActualCompression()
    {
        // 以前はLZW圧縮のYCbCrにも「非圧縮のYCbCr TIFFは未対応です」と出ていた
        var page = TiffBuilder.GrayPage(W, H, 8, new byte[W * H * 3], photometric: 6, compression: 5, samplesPerPixel: 3);
        using var file = TempTiff.Write(new TiffBuilder().Build(page));

        var ex = Assert.Throws<InvalidDataException>(() => ImageFileLoader.Load(file.Path));
        Assert.Contains("YCbCr", ex.Message);
        Assert.Contains("LZW(5)", ex.Message);
        Assert.DoesNotContain("非圧縮", ex.Message);
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

    [Fact]
    public void PageTable_IsParsedOncePerFileVersion()
    {
        // ページを1枚読むたびにIFDチェーン全体を(TryReadSampleInfo と TryProbePixelLayout で2回)解析していたため、
        // 多ページTIFFの再生・一括書き出しが O(N²) になっていた(全体レビュー 2026-10-01 B33)。ページ表は
        // パス・長さ・更新日時が同じ間は使い回し、どれかが変われば作り直す。
        // 使い回していることは、長さと更新日時を保ったままチェーンを切ったファイルでも前のページ表で読めることで確かめる
        var page = TiffBuilder.GrayPage(W, H, 16, Ramp16());
        using var file = TempTiff.Write(new TiffBuilder().Build(page, page, page));
        Assert.True(TiffLoader.TryReadSampleInfo(file.Path, out TiffSampleInfo? before));
        Assert.Equal(3, before!.PageCount);

        byte[] bytes = File.ReadAllBytes(file.Path);
        long link = NextIfdLink(bytes, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((int)link), 0); // 1ページ目でチェーンを切る
        DateTime written = File.GetLastWriteTimeUtc(file.Path);
        File.WriteAllBytes(file.Path, bytes);
        File.SetLastWriteTimeUtc(file.Path, written);

        Assert.True(TiffLoader.TryReadSampleInfo(file.Path, out TiffSampleInfo? reused, pageIndex: 2));
        Assert.Equal(3, reused!.PageCount);
        Assert.True(TiffLoader.TryProbePixelLayout(file.Path, out TiffPixelLayout? layout, out _, pageIndex: 2));
        Assert.Equal(3, layout!.PageCount);

        // 更新日時が変われば作り直す(ページ数の変化を見逃さない)
        File.SetLastWriteTimeUtc(file.Path, written.AddSeconds(2));
        Assert.True(TiffLoader.TryReadSampleInfo(file.Path, out TiffSampleInfo? after));
        Assert.Equal(1, after!.PageCount);
    }

    // ------------------------------------------------------------------ 補助

    /// <summary>リトルエンディアンのクラシックTIFFで、IFD の次IFDオフセットの位置。</summary>
    private static long NextIfdLink(byte[] bytes, long ifd)
    {
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)ifd));
        return ifd + 2 + (count * 12);
    }

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

    /// <summary>
    /// 2×1画素、指定の Photometric・サンプル数・ビット幅・サンプル形式のページ。色のチャネル
    /// (RGB・YCbCr・3サンプルのLinearRawは3、それ以外は1)より後ろの追加サンプルは非関連アルファとし、
    /// 値は画素・チャネルごとに変える。
    /// 実数は16/32/64bitのみIEEE形式で書き、それ以外の幅は整数のビット列を入れる(未対応の組み合わせ)。
    /// </summary>
    private static TiffBuilder.Page SamplePage(int photometric, int spp, int bits, int sampleFormat, bool compress)
    {
        int bytesPer = bits / 8;
        var bytes = new byte[2 * spp * bytesPer];
        for (int i = 0; i < 2 * spp; i++)
        {
            Span<byte> sample = bytes.AsSpan(i * bytesPer, bytesPer);
            int value = (i * 3) + 1;
            switch (sampleFormat, bits)
            {
                case (3, 16):
                    BinaryPrimitives.WriteHalfLittleEndian(sample, (Half)value);
                    break;
                case (3, 32):
                    BinaryPrimitives.WriteSingleLittleEndian(sample, value);
                    break;
                case (3, 64):
                    BinaryPrimitives.WriteDoubleLittleEndian(sample, value);
                    break;
                default:
                    for (int k = 0; k < bytesPer; k++)
                    {
                        sample[k] = (byte)(value >> (8 * k));
                    }

                    break;
            }
        }

        var page = TiffBuilder.GrayPage(
            2, 1, bits, bytes, photometric: photometric, sampleFormat: sampleFormat, samplesPerPixel: spp);
        int colorChannels = photometric is 2 or 6 || (photometric == TiffLoader.PhotometricLinearRaw && spp >= 3) ? 3 : 1;
        int extra = spp - colorChannels;
        if (extra > 0)
        {
            page.Tags[338] = (3, Enumerable.Repeat(2L, extra).ToArray()); // ExtraSamples = unassociated alpha
        }

        if (compress)
        {
            TiffBuilder.Deflate(page);
        }

        return page;
    }

    /// <summary>単一ページのクラシックTIFFとして本番経路で読み、開ければnull、開けなければエラー文を返す。</summary>
    private static string? LoadError(TiffBuilder.Page page)
    {
        return LoadError(new TiffBuilder().Build(page));
    }

    /// <summary>TIFFのバイト列を本番経路で読み、開ければnull、開けなければエラー文を返す。</summary>
    private static string? LoadError(byte[] tiff)
    {
        using var file = TempTiff.Write(tiff);
        try
        {
            ImageFileLoader.Load(file.Path).Luminance.Dispose();
            return null;
        }
        catch (InvalidDataException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// ページを格納形式ごとのTIFFにする。0=クラシック、1=BigTIFF、2=SubIFD(IFD0は縮小画像で、
    /// 本体がSubIFDにある一般的なDNGの構成。WICはこのページに届かない)。
    /// </summary>
    private static byte[] ContainerTiff(int container, TiffBuilder.Page page)
    {
        if (container == 2)
        {
            var thumbnail = TiffBuilder.RgbPage(2, 2, new byte[12]);
            thumbnail.Tags[254] = (4, new long[] { 1 });
            thumbnail.SubIfds.Add(page);
            return new TiffBuilder().Build(thumbnail);
        }

        return new TiffBuilder(bigTiff: container == 1).Build(page);
    }

    /// <summary>
    /// 非圧縮ページ・圧縮ページのエラー文の組を調べ、矛盾を failures に加える。(1) 非圧縮のページの
    /// エラー文は非圧縮を案内しない(自分の圧縮方式として「非圧縮(1)」と書くのは案内ではない)、
    /// (2) 圧縮ページのエラー文が非圧縮を案内するなら、同じ構成の非圧縮ページは実際に開ける。
    /// </summary>
    private static void CheckUncompressedHint(string layout, string? uncompressed, string? compressed, List<string> failures)
    {
        if (uncompressed is not null && uncompressed.Replace("非圧縮(1)", "").Contains("非圧縮"))
        {
            failures.Add($"{layout} 非圧縮: {uncompressed}");
        }

        if (compressed is not null && compressed.Contains("非圧縮") && uncompressed is not null)
        {
            failures.Add($"{layout} 圧縮: {compressed} ／ 非圧縮も開けない: {uncompressed}");
        }
    }
}

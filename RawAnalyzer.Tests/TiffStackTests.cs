using System.Buffers.Binary;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class TiffStackTests
{
    [Theory]
    [InlineData(8, false, TiffCompressOption.None)]
    [InlineData(16, false, TiffCompressOption.None)]
    [InlineData(16, false, TiffCompressOption.Lzw)]
    [InlineData(8, true, TiffCompressOption.Lzw)]
    [InlineData(16, true, TiffCompressOption.Lzw)]
    public void Load_RandomAccessPreservesEachPage(int bits, bool color, TiffCompressOption compression)
    {
        // 非圧縮16bitグレーだけが直接経路(RawLoader)で、残りはWICを通る。圧縮方式は自前コードの
        // 分岐に関与しないので、圧縮側は LZW を代表にする。
        using var file = new Fixture(compression,
            Page(5, 3, bits, color, 11), Page(5, 3, bits, color, 73), Page(5, 3, bits, color, 201));
        foreach (int index in new[] { 2, 0, 1, 2 })
        {
            DecodedImage decoded = ImageFileLoader.Load(file.Path, pageIndex: index);
            using RawImage image = decoded.Luminance;
            Assert.Equal(3, decoded.PageCount);
            Assert.Equal(index, decoded.PageIndex);
            // 整数の8/16bitはどの経路でも値域換算メモを付けない。
            Assert.Null(decoded.ValueNote);
            Assert.Equal(1, image.FrameCount);
            Assert.Equal(bits, image.Format.BitDepth);
            Assert.Equal(5, image.Width);
            Assert.Equal(3, image.Height);
            ushort code = new ushort[] { 11, 73, 201 }[index];
            ushort expected = bits == 8 ? (ushort)(code * 257) : code;
            if (color)
            {
                Assert.NotNull(decoded.Color);
                decoded.Color.GetPixel(4, 2, out ushort r, out ushort g, out ushort b);
                Assert.Equal(expected, r);
                Assert.Equal(bits == 8 ? (code + 1) * 257 : code + 1, g);
                Assert.Equal(bits == 8 ? (code + 2) * 257 : code + 2, b);
            }
            else
            {
                Assert.Null(decoded.Color);
                Assert.Equal(expected, image.GetPixel(4, 2));
            }
        }

        // デコーダ／ストリームを戻り値に残さない。
        using var exclusive = new FileStream(file.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task Source_MixedPagesCarryIndependentDimensionsDepthAndColor()
    {
        using var file = new Fixture(TiffCompressOption.Lzw,
            Page(5, 3, 8, false, 12), Page(4, 7, 16, true, 1234), Page(8, 2, 16, false, 60000));
        var source = new TiffStackSource(file.Path, 3);
        foreach ((int page, int width, int height, int depth, bool rgb) in new[]
        {
            (1, 4, 7, 16, true), (0, 5, 3, 8, false), (2, 8, 2, 16, false),
        })
        {
            DecodedImage decoded = await source.LoadPageAsync(page, CancellationToken.None);
            using RawImage image = decoded.Luminance;
            Assert.Equal((width, height, depth), (image.Width, image.Height, image.Format.BitDepth));
            Assert.Equal(rgb, decoded.Color is not null);
        }

        Assert.True(source.IsSourcePath(file.Path.ToUpperInvariant()));
        Assert.False(source.IsSourcePath(file.Path + ".new.tif"));
        Assert.True(source.IsSourcePath(System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(file.Path)!, ".", System.IO.Path.GetFileName(file.Path))));
    }

    [Fact]
    public async Task Source_BayerOverrideSurvivesRgbPagesAndCanBeChanged()
    {
        // GetPageFormat は「RGBページなら None、それ以外は BayerOverride」の素通しなので、パターンは1種で足りる。
        const BayerPattern pattern = BayerPattern.Rggb;
        using var file = new Fixture(TiffCompressOption.Lzw,
            Page(4, 4, 8, false, 12), Page(4, 4, 16, true, 1234), Page(4, 4, 16, false, 60000));
        var source = new TiffStackSource(file.Path, 3) { BayerOverride = pattern };
        foreach (int index in new[] { 0, 1, 2, 1, 0 })
        {
            DecodedImage decoded = await source.LoadPageAsync(index, CancellationToken.None);
            using RawImage image = decoded.Luminance;
            RawFormat format = source.GetPageFormat(decoded);
            Assert.Equal(index == 1 ? BayerPattern.None : pattern, format.Bayer);
            Assert.Equal(pattern, source.BayerOverride);
            Assert.Equal(image.Format with { Bayer = format.Bayer }, format);
        }

        DecodedImage gray = await source.LoadPageAsync(2, CancellationToken.None);
        using RawImage last = gray.Luminance;
        source.BayerOverride = BayerPattern.Gbrg;
        Assert.Equal(BayerPattern.Gbrg, source.GetPageFormat(gray).Bayer);
        source.BayerOverride = BayerPattern.None;
        Assert.Equal(BayerPattern.None, source.GetPageFormat(gray).Bayer);
        // 別ファイルを開いたときは前スタックの設定を引き継がない。
        Assert.Equal(BayerPattern.None, new TiffStackSource(file.Path, 3).GetPageFormat(gray).Bayer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Probe_FollowsIfdOffsetsWithGapsInEitherByteOrder(bool bigEndian)
    {
        using var file = new Fixture(BuildGappedStack(bigEndian));
        foreach (int page in new[] { 2, 0, 1 })
        {
            Assert.True(TiffLoader.TryProbePixelLayout(file.Path, out TiffPixelLayout? layout,
                out string reason, page), reason);
            Assert.Equal(3, layout!.PageCount);
            Assert.Equal(page, layout.PageIndex);
            RawFormat format = TiffLoader.ToRawFormat(layout);
            Assert.Equal(1, format.FrameCount);
            using RawImage image = RawLoader.Load(file.Path, format);
            Assert.Equal((page + 1) * 1000, image.GetPixel(1, 1));
        }
    }

    [Fact]
    public void Probe_RejectsCyclicAndOutOfBoundsDirectoryChains()
    {
        byte[] bytes = BuildGappedStack(false);
        int firstIfd = IfdOffset(bytes, 0);
        int nextLink = NextIfdLinkOffset(bytes, firstIfd);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(nextLink), (uint)firstIfd); // 先頭IFDが自分自身を指す
        using var cycle = new Fixture(bytes);
        Assert.False(TiffLoader.TryProbePixelLayout(cycle.Path, out _, out string cycleReason));
        Assert.Contains("循環", cycleReason); // 10万ページの上限ではなく、循環として断る

        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(nextLink), uint.MaxValue);
        using var invalid = new Fixture(bytes);
        Assert.False(TiffLoader.TryProbePixelLayout(invalid.Path, out _, out _));
    }

    [Fact]
    public void Load_RejectsOutOfRangePageAndHonorsCancellation()
    {
        using var file = new Fixture(TiffCompressOption.Lzw, Page(4, 4, 16, false, 10), Page(4, 4, 16, false, 20));
        Assert.Equal("pageIndex", Assert.Throws<ArgumentOutOfRangeException>(
            () => ImageFileLoader.Load(file.Path, pageIndex: -1)).ParamName);
        Assert.Equal("pageIndex", Assert.Throws<ArgumentOutOfRangeException>(
            () => ImageFileLoader.Load(file.Path, pageIndex: 2)).ParamName);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => ImageFileLoader.Load(file.Path, canceled.Token, pageIndex: 1));
        Assert.Throws<OperationCanceledException>(() => TiffLoader.TryProbePixelLayout(
            file.Path, out _, out _, 1, canceled.Token));
    }

    [Fact]
    public void Load_CorruptImageHasLocalizedDecodeErrorAndReleasesFile()
    {
        using var file = new Fixture(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 });
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => ImageFileLoader.Load(file.Path));
        Assert.StartsWith("画像をデコードできません:", error.Message);
        Assert.NotNull(error.InnerException);
        using var exclusive = new FileStream(file.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void Load_CanCancelBetweenPixelBands()
    {
        // グレーもRGBも ReadBands の同じ ThrowIfCancellationRequested で止まるので、軽いグレーだけを使う。
        using var file = new Fixture(TiffCompressOption.Lzw,
            Page(8, 8, 8, false, 10), Page(1024, 1025, 16, false, 30000));
        using var cts = new CancellationTokenSource();
        bool reported = false;
        var progress = new CallbackProgress(value =>
        {
            if (value > 0)
            {
                reported = true;
                cts.Cancel();
            }
        });
        Assert.Throws<OperationCanceledException>(() => ImageFileLoader.Load(file.Path, cts.Token, progress, 1));
        Assert.True(reported);
        using var exclusive = new FileStream(file.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData(TiffCompressOption.None, false)]
    [InlineData(TiffCompressOption.Lzw, false)]
    [InlineData(TiffCompressOption.Lzw, true)]
    public void Load_CancellationAtCompletionDoesNotReturnOwnedResources(TiffCompressOption compression, bool rgb)
    {
        using var file = new Fixture(compression, Page(4, 4, 16, rgb, 10), Page(4, 4, 16, rgb, 20));
        using var cts = new CancellationTokenSource();
        var progress = new CallbackProgress(value => { if (value == 1) cts.Cancel(); });
        Assert.Throws<OperationCanceledException>(() => ImageFileLoader.Load(file.Path, cts.Token, progress, 1));
        using var exclusive = new FileStream(file.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task Source_RejectsChangedPageCountAndCancellation()
    {
        using var file = new Fixture(TiffCompressOption.Lzw, Page(4, 4, 16, false, 10), Page(4, 4, 16, false, 20));
        var source = new TiffStackSource(file.Path, 2);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => source.LoadPageAsync(2, CancellationToken.None));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.LoadPageAsync(1, new CancellationToken(true)));
        new TiffBitmapEncoder { Frames = { BitmapFrame.Create(Page(4, 4, 16, false, 30)) } }.SaveTo(file.Path);
        await Assert.ThrowsAsync<InvalidDataException>(() => source.LoadPageAsync(0, CancellationToken.None));
    }

    [Fact]
    public async Task Source_SerializesDecodingAndCancelsObsoleteQueuedRequests()
    {
        using var file = new Fixture(TiffCompressOption.Lzw,
            Page(4, 4, 16, false, 10), Page(4, 4, 16, false, 20), Page(4, 4, 16, false, 30));
        var source = new TiffStackSource(file.Path, 3);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var firstCts = new CancellationTokenSource();
        using var queuedCts = new CancellationTokenSource();
        var progress = new CallbackProgress(value =>
        {
            if (value > 0)
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
        });
        Task<DecodedImage> first = source.LoadPageAsync(0, firstCts.Token, progress);
        Task<DecodedImage>? latest = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Task<DecodedImage> queued = source.LoadPageAsync(1, queuedCts.Token);
            latest = source.LoadPageAsync(2, CancellationToken.None);
            Assert.False(latest.IsCompleted);
            queuedCts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            firstCts.Cancel();
        }
        finally
        {
            release.Set();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        DecodedImage decoded = await latest!;
        using RawImage image = decoded.Luminance;
        Assert.Equal(2, decoded.PageIndex);
        Assert.Equal(30, image.GetPixel(0, 0));
    }

    [Fact]
    public void Batch_EnumeratesAllPagesAndDisposesOnAdvanceAndEarlyExit()
    {
        using var file = new Fixture(TiffCompressOption.Lzw,
            Page(4, 4, 16, false, 10), Page(6, 4, 16, false, 20), Page(4, 8, 8, true, 30));
        var format = new RawFormat { Width = 1, Height = 1 };
        RawImage? previous = null;
        int count = 0;
        foreach (FileFrame entry in FileFrameReader.Read(file.Path, format))
        {
            if (previous is not null)
            {
                Assert.Throws<ObjectDisposedException>(() => previous.GetPixel(0, 0));
            }

            Assert.Equal(count++, entry.Index);
            Assert.Equal(3, entry.Count);
            Assert.Equal(0, entry.Frame);
            Assert.True(entry.IsTiffPage);
            previous = entry.Image;
        }

        Assert.Equal(3, count);
        Assert.Throws<ObjectDisposedException>(() => previous!.GetPixel(0, 0));
        using (IEnumerator<FileFrame> iterator = FileFrameReader.Read(file.Path, format).GetEnumerator())
        {
            Assert.True(iterator.MoveNext());
            previous = iterator.Current.Image;
        }

        Assert.Throws<ObjectDisposedException>(() => previous.GetPixel(0, 0));
    }

    [Fact]
    public void Batch_RawFramesRemainAddressableUntilEnumerationEnds()
    {
        using var file = new Fixture(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, ".raw");
        var format = new RawFormat { Width = 2, Height = 2, BitDepth = 8, FrameCount = 2 };
        int count = 0;
        foreach (FileFrame entry in FileFrameReader.Read(file.Path, format))
        {
            Assert.Equal(count, entry.Frame);
            Assert.Equal(count++, entry.Index);
            Assert.Equal(2, entry.Count);
            Assert.False(entry.IsTiffPage);
            Assert.Equal((entry.Frame * 4 + 1) << 8, entry.Image.GetPixel(0, 0, entry.Frame));
        }

        Assert.Equal(2, count);
    }

    [Fact]
    public void Load_DoesNotDecodeUnselectedPagePixels()
    {
        byte[] bytes = BuildGappedStack(false);
        // 選択していないページのストリップだけをファイル外へ向ける。
        // 先頭ページは8bitにしてWIC経由にもオンデマンド性があることを検証する。
        int firstIfd = IfdOffset(bytes, 0);
        int secondIfd = IfdOffset(bytes, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(EntryValueOffset(bytes, firstIfd, 258)), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(EntryValueOffset(bytes, firstIfd, 279)), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(EntryValueOffset(bytes, secondIfd, 273)), uint.MaxValue);
        using var file = new Fixture(bytes);
        DecodedImage first = ImageFileLoader.Load(file.Path);
        using RawImage image = first.Luminance;
        Assert.Equal(3, first.PageCount);
        Assert.Equal(8, image.Format.BitDepth);
        Assert.Equal(232 * 257, image.GetPixel(0, 0));
        Assert.ThrowsAny<Exception>(() => ImageFileLoader.Load(file.Path, pageIndex: 1));
    }

    private sealed class CallbackProgress(Action<double> callback) : IProgress<double>
    {
        public void Report(double value) => callback(value);
    }

    private static BitmapSource Page(int width, int height, int bits, bool color, ushort code)
    {
        int channels = color ? 3 : 1;
        int samples = width * height * channels;
        Array data = bits == 16 ? new ushort[samples] : new byte[samples];
        for (int i = 0; i < samples; i++)
        {
            int value = code + i % channels;
            if (data is ushort[] words) words[i] = (ushort)value;
            else ((byte[])data)[i] = (byte)value;
        }

        PixelFormat format = color
            ? bits == 16 ? PixelFormats.Rgb48 : PixelFormats.Rgb24
            : bits == 16 ? PixelFormats.Gray16 : PixelFormats.Gray8;
        BitmapSource source = BitmapSource.Create(width, height, 96, 96, format, null, data,
            width * channels * (bits / 8));
        source.Freeze();
        return source;
    }

    /// <summary>
    /// 2×2・16bit・単一ストリップの3ページ(ページ n の画素値は (n+1)*1000)。各ページの前に未使用バイトを
    /// 挟み、IFD が詰めて並ばない(next-IFD を辿らないと次ページに着けない)ファイルにする。
    /// </summary>
    private static byte[] BuildGappedStack(bool bigEndian)
    {
        var pages = new TiffBuilder.Page[3];
        for (int page = 0; page < pages.Length; page++)
        {
            ushort[] pixels = Enumerable.Repeat((ushort)((page + 1) * 1000), 4).ToArray();
            pages[page] = TiffBuilder.GrayPage(2, 2, 16, TiffBuilder.SampleBytes(pixels, 16, bigEndian));
            pages[page].GapBefore = 128;
        }

        return new TiffBuilder(bigEndian).Build(pages);
    }

    /// <summary>リトルエンディアンのクラシックTIFFで、pageIndex 番目の IFD の先頭位置(ヘッダから next-IFD を辿る)。</summary>
    private static int IfdOffset(byte[] tiff, int pageIndex)
    {
        int ifd = BinaryPrimitives.ReadInt32LittleEndian(tiff.AsSpan(4));
        for (int i = 0; i < pageIndex; i++)
        {
            ifd = BinaryPrimitives.ReadInt32LittleEndian(tiff.AsSpan(NextIfdLinkOffset(tiff, ifd)));
        }

        return ifd;
    }

    /// <summary>IFD 末尾にある next-IFD ポインタの位置。</summary>
    private static int NextIfdLinkOffset(byte[] tiff, int ifd)
        => ifd + 2 + (BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(ifd)) * 12);

    /// <summary>IFD 内で tag を持つエントリの値フィールド(インライン値)の位置。</summary>
    private static int EntryValueOffset(byte[] tiff, int ifd, ushort tag)
    {
        int count = BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(ifd));
        for (int i = 0; i < count; i++)
        {
            int entry = ifd + 2 + (i * 12);
            if (BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(entry)) == tag)
            {
                return entry + 8;
            }
        }

        throw new ArgumentException($"タグ {tag} が IFD にありません。", nameof(tag));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Path { get; }

        internal Fixture(byte[] data, string extension = ".tif")
        {
            string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RawAnalyzerTests");
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, Guid.NewGuid().ToString("N") + extension);
            File.WriteAllBytes(Path, data);
        }

        internal Fixture(TiffCompressOption compression, params BitmapSource[] pages) : this(Array.Empty<byte>())
        {
            var encoder = new TiffBitmapEncoder { Compression = compression };
            foreach (BitmapSource page in pages) encoder.Frames.Add(BitmapFrame.Create(page));
            encoder.SaveTo(Path);
        }

        public void Dispose() => File.Delete(Path);
    }
}

/// <summary>
/// ページ差し替え時のビューポートの状態遷移。WPF要素を作るため、他のUIテストと同じ
/// 共有STAスレッド("WPF UI" コレクション)で直列に実行する。
/// </summary>
[Collection("WPF UI")]
public class TiffStackViewportTests
{
    [Fact]
    public Task Viewport_PageReplacementResetsRoiWhenDimensionsChange() => WpfTestHost.Run(async () =>
    {
        using RawImage first = RawImage.FromPixels(new RawFormat { Width = 8, Height = 8 }, new ushort[64]);
        using RawImage second = RawImage.FromPixels(new RawFormat { Width = 2, Height = 3 }, new ushort[6]);
        var viewport = new RawAnalyzer.App.Controls.ImageViewport();
        viewport.SetImage(first, first.Format);
        viewport.SetRoi(new RegionOfInterest(4, 4, 3, 3));
        Assert.NotNull(viewport.Roi);
        Assert.Same(first, await viewport.ReplaceImageAsync(second, second.Format));
        Assert.Same(second, viewport.Image);
        Assert.Null(viewport.Roi);
        Assert.Equal(0, viewport.Frame);
        await viewport.ClearImageAsync();
    });
}

internal static class TiffTestEncoderExtensions
{
    internal static void SaveTo(this TiffBitmapEncoder encoder, string path)
    {
        using var output = File.Create(path);
        encoder.Save(output);
    }
}

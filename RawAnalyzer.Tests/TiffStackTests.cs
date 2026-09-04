using System.Buffers.Binary;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RawAnalyzer.App.Compare;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class TiffStackTests
{
    [Theory]
    [InlineData(8, false, TiffCompressOption.None)]
    [InlineData(16, false, TiffCompressOption.None)]
    [InlineData(8, true, TiffCompressOption.None)]
    [InlineData(16, true, TiffCompressOption.None)]
    [InlineData(8, false, TiffCompressOption.Lzw)]
    [InlineData(16, false, TiffCompressOption.Lzw)]
    [InlineData(8, true, TiffCompressOption.Lzw)]
    [InlineData(16, true, TiffCompressOption.Lzw)]
    [InlineData(8, false, TiffCompressOption.Zip)]
    [InlineData(16, false, TiffCompressOption.Zip)]
    [InlineData(8, true, TiffCompressOption.Zip)]
    [InlineData(16, true, TiffCompressOption.Zip)]
    public void Load_RandomAccessPreservesEachPage(int bits, bool color, TiffCompressOption compression)
    {
        using var file = new Fixture(compression,
            Page(5, 3, bits, color, 11), Page(5, 3, bits, color, 73), Page(5, 3, bits, color, 201));
        foreach (int index in new[] { 2, 0, 1, 2 })
        {
            DecodedImage decoded = ImageFileLoader.Load(file.Path, pageIndex: index);
            using RawImage image = decoded.Luminance;
            Assert.Equal(3, decoded.PageCount);
            Assert.Equal(index, decoded.PageIndex);
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

        Assert.Equal("_p0001", source.PageSuffix(0));
        Assert.Equal("_p0003", source.PageSuffix(2));
        Assert.True(source.IsSourcePath(file.Path.ToUpperInvariant()));
        Assert.False(source.IsSourcePath(file.Path + ".new.tif"));
        Assert.True(source.IsSourcePath(System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(file.Path)!, ".", System.IO.Path.GetFileName(file.Path))));
    }

    [Theory]
    [InlineData(BayerPattern.Rggb)]
    [InlineData(BayerPattern.Bggr)]
    [InlineData(BayerPattern.Grbg)]
    [InlineData(BayerPattern.Gbrg)]
    [InlineData(BayerPattern.None)]
    public async Task Source_BayerOverrideSurvivesRgbPagesAndCanBeChanged(BayerPattern pattern)
    {
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

    [Fact]
    public async Task Source_FileSequenceDisablesPageNavigationButKeepsSaveProtection()
    {
        using var file = new Fixture(TiffCompressOption.Lzw,
            Page(4, 4, 16, false, 10), Page(4, 4, 16, false, 20));
        var source = new TiffStackSource(file.Path, 2, pageNavigationEnabled: false);
        Assert.False(source.PageNavigationEnabled);
        Assert.True(source.IsSourcePath(file.Path));
        Assert.Equal("_p0001", source.PageSuffix(0));
        DecodedImage decoded = await source.LoadPageAsync(0, CancellationToken.None);
        using RawImage image = decoded.Luminance;
        Assert.Equal(0, decoded.PageIndex);
        Assert.Equal(10, image.GetPixel(0, 0));
        // 明示的に開き直した場合にだけ、TIFF内のページ送りを有効にする。
        Assert.True(new TiffStackSource(file.Path, 2).PageNavigationEnabled);
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
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8 + 2 + 9 * 12), 8);
        using var cycle = new Fixture(bytes);
        Assert.False(TiffLoader.TryProbePixelLayout(cycle.Path, out _, out _));

        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8 + 2 + 9 * 12), uint.MaxValue);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Load_CanCancelBetweenPixelBands(bool rgb)
    {
        using var file = new Fixture(TiffCompressOption.Lzw,
            Page(8, 8, 8, rgb, 10), Page(1024, 1025, 16, rgb, 30000));
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
    public void ProcessingAndSave_UseSelectedPageOnlyAndLeaveStackIntact()
    {
        using var file = new Fixture(TiffCompressOption.Lzw, Page(8, 8, 16, false, 10), Page(8, 8, 16, false, 4321));
        byte[] original = File.ReadAllBytes(file.Path);
        DecodedImage decoded = ImageFileLoader.Load(file.Path, pageIndex: 1);
        using RawImage image = decoded.Luminance;
        using RawImage binned = ImageBinning.Apply(image, 2);
        using RawImage filtered = ImageFilters.Apply(binned, new ImageFilterOptions(ImageFilterKind.Gaussian));
        using var output = new Fixture(Array.Empty<byte>());
        TiffWriter.SaveGray16(filtered, 0, output.Path);
        DecodedImage saved = ImageFileLoader.Load(output.Path);
        using RawImage roundtrip = saved.Luminance;
        Assert.Equal(1, saved.PageCount);
        Assert.Equal(4, roundtrip.Width);
        Assert.Equal(4, roundtrip.Height);
        Assert.Equal(4321, roundtrip.GetPixel(2, 2));
        Assert.Equal(original, File.ReadAllBytes(file.Path));
    }

    [Fact]
    public async Task Compare_LabelsFirstPageExplicitly()
    {
        using var file = new Fixture(TiffCompressOption.Lzw, Page(4, 4, 16, false, 10), Page(4, 4, 16, false, 20));
        using ComparePane pane = await ComparePane.LoadAsync(file.Path, null);
        Assert.Equal(2, pane.PageCount);
        Assert.Contains("TIFFページ 1/2", pane.FileName);
        Assert.Equal(10, pane.Image.GetPixel(0, 0));
    }

    [Fact]
    public void Load_DoesNotDecodeUnselectedPagePixels()
    {
        byte[] bytes = BuildGappedStack(false);
        // 選択していないページのストリップだけをファイル外へ向ける。
        // 先頭ページは8bitにしてWIC経由にもオンデマンド性があることを検証する。
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8 + 2 + 2 * 12 + 8), 8);
        WriteU32(bytes, 8 + 2 + 8 * 12 + 8, 4, false);
        WriteU32(bytes, 256 + 8 + 2 + 5 * 12 + 8, uint.MaxValue, false);
        using var file = new Fixture(bytes);
        DecodedImage first = ImageFileLoader.Load(file.Path);
        using RawImage image = first.Luminance;
        Assert.Equal(3, first.PageCount);
        Assert.Equal(8, image.Format.BitDepth);
        Assert.Equal(232 * 257, image.GetPixel(0, 0));
        Assert.ThrowsAny<Exception>(() => ImageFileLoader.Load(file.Path, pageIndex: 1));
    }

    [Fact]
    public void Viewport_PageReplacementResetsRoiWhenDimensionsChange()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using RawImage first = RawImage.FromPixels(new RawFormat { Width = 8, Height = 8 }, new ushort[64]);
                using RawImage second = RawImage.FromPixels(new RawFormat { Width = 2, Height = 3 }, new ushort[6]);
                var viewport = new RawAnalyzer.App.Controls.ImageViewport();
                viewport.SetImage(first, first.Format);
                viewport.SetRoi(new RegionOfInterest(4, 4, 3, 3));
                Assert.Same(first, viewport.ReplaceImageAsync(second, second.Format).GetAwaiter().GetResult());
                Assert.Same(second, viewport.Image);
                Assert.Null(viewport.Roi);
                Assert.Equal(0, viewport.Frame);
                viewport.ClearImageAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
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

    private static byte[] BuildGappedStack(bool bigEndian)
    {
        const int spacing = 256;
        var result = new byte[spacing * 3];
        for (int page = 0; page < 3; page++)
        {
            byte[] single = TestData.BuildTiff(Enumerable.Repeat((ushort)((page + 1) * 1000), 4).ToArray(),
                2, 2, 16, bigEndian);
            int start = spacing * page;
            single.CopyTo(result, start);
            // 単一ストリップの絶対オフセットとnext-IFDだけを再配置する。
            WriteU32(result, start + 8 + 2 + 5 * 12 + 8, (uint)(start + 122), bigEndian);
            WriteU32(result, start + 8 + 2 + 9 * 12, page == 2 ? 0 : (uint)(start + spacing + 8), bigEndian);
        }

        return result;
    }

    private static void WriteU32(byte[] data, int offset, uint value, bool bigEndian)
    {
        if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset), value);
        else BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
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

internal static class TiffTestEncoderExtensions
{
    internal static void SaveTo(this TiffBitmapEncoder encoder, string path)
    {
        using var output = File.Create(path);
        encoder.Save(output);
    }
}

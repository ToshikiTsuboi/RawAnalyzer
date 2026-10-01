using System.Buffers.Binary;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// long.MaxValue 付近のオフセット(BigTIFF のオフセット、LONG8/IFD8 型のタグ値)で範囲検査の
/// 加算が桁あふれしないことを確かめる回帰テスト(全体レビュー 2026-10-01 B1)。
/// </summary>
/// <remarks>
/// 範囲検査が <c>offset + length &gt; Length</c> の形だと、offset が long.MaxValue の手前にあるとき
/// 加算が負へ回り込んで検査を素通りし、ファイルの写像の外(正準でないアドレス)を読みに行く。
/// .NET 8 では AccessViolationException を捕捉できずプロセスごと落ちるので、修正前のコードでは
/// ここのテスト自体がテストプロセスを落とす。修正後は理由付きの InvalidDataException(Try 系は
/// 理由付きの false)になることを確かめる。
/// </remarks>
public class TiffOffsetOverflowTests
{
    private const int W = 16;
    private const int H = 2;

    /// <summary>読み出し長が 16 バイトを超えれば加算が回り込む位置。</summary>
    private const long NearMax = long.MaxValue - 15;

    [Theory]
    [InlineData(0x7FFF_FFFF_FFFF_FFF8L)]
    [InlineData(long.MaxValue)]
    public void BigTiff_FirstIfdOffsetNearInt64Max_IsRejectedWithReason(long firstIfd)
    {
        byte[] bytes = new TiffBuilder(bigTiff: true).Build(TiffBuilder.GrayPage(W, H, 16, Ramp16()));
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8, 8), firstIfd);
        using var file = TempTiff.Write(bytes);

        Assert.False(TiffLoader.TryReadSampleInfo(file.Path, out TiffSampleInfo? info, out string reason));
        Assert.Null(info);
        Assert.NotEmpty(reason);
        Assert.False(TiffLoader.TryProbePixelLayout(file.Path, out _, out string probeReason));
        Assert.NotEmpty(probeReason);
        Assert.Throws<InvalidDataException>(
            () => TiffLoader.TryDecodeUncompressed(file.Path, 0, out _, out _, out _));
        Assert.Throws<InvalidDataException>(() => TiffLoader.Load(bytes));
        Assert.Throws<InvalidDataException>(() => ImageFileLoader.Load(file.Path));
    }

    [Theory]
    [InlineData(18, 0x7FFF_FFFF_FFFF_FFF8L)]
    [InlineData(18, long.MaxValue)]
    [InlineData(16, long.MaxValue)]
    public void ClassicSubIfdsNearInt64Max_AreSkipped(int type, long subIfd)
    {
        // クラシックTIFFでも IFD8/LONG8 型の SubIFDs は8バイトの値として読まれる。壊れた SubIFD は
        // 無視して主チェーンのページだけで開く(範囲外の SubIFD の従来の扱いと同じ)
        var page = TiffBuilder.GrayPage(W, H, 16, Ramp16());
        page.Tags[330] = ((ushort)type, new[] { subIfd });
        using var file = TempTiff.Write(new TiffBuilder().Build(page));

        Assert.True(TiffLoader.TryReadSampleInfo(file.Path, out TiffSampleInfo? info, out string reason), reason);
        Assert.Equal(1, info!.PageCount);
        DecodedImage decoded = ImageFileLoader.Load(file.Path);
        using RawImage image = decoded.Luminance;
        AssertRamp16(image);
    }

    [Fact]
    public void BigTiff_TagValueOffsetNearInt64Max_IsRejectedWithReason()
    {
        // ImageWidth を LONG8×2(16バイト、インラインに入らない)にして、値の位置を long.MaxValue の手前へ向ける
        var page = TiffBuilder.GrayPage(W, H, 16, Ramp16());
        page.Tags[256] = (16, new long[] { W, W });
        byte[] bytes = new TiffBuilder(bigTiff: true).Build(page);
        PatchBigTiffEntryValue(bytes, tag: 256, 0x7FFF_FFFF_FFFF_FFF9L);
        using var file = TempTiff.Write(bytes);

        Assert.False(TiffLoader.TryReadSampleInfo(file.Path, out _, out string reason));
        Assert.Contains("256", reason);
        Assert.Throws<InvalidDataException>(() => TiffLoader.Load(bytes));
    }

    [Fact]
    public void PackedStripOffsetNearInt64Max_ThrowsInvalidData()
    {
        // 12bit詰めは自前復号(ForEachRow → Slice)で読む。1行24バイトなので位置 + 長さが回り込む
        int[] values = Enumerable.Range(0, W * H).Select(i => i * 4095 / (W * H - 1)).ToArray();
        var page = TiffBuilder.GrayPage(W, H, 12, TiffBuilder.PackRows(values, W, 12));
        PointStripsAt(page, type: 16, NearMax, count: W * H * 12 / 8);
        using var file = TempTiff.Write(new TiffBuilder().Build(page));

        var ex = Assert.Throws<InvalidDataException>(() => ImageFileLoader.Load(file.Path));
        Assert.Contains("ストリップ", ex.Message);
    }

    [Fact]
    public void BigTiff_TileOffsetNearInt64Max_ThrowsInvalidData()
    {
        // BigTIFFのタイルは自前復号(ForEachRow のタイル経路)で読む。タイル1行32バイト
        var page = TiffBuilder.TiledGrayPage(W, H, 16, Ramp16(), W, H);
        page.Blocks.Clear();
        page.Tags[324] = (16, new[] { NearMax });
        page.Tags[325] = (4, new long[] { W * H * 2 });
        using var file = TempTiff.Write(new TiffBuilder(bigTiff: true).Build(page));

        var ex = Assert.Throws<InvalidDataException>(() => ImageFileLoader.Load(file.Path));
        Assert.Contains("タイル", ex.Message);
    }

    [Fact]
    public void InMemoryLoad_StripOffsetNearInt64Max_ThrowsInvalidData()
    {
        var page = TiffBuilder.GrayPage(W, H, 16, Ramp16());
        PointStripsAt(page, type: 16, NearMax, count: W * H * 2);
        byte[] bytes = new TiffBuilder().Build(page);

        var ex = Assert.Throws<InvalidDataException>(() => TiffLoader.Load(bytes));
        Assert.Contains("ストリップ0", ex.Message);
    }

    [Fact]
    public void Probe_StripOffsetNearInt64Max_IsNotDirectlyReadable()
    {
        // 16bit連続配置の判定(TryProbeCore)でも 先頭 + 合計 が回り込み、範囲外の位置を
        // HeaderOffset とする配置を返していた
        var page = TiffBuilder.GrayPage(W, H, 16, Ramp16());
        PointStripsAt(page, type: 16, NearMax, count: W * H * 2);
        using var file = TempTiff.Write(new TiffBuilder().Build(page));

        Assert.False(TiffLoader.TryProbePixelLayout(file.Path, out TiffPixelLayout? layout, out string reason));
        Assert.Null(layout);
        Assert.Contains("範囲外", reason);
    }

    // ------------------------------------------------------------------ 補助

    /// <summary>ストリップを1本にし、その位置を指定の型・値にする(画素ブロックは書かない)。</summary>
    private static void PointStripsAt(TiffBuilder.Page page, ushort type, long offset, long count)
    {
        page.Blocks.Clear();
        page.Tags[278] = (4, new long[] { H });
        page.Tags[273] = (type, new[] { offset });
        page.Tags[279] = (4, new[] { count });
    }

    /// <summary>リトルエンディアンBigTIFFの先頭IFDで、指定タグの値フィールド(8バイト)を書き換える。</summary>
    private static void PatchBigTiffEntryValue(byte[] bytes, ushort tag, long value)
    {
        long ifd = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(8, 8));
        long count = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan((int)ifd, 8));
        for (int i = 0; i < count; i++)
        {
            int entry = (int)ifd + 8 + (i * 20);
            if (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(entry, 2)) == tag)
            {
                BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(entry + 12, 8), value);
                return;
            }
        }

        throw new InvalidOperationException($"タグ{tag}がありません。");
    }

    private static byte[] Ramp16()
    {
        var bytes = new byte[W * H * 2];
        for (int i = 0; i < W * H; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), (ushort)(i * 65535 / (W * H - 1)));
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

using System.Buffers.Binary;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// TIFF のページ表を使い回すことのテスト。ページ表はプロセス全体で共有する最近の数件(LRU)に覚えるので、
/// 並列に走る他の TIFF のテストがその間に項目を追い出すと、使い回しの確認が成り立たない。
/// そのため並列実行から外したコレクションで走らせる。
/// </summary>
[CollectionDefinition("TIFF page table cache", DisableParallelization = true)]
public sealed class TiffPageTableCacheCollection;

[Collection("TIFF page table cache")]
public class TiffPageTableCacheTests
{
    private const int W = 6;
    private const int H = 4;

    [Fact]
    public void PageTable_IsParsedOncePerFileVersion()
    {
        // ページを1枚読むたびにIFDチェーン全体を(TryReadSampleInfo と TryProbePixelLayout で2回)解析していたため、
        // 多ページTIFFの再生・一括書き出しが O(N²) になっていた(全体レビュー 2026-10-01 B33)。ページ表は
        // パス・長さ・更新日時が同じ間は使い回し、どれかが変われば作り直す。
        // 使い回していることは、長さと更新日時を保ったままチェーンを切ったファイルでも前のページ表で読めることで確かめる
        var page = TiffBuilder.GrayPage(W, H, 16, new byte[W * H * 2]);
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

    /// <summary>リトルエンディアンのクラシックTIFFで、IFD の次IFDオフセットの位置。</summary>
    private static long NextIfdLink(byte[] bytes, long ifd)
    {
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)ifd));
        return ifd + 2 + (count * 12);
    }
}

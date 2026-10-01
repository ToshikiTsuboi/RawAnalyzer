using System.Runtime.CompilerServices;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// ネットワーク上の1億画素超のファイルをローカルの一時ファイルへ写してから開く経路(RawLoader)の検証。
/// </summary>
/// <remarks>
/// ネットワーク上のパスは用意できないので、<see cref="RawLoader.IsNetworkPath"/> がネットワーク扱いする
/// 拡張パス(\\?\C:\...)でローカルのファイルを指して複製の経路を通し、閾値0でMMF経路にする。
/// 一時ファイルは元ファイルの拡張子を引き継ぐので、テストごとに一意な拡張子にして見分ける。
/// </remarks>
public class RawLoaderTemporaryCopyTests
{
    private static readonly RawFormat Format = new() { Width = 16, Height = 8, BitDepth = 12 };

    [Fact]
    public void TemporaryCopy_ReadsSameValues_AndIsDeletedOnDispose()
    {
        ushort[] codes = TestData.MakePattern(Format.Width * Format.Height, Format.BitDepth);
        (string source, string extension) = WriteSource(codes);
        try
        {
            string copy;
            using (RawImage image = RawLoader.Load(ExtendedPath(source), Format, inMemoryPixelThreshold: 0))
            {
                copy = FindCopy(extension);
                Assert.True(image.IsMemoryMapped);
                Assert.Equal((ushort)(codes[5] << 4), image.GetPixel(5, 0));
            }

            Assert.False(File.Exists(copy));
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public void TemporaryCopy_IsDeletedWhenHandlesCloseWithoutDispose()
    {
        // アプリの終了で RawImage.Dispose に届かない(Closed ハンドラの await の後の破棄が実行されない)、
        // または読み出し中で解放が遅延されたままプロセスが終わる場合と同じく、Dispose を経ずにファイルの
        // ハンドルが閉じられる。以前は一時ファイル(実際には数GB)が %TEMP%\RawAnalyzer に残り続けた。
        // 一時ファイルは閉じたら OS が消すように開くので、ハンドルが閉じれば残らない
        ushort[] codes = TestData.MakePattern(Format.Width * Format.Height, Format.BitDepth);
        (string source, string extension) = WriteSource(codes);
        try
        {
            string copy = LoadAndAbandon(source, extension);
            for (int i = 0; i < 3 && File.Exists(copy); i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.False(File.Exists(copy));
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public void TemporaryCopy_IsCreatedInTemporaryCopyFolder()
    {
        ushort[] codes = TestData.MakePattern(Format.Width * Format.Height, Format.BitDepth);
        (string source, string extension) = WriteSource(codes);
        try
        {
            using RawImage image = RawLoader.Load(ExtendedPath(source), Format, inMemoryPixelThreshold: 0);

            Assert.Single(Directory.GetFiles(RawLoader.TemporaryCopyFolder, "*" + extension));
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public void DeleteUnusedTemporaryCopies_DeletesLeftovers()
    {
        // 電源断などで OS が後始末できなかった・以前の版が残した一時コピーを、次の起動で消す
        string folder = MakeFolder();
        File.WriteAllBytes(Path.Combine(folder, "a.raw"), new byte[16]);
        File.WriteAllBytes(Path.Combine(folder, "b.bin"), new byte[16]);

        Assert.Equal(2, RawLoader.DeleteUnusedTemporaryCopies(folder));
        Assert.Empty(Directory.GetFiles(folder));
        Directory.Delete(folder);
    }

    [Fact]
    public void DeleteUnusedTemporaryCopies_KeepsCopiesInUse()
    {
        // 実行中の別のインスタンスが開いている一時コピー(以前の版のマップ・この版の削除予約付きのハンドル)は消さない
        string folder = MakeFolder();
        string mapped = Path.Combine(folder, "mapped.raw");
        string copying = Path.Combine(folder, "copying.raw");
        string leftover = Path.Combine(folder, "leftover.raw");
        File.WriteAllBytes(mapped, new byte[16]);
        File.WriteAllBytes(leftover, new byte[16]);
        try
        {
            using (new FileStream(mapped, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (new FileStream(
                copying, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.DeleteOnClose))
            {
                Assert.Equal(1, RawLoader.DeleteUnusedTemporaryCopies(folder));
                Assert.True(File.Exists(mapped));
                Assert.True(File.Exists(copying));
                Assert.False(File.Exists(leftover));
            }
        }
        finally
        {
            File.Delete(mapped);
            Directory.Delete(folder);
        }
    }

    [Fact]
    public void DeleteUnusedTemporaryCopies_MissingFolder_DoesNothing()
    {
        string folder = Path.Combine(Path.GetTempPath(), "RawAnalyzerTests", Guid.NewGuid().ToString("N"));

        Assert.Equal(0, RawLoader.DeleteUnusedTemporaryCopies(folder));
    }

    private static string MakeFolder()
    {
        // 利用者の実際の一時コピーのフォルダには触れない
        string folder = Path.Combine(Path.GetTempPath(), "RawAnalyzerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string LoadAndAbandon(string source, string extension)
    {
        RawImage image = RawLoader.Load(ExtendedPath(source), Format, inMemoryPixelThreshold: 0);
        Assert.True(image.IsMemoryMapped);
        return FindCopy(extension);
    }

    private static (string Source, string Extension) WriteSource(ushort[] codes)
    {
        string extension = "." + Guid.NewGuid().ToString("N");
        string path = Path.Combine(Path.GetTempPath(), "RawAnalyzerTests", "copy" + extension);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, TestData.EncodeRawFile(codes, Format));
        return (path, extension);
    }

    private static string ExtendedPath(string path)
    {
        string extended = @"\\?\" + Path.GetFullPath(path);
        Assert.True(RawLoader.IsNetworkPath(extended)); // 複製の経路を通ることの前提
        return extended;
    }

    private static string FindCopy(string extension)
    {
        return Assert.Single(Directory.GetFiles(
            Path.Combine(Path.GetTempPath(), "RawAnalyzer"), "*" + extension));
    }
}

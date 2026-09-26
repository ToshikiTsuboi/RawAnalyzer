using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class AtomicFileWriterTests
{
    private static string TempPath()
    {
        return Path.Combine(
            Path.GetTempPath(), "RawAnalyzerTests", Guid.NewGuid().ToString("N") + ".bin");
    }

    private static string PrepareExisting(byte[] content)
    {
        string path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public void Write_Success_ReplacesTarget()
    {
        string path = PrepareExisting(new byte[] { 1, 2, 3 });
        try
        {
            AtomicFileWriter.Write(path, s => s.Write(new byte[] { 9, 9 }));

            Assert.Equal(new byte[] { 9, 9 }, File.ReadAllBytes(path));
        }
        finally
        {
            AtomicFileWriter.TryDelete(path);
        }
    }

    [Fact]
    public void Write_Throws_KeepsExistingFileIntact()
    {
        // 上書き保存が途中で失敗しても、元のファイルを失わず、一時ファイルも残さないこと
        byte[] original = { 10, 20, 30, 40 };
        string path = PrepareExisting(original);
        string directory = Path.GetDirectoryName(path)!;
        string stem = Path.GetFileName(path);
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                AtomicFileWriter.Write(path, s =>
                {
                    s.Write(new byte[] { 7, 7, 7 });
                    throw new InvalidOperationException("書き込み中の失敗");
                }));

            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Empty(Directory.GetFiles(directory, stem + ".part*"));
        }
        finally
        {
            AtomicFileWriter.TryDelete(path);
        }
    }

    [Fact]
    public void RawSaver_CanceledOverwrite_KeepsExistingFile()
    {
        // 実際の保存経路でも既存ファイルが守られること
        var format = new RawFormat { Width = 64, Height = 64, BitDepth = 16 };
        var codes = new ushort[64 * 64];
        string source = TestData.WriteTempFile(TestData.EncodeRawFile(codes, format));
        byte[] original = { 0xAB, 0xCD };
        string destination = PrepareExisting(original);
        using var cts = new CancellationTokenSource();
        try
        {
            using RawImage image = RawLoader.Load(source, format);
            cts.Cancel();

            Assert.Throws<OperationCanceledException>(() => RawSaver.Save(
                image, destination, BitPacking.Lsb, Endianness.Little,
                cancellationToken: cts.Token));

            Assert.Equal(original, File.ReadAllBytes(destination));
        }
        finally
        {
            AtomicFileWriter.TryDelete(source);
            AtomicFileWriter.TryDelete(destination);
        }
    }
}

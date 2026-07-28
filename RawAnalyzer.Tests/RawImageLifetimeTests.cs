using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// MemoryMappedFile経路の寿命管理(参照カウント)に関するテスト。
/// 読み出し中にDisposeされてもアンマップ済みメモリを触らないことを確認する。
/// </summary>
public class RawImageLifetimeTests
{
    private const int Size = 256;

    private static RawFormat MakeFormat() => new()
    {
        Width = Size,
        Height = Size,
        BitDepth = 12,
    };

    private static string WriteRampFile(RawFormat format)
    {
        var codes = new ushort[format.TotalPixels];
        for (int i = 0; i < codes.Length; i++)
        {
            codes[i] = (ushort)(i & 0xFFF);
        }

        return TestData.WriteTempFile(TestData.EncodeRawFile(codes, format));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // MMFの解放がOSに反映されるまで削除できないことがある
        }
    }

    [Fact]
    public void MemoryMapped_ReadAfterDispose_ThrowsObjectDisposed()
    {
        RawFormat format = MakeFormat();
        string path = WriteRampFile(format);
        try
        {
            RawImage image = RawLoader.Load(path, format, inMemoryPixelThreshold: 0);
            Assert.True(image.IsMemoryMapped);
            image.Dispose();

            Assert.Throws<ObjectDisposedException>(() => image.GetPixel(0, 0));
            Assert.Throws<ObjectDisposedException>(
                () => image.CopyRegion(0, 0, 0, Size, 1, new ushort[Size]));

            // 二重Disposeが例外にならないこと
            image.Dispose();
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task MemoryMapped_DisposeDuringConcurrentReads_DoesNotCorruptMemory()
    {
        // 参照カウントがないと、読み出しスレッドがアンマップ済みポインタを触り
        // AccessViolation(.NETでは捕捉不能=プロセス即死)になる。
        RawFormat format = MakeFormat();
        string path = WriteRampFile(format);
        RawImage image = RawLoader.Load(path, format, inMemoryPixelThreshold: 0);
        try
        {
            Assert.True(image.IsMemoryMapped);

            using var started = new CountdownEvent(4);
            using var stop = new CancellationTokenSource();
            long successfulReads = 0;
            long disposedReads = 0;

            var readers = new Task[4];
            for (int t = 0; t < readers.Length; t++)
            {
                readers[t] = Task.Factory.StartNew(
                    () =>
                    {
                        var buffer = new ushort[Size];
                        started.Signal();
                        while (!stop.IsCancellationRequested)
                        {
                            try
                            {
                                for (int y = 0; y < Size; y++)
                                {
                                    image.CopyRegion(0, 0, y, Size, 1, buffer);
                                    ushort expected = (ushort)(((y * Size) & 0xFFF) << 4);
                                    Assert.Equal(expected, buffer[0]);
                                }

                                Interlocked.Increment(ref successfulReads);
                            }
                            catch (ObjectDisposedException)
                            {
                                // 破棄後の読み出しは、この例外で明確に失敗するのが正しい挙動
                                Interlocked.Increment(ref disposedReads);
                                return;
                            }
                        }
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            }

            started.Wait(TimeSpan.FromSeconds(10));

            // 読み出しが飛び交っている最中に破棄する
            image.Dispose();
            stop.Cancel();
            await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(30));

            Assert.True(
                Interlocked.Read(ref successfulReads) > 0 || Interlocked.Read(ref disposedReads) > 0,
                "少なくとも1回は読み出しが試行されていること");

            // 破棄後の読み出しは必ずObjectDisposedException
            Assert.Throws<ObjectDisposedException>(() => image.GetPixel(0, 0));
        }
        finally
        {
            image.Dispose();
            TryDelete(path);
        }
    }
}

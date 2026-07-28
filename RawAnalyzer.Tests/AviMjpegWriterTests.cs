using System.Buffers.Binary;
using System.Text;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class AviMjpegWriterTests
{
    private static string TempPath()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RawAnalyzerTests");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Guid.NewGuid().ToString("N") + ".avi");
    }

    private static uint ReadU32(byte[] data, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
    }

    private static int FindFourCc(byte[] data, string fourCc)
    {
        byte[] pattern = Encoding.ASCII.GetBytes(fourCc);
        for (int i = 0; i <= data.Length - 4; i++)
        {
            if (data[i] == pattern[0] && data[i + 1] == pattern[1]
                && data[i + 2] == pattern[2] && data[i + 3] == pattern[3])
            {
                return i;
            }
        }

        return -1;
    }

    [Fact]
    public void Write_ThreeFrames_ProducesValidRiffStructure()
    {
        string path = TempPath();
        byte[][] frames =
        {
            new byte[] { 0xFF, 0xD8, 1, 2, 3, 0xFF, 0xD9 },          // 7バイト(奇数)
            new byte[] { 0xFF, 0xD8, 9, 8, 7, 6, 0xFF, 0xD9 },       // 8バイト
            new byte[] { 0xFF, 0xD8, 0xAA, 0xFF, 0xD9 },             // 5バイト(奇数)
        };
        try
        {
            using (var writer = new AviMjpegWriter(path, 640, 480, 15))
            {
                foreach (byte[] frame in frames)
                {
                    writer.AddFrame(frame);
                }

                writer.Finish();
                Assert.Equal(3, writer.FrameCount);
            }

            byte[] data = File.ReadAllBytes(path);

            // RIFF/AVI ヘッダとRIFFサイズ
            Assert.Equal("RIFF", Encoding.ASCII.GetString(data, 0, 4));
            Assert.Equal("AVI ", Encoding.ASCII.GetString(data, 8, 4));
            Assert.Equal((uint)(data.Length - 8), ReadU32(data, 4));

            // avih: dwTotalFrames(オフセット32+16=48…構造から検索で確認)
            int avih = FindFourCc(data, "avih");
            Assert.True(avih > 0);
            Assert.Equal(3u, ReadU32(data, avih + 8 + 16));                // dwTotalFrames
            Assert.Equal(1_000_000u / 15, ReadU32(data, avih + 8));        // dwMicroSecPerFrame
            Assert.Equal(640u, ReadU32(data, avih + 8 + 32));              // dwWidth
            Assert.Equal(480u, ReadU32(data, avih + 8 + 36));              // dwHeight

            // strh: MJPG / dwLength
            int strh = FindFourCc(data, "strh");
            Assert.Equal("vids", Encoding.ASCII.GetString(data, strh + 8, 4));
            Assert.Equal("MJPG", Encoding.ASCII.GetString(data, strh + 12, 4));
            Assert.Equal(3u, ReadU32(data, strh + 8 + 32));                // dwLength

            // moviとフレームチャンク(奇数フレームはパディングされる)
            int movi = FindFourCc(data, "movi");
            Assert.True(movi > 0);
            int firstChunk = movi + 4;
            Assert.Equal("00dc", Encoding.ASCII.GetString(data, firstChunk, 4));
            Assert.Equal(7u, ReadU32(data, firstChunk + 4));
            Assert.Equal(0xFF, data[firstChunk + 8]);
            Assert.Equal(0xD8, data[firstChunk + 9]);

            // idx1: 3エントリ、オフセットは最初のフレームで4
            int idx1 = FindFourCc(data, "idx1");
            Assert.True(idx1 > 0);
            Assert.Equal(3u * 16, ReadU32(data, idx1 + 4));
            Assert.Equal("00dc", Encoding.ASCII.GetString(data, idx1 + 8, 4));
            Assert.Equal(0x10u, ReadU32(data, idx1 + 12));                 // AVIIF_KEYFRAME
            Assert.Equal(4u, ReadU32(data, idx1 + 16));                    // 先頭フレームのオフセット
            Assert.Equal(7u, ReadU32(data, idx1 + 20));                    // 先頭フレームのサイズ

            // 2フレーム目のオフセット = 4 + 8 + 7(+1パディング) = 20
            Assert.Equal(20u, ReadU32(data, idx1 + 8 + 16 + 8));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AddFrame_AfterFinish_Throws()
    {
        string path = TempPath();
        try
        {
            using var writer = new AviMjpegWriter(path, 8, 8, 10);
            writer.AddFrame(new byte[] { 1, 2 });
            writer.Finish();
            Assert.Throws<InvalidOperationException>(() => writer.AddFrame(new byte[] { 3 }));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Constructor_InvalidArguments_Throw()
    {
        string path = TempPath();
        Assert.Throws<ArgumentOutOfRangeException>(() => new AviMjpegWriter(path, 0, 10, 15));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AviMjpegWriter(path, 10, 10, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AviMjpegWriter(path, 10, 10, 15, maxFileBytes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AviMjpegWriter(
                path, 10, 10, 15, maxFileBytes: AviMjpegWriter.DefaultMaxFileBytes + 1));
    }

    [Fact]
    public void AddFrame_ExceedingSizeLimit_ThrowsAndKeepsEarlierFramesReadable()
    {
        // 上限を超えると idx1 の dwChunkOffset と RIFF サイズが32bitで巻き戻り、
        // 「動画書き出し完了」と表示されるのに先頭数フレームしか再生できないAVIになる。
        // 実サイズ2GiBを書かずに検証するため上限を小さくして再現する。
        string path = TempPath();
        var frame = new byte[512];
        try
        {
            int written = 0;
            using (var writer = new AviMjpegWriter(path, 8, 8, 10, maxFileBytes: 4096))
            {
                NotSupportedException? thrown = null;
                for (int i = 0; i < 100; i++)
                {
                    try
                    {
                        writer.AddFrame(frame);
                        written++;
                    }
                    catch (NotSupportedException ex)
                    {
                        thrown = ex;
                        break;
                    }
                }

                Assert.NotNull(thrown);
                Assert.Contains("上限", thrown!.Message);
                Assert.Equal(written, writer.FrameCount);
            }

            // 打ち切った時点までは正しいAVIとして閉じられていること
            Assert.True(written > 0);
            byte[] data = File.ReadAllBytes(path);
            Assert.True(data.Length <= 4096, $"上限内に収まること: {data.Length}");
            Assert.Equal((uint)(data.Length - 8), ReadU32(data, 4));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class NaturalOrderComparerTests
{
    [Theory]
    [InlineData("img2.raw", "img10.raw", -1)]
    [InlineData("img10.raw", "img2.raw", 1)]
    [InlineData("img002.raw", "img2.raw", 0)]
    [InlineData("a.raw", "b.raw", -1)]
    [InlineData("frame1a.raw", "frame1b.raw", -1)]
    [InlineData("cap_5_2.raw", "cap_5_10.raw", -1)]
    public void Compare_OrdersNaturally(string x, string y, int expectedSign)
    {
        int result = NaturalOrderComparer.Instance.Compare(x, y);
        Assert.Equal(expectedSign, Math.Sign(result));
    }

    [Fact]
    public void Sort_NumberedSequence_IsInNumericOrder()
    {
        var files = new List<string> { "f10.raw", "f2.raw", "f1.raw", "f20.raw", "f3.raw" };
        files.Sort(NaturalOrderComparer.Instance);
        Assert.Equal(new[] { "f1.raw", "f2.raw", "f3.raw", "f10.raw", "f20.raw" }, files);
    }
}

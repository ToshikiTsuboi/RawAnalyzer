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

            // 29バイト(奇数)。本番の JpegBitmapEncoder の出力と同じく SOI の直後に JFIF APP0 がある。
            // APP0 の長さは標準の 0x10 ではなく 0x12 にして、読み飛ばす長さの決め打ちも見分ける
            new byte[]
            {
                0xFF, 0xD8,
                0xFF, 0xE0, 0x00, 0x12, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00,
                1, 2, 0, 0, 1, 0, 1, 0, 0, 0xAB, 0xCD,
                0xFF, 0xDB, 0x11, 0x22, 0x33, 0xFF, 0xD9,
            },
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

            // moviとフレームチャンク(奇数フレームはパディングされる)。
            // APP0 のないフレームはSOI直後に AVI1 APP0(18バイト)が挿入される
            const uint App0 = 18;
            int movi = FindFourCc(data, "movi");
            Assert.True(movi > 0);
            int firstChunk = movi + 4;
            Assert.Equal("00dc", Encoding.ASCII.GetString(data, firstChunk, 4));
            Assert.Equal(7u + App0, ReadU32(data, firstChunk + 4));
            Assert.Equal(0xFF, data[firstChunk + 8]);
            Assert.Equal(0xD8, data[firstChunk + 9]);

            // MJPEG in AVI 仕様のAVI1識別子がAPP0に入っていること
            Assert.Equal(0xFF, data[firstChunk + 10]);
            Assert.Equal(0xE0, data[firstChunk + 11]);
            Assert.Equal("AVI1", Encoding.ASCII.GetString(data, firstChunk + 14, 4));

            // idx1: 3エントリ、オフセットは最初のフレームで4
            int idx1 = FindFourCc(data, "idx1");
            Assert.True(idx1 > 0);
            Assert.Equal(3u * 16, ReadU32(data, idx1 + 4));
            Assert.Equal("00dc", Encoding.ASCII.GetString(data, idx1 + 8, 4));
            Assert.Equal(0x10u, ReadU32(data, idx1 + 12));                 // AVIIF_KEYFRAME
            Assert.Equal(4u, ReadU32(data, idx1 + 16));                    // 先頭フレームのオフセット
            Assert.Equal(7u + App0, ReadU32(data, idx1 + 20));             // 先頭フレームのサイズ

            // 2フレーム目のオフセット = 4 + 8 + 25(+1パディング) = 38
            Assert.Equal(4u + 8 + 7 + App0 + 1, ReadU32(data, idx1 + 8 + 16 + 8));

            // 3フレーム目: JFIF APP0 を読み飛ばして AVI1 APP0 に置き換え、その直後に APP0 の次のマーカ(DQT)
            // から続ける。長さ = 29 − (2+18) + 18 = 27(置き換えずに挿入すると 47、0x10 と決め打ちすると 29)
            int third = movi + (int)ReadU32(data, idx1 + 8 + (2 * 16) + 8);
            Assert.Equal("00dc", Encoding.ASCII.GetString(data, third, 4));
            Assert.Equal(27u, ReadU32(data, third + 4));
            Assert.Equal(27u, ReadU32(data, idx1 + 8 + (2 * 16) + 12));
            byte[] chunk = data.AsSpan(third + 8, 27).ToArray();
            Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 }, chunk[..6]);
            Assert.Equal("AVI1", Encoding.ASCII.GetString(chunk, 6, 4));
            Assert.Equal(new byte[] { 0xFF, 0xDB, 0x11, 0x22, 0x33, 0xFF, 0xD9 }, chunk[20..]);
            Assert.DoesNotContain("JFIF", Encoding.ASCII.GetString(chunk));
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
    [InlineData("img2.raw", "img10.raw", -1)]      // 桁数違いの数値ブロック
    [InlineData("img002.raw", "img2.raw", 0)]      // 先頭の 0 は無視
    [InlineData("a.raw", "b.raw", -1)]             // 非数字の比較
    [InlineData("frame1a.raw", "frame1b.raw", -1)] // 数値ブロックが等しければ続きで決まる
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

    /// <summary>
    /// IME がオンのまま付けた全角数字の連番も数値として比べる。以前は全角数字を1文字ずつ文字コードで比べ、
    /// 「暗室１, 暗室１０, 暗室１１, 暗室１２, 暗室２…」と並べた(連番の再生・送り・一括書き出しの順になる)。
    /// </summary>
    [Theory]
    [InlineData("img２.raw", "img１０.raw", -1)]
    [InlineData("img２.raw", "img10.raw", -1)]  // 全角と半角が混ざっても数値で比べる
    [InlineData("img2.raw", "img１０.raw", -1)]
    [InlineData("img０２.raw", "img2.raw", 0)]  // 全角の先頭の 0 も無視
    [InlineData("img１.raw", "img1.raw", 0)]
    [InlineData("img１a.raw", "img1b.raw", -1)]
    public void Compare_FullWidthDigits_AreComparedAsNumbers(string x, string y, int expectedSign)
    {
        Assert.Equal(expectedSign, Math.Sign(NaturalOrderComparer.Instance.Compare(x, y)));
        Assert.Equal(-expectedSign, Math.Sign(NaturalOrderComparer.Instance.Compare(y, x)));
    }

    [Fact]
    public void Sort_FullWidthNumberedSequence_IsInNumericOrder()
    {
        string[] expected = Enumerable.Range(1, 12).Select(i => "暗室" + ToFullWidth(i) + ".raw").ToArray();
        var files = expected.Reverse().ToList();
        files.Sort(NaturalOrderComparer.Instance);
        Assert.Equal(expected, files);
    }

    /// <summary>
    /// 全角数字・半角数字・その他の文字が混ざっても、並べ替えに要る全順序の性質(反対称・推移律)を保つ。
    /// 全角数字を数値として比べるだけで、全角数字と他の文字の大小が半角数字と食い違うと推移律が崩れる。
    /// </summary>
    [Fact]
    public void Compare_MixedDigits_IsConsistentTotalOrder()
    {
        char[] alphabet = { '0', '1', '2', '9', '０', '１', '２', '９', 'a', 'B', '_', '.', '/', ':', '～' };
        var random = new Random(1234);
        var strings = new List<string>();
        for (int n = 0; n < 160; n++)
        {
            int length = random.Next(0, 6);
            var chars = new char[length];
            for (int k = 0; k < length; k++)
            {
                chars[k] = alphabet[random.Next(alphabet.Length)];
            }

            strings.Add(new string(chars));
        }

        NaturalOrderComparer comparer = NaturalOrderComparer.Instance;
        foreach (string a in strings)
        {
            foreach (string b in strings)
            {
                int ab = Math.Sign(comparer.Compare(a, b));
                Assert.True(ab == -Math.Sign(comparer.Compare(b, a)), $"antisymmetry: '{a}' '{b}'");
                foreach (string c in strings)
                {
                    if (ab <= 0 && comparer.Compare(b, c) <= 0)
                    {
                        Assert.True(comparer.Compare(a, c) <= 0, $"transitivity: '{a}' <= '{b}' <= '{c}'");
                    }
                }
            }
        }
    }

    /// <summary>
    /// 戻り値は IComparer の慣例どおり符号だけが意味を持つ(XML ドキュメントのとおり -1/1 とは限らない)。
    /// </summary>
    [Theory]
    [InlineData("a1", "a_")]               // 文字コードの差(-46)を返す
    [InlineData("x", "xyzzzzzzzzzzzzzzz")] // 残りの長さの差(-16)を返す
    [InlineData("img2", "img10")]
    public void Compare_SignTellsOrder(string x, string y)
    {
        Assert.True(NaturalOrderComparer.Instance.Compare(x, y) < 0);
        Assert.True(NaturalOrderComparer.Instance.Compare(y, x) > 0);
    }

    internal static string ToFullWidth(int number)
    {
        return new string(number.ToString(System.Globalization.CultureInfo.InvariantCulture)
            .Select(c => (char)('０' + (c - '0'))).ToArray());
    }
}

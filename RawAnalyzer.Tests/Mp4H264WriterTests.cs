using RawAnalyzer.App.Services;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// Media Foundationが使える環境でだけ実行するテスト
/// (Server Core等ではmfplat.dllが無いためスキップになる)。
/// </summary>
public sealed class Mp4FactAttribute : FactAttribute
{
    /// <summary>属性を生成し、MFが無ければスキップ理由を設定する。</summary>
    public Mp4FactAttribute()
    {
        if (!Mp4H264Writer.IsSupported())
        {
            Skip = "この環境にはMedia Foundationがありません。";
        }
    }
}

public class Mp4H264WriterTests
{
    private static string TempPath()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RawAnalyzerTests");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Guid.NewGuid().ToString("N") + ".mp4");
    }

    private static byte[] MakeFrame(int width, int height, int index)
    {
        // フレームごとに動く縦帯 + 勾配
        var rgb = new byte[width * height * 3];
        int band = (index * 13) % width;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 3;
                byte g = (byte)(255 * y / height);
                rgb[i] = (byte)(x == band ? 255 : 30);
                rgb[i + 1] = g;
                rgb[i + 2] = 80;
            }
        }

        return rgb;
    }

    [Mp4Fact]
    public void Write_Frames_ProducesPlayableMp4Structure()
    {
        string path = TempPath();
        try
        {
            using (var writer = new Mp4H264Writer(path, 64, 48, 15))
            {
                for (int i = 0; i < 15; i++)
                {
                    writer.AddFrameRgb24(MakeFrame(64, 48, i));
                }

                Assert.Equal(15, writer.FrameCount);
                writer.Finish();
            }

            byte[] data = File.ReadAllBytes(path);
            Assert.True(data.Length > 1000, $"出力が小さすぎる: {data.Length} bytes");

            // ISO BMFF: 先頭ボックスが ftyp、moov(インデックス)と mdat があること
            Assert.Equal("ftyp", System.Text.Encoding.ASCII.GetString(data, 4, 4));
            string boxes = FindBoxes(data);
            Assert.Contains("moov", boxes);
            Assert.Contains("mdat", boxes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string FindBoxes(byte[] data)
    {
        // トップレベルのボックス名を列挙する
        var names = new List<string>();
        long pos = 0;
        while (pos + 8 <= data.Length)
        {
            long size = ((long)data[pos] << 24) | ((long)data[pos + 1] << 16)
                | ((long)data[pos + 2] << 8) | data[pos + 3];
            names.Add(System.Text.Encoding.ASCII.GetString(data, (int)pos + 4, 4));
            if (size < 8)
            {
                break;
            }

            pos += size;
        }

        return string.Join(",", names);
    }

    [Mp4Fact]
    public void Constructor_OddSize_Throws()
    {
        string path = TempPath();
        try
        {
            Assert.Throws<NotSupportedException>(() => new Mp4H264Writer(path, 63, 48, 15));
            Assert.Throws<NotSupportedException>(() => new Mp4H264Writer(path, 64, 47, 15));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Mp4Fact]
    public void Dispose_WithoutFinish_DeletesPartialFile()
    {
        string path = TempPath();
        using (var writer = new Mp4H264Writer(path, 64, 48, 15))
        {
            writer.AddFrameRgb24(MakeFrame(64, 48, 0));

            // Finishしない = 中断
        }

        Assert.False(File.Exists(path), "書きかけのMP4が残らないこと");
    }

    [Mp4Fact]
    public void AddFrame_WrongBufferLength_Throws()
    {
        string path = TempPath();
        try
        {
            using var writer = new Mp4H264Writer(path, 64, 48, 15);
            Assert.Throws<ArgumentException>(() => writer.AddFrameRgb24(new byte[10]));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

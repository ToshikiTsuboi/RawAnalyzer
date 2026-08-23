using RawAnalyzer.App.Services;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 動画の一時ファイル名にまつわる回帰テスト。
/// </summary>
/// <remarks>
/// 書き込み中の一時ファイルを "foo.mp4.part" にすると、Media Foundation の
/// SinkWriter が URL の拡張子から出力コンテナを判別できず初期化に失敗する
/// (解像度に関係なく「エンコーダを初期化できませんでした」になる)。
/// </remarks>
public class VideoTempPathTests
{
    [Theory]
    [InlineData(@"C:\out\clip_seq.mp4", "clip_seq.part.mp4")]
    [InlineData(@"C:\out\clip_seq.avi", "clip_seq.part.avi")]
    [InlineData("clip_seq.mp4", "clip_seq.part.mp4")]
    public void BuildPartialPath_KeepsExtensionLast(string finalPath, string expectedName)
    {
        string partial = OutputPaths.BuildPartialPath(finalPath);

        Assert.Equal(expectedName, Path.GetFileName(partial));
        Assert.Equal(Path.GetExtension(finalPath), Path.GetExtension(partial));
        Assert.Equal(Path.GetDirectoryName(finalPath), Path.GetDirectoryName(partial));
        Assert.NotEqual(finalPath, partial);
    }

    [Mp4Fact]
    public void Mp4Writer_AcceptsPartialPath()
    {
        // 実際に一時パスでエンコーダを初期化し、置換まで通ること
        const int Size = 512;
        string dir = Path.Combine(Path.GetTempPath(), "RawAnalyzerTests");
        Directory.CreateDirectory(dir);
        string finalPath = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".mp4");
        string partial = OutputPaths.BuildPartialPath(finalPath);
        try
        {
            using (var writer = new Mp4H264Writer(partial, Size, Size, 15))
            {
                writer.AddFrameRgb24(new byte[Size * Size * 3]);
                writer.Finish();
            }

            File.Move(partial, finalPath, overwrite: true);
            Assert.True(new FileInfo(finalPath).Length > 0);
        }
        finally
        {
            foreach (string path in new[] { partial, finalPath })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Mp4Fact]
    public void Mp4Writer_RejectsUnknownContainerExtension()
    {
        // 拡張子で出力コンテナが決まるという前提そのものを固定しておく
        string dir = Path.Combine(Path.GetTempPath(), "RawAnalyzerTests");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".mp4.part");
        try
        {
            Assert.Throws<IOException>(() => new Mp4H264Writer(path, 512, 512, 15));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}

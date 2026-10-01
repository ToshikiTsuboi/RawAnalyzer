using RawAnalyzer.App.Services;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 動画の一時ファイル名の組み立て。
/// </summary>
/// <remarks>
/// 拡張子を末尾に残す理由(Media Foundation の SinkWriter は URL の拡張子で出力コンテナを
/// 判別する)と、一時パスで実際にエンコーダを初期化して置換まで通す回帰テストは
/// Mp4H264WriterTests.Write_Frames_ProducesPlayableMp4Structure にある。
/// </remarks>
public class VideoTempPathTests
{
    [Theory]
    [InlineData(@"C:\out\clip_seq.mp4", "clip_seq.part.mp4")]
    public void BuildPartialPath_KeepsExtensionLast(string finalPath, string expectedName)
    {
        string partial = OutputPaths.BuildPartialPath(finalPath);

        Assert.Equal(expectedName, Path.GetFileName(partial));
        Assert.Equal(Path.GetExtension(finalPath), Path.GetExtension(partial));
        Assert.Equal(Path.GetDirectoryName(finalPath), Path.GetDirectoryName(partial));
        Assert.NotEqual(finalPath, partial);
    }
}

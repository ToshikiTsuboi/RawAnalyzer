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

    [Theory]
    [InlineData("io")]
    [InlineData("canceled")]
    public void FailedOrCanceledWrite_DeletesThePartialFile_AndRethrows(string kind)
    {
        // 残課題 2026-10-02 I1。動画の一時ファイルの片付けは、以前は進捗表示を閉じた後に UI スレッドで File.Exists と
        // 削除を行い、出力先(NAS)の切断で失敗したときにタイムアウトまで固まった。書き出しと同じく UI スレッドの外で、
        // 失敗・取り消しで終わったら消してから例外を伝える
        string partial = MakePartial();
        Exception thrown = kind == "io" ? new IOException("ネットワーク名が見つかりません。") : new OperationCanceledException();

        Exception caught = Assert.ThrowsAny<Exception>(
            () => OutputPaths.RunDeletingPartialOnFailure(partial, () => throw thrown));

        Assert.Same(thrown, caught);
        Assert.False(File.Exists(partial));
    }

    [Fact]
    public void CompletedWrite_LeavesFilesAlone()
    {
        // 書き切れたら一時ファイルは処理の中で最終パスへ置き換わっている(ここでは残したものを消さないことを見る)
        string partial = MakePartial();
        try
        {
            bool written = false;
            OutputPaths.RunDeletingPartialOnFailure(partial, () => written = true);

            Assert.True(written);
            Assert.True(File.Exists(partial));
        }
        finally
        {
            File.Delete(partial);
        }
    }

    [Fact]
    public void WithoutPartialFile_FailureIsRethrownAsIs()
    {
        // 静止画の書き出し(一時ファイルを使わない)は片付けずに例外だけを伝える
        var thrown = new IOException("書けません。");

        Assert.Same(thrown, Assert.Throws<IOException>(
            () => OutputPaths.RunDeletingPartialOnFailure(null, () => throw thrown)));
    }

    private static string MakePartial()
    {
        string folder = Path.Combine(Path.GetTempPath(), "RawAnalyzerTests");
        Directory.CreateDirectory(folder);
        string partial = OutputPaths.BuildPartialPath(Path.Combine(folder, Guid.NewGuid().ToString("N") + "_seq.mp4"));
        File.WriteAllBytes(partial, new byte[16]);
        return partial;
    }
}

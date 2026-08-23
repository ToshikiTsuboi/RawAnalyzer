using System.IO;

namespace RawAnalyzer.App.Services;

/// <summary>
/// 書き出し先パスの組み立て。
/// </summary>
internal static class OutputPaths
{
    /// <summary>
    /// 書き込み中の一時ファイルのパスを作る(完了後に最終パスへ置換する用)。
    /// </summary>
    /// <remarks>
    /// 拡張子は末尾に残すこと。Media Foundation の SinkWriter は URL の拡張子から
    /// 出力コンテナを判別するため、"foo.mp4.part" のようにすると
    /// コンテナを決められず初期化に失敗する。
    /// </remarks>
    /// <param name="finalPath">最終的な出力先。</param>
    /// <returns>"foo.part.mp4" のような一時パス。</returns>
    internal static string BuildPartialPath(string finalPath)
    {
        string name = Path.GetFileNameWithoutExtension(finalPath) + ".part"
            + Path.GetExtension(finalPath);
        string? directory = Path.GetDirectoryName(finalPath);
        return string.IsNullOrEmpty(directory) ? name : Path.Combine(directory, name);
    }
}

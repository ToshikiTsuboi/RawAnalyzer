using System.IO;
using RawAnalyzer.Core;

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

    /// <summary>
    /// 一時ファイル(<see cref="BuildPartialPath"/>)へ書き出す処理を実行し、失敗・取り消し(例外)で終わったら
    /// 一時ファイルを消してから例外を投げ直す。
    /// </summary>
    /// <remarks>
    /// 書き出しと同じく UI スレッドの外で呼ぶ(出力先はネットワーク上のことがあり、切断していれば実在の確認・削除も
    /// タイムアウトまで戻らない)。書き切れたら一時ファイルは処理の中で最終パスへ置き換わっているので触らない。
    /// </remarks>
    /// <param name="partialPath">一時ファイルのパス。null なら片付けない。</param>
    /// <param name="write">書き出し(書き切れたら一時ファイルを最終パスへ置き換える)。</param>
    internal static void RunDeletingPartialOnFailure(string? partialPath, Action write)
    {
        try
        {
            write();
        }
        catch when (partialPath is not null)
        {
            AtomicFileWriter.TryDelete(partialPath);
            throw;
        }
    }

    /// <summary>
    /// 出力先フォルダの入力を絶対パスにする。
    /// </summary>
    /// <remarks>
    /// 相対パス(例 export2)をそのまま使うと、元画像のフォルダではなくプロセスのカレントディレクトリ
    /// (exe の場所など)を基準に書き出し、完了表示も相対パスのままで出力の場所が分からない。
    /// 元画像のフォルダを基準に解決する(「\out」のようなドライブの根からの相対は元画像のドライブ)。
    /// ファイルシステムには触れない(NAS の出力先でも UI スレッドで待たない)。
    /// </remarks>
    /// <param name="input">入力された出力先(前後の空白は除いたもの)。</param>
    /// <param name="baseFolder">相対パスの基準にする元画像のフォルダ(絶対パス)。</param>
    /// <param name="folder">絶対パスの出力先。</param>
    /// <returns>パスとして解釈できればtrue。</returns>
    internal static bool TryResolveOutputFolder(string input, string baseFolder, out string folder)
    {
        try
        {
            folder = Path.GetFullPath(input, baseFolder);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            folder = input;
            return false;
        }
    }

    /// <summary>
    /// 一括書き出しで静止画1枚(ファイル・RAWのフレーム・TIFFのページ)を書き出すパスを作る。
    /// </summary>
    /// <remarks>
    /// 元ファイルの拡張子を除いた名前に、TIFFのページなら _p0001(1始まり)、複数フレームなら _f000(0始まり)を付ける。
    /// 1枚ものはそのままの名前なので、出力先が元のフォルダで拡張子が同じなら元ファイルと同じパスになる
    /// (<see cref="BatchSourceGuard"/> で断る)。
    /// </remarks>
    /// <param name="outputFolder">出力先フォルダ。</param>
    /// <param name="sourcePath">元ファイル。</param>
    /// <param name="index">ファイル内の番号(フレーム・ページ。0始まり)。</param>
    /// <param name="count">ファイル内の枚数。</param>
    /// <param name="isTiffPage">複数ページTIFFのページか。</param>
    /// <param name="extension">出力の拡張子(".png" など)。</param>
    /// <returns>出力パス。</returns>
    internal static string BatchImagePath(
        string outputFolder, string sourcePath, int index, int count, bool isTiffPage, string extension)
    {
        string baseName = Path.GetFileNameWithoutExtension(sourcePath);
        string stem = isTiffPage ? $"{baseName}_p{index + 1:D4}"
            : count > 1 ? $"{baseName}_f{index:D3}"
            : baseName;
        return Path.Combine(outputFolder, stem + extension);
    }
}

/// <summary>
/// 一括書き出しの出力が書き出し対象の元ファイルを置き換えないか確かめる。
/// </summary>
/// <remarks>
/// 出力先に元のフォルダを選び、元と同じ拡張子の形式(PNG→PNG、JPEG→JPEG、1ページのTIFF→TIFF16)で書き出すと、
/// 1枚ものの出力名は元ファイルと同じになる。元ファイルは読み終えると閉じ、出力は一時ファイルからの置換で書くので、
/// そのままでは確認なしで元画像が焼き込み結果(8bit・輝度だけの Gray16)に置き換わり、センサデータを失う。
/// 元ファイルと同じパスへは書かずに中止する。比較は完全パスで、大文字小文字を区別しない。
/// </remarks>
internal sealed class BatchSourceGuard
{
    private readonly HashSet<string> _sources;

    /// <summary>書き出し対象の元ファイルから作る。</summary>
    /// <param name="sources">書き出し対象の元ファイル。</param>
    internal BatchSourceGuard(IEnumerable<string> sources)
    {
        _sources = new HashSet<string>(sources.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>出力パスが元ファイルと同じなら書かずに中止する。</summary>
    /// <param name="outputPath">これから書き出すパス。</param>
    /// <exception cref="IOException">元ファイルと同じパスの場合。</exception>
    internal void EnsureNotSource(string outputPath)
    {
        if (_sources.Contains(Path.GetFullPath(outputPath)))
        {
            throw new IOException(
                $"出力 {Path.GetFileName(outputPath)} が書き出し対象の元ファイルと同じパスになるため、" +
                "元ファイルを上書きせずに中止しました。出力先に元のフォルダ以外を選んでください。");
        }
    }
}

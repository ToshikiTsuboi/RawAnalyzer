namespace RawAnalyzer.Core;

/// <summary>
/// 「書き終わったものだけを目的地に置く」ファイル書き出し。
/// </summary>
/// <remarks>
/// 出力先を直接 FileMode.Create で開くと、その時点で既存ファイルが 0 バイトに
/// 切り詰められる。途中でキャンセル・ディスクフル・書き込みエラーが起きると、
/// 元のファイルは失われたうえに中途半端な出力だけが残る。
/// 同一フォルダの一時ファイルへ書き、成功したときだけ置き換えることで、
/// 失敗しても元のファイルをそのまま残す。
/// </remarks>
public static class AtomicFileWriter
{
    /// <summary>
    /// 一時ファイルへ書き出し、成功時のみ目的のパスへ置き換える。
    /// </summary>
    /// <param name="path">最終的な出力先パス。</param>
    /// <param name="write">書き込み処理。渡されたストリームへ出力する。</param>
    /// <param name="bufferSize">ストリームのバッファサイズ。</param>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="write"/> がキャンセルされた場合(一時ファイルは削除される)。
    /// </exception>
    public static void Write(string path, Action<FileStream> write, int bufferSize = 1 << 20)
    {
        // 置き換えを確実にするため、一時ファイルは同じフォルダに作る
        // (別ボリュームだとFile.Moveがコピーになり原子性が崩れる)
        string temporary = path + ".part" + Environment.CurrentManagedThreadId
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            using (var stream = new FileStream(
                temporary, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize))
            {
                write(stream);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>後始末用の削除。失敗しても本体のエラーを覆い隠さない。</summary>
    /// <param name="path">削除するパス。</param>
    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

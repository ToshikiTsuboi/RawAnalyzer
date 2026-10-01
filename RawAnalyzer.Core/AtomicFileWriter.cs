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
        // (別ボリュームだとFile.Moveがコピーになり原子性が崩れる)。
        // 名前にはプロセスIDも入れる。スレッドIDだけだと、どのプロセスでも UI スレッドのIDは同じなので、
        // 複数起動したインスタンスが同じファイル(記憶・セッション)を同時に保存すると同じ一時ファイルを取り合う
        string temporary = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{path}.part{Environment.ProcessId}-{Environment.CurrentManagedThreadId}");
        try
        {
            using (var stream = new FileStream(
                temporary, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize))
            {
                write(stream);

                // 置換前にディスクへ確定させる。書き込みがOSキャッシュに残ったまま
                // File.Move が先に永続化されると、直後の電源断で
                // 「新しい中身は未達・古いファイルは消滅」になり得る
                // (writeがストリームを閉じた場合はフラッシュ済みなので何もしない)
                if (stream.CanWrite)
                {
                    stream.Flush(flushToDisk: true);
                }
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

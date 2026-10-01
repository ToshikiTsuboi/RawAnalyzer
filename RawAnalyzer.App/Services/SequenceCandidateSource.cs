using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>
/// 連番(仮想スタック)の判定と一括書き出しの対象を決める候補ファイルを、表示中のファイルのフォルダの列挙から選ぶ。
/// </summary>
/// <remarks>
/// <para>
/// 左パネルのファイル一覧は、フォルダツリー・「フォルダを開く」・フォルダのドロップで、表示中の画像を残したまま
/// 別のフォルダの一覧に差し替わる。一覧をそのまま候補にすると、ツリーで別のフォルダへ移った後に F2 で開き直す・
/// HDR表示から Raw 表示へ戻る・Ctrl+B で一括書き出しすると、別のフォルダの同じサイズ・同じ命名のファイルを
/// 連番・書き出しの対象にしていた(再生バーは開いた時点のフォルダの連番を指したまま、Ctrl+B だけが別のフォルダを
/// 指すという、<see cref="SequenceScanner"/> が防ごうとしている食い違いそのもの)。
/// </para>
/// <para>
/// 一覧が表示中のファイルのフォルダのものなら一覧を使い、その内容を覚えておく。一覧が別のフォルダのものなら、
/// 覚えておいた表示中のファイルのフォルダの一覧を使う(UI スレッドでフォルダを列挙し直さない)。
/// どちらもなければ候補なし(連番なし)とする。
/// </para>
/// <para>UI スレッド専用(排他制御はしない)。</para>
/// </remarks>
internal sealed class SequenceCandidateSource
{
    private string? _folder;
    private IReadOnlyList<SequenceFile> _files = Array.Empty<SequenceFile>();

    /// <summary>表示中のファイルの連番判定・一括書き出しに使う候補を返す。</summary>
    /// <param name="currentPath">表示中のファイル。</param>
    /// <param name="listFolder">左パネルの一覧のフォルダ(一覧がなければ null)。</param>
    /// <param name="list">左パネルの一覧の内容(サイズは列挙時のもの)。</param>
    /// <returns>表示中のファイルと同じフォルダの候補。分からなければ空。</returns>
    internal IReadOnlyList<SequenceFile> Resolve(
        string currentPath, string? listFolder, IEnumerable<SequenceFile> list)
    {
        if (SequenceScanner.IsInFolder(currentPath, listFolder))
        {
            _folder = listFolder;
            _files = list.ToArray();
            return _files;
        }

        return SequenceScanner.IsInFolder(currentPath, _folder) ? _files : Array.Empty<SequenceFile>();
    }
}

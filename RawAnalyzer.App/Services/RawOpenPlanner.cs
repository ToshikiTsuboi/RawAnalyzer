using System.Globalization;
using System.IO;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>raw を開くときのフォーマットの出どころ。</summary>
internal enum RawFormatOrigin
{
    /// <summary>同じパスの記憶(従来どおり、ダイアログなしで開く)。</summary>
    PathMemory,

    /// <summary>同じサイズ・拡張子のファイルの記憶(自動適用がオンの記憶がちょうど1つ)。ダイアログなしで開く。</summary>
    SizeMemory,

    /// <summary>インポートダイアログで指定する。</summary>
    Dialog,
}

/// <summary>raw を開くときの判断の結果。</summary>
/// <param name="Origin">フォーマットの出どころ。</param>
/// <param name="Format">ダイアログなしで開くフォーマット(<see cref="RawFormatOrigin.Dialog"/> のときは null)。</param>
/// <param name="DialogInitial">ダイアログの初期値(null ならダイアログ既定の推定)。</param>
internal sealed record RawOpenPlan(RawFormatOrigin Origin, RawFormat? Format, RawFormat? DialogInitial);

/// <summary>
/// raw(.raw/.bin)を開くときに、どのフォーマットで開くか(ダイアログを出すか)を決める。
/// </summary>
/// <remarks>
/// <para>通常の「開く」(ドラッグ&amp;ドロップ・最近使ったファイル・起動引数・一覧のダブルクリック・比較ペイン)の判断順:</para>
/// <list type="number">
/// <item>同じパスの記憶があれば従来どおりそのまま開く。</item>
/// <item>同じサイズ・拡張子の記憶のうち自動適用がオンのものがちょうど1つなら、ダイアログを出さずにそのフォーマットで開く。</item>
/// <item>それ以外はダイアログを出す。候補一覧(<see cref="FormatCandidates"/>)の先頭を初期値にする
/// (候補がなければ表示中の画像のフォーマット、それもなければダイアログ既定の推定)。</item>
/// </list>
/// <para>
/// フォーマットを指定し直して開く(F2・一覧の「フォーマットを指定して開く…」)ときは記憶を使わず必ずダイアログを出し、
/// 渡された初期値を優先する(候補一覧はダイアログで選べる)。
/// </para>
/// </remarks>
internal static class RawOpenPlanner
{
    /// <summary>同じサイズのファイルの記憶から推定して開いたときにステータスバーへ出す文。</summary>
    internal const string AutoOpenNotice = "同じサイズのファイルの記憶から推定して開きました(F2 で変更)";

    /// <summary>raw をどのフォーマットで開くかを決める。</summary>
    /// <param name="path">開くファイル。</param>
    /// <param name="fileSize">ファイルサイズ(バイト)。不明なら負。</param>
    /// <param name="pathMemory">同じパスの記憶(ファイルサイズで開けるものだけ。なければ null)。</param>
    /// <param name="history">サイズ別フォーマット記憶。</param>
    /// <param name="displayedFormat">表示中の画像のフォーマット(なければ null)。</param>
    /// <param name="requestedInitial">フォーマットを指定し直すときにダイアログへ渡す初期値。</param>
    /// <param name="chooseFormat">フォーマットを指定し直して開くか(記憶を使わず必ずダイアログを出す)。</param>
    /// <returns>判断の結果。</returns>
    internal static RawOpenPlan Plan(
        string path, long fileSize, RawFormat? pathMemory, IReadOnlyFormatHistory history,
        RawFormat? displayedFormat, RawFormat? requestedInitial = null, bool chooseFormat = false)
    {
        string extension = FormatHistory.ExtensionOf(path);
        if (!chooseFormat)
        {
            if (pathMemory is not null)
            {
                return new RawOpenPlan(RawFormatOrigin.PathMemory, pathMemory, null);
            }

            if (fileSize > 0 && history.FindAutoOpenFormat(fileSize, extension) is { } remembered)
            {
                return new RawOpenPlan(RawFormatOrigin.SizeMemory, remembered, null);
            }
        }

        IReadOnlyList<FormatCandidate> candidates = FormatCandidates.Build(
            fileSize, extension, path, history, displayedFormat);
        RawFormat? first = candidates.Count > 0 ? candidates[0].Format : null;
        RawFormat? initial = chooseFormat
            ? requestedInitial ?? first ?? displayedFormat
            : first ?? requestedInitial ?? displayedFormat;
        return new RawOpenPlan(RawFormatOrigin.Dialog, null, initial);
    }

    /// <summary>比較ペインを記憶から推定して開いたときにステータスバーへ出す文。</summary>
    /// <param name="path">開いたファイル。</param>
    /// <returns>表示する文。</returns>
    internal static string CompareAutoOpenNotice(string path)
    {
        // 比較ペインは F2 で開き直せないので、直し方(通常表示で開いて F2)を示す
        return $"比較: {Path.GetFileName(path)} を同じサイズのファイルの記憶から推定して開きました" +
            "(変更は通常表示で開いて F2)";
    }

    /// <summary>記憶から推定して開いたときの説明(ステータスバーのツールチップ)。</summary>
    /// <param name="path">開いたファイル。</param>
    /// <param name="fileSize">ファイルサイズ(バイト)。</param>
    /// <param name="format">推定したフォーマット。</param>
    /// <returns>説明文。</returns>
    internal static string AutoOpenToolTip(string path, long fileSize, RawFormat format)
    {
        string extension = FormatHistory.ExtensionOf(path);
        return $"推定したフォーマット: {FormatCandidateText.Describe(format)}\n" +
            $"同じサイズ({fileSize.ToString("N0", CultureInfo.InvariantCulture)} バイト)・同じ拡張子" +
            $"({(extension.Length > 0 ? extension : "なし")})のファイルを開いたときの記憶です。\n" +
            "F2 で開き直して変更すると、次からは変更後の形式で開きます。";
    }
}

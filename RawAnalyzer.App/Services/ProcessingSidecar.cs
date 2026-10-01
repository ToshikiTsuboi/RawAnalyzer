using System.IO;
using System.Text;
using RawAnalyzer.App.Views;

namespace RawAnalyzer.App.Services;

/// <summary>
/// 保存の付随テキスト(保存した画像に何が適用されたかの記録。本文は MainWindow の WriteProcessingSidecarAsync が作る)の
/// 判断と書き出し。
/// </summary>
internal static class ProcessingSidecar
{
    /// <summary>付随テキストの先頭行(見出し)。</summary>
    internal const string Title = "RawAnalyzer 保存情報";

    /// <summary>付随テキストに保存した画像のファイル名を書く行の始まり。</summary>
    internal const string OutputFileLabel = "出力ファイル: ";

    // 番号を付けた名前を試す上限(これを超えたら書かない)
    private const int MaxNumberedCandidates = 99;

    /// <summary>
    /// 付随テキストの書き出し先を決める。既定は画像と同名の .txt。
    /// </summary>
    /// <remarks>
    /// 保存ダイアログの上書き確認は画像のパスにしか掛からない。同名の .txt を確かめずに上書きすると、元 raw の横に
    /// 置いた撮影メモや、同じ名前で別の形式に保存した画像(foo.tif と foo.png)の付随テキストを黙って消す。
    /// 同名の .txt がない、または同じ画像(同じファイル名)を前に保存したときの付随テキストなら従来どおりそこへ書き、
    /// それ以外のファイルがあれば残して、画像の名前に .txt を足した名前(foo.png.txt)、それも使われていれば
    /// 番号を付けた名前(foo.png (2).txt …)へ書く。
    /// </remarks>
    /// <param name="imagePath">保存した画像のパス。</param>
    /// <returns>書き出し先。使える名前が見つからなければ null。</returns>
    internal static string? ResolvePath(string imagePath)
    {
        string imageFileName = Path.GetFileName(imagePath);
        var candidates = new List<string> { Path.ChangeExtension(imagePath, ".txt"), imagePath + ".txt" };
        for (int n = 2; n <= MaxNumberedCandidates; n++)
        {
            candidates.Add($"{imagePath} ({n}).txt");
        }

        foreach (string candidate in candidates)
        {
            if (!File.Exists(candidate) || IsSidecarOf(candidate, imageFileName))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// 付随テキストを書き出し先(<see cref="ResolvePath"/>)へ書き、保存完了の表示に添える注記を返す。
    /// </summary>
    /// <remarks>
    /// 書き出し先の確認(同名の .txt の実在・中身)と書き込みはファイルシステムに触れるので、UI スレッドの外で呼ぶ
    /// (保存先はネットワーク上のことがあり、切断していればタイムアウトまで戻らない)。付随テキストを書けなくても
    /// 本体の保存結果には影響させず、注記で知らせる。
    /// </remarks>
    /// <param name="imagePath">保存した画像のパス。</param>
    /// <param name="text">付随テキストの本文。</param>
    /// <returns>
    /// 画像と同名の .txt へ書いたときは空。既存のファイルを残すため別名へ書いたとき・書けなかったときはそのことを示す注記。
    /// </returns>
    internal static string Write(string imagePath, string text)
    {
        try
        {
            string? sidecarPath = ResolvePath(imagePath);
            if (sidecarPath is null)
            {
                return " / 付随テキストは同名・別名の .txt がすべて使われているため保存していません";
            }

            File.WriteAllText(sidecarPath, text, Encoding.UTF8);
            return string.Equals(sidecarPath, Path.ChangeExtension(imagePath, ".txt"), StringComparison.OrdinalIgnoreCase)
                ? ""
                : $" / 付随テキスト: {Path.GetFileName(sidecarPath)} (同名の .txt は別のファイルのため残しました)";
        }
        catch (Exception)
        {
            return " / 付随テキストを保存できませんでした";
        }
    }

    /// <summary>
    /// 既存のテキストが、同じファイル名の画像を保存したときの付随テキストか(先頭行が見出しで、出力ファイルの行が
    /// その画像の名前)。読めないときは付随テキストではないとみなす(上書きしない)。
    /// </summary>
    private static bool IsSidecarOf(string path, string imageFileName)
    {
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            if (reader.ReadLine() != Title)
            {
                return false;
            }

            // 出力ファイルの行は見出しの数行下にある
            for (int i = 0; i < 8 && reader.ReadLine() is { } line; i++)
            {
                if (line.StartsWith(OutputFileLabel, StringComparison.Ordinal))
                {
                    return string.Equals(
                        line[OutputFileLabel.Length..], imageFileName, StringComparison.OrdinalIgnoreCase);
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// マルチフレームの画像の1フレームだけを書き出したときの「元画像のフレーム: k/N」の行。
    /// </summary>
    /// <remarks>
    /// TIFF/PNG/JPEG・float raw は表示中の1フレームだけを書き出す。どのフレームかを書かないと、同じ raw から別の
    /// フレームを保存したファイル同士を後から区別できない。raw 形式は全フレームを書き出すので書かない。TIFF スタックは
    /// 「元TIFFのページ」を書く(各ページが1枚の画像)。HDR派生ビューは派生画像が1フレームで、元にしたフレームは
    /// [HDR派生ビュー]に書く(<see cref="HdrViewSidecar"/>)。
    /// </remarks>
    /// <param name="frameCount">保存した画像のフレーム数。</param>
    /// <param name="frame">保存したフレーム番号(0始まり)。</param>
    /// <param name="tiffStack">TIFFスタックのページか。</param>
    /// <param name="format">保存形式。</param>
    /// <returns>行(改行なし)。書かないときは null。</returns>
    internal static string? SourceFrameLine(int frameCount, int frame, bool tiffStack, SaveFormat format)
    {
        return frameCount > 1 && !tiffStack && format != SaveFormat.Raw
            ? $"元画像のフレーム: {frame + 1}/{frameCount}"
            : null;
    }
}

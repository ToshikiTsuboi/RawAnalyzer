using System.Diagnostics.CodeAnalysis;
using System.Windows.Controls;
using RawAnalyzer.App.Controls;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>
/// 右パネルのカラーマトリクスの 9 個の入力欄(行優先)の解釈。
/// </summary>
/// <remarks>
/// 読めない欄があると前の行列のまま現像する。以前は行列の下に「入力エラー」と出すだけで、9 個のどの欄が
/// 悪いのかは欄の見た目で分からなかった。ファイル一覧の絞り込み欄と同じく、悪い欄をすべて赤枠にして
/// ツールチップに理由を出す。
/// </remarks>
internal static class ColorMatrixInput
{
    /// <summary>読めない欄に出す理由。</summary>
    internal const string InvalidReason = "有限の数値で指定してください。直すまで前の行列のまま現像します";

    /// <summary>9 個の欄から行列を読み、読めない欄を不正の見せ方にする(読める欄は戻す)。</summary>
    /// <param name="boxes">M11〜M33 の欄(行優先の 9 個)。</param>
    /// <param name="matrix">読めた行列。読めない欄があれば null。</param>
    /// <returns>すべての欄が有限の数値ならtrue。</returns>
    internal static bool TryRead(IReadOnlyList<TextBox> boxes, [NotNullWhen(true)] out ColorMatrix? matrix)
    {
        // NaN/Infinity が入ると現像結果が全画素破綻するので有限値だけ通す
        var values = new double[9];
        bool valid = true;
        for (int i = 0; i < values.Length; i++)
        {
            valid &= InputFeedback.Check(
                boxes[i], NumericInput.TryParseFinite(boxes[i].Text, out values[i]), InvalidReason);
        }

        matrix = valid
            ? new ColorMatrix(
                values[0], values[1], values[2],
                values[3], values[4], values[5],
                values[6], values[7], values[8])
            : null;
        return valid;
    }
}

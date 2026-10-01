using System.Globalization;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>
/// キャンバス右下のオーバーレイの文字列(表示中のビューの注記と、描画した間引きレベル)。
/// </summary>
/// <remarks>
/// オーバーレイは描画のたびに間引きレベルを示す(MainWindow の OnViewportStateChanged)。以前はそのたびに文字列ごと
/// 上書きしていたので、HDR合成ビューの「表示・解析は16bit量子化後」の注意やHDR分割ビューの並びの説明、フルスクリーンの
/// 案内は、設定した直後の描画で消えて実質的に表示されなかった。注記は別に持ち、間引きレベルはその下の行に出す。
/// </remarks>
internal static class LevelOverlay
{
    /// <summary>フルスクリーン表示中の案内。</summary>
    internal const string FullscreenHint = "フルスクリーン (F11 / Esc で解除)";

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>オーバーレイの文字列。注記(空・null は飛ばす)を上から並べ、最後の行に間引きレベルを出す。</summary>
    /// <param name="renderedFactor">描画に使った縮小率(1=等倍データ、2^n=ピラミッドのレベルn)。</param>
    /// <param name="notes">表示中のビューの注記(HDR派生ビューの説明・フルスクリーンの案内など)。</param>
    /// <returns>オーバーレイの文字列(複数行)。</returns>
    internal static string Describe(int renderedFactor, params string?[] notes)
    {
        int levelIndex = (int)Math.Round(Math.Log2(Math.Max(1, renderedFactor)));
        string level = renderedFactor > 1
            ? $"1/{renderedFactor} 間引き表示 (ピラミッド L{levelIndex})"
            : "等倍データ表示 (L0)";
        return string.Join("\n", notes.Where(note => !string.IsNullOrEmpty(note)).Append(level));
    }

    /// <summary>HDR分割ビューの説明(長秒→短秒の各段を左から並置)。</summary>
    /// <param name="stages">段数。</param>
    /// <returns>説明。</returns>
    internal static string DescribeSplit(int stages) => $"HDR分割表示 (左: 長秒 → 右: 短秒, {stages}段)";

    /// <summary>
    /// HDR合成ビューの説明。表示・解析は16bitへ量子化した合成画像に対して行うので、黒点未満を0に切り詰めていること、
    /// 量子化で元素材より情報が落ちる構成ではその量を示す(LostBits は元素材のLSB基準。12bit・2段・露光比16などは
    /// 無損失なので出さない)。
    /// </summary>
    /// <param name="merged">合成結果。</param>
    /// <returns>説明。</returns>
    internal static string DescribeMerge(HdrImage merged)
    {
        string lossNote = merged.LostBits >= 0.5
            ? ", 表示・解析は16bit量子化後 (1LSB=" + merged.QuantizationStep.ToString("F1", Invariant)
              + ", 元素材比 約" + merged.LostBits.ToString("F0", Invariant) + "bit損失 / 無損失はfloat raw保存)"
            : "";
        return $"HDR合成表示 (フルスケール {merged.FullScale.ToString("F0", Invariant)}, ゲイン=露出, 黒点未満は0{lossNote})";
    }
}

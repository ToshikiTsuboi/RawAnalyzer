using System.Globalization;

namespace RawAnalyzer.App.Compare;

/// <summary>条件チップに表示する表示調整の項目。</summary>
internal enum ConditionKey
{
    /// <summary>ゲイン[dB]。</summary>
    Gain,

    /// <summary>ガンマ。</summary>
    Gamma,

    /// <summary>コントラスト。</summary>
    Contrast,

    /// <summary>黒レベル(%FS)。</summary>
    Black,

    /// <summary>白レベル(%FS)。</summary>
    White,
}

/// <summary>
/// ペイン間の表示条件の比較・転写(純関数)。
/// </summary>
/// <remarks>
/// 黒/白レベルの raw code はビット深度に依存するため、ペイン間の
/// 一致判定と転写は %FS(フルスケール比)を介して行う。
/// ゲイン/γ/コントラストは無次元なのでそのまま比較・コピーする。
/// </remarks>
internal static class DisplayConditions
{
    /// <summary>チップの表示順。</summary>
    internal static readonly ConditionKey[] Keys =
    {
        ConditionKey.Gain, ConditionKey.Gamma, ConditionKey.Contrast,
        ConditionKey.Black, ConditionKey.White,
    };

    /// <summary>チップ表示用の短いテキストを作る。</summary>
    /// <param name="key">項目。</param>
    /// <param name="settings">表示調整。</param>
    /// <param name="bitDepth">対象画像のビット深度(%FS換算用)。</param>
    /// <returns>表示テキスト(例: "+6.0dB"、"黒3.3%")。</returns>
    internal static string Format(ConditionKey key, DisplaySettings settings, int bitDepth)
    {
        CultureInfo ci = CultureInfo.InvariantCulture;
        return key switch
        {
            ConditionKey.Gain => settings.GainDb.ToString("+0.0;-0.0;0.0", ci) + "dB",
            ConditionKey.Gamma => "γ" + settings.Gamma.ToString("0.00", ci),
            ConditionKey.Contrast => "C" + settings.Contrast.ToString("0.00", ci),
            ConditionKey.Black =>
                "黒" + settings.BlackPercent(bitDepth).ToString("0.0", ci) + "%",
            _ => "白" + settings.WhitePercent(bitDepth).ToString("0.0", ci) + "%",
        };
    }

    /// <summary>
    /// 2ペインの表示条件が項目単位で一致しているか。
    /// </summary>
    /// <remarks>
    /// 黒/白は %FS で比較し、粗い方のビット深度の 0.5LSB を許容する
    /// (転写(<see cref="Transfer"/>)後の丸め誤差を不一致にしないため)。
    /// </remarks>
    /// <param name="key">項目。</param>
    /// <param name="a">ペインAの表示調整。</param>
    /// <param name="aBits">ペインAのビット深度。</param>
    /// <param name="b">ペインBの表示調整。</param>
    /// <param name="bBits">ペインBのビット深度。</param>
    /// <returns>一致していればtrue。</returns>
    internal static bool AreEqual(
        ConditionKey key, DisplaySettings a, int aBits, DisplaySettings b, int bBits)
    {
        switch (key)
        {
            case ConditionKey.Gain:
                return Math.Abs(a.GainDb - b.GainDb) < 1e-6;
            case ConditionKey.Gamma:
                return Math.Abs(a.Gamma - b.Gamma) < 1e-6;
            case ConditionKey.Contrast:
                return Math.Abs(a.Contrast - b.Contrast) < 1e-6;
            default:
                double coarseMax = Math.Min((1 << aBits) - 1, (1 << bBits) - 1);
                double tolerance = 100.0 * 0.5 / coarseMax;
                double pa = key == ConditionKey.Black ? a.BlackPercent(aBits) : a.WhitePercent(aBits);
                double pb = key == ConditionKey.Black ? b.BlackPercent(bBits) : b.WhitePercent(bBits);
                return Math.Abs(pa - pb) <= tolerance;
        }
    }

    /// <summary>
    /// 表示条件を別ビット深度のペインへ転写する(黒/白は%FS等価のcodeに丸める)。
    /// </summary>
    /// <param name="source">元の表示調整。</param>
    /// <param name="sourceBits">元のビット深度。</param>
    /// <param name="targetBits">転写先のビット深度。</param>
    /// <returns>転写先ペイン用の表示調整。</returns>
    internal static DisplaySettings Transfer(
        DisplaySettings source, int sourceBits, int targetBits)
    {
        if (sourceBits == targetBits)
        {
            return source;
        }

        double maxSource = (1 << sourceBits) - 1;
        double maxTarget = (1 << targetBits) - 1;
        return source with
        {
            BlackCode = Math.Round(source.BlackCode / maxSource * maxTarget),
            WhiteCode = Math.Round(source.WhiteCode / maxSource * maxTarget),
        };
    }
}

namespace RawAnalyzer.App.Services;

/// <summary>
/// 表示中の画像に画像演算(ダーク減算・フラット補正・差分)を掛けられるか(できないときの理由)の判定。
/// </summary>
internal static class ImageCalculationAvailability
{
    /// <summary>演算できない理由を返す。</summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>比較表示中は、見えていないメイン画像を演算結果へ差し替えてしまう(ビニング・フィルタと同じく断る)。</item>
    /// <item>HDR分割・合成の派生ビューは元画像ではない(結果は元画像を差し替える)。</item>
    /// <item>
    /// カラー画像は輝度の画像で演算し、結果はカラーを持たないので、RGB の画像が輝度だけのグレーの画像に
    /// 置き換わる(そのまま保存するとグレー)。ビニング・フィルタは RGB の成分ごとに処理してカラーを保つ。
    /// </item>
    /// </list>
    /// </remarks>
    /// <param name="compareMode">比較表示中か。</param>
    /// <param name="derivedViewShown">HDR分割・合成の派生ビューを表示しているか。</param>
    /// <param name="colorImage">表示中の画像がカラー(RGB)か。</param>
    /// <returns>演算できない理由。演算できるならnull。</returns>
    internal static string? Refusal(bool compareMode, bool derivedViewShown, bool colorImage)
    {
        if (compareMode)
        {
            return "比較表示中は画像演算できません。通常の単画像表示で実行してください。";
        }

        if (derivedViewShown)
        {
            return "HDR表示中は画像演算できません。Raw表示に戻してから実行してください。";
        }

        return colorImage
            ? "カラー画像(RGB)は画像演算できません(演算は輝度で行うため、結果はカラーが失われ、" +
              "輝度だけのグレーの画像に置き換わります)。グレー(1チャネル)の画像で実行してください。"
            : null;
    }
}

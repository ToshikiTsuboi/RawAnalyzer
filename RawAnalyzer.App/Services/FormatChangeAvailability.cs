namespace RawAnalyzer.App.Services;

/// <summary>
/// 「フォーマット変更…」(raw を読み込みダイアログで開き直す)を使えない理由の文言。
/// </summary>
/// <remarks>
/// <para>
/// フォーマット(寸法・ビット深度・詰め・エンディアン・HDR方式)を指定し直して開けるのは
/// raw(.raw/.bin)だけ。画像ファイル(TIFF・PNG・JPEG など)はフォーマットをファイル自身が持ち、
/// 開き直しても同じ画像になる(以前は画像ファイルで押しても何も起きなかった)。
/// </para>
/// <para>
/// メニューの「HDR方式の設定 (フォーマット変更)…」と右パネルの「変更…」は無効にしてツールチップで、
/// キー(F2)・コマンドパレットから実行されたときはメッセージで、同じ理由を示す。
/// </para>
/// </remarks>
internal static class FormatChangeAvailability
{
    /// <summary>
    /// 画像ファイルの表示中に「フォーマット変更…」を使えない理由を、利用者に示す文にする。
    /// </summary>
    /// <param name="isColor">表示中の画像がデコード済みのカラー画像か。</param>
    /// <returns>ツールチップ・メッセージボックスに出す文。</returns>
    internal static string ExplainUnavailable(bool isColor)
    {
        const string rawOnly =
            "フォーマット変更は raw(.raw/.bin)を開いているときに使えます。\n" +
            "画像ファイル(TIFF・PNG・JPEG など)は寸法・ビット深度などをファイル自身が持つため、指定し直せません。\n";

        // カラー画像には Bayer・HDR 方式を当てない(右パネルの Bayer も操作不可。DisplayModeSelection と同じ規約)
        return isColor
            ? rawOnly + "カラー画像(RGB)はカラーのまま表示します。"
            : rawOnly + "Bayer は右パネルの「Bayer」で指定できます" +
              "(HDR方式は、rawで保存してから開き直すと指定できます)。";
    }
}

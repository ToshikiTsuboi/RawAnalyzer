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
/// <para>
/// ファイル一覧の右クリックメニュー「フォーマットを指定して開く…」も raw でだけ使え、画像ファイルでは
/// 同じ規約で無効にしてツールチップで理由を示す(以前は画像ファイルをダイアログなしで普通に開いた)。
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

    /// <summary>
    /// 「フォーマットを指定して開く…」(ファイル一覧の右クリックメニュー)の説明。使えるときのツールチップ。
    /// </summary>
    internal const string OpenWithFormatDescription = "記憶したフォーマットを使わずインポートダイアログを開きます";

    /// <summary>
    /// ファイル一覧で選んだ画像ファイルに「フォーマットを指定して開く…」を使えない理由(ツールチップ)。
    /// </summary>
    /// <remarks>
    /// 開く前なのでカラーかグレーかは分からない。グレーの画像で指定できるもの(Bayer・HDR方式)は
    /// <see cref="ExplainUnavailable"/> と同じ案内にする。
    /// </remarks>
    internal const string OpenWithFormatUnavailableReason =
        "フォーマットを指定して開けるのは raw(.raw/.bin)だけです。\n" +
        "画像ファイル(TIFF・PNG・JPEG など)は寸法・ビット深度などをファイル自身が持つため、「開く」でそのまま開きます。\n" +
        "グレーの画像は、開いた後に右パネルの「Bayer」で Bayer を指定できます" +
        "(HDR方式は、rawで保存してから開き直すと指定できます)。";

    /// <summary>ファイルをフォーマットを指定して開けるか(raw(.raw/.bin)だけ)。</summary>
    /// <param name="path">ファイルのパス。</param>
    /// <returns>raw ならtrue。</returns>
    internal static bool CanOpenWithFormat(string path) => Compare.ComparePane.IsRawFile(path);
}

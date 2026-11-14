namespace RawAnalyzer.App.Services;

/// <summary>
/// コマンド(<see cref="AppCommand"/>)を実行できない主な理由の文言。
/// </summary>
/// <remarks>
/// コマンドパレットで実行できないコマンドを選んだとき・Enter を押したときに示す
/// (<see cref="AppCommand.DisabledReason"/>)。以前はパレットでも見た目が同じで、Enter を押しても
/// 何も起きなかった。
/// </remarks>
internal static class CommandDisabledReasons
{
    /// <summary>画像を開いていない(ほとんどのコマンドの前提)。</summary>
    public const string NoImage =
        "画像を開いていないため実行できません。「開く…」(Ctrl+O)で画像を開くと使えます。";

    /// <summary>
    /// 比較モード中(メインの画像・通常表示を対象にするコマンド)。比較モードは通常表示の上に比較画面を重ねるだけなので、
    /// 実行すると見えている比較ペインではなく、隠れた通常表示の画像・ビューポートに効いてしまう。
    /// </summary>
    public const string CompareMode =
        "比較モード中は実行できません(比較ペインではなく、比較画面に隠れた通常表示の画像が対象になるため)。" +
        "比較モードを終了して、通常の単画像表示で実行してください。";

    /// <summary>
    /// 比較モード中の右の調整パネル(パネルの上端に示す)。表示調整・ヒストグラム・ホワイトバランス・
    /// カラーマトリクス・フォーマットは通常表示を対象にするので、比較画面に隠れた通常表示に効いてしまう。
    /// </summary>
    public const string AdjustPanelInCompareMode =
        "比較モード中は調整パネルを使えません(比較ペインではなく、比較画面に隠れた通常表示の画像が対象になるため)。" +
        "比較ペインの表示は、各ペインの「自動」「リセット」と比較バーの「条件を揃える」で調整してください。";

    /// <summary>送れるフレーム・ページ・ファイルがない(フレーム送り・再生)。</summary>
    public const string NoSequence =
        "送れるフレームがないため実行できません。連番ファイル・マルチフレームの raw・複数ページTIFF を" +
        "表示しているときに使えます(HDR表示中・処理結果の表示中は送れません)。";

    /// <summary>ROI を設定していない(ROI の解除)。</summary>
    public const string NoRoi = "ROI を設定していないため実行できません。";

    /// <summary>フルスクリーン中(左右パネルの表示切替。フルスクリーンではパネルを表示しない)。</summary>
    public const string Fullscreen =
        "フルスクリーン中はパネルを表示しないため切り替えられません。F11 か Esc でフルスクリーンを解除してから切り替えてください。";

    /// <summary>
    /// メインの画像・通常表示を対象にするコマンド(画像があり比較モードでないこと)を実行できない理由。
    /// </summary>
    /// <remarks>
    /// 比較モードなら画像の有無より先に示す(比較だけ使っていて通常表示に画像がないときに
    /// 「画像を開いていない」と出すと、比較ペインに画像があるのにと利用者を迷わせる)。
    /// </remarks>
    /// <param name="compareMode">比較モード中か。</param>
    /// <returns>理由の文言。</returns>
    internal static string ForMainView(bool compareMode) => compareMode ? CompareMode : NoImage;

    /// <summary>フレーム送り・再生(送れるフレームがあり比較モードでないこと)を実行できない理由。</summary>
    /// <param name="hasImage">通常表示に画像を開いているか。</param>
    /// <param name="compareMode">比較モード中か。</param>
    /// <returns>理由の文言。</returns>
    internal static string ForSequence(bool hasImage, bool compareMode) =>
        compareMode ? CompareMode : hasImage ? NoSequence : NoImage;

    /// <summary>ROI の解除(ROI があり比較モードでないこと)を実行できない理由。</summary>
    /// <param name="hasImage">通常表示に画像を開いているか。</param>
    /// <param name="compareMode">比較モード中か。</param>
    /// <returns>理由の文言。</returns>
    internal static string ForRoi(bool hasImage, bool compareMode) =>
        compareMode ? CompareMode : hasImage ? NoRoi : NoImage;
}

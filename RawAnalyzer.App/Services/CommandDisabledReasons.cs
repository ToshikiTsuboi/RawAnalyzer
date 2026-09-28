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

    /// <summary>比較モード中(ビニング・フィルタは比較表示の画像を処理しない)。</summary>
    public const string CompareMode =
        "比較モード中は実行できません。比較モードを終了して、通常の単画像表示で実行してください。";

    /// <summary>送れるフレーム・ページ・ファイルがない(フレーム送り・再生)。</summary>
    public const string NoSequence =
        "送れるフレームがないため実行できません。連番ファイル・マルチフレームの raw・複数ページTIFF を" +
        "表示しているときに使えます(HDR表示中・処理結果の表示中は送れません)。";

    /// <summary>ROI を設定していない(ROI の解除)。</summary>
    public const string NoRoi = "ROI を設定していないため実行できません。";
}

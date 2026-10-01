using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>
/// 表示中の画像の欠陥を補正できるか(できないときの理由と次にすること)の判定。
/// </summary>
/// <remarks>
/// 欠陥ウィンドウの「この欠陥を補正」を有効にするか(検出結果に理由を添える)と、補正の要求を断るかの両方で使う。
/// </remarks>
internal static class DefectCorrectionAvailability
{
    /// <summary>HDR素材を補正しない理由と次にすること。</summary>
    /// <remarks>
    /// 補正は Bayer の同色近傍(±2行・±2列)から補う。行交互HDR(Bayer の既定はライン単位2)では縦の近傍が
    /// 別の露光の行になり、長秒の画素を短秒の値で置き換えて新しい暗点を作る。結果は1フレーム・HDR方式なしに
    /// なり、以後 HDR 分割・合成もできなくなる(フレーム連結では表示中の露光だけが残る)。派生ビュー(分割・合成)
    /// は元の raw の座標・画素ではない。ビニング・フィルタと同じく、HDR素材は露光ごとに分割して単独の画像と
    /// して開いてから補正してもらう(Raw表示へ戻しても HDR 素材のままなので補正できない)。
    /// </remarks>
    internal const string HdrRefusal =
        "HDR素材は欠陥補正できません(補正の近傍に露光の違う行・段が混ざり、補正結果からHDRのレイアウトも失われます)。\n" +
        "HDR素材は先に露光ごとに分割し、単独の画像として開いてから補正してください。";

    /// <summary>カラー画像を補正しない理由。</summary>
    /// <remarks>
    /// 検出・補正は輝度の画像で行う。補正結果はカラーを持たないので、差し替えると RGB の画像が輝度だけの
    /// グレーの画像になり、そのまま保存するとグレーの画像が出力される(ビニング・フィルタは RGB の成分ごとに
    /// 処理してカラーを保つ)。一覧の移動・コピー・CSV 保存は使える。
    /// </remarks>
    internal const string ColorRefusal =
        "カラー画像(RGB)は欠陥補正できません(検出・補正は輝度で行うため、補正するとカラーが失われ、" +
        "輝度だけのグレーの画像に置き換わります)。一覧の移動・コピー・CSV保存は使えます。";

    /// <summary>補正できない理由を返す。</summary>
    /// <remarks>
    /// 比較モード中は、開いたままの欠陥ウィンドウから補正すると、比較画面に隠れた通常表示の画像を補正結果へ
    /// 差し替えてしまう(画像演算・ビニング・フィルタと同じく断る)。比較モードの理由を HDR・カラーより先に示す。
    /// </remarks>
    /// <param name="compareMode">比較モード中か。</param>
    /// <param name="currentFormat">表示中の元画像のフォーマット(右パネルでの変更を含む)。</param>
    /// <param name="derivedViewShown">HDR分割・合成の派生ビューを表示しているか。</param>
    /// <param name="colorImage">表示中の画像がカラー(RGB)か。</param>
    /// <returns>補正できない理由。補正できるならnull。</returns>
    internal static string? Refusal(
        bool compareMode, RawFormat currentFormat, bool derivedViewShown, bool colorImage)
    {
        if (compareMode)
        {
            return CommandDisabledReasons.CompareMode;
        }

        if (derivedViewShown || currentFormat.Hdr != HdrMode.None)
        {
            return HdrRefusal;
        }

        return colorImage ? ColorRefusal : null;
    }
}

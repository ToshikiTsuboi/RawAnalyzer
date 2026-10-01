using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>
/// Raw表示のHDR素材で、露光の違う行を1つの母集団として扱う解析を断る規約。
/// </summary>
/// <remarks>
/// 行交互HDRは1フレームの中に長秒と短秒の行が交互に並ぶ(Bayer の既定はライン単位2なので、同じ Bayer
/// チャネルにも両方の露光の行が入る)。画像全体・ROI の統計を1つの母集団として取ると、σ に露光差が乗る。
/// HDR分割ビュー(各露光の段を左右に並べた1枚)では段ごとに扱える。フレーム連結は1フレーム=1露光なので
/// Raw 表示でも混ざらない。
/// </remarks>
internal static class HdrExposureMix
{
    /// <summary>Raw表示で、1フレームの中に露光の違う行が並ぶ(行交互HDR)か。</summary>
    /// <remarks>
    /// レイアウトを決められない指定(Auto でフレーム数が1でも段数でもない)は分割もできないので、従来どおり扱う。
    /// </remarks>
    /// <param name="format">表示中の元画像のフォーマット。</param>
    /// <returns>行交互HDRならtrue。</returns>
    internal static bool InFrame(RawFormat format)
    {
        if (format.Hdr == HdrMode.None)
        {
            return false;
        }

        try
        {
            return HdrSplitter.ResolveLayout(format, format.HdrStages) == HdrMode.LineInterleaved;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>欠陥検出を断る理由を返す。</summary>
    /// <remarks>
    /// 検出はチャネル別の mean±Nσ を閾値にする。行交互HDRの Raw 表示では長秒と短秒の行が同じチャネルの
    /// 母集団に混ざり(露光比4のフラットで σ≈750 code)、閾値が値域の外へ出て欠陥を見逃す。分割ビューでは
    /// 段ごと・チャネルごとに閾値を求めて検出する。
    /// </remarks>
    /// <param name="currentFormat">表示中の元画像のフォーマット。</param>
    /// <param name="derivedViewShown">HDR分割・合成の派生ビューを表示しているか。</param>
    /// <returns>断る理由。検出できるならnull。</returns>
    internal static string? DefectDetectionRefusal(RawFormat currentFormat, bool derivedViewShown)
    {
        return !derivedViewShown && InFrame(currentFormat)
            ? "行交互HDRのrawは、Raw表示のままでは欠陥画素を検出できません" +
              "(長秒と短秒の行が同じチャネルの統計に混ざり、閾値が値域の外へ出て欠陥を見逃します)。\n" +
              "HDR分割ビュー(Ctrl+5)に切り替えて検出してください(段ごと・チャネルごとに閾値を求めます)。"
            : null;
    }
}

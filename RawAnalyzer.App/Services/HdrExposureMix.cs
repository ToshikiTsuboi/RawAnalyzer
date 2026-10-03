using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>
/// Raw表示のHDR素材で、露光の違う行を1つの母集団として扱う解析(ノイズ測定・欠陥検出・水平射影)を断る規約。
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

    /// <summary>ノイズ測定を断る理由を返す。</summary>
    /// <remarks>
    /// σ_total・σ_FPN・DR は測る画素の集合の空間分散から求める。行交互HDRの Raw 表示では、どの ROI にも
    /// 長秒と短秒の行が交互に入り、露光差(行ごとの段差)が分散に乗る(平均も2露光の中間になる)。
    /// 分割ビューでは段の中に置いた ROI なら1つの露光だけを測れるが、画像全体や段をまたぐ ROI は両方の露光を含む。
    /// 合成ビュー・フレーム連結の Raw 表示(1フレーム=1露光)は混ざらない。
    /// </remarks>
    /// <param name="lineInterleavedRawView">行交互HDRの raw を Raw 表示しているか(<see cref="InFrame"/>)。</param>
    /// <param name="splitSegmentWidth">HDR分割ビューの段の幅。分割ビューでなければ0。</param>
    /// <param name="target">測定する画素の集合。</param>
    /// <returns>断る理由。測定できるならnull。</returns>
    internal static string? NoiseRefusal(
        bool lineInterleavedRawView, int splitSegmentWidth, RoiAnalysisTarget target)
    {
        if (lineInterleavedRawView)
        {
            return "行交互HDRのrawは、Raw表示のままではノイズを測定できません" +
                "(どのROIにも長秒と短秒の行が交互に入り、露光差がσ_total・σ_FPN・DRに乗ります)。\n" +
                "HDR分割ビュー(Ctrl+5)に切り替え、測る段の中にROIを置いて「ROI内のみで測定」で測定してください。";
        }

        if (splitSegmentWidth <= 0)
        {
            return null;
        }

        (long first, long last) = target switch
        {
            SourceRoiTarget source => ((long)source.Roi.X, (long)source.Roi.X + source.Roi.Width - 1),
            ChannelRoiTarget channel => (
                (long)channel.Region.X, channel.Region.X + (2L * (channel.Region.Width - 1))),
            _ => (-1L, -1L),
        };
        if (first >= 0 && first / splitSegmentWidth == last / splitSegmentWidth)
        {
            return null;
        }

        return "HDR分割ビューでは、長秒と短秒の段をまたいだ範囲(画像全体を含む)のノイズは測定できません" +
            "(露光差がσ_total・σ_FPN・DRに乗ります)。\n" +
            "測る段の中にROIを置き、「ROI内のみで測定」で測定してください。";
    }

    /// <summary>水平射影を断る理由を返す。</summary>
    /// <remarks>
    /// 水平射影は各列を縦に平均する。行交互HDRの Raw 表示では、どの列(ROI・チャネル分割表示の象限の格子の列も)
    /// にも長秒と短秒の行が交互に入り、露光差が列ごとの平均・最大・最小に乗る(ノイズ測定と同じく、1つの露光の行
    /// だけに掛かる ROI かどうかは見ない)。垂直射影は各行が1つの露光なので断らない。分割ビュー(各段が1つの露光)と
    /// フレーム連結の Raw 表示(1フレーム=1露光)は混ざらない。
    /// </remarks>
    /// <param name="lineInterleavedRawView">行交互HDRの raw を Raw 表示しているか(<see cref="InFrame"/>)。</param>
    /// <returns>断る理由。射影できるならnull。</returns>
    internal static string? HorizontalProjectionRefusal(bool lineInterleavedRawView)
    {
        return lineInterleavedRawView
            ? "行交互HDRのrawは、Raw表示のままでは水平射影を取れません" +
              "(どの列にも長秒と短秒の行が交互に入り、露光差が列ごとの平均・最大・最小に乗ります)。\n" +
              "HDR分割ビュー(Ctrl+5)にすると露光ごとに射影できます。垂直射影は各行が1つの露光なのでそのまま取れます。"
            : null;
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

using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>
/// ROI解析で実際に集計する画素の集合。表示中のROI(表示座標)を元画像へ対応づけた結果。
/// </summary>
internal abstract record RoiAnalysisTarget;

/// <summary>ROIなし(画像全体)。</summary>
internal sealed record WholeImageTarget : RoiAnalysisTarget;

/// <summary>元画像上の矩形(表示座標=元画像座標の表示モード)。</summary>
/// <param name="Roi">画像範囲へクランプ済みの矩形。</param>
internal sealed record SourceRoiTarget(RegionOfInterest Roi) : RoiAnalysisTarget;

/// <summary>チャネル分割表示の1象限内のROI。元画像ではそのチャネルだけの格子になる。</summary>
/// <param name="Region">元画像上の格子領域。</param>
/// <param name="Channel">領域のチャネル。</param>
/// <param name="DisplayRoi">表示されている範囲へクランプした表示座標の矩形。</param>
internal sealed record ChannelRoiTarget(
    ChannelRegion Region, BayerChannel Channel, RegionOfInterest DisplayRoi) : RoiAnalysisTarget;

/// <summary>表示中の画素と1対1に対応づけられないROI。解析せず理由を示す。</summary>
/// <param name="Reason">利用者向けの理由。</param>
internal sealed record UnsupportedRoiTarget(string Reason) : RoiAnalysisTarget;

/// <summary>ROIのヒストグラム解析結果。</summary>
/// <param name="Histogram">集計対象のヒストグラム(指標の元)。</param>
/// <param name="Channels">Bayerチャネル別の内訳(作らない場合はnull)。</param>
/// <param name="RoiStatistics">ROIの統計(ROIなしならnull)。</param>
internal sealed record RoiHistogram(
    HistogramResult Histogram,
    IReadOnlyList<ChannelHistogram>? Channels,
    RegionStatistics? RoiStatistics);

/// <summary>
/// 表示中のROIを「表示されている画素」の集合へ対応づけ、その集合を解析する。
/// </summary>
/// <remarks>
/// ROIは表示座標で選ばれる。チャネル分割表示では表示座標がR/Gr/Gb/Bを2x2に並べた
/// タイル画像の座標なので、そのまま元画像の矩形として解析すると、表示されている画素とは
/// 別の(他チャネルを含む)画素を集計してしまう。1つの象限内のROIはそのチャネルの格子へ写像し、
/// 対応づけられないROI(象限をまたぐ等)は解析せずに断る。HDR分割ビューで露光の段をまたぐROIも断る。
/// </remarks>
internal static class RoiAnalysis
{
    /// <summary>
    /// 表示中のROIを、解析で集計する画素の集合へ対応づける。
    /// </summary>
    /// <param name="displayRoi">表示座標のROI(なければnull)。</param>
    /// <param name="channelSplitLayout">チャネル分割のタイル表示中か(表示座標=タイル座標)。</param>
    /// <param name="imageWidth">元画像の幅。</param>
    /// <param name="imageHeight">元画像の高さ。</param>
    /// <param name="pattern">Bayerパターン(チャネル名の決定に使う)。</param>
    /// <param name="splitSegmentWidth">
    /// HDR分割ビューの段の幅(各露光の段を左右に並べた画像)。分割ビューでなければ0。段をまたぐROIは断る
    /// (長秒と短秒の画素を1つの母集団として集計すると、露光差が平均・σ・射影に乗る)。
    /// </param>
    /// <returns>集計対象。</returns>
    internal static RoiAnalysisTarget Resolve(
        RegionOfInterest? displayRoi, bool channelSplitLayout,
        int imageWidth, int imageHeight, BayerPattern pattern, int splitSegmentWidth = 0)
    {
        if (displayRoi is not { } roi)
        {
            return new WholeImageTarget();
        }

        if (!channelSplitLayout)
        {
            RegionOfInterest clamped = roi.Clamp(imageWidth, imageHeight);
            if (clamped.PixelCount == 0)
            {
                return new UnsupportedRoiTarget("ROIが画像の範囲外です");
            }

            return SpansSegments(clamped.X, clamped.X + clamped.Width - 1L, splitSegmentWidth)
                ? new UnsupportedRoiTarget(AcrossSegmentsReason)
                : new SourceRoiTarget(clamped);
        }

        // タイル表示は奇数寸法の端の行・列を並べない(表示されていない画素は集計しない)
        int tiledWidth = imageWidth & ~1;
        int tiledHeight = imageHeight & ~1;
        RegionOfInterest shown = roi.Clamp(tiledWidth, tiledHeight);
        if (shown.PixelCount == 0)
        {
            return new UnsupportedRoiTarget("ROIがチャネル分割表示の画像の範囲外です");
        }

        if (!BayerSplit.TryMapTiledRegion(shown, tiledWidth, tiledHeight, out ChannelRegion region))
        {
            return new UnsupportedRoiTarget(
                "ROIが象限(チャネル)をまたいでいるため解析できません。\n" +
                "チャネル分割表示では1つの象限の中で選択してください");
        }

        // 象限の中のROIでも、元画像(分割ビューの並置画像)では1列おきの列にまたがる
        if (SpansSegments(region.X, region.X + (2L * (region.Width - 1)), splitSegmentWidth))
        {
            return new UnsupportedRoiTarget(AcrossSegmentsReason);
        }

        return new ChannelRoiTarget(
            region, BayerHelper.GetChannel(pattern, region.X, region.Y), shown);
    }

    private const string AcrossSegmentsReason =
        "ROIがHDR分割ビューの段(露光)をまたいでいるため解析できません" +
        "(長秒と短秒の画素を1つの集合として集計すると、露光差が平均・σ・射影に乗ります)。\n" +
        "1つの段の中で選択してください";

    /// <summary>元画像の列 first〜last が、HDR分割ビューの2つ以上の段にまたがるか。</summary>
    /// <param name="first">最初の列。</param>
    /// <param name="last">最後の列。</param>
    /// <param name="splitSegmentWidth">段の幅(分割ビューでなければ0)。</param>
    /// <returns>またがるならtrue。</returns>
    private static bool SpansSegments(long first, long last, int splitSegmentWidth)
    {
        return splitSegmentWidth > 0 && first / splitSegmentWidth != last / splitSegmentWidth;
    }

    /// <summary>対象が解析できるROIか(ROIなし・対応づけ不能はfalse)。</summary>
    /// <param name="target">集計対象。</param>
    /// <returns>ROIとして解析できればtrue。</returns>
    internal static bool IsAnalyzableRoi(RoiAnalysisTarget target)
    {
        return target is SourceRoiTarget or ChannelRoiTarget;
    }

    /// <summary>
    /// ヒストグラムの集計の対象とした画素数(間引く前)。ROIなしは1フレームの全画素、対応づけ不能は0。
    /// ヒストグラムの表(CSV・コピー)に、count が間引いたサンプル数であることを書くのに使う。
    /// </summary>
    /// <param name="target">集計対象。</param>
    /// <param name="imageWidth">画像の幅。</param>
    /// <param name="imageHeight">画像の高さ。</param>
    /// <returns>画素数。</returns>
    internal static long PopulationCount(RoiAnalysisTarget target, int imageWidth, int imageHeight)
    {
        return target is WholeImageTarget ? (long)imageWidth * imageHeight : PixelCount(target);
    }

    /// <summary>ROIとして集計する画素数(ROIなし・対応づけ不能は0)。</summary>
    /// <param name="target">集計対象。</param>
    /// <returns>画素数。</returns>
    internal static long PixelCount(RoiAnalysisTarget target)
    {
        return target switch
        {
            SourceRoiTarget source => source.Roi.PixelCount,
            ChannelRoiTarget channel => channel.Region.PixelCount,
            _ => 0,
        };
    }

    /// <summary>
    /// 集計対象のヒストグラム・指標用統計・ROI統計を計算する(重い処理。UIスレッド外で呼ぶ)。
    /// </summary>
    /// <param name="image">対象画像。</param>
    /// <param name="frame">フレーム番号。</param>
    /// <param name="target">集計対象。</param>
    /// <param name="pattern">Bayerパターン。</param>
    /// <param name="byChannel">Bayerチャネル別の内訳も作るか。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>解析結果。</returns>
    /// <exception cref="InvalidOperationException">対応づけできないROIの場合(別の画素で代用しない)。</exception>
    internal static RoiHistogram ComputeHistogram(
        RawImage image, int frame, RoiAnalysisTarget target, BayerPattern pattern,
        bool byChannel, CancellationToken cancellationToken)
    {
        switch (target)
        {
            case ChannelRoiTarget channel:
            {
                // 1チャネルだけの格子なので内訳は作らない(全体=そのチャネル)
                HistogramResult histogram = ChannelRegionAnalysis.ComputeHistogram(
                    image, frame, channel.Region, cancellationToken: cancellationToken);
                return new RoiHistogram(histogram, null, histogram.Statistics);
            }

            case SourceRoiTarget source:
            {
                (HistogramResult histogram, IReadOnlyList<ChannelHistogram>? channels) =
                    ComputeSourceHistogram(
                        image, frame, source.Roi, pattern, byChannel, cancellationToken);

                // ヒストグラムと同じ基準で間引く。全面ROIの10億画素で
                // 2GBを毎回読み直すのを避ける(厳密値かはSampleCountで判別できる)
                RegionStatistics statistics = ImageAnalysis.ComputeStatistics(
                    image, frame, source.Roi, ImageAnalysis.DefaultMaxHistogramSamples,
                    cancellationToken);
                return new RoiHistogram(histogram, channels, statistics);
            }

            case WholeImageTarget:
            {
                (HistogramResult histogram, IReadOnlyList<ChannelHistogram>? channels) =
                    ComputeSourceHistogram(image, frame, null, pattern, byChannel, cancellationToken);
                return new RoiHistogram(histogram, channels, null);
            }

            default:
                throw new InvalidOperationException(ReasonOf(target));
        }
    }

    /// <summary>
    /// 集計対象のノイズを測定する(重い処理。UIスレッド外で呼ぶ)。
    /// </summary>
    /// <param name="image">対象画像(A)。</param>
    /// <param name="reference">2枚目(B、フレーム0を使う)。単一測定ならnull。</param>
    /// <param name="frame">Aのフレーム番号。</param>
    /// <param name="target">集計対象。</param>
    /// <param name="pattern">Bayerパターン(矩形・画像全体ではチャネル別に空間統計を取る)。</param>
    /// <param name="saturationCode">飽和信号レベル(raw code)。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>測定結果。</returns>
    /// <exception cref="InvalidOperationException">対応づけできないROIの場合(別の画素で代用しない)。</exception>
    internal static NoiseMeasurement MeasureNoise(
        RawImage image, RawImage? reference, int frame, RoiAnalysisTarget target,
        BayerPattern pattern, double saturationCode, CancellationToken cancellationToken)
    {
        if (target is ChannelRoiTarget channel)
        {
            return reference is null
                ? ChannelRegionAnalysis.MeasureSingle(
                    image, frame, channel.Region, saturationCode, cancellationToken)
                : ChannelRegionAnalysis.MeasurePair(
                    image, reference, frame, 0, channel.Region, saturationCode, cancellationToken);
        }

        RegionOfInterest? roi = target switch
        {
            SourceRoiTarget source => source.Roi,
            WholeImageTarget => null,
            _ => throw new InvalidOperationException(ReasonOf(target)),
        };
        return reference is null
            ? NoiseAnalysis.MeasureSingle(
                image, frame, roi, pattern, saturationCode, cancellationToken)
            : NoiseAnalysis.MeasurePair(
                image, reference, frame, 0, roi, pattern, saturationCode, cancellationToken);
    }

    private static (HistogramResult Histogram, IReadOnlyList<ChannelHistogram>? Channels)
        ComputeSourceHistogram(
            RawImage image, int frame, RegionOfInterest? region, BayerPattern pattern,
            bool byChannel, CancellationToken cancellationToken)
    {
        if (byChannel && pattern != BayerPattern.None)
        {
            ChannelAnalysisResult analysis = ImageAnalysis.ComputeChannelAnalysis(
                image, frame, pattern, region, cancellationToken: cancellationToken);
            return (analysis.Total, analysis.Channels);
        }

        return (ImageAnalysis.ComputeHistogram(
            image, frame, region, cancellationToken: cancellationToken), null);
    }

    private static string ReasonOf(RoiAnalysisTarget target)
    {
        return target is UnsupportedRoiTarget unsupported
            ? unsupported.Reason
            : "解析できないROIです";
    }
}

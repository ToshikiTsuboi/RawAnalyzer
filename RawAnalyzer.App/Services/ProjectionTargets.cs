using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>
/// 射影の窓に出す1方向の射影の求め方(表示中の画像・フレーム・対象)と、窓の上部に出す対象の説明。
/// </summary>
/// <remarks>
/// 画像は参照で、対象は値で比べる(<see cref="RawImage"/> は作った後に画素が変わらない)。窓が出している
/// (計算中の)求め方と同じなら計算し直さない(<see cref="ProjectionRefreshPlan"/>)。
/// </remarks>
/// <param name="Direction">向き。</param>
/// <param name="Image">表示中の画像(HDR表示中は派生ビューの画像)。</param>
/// <param name="Frame">表示中のフレーム。</param>
/// <param name="Target">対象(断るときは <see cref="UnsupportedRoiTarget"/>)。</param>
/// <param name="Header">窓の上部に出す対象の説明(「対象: ROI (x, y, w×h)」など)。</param>
/// <param name="MaxCode">表示中の画像のビット深度の最大 raw code。</param>
internal sealed record ProjectionRequest(
    ProjectionDirection Direction, RawImage Image, int Frame, RoiAnalysisTarget Target, string Header, int MaxCode)
{
    /// <summary>射影を求められるか(断る対象・対応づけ不能なら false)。</summary>
    internal bool IsComputable => ProjectionTargets.IsComputable(Target);

    /// <summary>断る理由(求められるなら null)。</summary>
    internal string? RefusalReason => Target is UnsupportedRoiTarget unsupported ? unsupported.Reason : null;
}

/// <summary>射影の横軸の座標(表示する座標と、チャネル分割表示なら元画像の座標)。</summary>
/// <param name="Origin">射影の先頭の位置の横軸の座標(ROI を描いた表示座標)。</param>
/// <param name="SourceOrigin">
/// チャネル分割表示の射影なら、先頭の位置の元画像の座標(1チャネルの格子なので以降は2画素おき)。それ以外は null。
/// </param>
internal readonly record struct ProjectionAxis(long Origin, long? SourceOrigin)
{
    /// <summary>横軸がチャネル分割表示の座標か(元画像の列・行ではない)。</summary>
    internal bool IsSplitDisplay => SourceOrigin is not null;

    /// <summary>位置 i の元画像の座標(チャネル分割表示でなければ表示の座標と同じ)。</summary>
    /// <param name="index">射影の位置。</param>
    /// <returns>元画像の座標。</returns>
    internal long SourceCoordinate(int index) => SourceOrigin is { } origin ? origin + (2L * index) : Origin + index;
}

/// <summary>
/// 射影の対象の決め方と断る理由。ROI があれば ROI、なければ画像全体。
/// </summary>
/// <remarks>
/// <para>
/// ROI の扱いはヒストグラム・ROI 統計と同じ(<see cref="RoiAnalysis.Resolve"/>): チャネル分割表示では1つの象限の中の
/// ROI をそのチャネルの格子へ写し、象限をまたぐ ROI と、HDR 分割ビューで段(露光)をまたぐ ROI は断る。
/// </para>
/// <para>
/// ROI がないときは画像全体。ただし、チャネル分割表示では4チャネルのどれかを勝手に選ばず、象限を ROI で囲むよう
/// 案内する。HDR 分割ビュー(各露光の段を左右に並べた1枚)では、画像全体の水平射影は各列が1つの段に収まるので
/// 求めるが、垂直射影は各行が段をまたいで露光の違う画素を1つの平均にするので断る(段をまたぐ ROI を断る規則、
/// 分割ビューの画像全体のノイズ測定を断る規則(<see cref="HdrExposureMix.NoiseRefusal"/>)と同じ考え方)。
/// </para>
/// </remarks>
internal static class ProjectionTargets
{
    /// <summary>チャネル分割表示で ROI がないときの案内。</summary>
    internal const string ChannelSplitWithoutRoi =
        "チャネル分割表示では、射影を取るチャネルの象限を ROI で囲んでください" +
        "(象限全体を囲むとそのチャネル全体になります)。";

    /// <summary>HDR 分割ビューで画像全体の垂直射影を断る理由。</summary>
    internal const string HdrSplitWholeImageVertical =
        "HDR分割ビューでは、画像全体の垂直射影は取れません" +
        "(各行が長秒と短秒の段をまたぎ、露光差が行ごとの平均に乗ります)。\n" +
        "段の中に ROI を置いてください(段全体を囲むとその段の垂直射影になります)。" +
        "画像全体の水平射影は、各列が1つの段に収まるので取れます。";

    /// <summary>再生中に計算しないことの知らせ。</summary>
    internal const string Playback = "再生中は計算しません。停止すると、表示中のフレームで計算し直します。";

    /// <summary>対象を決める。</summary>
    /// <param name="direction">向き。</param>
    /// <param name="displayRoi">表示座標の ROI(なければ null。画素数0は ROI なしとみなす)。</param>
    /// <param name="channelSplitLayout">チャネル分割のタイル表示中か(表示座標 = タイル座標)。</param>
    /// <param name="imageWidth">表示中の画像の幅。</param>
    /// <param name="imageHeight">表示中の画像の高さ。</param>
    /// <param name="pattern">Bayer パターン(チャネル名に使う)。</param>
    /// <param name="splitSegmentWidth">HDR 分割ビューの段の幅(分割ビューでなければ0)。</param>
    /// <returns>対象。断るときは理由付きの <see cref="UnsupportedRoiTarget"/>。</returns>
    internal static RoiAnalysisTarget Resolve(
        ProjectionDirection direction, RegionOfInterest? displayRoi, bool channelSplitLayout,
        int imageWidth, int imageHeight, BayerPattern pattern, int splitSegmentWidth)
    {
        if (displayRoi is { PixelCount: > 0 } roi)
        {
            return RoiAnalysis.Resolve(roi, channelSplitLayout, imageWidth, imageHeight, pattern, splitSegmentWidth);
        }

        if (channelSplitLayout)
        {
            return new UnsupportedRoiTarget(ChannelSplitWithoutRoi);
        }

        if (splitSegmentWidth > 0 && direction == ProjectionDirection.Vertical && imageWidth > splitSegmentWidth)
        {
            return new UnsupportedRoiTarget(HdrSplitWholeImageVertical);
        }

        return new WholeImageTarget();
    }

    /// <summary>射影を求められる対象か。</summary>
    /// <param name="target">対象。</param>
    /// <returns>求められれば true。</returns>
    internal static bool IsComputable(RoiAnalysisTarget target) =>
        target is WholeImageTarget or SourceRoiTarget or ChannelRoiTarget;

    /// <summary>横軸の座標を決める。</summary>
    /// <param name="target">対象(求められるもの)。</param>
    /// <param name="direction">向き。</param>
    /// <returns>横軸の座標。</returns>
    internal static ProjectionAxis AxisOf(RoiAnalysisTarget target, ProjectionDirection direction)
    {
        bool horizontal = direction == ProjectionDirection.Horizontal;
        return target switch
        {
            SourceRoiTarget source => new ProjectionAxis(horizontal ? source.Roi.X : source.Roi.Y, null),

            // チャネル分割表示の射影は ROI を描いた分割表示(タイル)の座標に並べ、元画像の座標も持つ
            ChannelRoiTarget channel => new ProjectionAxis(
                horizontal ? channel.DisplayRoi.X : channel.DisplayRoi.Y,
                horizontal ? channel.Region.X : channel.Region.Y),
            _ => new ProjectionAxis(0, null),
        };
    }

    /// <summary>窓の上部に出す対象の説明を作る。</summary>
    /// <param name="displayRoi">表示座標の ROI(なければ null)。</param>
    /// <param name="target">対象。</param>
    /// <param name="imageWidth">表示中の画像の幅。</param>
    /// <param name="imageHeight">表示中の画像の高さ。</param>
    /// <param name="sourceNote">表示中の画像の説明(フレーム・ページ・HDR 表示。<see cref="SourceNote"/>)。</param>
    /// <returns>「対象: ROI (x, y, w×h) · チャネル Gr · フレーム 2/5」など。</returns>
    internal static string Describe(
        RegionOfInterest? displayRoi, RoiAnalysisTarget target, int imageWidth, int imageHeight, string sourceNote)
    {
        string subject = target switch
        {
            WholeImageTarget => $"画像全体 ({imageWidth}×{imageHeight})",
            SourceRoiTarget source => $"ROI ({Rect(source.Roi)})",
            ChannelRoiTarget channel =>
                $"ROI ({Rect(channel.DisplayRoi)}・チャネル分割表示の座標) · チャネル {BayerHelper.GetLabel(channel.Channel)}",
            _ when displayRoi is { PixelCount: > 0 } roi => $"ROI ({Rect(roi)})",
            _ => "ROI なし",
        };
        return $"対象: {subject}" + (sourceNote.Length > 0 ? $" · {sourceNote}" : "");
    }

    /// <summary>表示中の画像の説明(HDR 表示・ページ・フレーム)を作る。</summary>
    /// <param name="frame">表示中のフレーム(HDR 表示中は元にしたフレーム)。</param>
    /// <param name="frameCount">元画像のフレーム数。</param>
    /// <param name="tiffPage">複数ページ TIFF の表示中のページ(0始まり)。</param>
    /// <param name="tiffPageCount">複数ページ TIFF のページ数(TIFF のスタックでなければ0)。</param>
    /// <param name="hdrView">HDR 表示の名前(「HDR分割ビュー」など。HDR 表示でなければ null)。</param>
    /// <returns>説明(なければ空)。</returns>
    internal static string SourceNote(int frame, int frameCount, int tiffPage, int tiffPageCount, string? hdrView)
    {
        var parts = new List<string>();
        if (hdrView is not null)
        {
            parts.Add(hdrView);
        }

        if (tiffPageCount > 1)
        {
            parts.Add($"ページ {tiffPage + 1}/{tiffPageCount}");
        }
        else if (frameCount > 1)
        {
            parts.Add($"フレーム {frame + 1}/{frameCount}");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// 射影を求める(重い処理。UI スレッドの外で呼ぶ)。
    /// </summary>
    /// <param name="image">対象画像。</param>
    /// <param name="frame">フレーム番号。</param>
    /// <param name="target">対象(求められるもの)。</param>
    /// <param name="axes">求める向き。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <param name="progress">進み具合(0〜1)。</param>
    /// <returns>射影。</returns>
    /// <exception cref="InvalidOperationException">求められない対象の場合(別の画素で代用しない)。</exception>
    internal static ProjectionResult Compute(
        RawImage image, int frame, RoiAnalysisTarget target, ProjectionAxes axes,
        CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        return target switch
        {
            WholeImageTarget => ProjectionAnalysis.ComputeWholeImage(image, frame, axes, cancellationToken, progress),
            SourceRoiTarget source => ProjectionAnalysis.Compute(
                image, frame, source.Roi, axes, cancellationToken, progress),
            ChannelRoiTarget channel => ProjectionAnalysis.Compute(
                image, frame, channel.Region, axes, cancellationToken, progress),
            UnsupportedRoiTarget unsupported => throw new InvalidOperationException(unsupported.Reason),
            _ => throw new InvalidOperationException("射影を取れない対象です"),
        };
    }

    /// <summary>向きを Core の向きへ写す。</summary>
    /// <param name="direction">向き。</param>
    /// <returns>Core の向き。</returns>
    internal static ProjectionAxes ToAxes(ProjectionDirection direction) =>
        direction == ProjectionDirection.Horizontal ? ProjectionAxes.Horizontal : ProjectionAxes.Vertical;

    private static string Rect(RegionOfInterest roi) => $"{roi.X}, {roi.Y}, {roi.Width}×{roi.Height}";
}

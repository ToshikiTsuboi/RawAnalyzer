using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>
/// ラインプロファイル窓に出す値。基準点を通る行・列の断面(raw code)と、ROIの水平・垂直射影。
/// </summary>
/// <remarks>
/// 窓はクリックしたときだけでなく、フレーム・ページ・ファイルを送った後や表示画像を差し替えた後
/// (別ファイルを開く・処理結果・HDR表示の出入り)にも、同じ基準点(元画像の座標)と送った後のROIで
/// 計算し直す。寸法の違う画像では基準点が範囲外になり得るので、そのときは読まずに null を返す
/// (窓は前の断面を残さず、範囲外であることを示す)。
/// </remarks>
/// <param name="Row">基準点の行の水平プロファイル。</param>
/// <param name="Column">基準点の列の垂直プロファイル。</param>
/// <param name="HorizontalProjection">ROIの水平射影(ROIなし・対応づけ不能なら空)。</param>
/// <param name="VerticalProjection">ROIの垂直射影(ROIなし・対応づけ不能なら空)。</param>
internal sealed record LineProfileData(
    double[] Row, double[] Column, double[] HorizontalProjection, double[] VerticalProjection)
{
    /// <summary>
    /// 基準点を通る行・列の断面と、ROIの射影を計算する(重い処理。UIスレッド外で呼ぶ)。
    /// </summary>
    /// <param name="image">表示中の画像。</param>
    /// <param name="frame">表示中のフレーム。</param>
    /// <param name="x">基準点の元画像X座標。</param>
    /// <param name="y">基準点の元画像Y座標。</param>
    /// <param name="target">射影を求めるROI(表示中のROIを対応づけたもの)。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>計算結果。基準点が画像の範囲外なら null。</returns>
    internal static LineProfileData? Compute(
        RawImage image, int frame, int x, int y, RoiAnalysisTarget target,
        CancellationToken cancellationToken)
    {
        if ((uint)x >= (uint)image.Width || (uint)y >= (uint)image.Height)
        {
            return null;
        }

        double[] row = Array.ConvertAll(
            ImageAnalysis.ExtractRowProfile(image, frame, y), v => (double)v);
        double[] column = Array.ConvertAll(
            ImageAnalysis.ExtractColumnProfile(image, frame, x, cancellationToken), v => (double)v);
        (double[] horizontal, double[] vertical) =
            RoiAnalysis.ComputeProjections(image, frame, target, cancellationToken);
        return new LineProfileData(row, column, horizontal, vertical);
    }
}

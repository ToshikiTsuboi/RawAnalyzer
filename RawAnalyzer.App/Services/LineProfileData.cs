using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>
/// ラインプロファイル窓に出す値。基準点を通る行・列の断面(raw code)。
/// </summary>
/// <remarks>
/// 窓はクリックしたときだけでなく、フレーム・ページ・ファイルを送った後や表示画像を差し替えた後
/// (別ファイルを開く・処理結果・HDR表示の出入り)にも、同じ基準点(元画像の座標)で計算し直す。
/// 寸法の違う画像では基準点が範囲外になり得るので、そのときは読まずに null を返す
/// (窓は前の断面を残さず、範囲外であることを示す)。ROI の射影は射影の窓(<see cref="ProjectionTargets"/>)が出す。
/// </remarks>
/// <param name="Row">基準点の行の水平プロファイル。</param>
/// <param name="Column">基準点の列の垂直プロファイル。</param>
internal sealed record LineProfileData(double[] Row, double[] Column)
{
    /// <summary>
    /// 基準点を通る行・列の断面を計算する(重い処理。UIスレッド外で呼ぶ)。
    /// </summary>
    /// <param name="image">表示中の画像。</param>
    /// <param name="frame">表示中のフレーム。</param>
    /// <param name="x">基準点の元画像X座標。</param>
    /// <param name="y">基準点の元画像Y座標。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>計算結果。基準点が画像の範囲外なら null。</returns>
    internal static LineProfileData? Compute(
        RawImage image, int frame, int x, int y, CancellationToken cancellationToken)
    {
        if ((uint)x >= (uint)image.Width || (uint)y >= (uint)image.Height)
        {
            return null;
        }

        double[] row = Array.ConvertAll(
            ImageAnalysis.ExtractRowProfile(image, frame, y), v => (double)v);
        double[] column = Array.ConvertAll(
            ImageAnalysis.ExtractColumnProfile(image, frame, x, cancellationToken), v => (double)v);
        return new LineProfileData(row, column);
    }
}

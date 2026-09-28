using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>
/// 非同期の解析(ヒストグラム・ROI統計・ラインプロファイル・射影)を始めたときの対象(画像とフレーム)。
/// 結果を表示する時点で、いまも表示中の画像・フレームか確かめる。
/// </summary>
/// <remarks>
/// マルチフレームのフレーム送りでは画像はそのままでフレームだけが替わる。画像の照合だけで採用すると、
/// 送る前のフレームで計算した結果を、送った後のフレームの測定値として表示してしまう。
/// 欠陥検出はBayerパターンも前提にするので <see cref="DefectDetectionSource"/> で確かめる。
/// </remarks>
/// <param name="Image">解析した画像(HDR表示中は派生ビューの画像)。</param>
/// <param name="Frame">解析したフレーム。</param>
internal sealed record AnalysisSource(RawImage Image, int Frame)
{
    /// <summary>解析の結果が、いま表示中の画像・フレームのものか判定する。</summary>
    /// <param name="activeImage">表示中の画像。</param>
    /// <param name="viewportFrame">表示中のフレーム。</param>
    /// <returns>解析を始めたときと同じならtrue。</returns>
    internal bool IsCurrent(RawImage? activeImage, int viewportFrame)
    {
        return ReferenceEquals(Image, activeImage) && Frame == viewportFrame;
    }
}

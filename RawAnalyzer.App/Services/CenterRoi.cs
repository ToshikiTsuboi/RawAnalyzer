using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>
/// 「中央に ROI を設定」(Ctrl+Shift+G)で置く ROI。画像の中央に、幅・高さが半分(面積 1/4)の ROI を作る。
/// </summary>
/// <remarks>
/// HDR分割ビューは各段(長秒・中秒・短秒)を横に並べた画像なので、画像全体の中央は段の継ぎ目
/// (2段なら W/2、3段なら W/3 と 2W/3)になる。以前は継ぎ目をまたぐ ROI を作り、露光の違う画素を混ぜた
/// 平均・σを ROI 統計・ヒストグラムに出していた。分割ビューでは調整対象の段(「全体」なら長秒)を
/// 1枚の画像とみなし、その中央に置く。
/// </remarks>
internal static class CenterRoi
{
    /// <summary>ROI を計算する。</summary>
    /// <param name="imageWidth">表示中の画像の幅(HDR分割ビューでは段を並べた幅)。</param>
    /// <param name="imageHeight">表示中の画像の高さ。</param>
    /// <param name="segmentWidth">HDR分割ビューの1段の幅。分割ビューでなければ 0。</param>
    /// <param name="stage">ROI を置く段(0 始まり。<see cref="StageForTarget"/>)。分割ビューでなければ無視する。</param>
    /// <returns>中央の ROI(幅・高さは 1 以上)。</returns>
    internal static RegionOfInterest Compute(int imageWidth, int imageHeight, int segmentWidth = 0, int stage = 0)
    {
        int left = 0;
        int areaWidth = imageWidth;
        if (segmentWidth > 0 && segmentWidth <= imageWidth)
        {
            int stages = Math.Max(1, imageWidth / segmentWidth);
            left = Math.Clamp(stage, 0, stages - 1) * segmentWidth;
            areaWidth = segmentWidth;
        }

        int width = Math.Max(1, areaWidth / 2);
        int height = Math.Max(1, imageHeight / 2);
        return new RegionOfInterest(left + (areaWidth - width) / 2, (imageHeight - height) / 2, width, height);
    }

    /// <summary>
    /// HDR分割ビューの調整対象(右パネルの「調整対象」の選択)から、ROI を置く段を決める。
    /// </summary>
    /// <param name="targetIndex">調整対象の項目番号(0=全体、1=長秒、2 以降=中秒・短秒。未選択は -1)。</param>
    /// <param name="stageCount">段数。</param>
    /// <returns>段(0 始まり)。「全体」・未選択は長秒(0)。</returns>
    internal static int StageForTarget(int targetIndex, int stageCount) =>
        Math.Clamp(targetIndex - 1, 0, Math.Max(0, stageCount - 1));
}

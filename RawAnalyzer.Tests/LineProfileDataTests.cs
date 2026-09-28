using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// ラインプロファイル窓に出す断面・射影の計算。窓はクリックしたときだけでなく、フレーム・ページ・ファイルを
/// 送った後や表示画像を差し替えた後にも、同じ基準点(元画像の座標)でこの計算をし直す。
/// </summary>
public class LineProfileDataTests
{
    [Fact]
    public void SamePointAfterFrameMove_ReadsDisplayedFrameAndRoi()
    {
        // 4×3×2フレームの12bit。frame0 は全画素100、frame1 は 1000+10y+x。frame0 でクリックした基準点(2,1)を
        // frame1 へ送った後に計算し直すと、frame1 の行・列と、送った後のROI(x=1〜2 の全行)の射影になる
        var format = new RawFormat { Width = 4, Height = 3, BitDepth = 12, FrameCount = 2 };
        ushort[] frame0 = Enumerable.Repeat((ushort)100, 12).ToArray();
        ushort[] frame1 = Enumerable.Range(0, 12).Select(i => (ushort)(1000 + (10 * (i / 4)) + (i % 4))).ToArray();
        using RawImage image = TestImages.FromCodes(frame0.Concat(frame1).ToArray(), format);

        LineProfileData? data = LineProfileData.Compute(
            image, 1, 2, 1, new SourceRoiTarget(new RegionOfInterest(1, 0, 2, 3)), CancellationToken.None);

        Assert.NotNull(data);
        Assert.Equal(new double[] { 1010, 1011, 1012, 1013 }, data.Row);
        Assert.Equal(new double[] { 1002, 1012, 1022 }, data.Column);
        Assert.Equal(new double[] { 1011, 1012 }, data.HorizontalProjection);
        Assert.Equal(new double[] { 1001.5, 1011.5, 1021.5 }, data.VerticalProjection);
    }

    [Fact]
    public void PointOutsideDisplayedImage_ReturnsNullInsteadOfThrowing()
    {
        // 寸法の違う画像(TIFFのページ・別のファイル・ビニングの結果)では、前の画像でクリックした基準点が
        // 範囲外になり得る。範囲を確かめずに読むと ArgumentOutOfRangeException になり、例外を捨てる
        // 呼び出し側では窓に前の画像の断面が残る。範囲外は null を返し、窓で範囲外であることを示させる
        using RawImage image = TestImages.FromCodes(new ushort[6], 3, 2);
        var whole = new WholeImageTarget();

        Assert.Null(LineProfileData.Compute(image, 0, 3, 1, whole, CancellationToken.None));
        Assert.Null(LineProfileData.Compute(image, 0, 2, 2, whole, CancellationToken.None));
        Assert.Null(LineProfileData.Compute(image, 0, -1, 0, whole, CancellationToken.None));
        Assert.NotNull(LineProfileData.Compute(image, 0, 2, 1, whole, CancellationToken.None));
    }
}

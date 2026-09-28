using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 非同期の解析(ヒストグラム・ROI統計・ラインプロファイル・射影)の結果を、表示中の画像・フレームの
/// ものとして採用してよいかの判定。
/// </summary>
/// <remarks>
/// MainWindow はフレーム送りで進行中の解析を取り消し、結果の採用時にもこの判定で照合する。
/// </remarks>
public class AnalysisSourceTests
{
    [Fact]
    public void FrameMovedDuringAnalysis_ResultIsNoLongerCurrent()
    {
        // レビューの再現。4×4×2フレーム、frame0 は全画素100、frame1 は全画素200。frame0 で求めたプロファイルが
        // frame1 へ送った後に届くと、画像は同じなので画像の照合だけでは frame0 の値(100)を frame1 の
        // プロファイルとして表示していた
        var format = new RawFormat { Width = 4, Height = 4, FrameCount = 2 };
        ushort[] codes = Enumerable.Repeat((ushort)100, 16).Concat(Enumerable.Repeat((ushort)200, 16)).ToArray();
        using RawImage image = TestImages.FromCodes(codes, format);
        var source = new AnalysisSource(image, 0);
        ushort[] profile = ImageAnalysis.ExtractRowProfile(image, source.Frame, 1);
        Assert.Equal(100, profile[0]);
        Assert.Equal(200, image.GetPixel(0, 1, frame: 1));

        Assert.True(source.IsCurrent(image, 0));
        Assert.False(source.IsCurrent(image, 1));
    }

    [Fact]
    public void OtherImage_IsNotCurrent()
    {
        // 画像の差し替え(開く・送り・処理結果・HDR表示の出入り)の後に届いた結果も採用しない
        var format = new RawFormat { Width = 2, Height = 2 };
        using RawImage image = TestImages.FromCodes(new ushort[4], format);
        using RawImage other = TestImages.FromCodes(new ushort[4], format);
        var source = new AnalysisSource(image, 0);

        Assert.False(source.IsCurrent(other, 0));
        Assert.False(source.IsCurrent(null, 0));
    }
}

using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 射影の窓で選んだチャネル(チャネル分割表示で ROI がないとき、そのチャネル全体の射影を取る)を、いつまで保つか。
/// </summary>
public class ProjectionChannelSelectionTests
{
    private static readonly RawImage Image = TestImages.FromCodes(new ushort[8 * 4 * 2],
        new RawFormat { Width = 8, Height = 4, BitDepth = 12, Bayer = BayerPattern.Rggb, FrameCount = 2 });

    private static readonly RawImage SameSize = TestImages.FromCodes(new ushort[8 * 4], 8, 4, 12, BayerPattern.Rggb);

    private static readonly RawImage OtherSize = TestImages.FromCodes(new ushort[6 * 4], 6, 4, 12, BayerPattern.Rggb);

    [Fact]
    public void StartsUnselected_TheSoftwareDoesNotPickAChannel()
    {
        var selection = new ProjectionChannelSelection();

        Assert.Null(selection.Channel);
        Assert.Null(selection.Current(Split(Image)));
    }

    [Fact]
    public void KeptAcrossFramesFilesOfTheSameSizeRoiAndBayerChange()
    {
        // 同じ大きさの画像の間(フレーム・ページ・連番のファイルの送り)、ROI を描いて消したとき、Bayer を直したときは保つ
        // (Bayer を直したら、選んだチャネルの象限が変わるだけでチャネルは同じ)
        var selection = new ProjectionChannelSelection();
        selection.Select(BayerChannel.Gr, Split(Image));

        Assert.Equal(BayerChannel.Gr, selection.Current(Split(Image) with { Frame = 1 }));
        Assert.Equal(BayerChannel.Gr, selection.Current(Split(Image) with { Roi = new RegionOfInterest(0, 0, 2, 2) }));
        Assert.Equal(BayerChannel.Gr, selection.Current(Split(Image) with { Pattern = BayerPattern.Bggr }));
        Assert.Equal(BayerChannel.Gr, selection.Current(Split(SameSize)));
        Assert.Equal(BayerChannel.Gr, selection.Channel);
    }

    [Fact]
    public void ForgottenWhenLeavingChannelSplitOrSizeChangesOrAnotherFileIsOpened()
    {
        var selection = new ProjectionChannelSelection();

        // チャネル分割表示を抜けたら忘れる(戻っても未選択から)
        selection.Select(BayerChannel.B, Split(Image));
        Assert.Null(selection.Current(Split(Image) with { ChannelSplitLayout = false }));
        Assert.Null(selection.Current(Split(Image)));

        // 大きさの違う画像(ビニングの結果・別の形式の画像)では忘れる
        selection.Select(BayerChannel.B, Split(Image));
        Assert.Null(selection.Current(Split(OtherSize)));
        Assert.Null(selection.Current(Split(Image)));

        // 別のファイルを開いた(MainWindow が知らせる)
        selection.Select(BayerChannel.R, Split(Image));
        selection.Forget();
        Assert.Null(selection.Current(Split(Image)));

        // 未選択に戻す
        selection.Select(BayerChannel.R, Split(Image));
        selection.Select(null, Split(Image));
        Assert.Null(selection.Current(Split(Image)));
    }

    private static ProjectionView Split(RawImage image) =>
        new(image, 0, null, true, BayerPattern.Rggb, 0, "");
}

using System.Windows;
using RawAnalyzer.App.Controls;
using RawAnalyzer.App.Rendering;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// ファイル連番の送りがビューポートへ行う差し替え(ReplaceImageAsync → 表示モードの適用)が、
/// 選択中の表示モードを保ち、ROI は分割⇔非分割の切替でだけ捨てることの検証。
/// </summary>
[Collection("WPF UI")]
public class SequenceStepViewportTests
{
    private const int Size = 8;

    [Fact]
    public Task SplitToSameLayout_KeepsModeAndRoi() => WpfTestHost.Run(async () =>
    {
        // チャネル分割のまま同じ寸法・Bayer の画像へ送る。以前は送りのたびに表示だけ
        // Raw へ戻り(選択は分割のまま)、分割→非分割の切替として ROI も捨てていた
        using RawImage first = CreateGray(BayerPattern.Rggb);
        using RawImage next = CreateGray(BayerPattern.Rggb);
        ImageViewport viewport = CreateViewport(first);
        try
        {
            viewport.SetDisplayMode(ViewportDisplayMode.ChannelSplit);
            var roi = new RegionOfInterest(0, 0, 2, 2);
            viewport.SetRoi(roi);
            int notified = 0;
            viewport.RoiChanged += (_, _) => notified++;

            await StepAsync(viewport, next, color: null, selectedIndex: 3);

            Assert.Equal(ViewportDisplayMode.ChannelSplit, viewport.DisplayMode);
            Assert.Equal(roi, viewport.Roi);
            Assert.Equal(0, notified);
        }
        finally
        {
            await viewport.ClearImageAsync();
        }
    });

    [Fact]
    public Task SplitToImageWithoutBayer_FallsBackToRawAndDiscardsRoiOnce() => WpfTestHost.Run(async () =>
    {
        // Bayer なしでは分割は成立しない。Raw 表示へ戻し、タイル座標の ROI は一度だけ捨てて通知する
        // (カラー画像へ送る場合も、カラー画像は常に Bayer なしなので ROI は同じ経路で捨てる)
        using RawImage first = CreateGray(BayerPattern.Rggb);
        using RawImage next = CreateGray(BayerPattern.None);
        ImageViewport viewport = CreateViewport(first);
        try
        {
            viewport.SetDisplayMode(ViewportDisplayMode.ChannelSplit);
            viewport.SetRoi(new RegionOfInterest(0, 0, 2, 2));
            int notified = 0;
            viewport.RoiChanged += (_, _) => notified++;

            await StepAsync(viewport, next, color: null, selectedIndex: 3);

            Assert.Equal(ViewportDisplayMode.Raw, viewport.DisplayMode);
            Assert.False(viewport.IsChannelSplitLayout);
            Assert.Null(viewport.Roi);
            Assert.Equal(1, notified);
        }
        finally
        {
            await viewport.ClearImageAsync();
        }
    });

    [Fact]
    public Task ColorToGrayImage_ShowsRawAndKeepsSourceRoi() => WpfTestHost.Run(async () =>
    {
        // カラー表示と Raw 表示はどちらも元画像の座標なので、同じ寸法なら ROI はそのまま使える
        ColorImage color = CreateColor();
        using RawImage first = color.ToLuminance();
        using RawImage next = CreateGray(BayerPattern.Rggb);
        ImageViewport viewport = CreateViewport(first);
        try
        {
            viewport.SetColorImage(color);
            var roi = new RegionOfInterest(1, 1, 3, 2);
            viewport.SetRoi(roi);
            int notified = 0;
            viewport.RoiChanged += (_, _) => notified++;

            await StepAsync(viewport, next, color: null, selectedIndex: 0);

            Assert.Equal(ViewportDisplayMode.Raw, viewport.DisplayMode);
            Assert.Equal(roi, viewport.Roi);
            Assert.Equal(0, notified);
        }
        finally
        {
            await viewport.ClearImageAsync();
        }
    });

    /// <summary>MainWindow のファイル連番の送りと同じ順でビューポートを差し替える。</summary>
    private static async Task StepAsync(
        ImageViewport viewport, RawImage next, ColorImage? color, int selectedIndex)
    {
        Task<RawImage?> pending = viewport.ReplaceImageAsync(next, next.Format, color: color);
        DisplayModeSelection.Choice choice = DisplayModeSelection.ForSequenceImage(
            selectedIndex, color is not null, next.Format.Bayer);
        if (viewport.DisplayMode != choice.ViewportMode)
        {
            viewport.SetDisplayMode(choice.ViewportMode);
        }

        await pending;
    }

    private static RawImage CreateGray(BayerPattern pattern)
    {
        var format = new RawFormat { Width = Size, Height = Size, BitDepth = 12, Bayer = pattern };
        return RawImage.FromPixels(format, new ushort[Size * Size]);
    }

    private static ColorImage CreateColor()
    {
        return ColorImage.FromInterleaved(Size, Size, 8, new ushort[Size * Size * 3]);
    }

    private static ImageViewport CreateViewport(RawImage image)
    {
        var viewport = new ImageViewport();
        viewport.Measure(new Size(240, 180));
        viewport.Arrange(new Rect(0, 0, 240, 180));
        viewport.SetImage(image, image.Format);
        return viewport;
    }
}

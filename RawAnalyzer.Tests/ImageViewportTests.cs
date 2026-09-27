using System.Windows;
using System.Windows.Media;
using RawAnalyzer.App.Controls;
using RawAnalyzer.App.Rendering;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>ImageViewport の描画(実際に OnRender を通す)。</summary>
[Collection("WPF UI")]
public class ImageViewportTests
{
    [Fact]
    public Task ZoomedIn_DrawsRawCodeOverlay() => WpfTestHost.Run(async () =>
    {
        // 32倍以上に拡大すると各画素に raw 値(コード値)を重ねて描く。
        // 以前は描画コンテキストを閉じる前に DrawingGroup を Freeze しており、
        // この倍率に入った最初の描画で InvalidOperationException になっていた
        const int size = 16;
        const int bitDepth = 12;
        var format = new RawFormat { Width = size, Height = size, BitDepth = bitDepth };
        var pixels = new ushort[size * size];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (ushort)(i << (16 - bitDepth)); // コード値 = y * 16 + x
        }

        using RawImage image = RawImage.FromPixels(format, pixels);
        var viewport = new ImageViewport();
        viewport.Measure(new Size(240, 180));
        viewport.Arrange(new Rect(0, 0, 240, 180));
        viewport.SetImage(image, format);

        var presented = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewport.ViewportStateChanged += (_, e) =>
        {
            if (e.Zoom >= 32)
            {
                presented.TrySetResult(); // オーバーレイを取得する品質パスが表示された
            }
        };

        List<string> texts;
        try
        {
            viewport.CenterOn(8, 8, 48);
            await presented.Task.WaitAsync(TimeSpan.FromSeconds(10));

            viewport.UpdateLayout(); // OnRender → 画素値オーバーレイの描画
            texts = CollectTexts(VisualTreeHelper.GetDrawing(viewport));
        }
        finally
        {
            // 失敗時もオーバーレイを外し、後続の自動レイアウトで同じ例外を繰り返さない
            await viewport.ClearImageAsync();
        }

        // 中央の画素 (8,8) はコード値 136 で描かれる(16bit 正規化値の 2176 ではない)
        Assert.Contains("136", texts);
        Assert.DoesNotContain("2176", texts);
    });

    [Theory]
    [InlineData(ViewportDisplayMode.Raw, ViewportDisplayMode.ChannelSplit)]
    [InlineData(ViewportDisplayMode.ChannelSplit, ViewportDisplayMode.BayerColor)]
    public Task SwitchingToOrFromChannelSplit_DiscardsRoi(
        ViewportDisplayMode from, ViewportDisplayMode to) => WpfTestHost.Run(async () =>
    {
        // ROIは表示座標で持つ。チャネル分割(タイル座標)と通常表示(元画像座標)では
        // 同じ矩形が別の画素を指すため、切り替えたら選択を残さない
        (ImageViewport viewport, RawImage image) = CreateBayerViewport(BayerPattern.Rggb);
        try
        {
            viewport.SetDisplayMode(from);
            viewport.SetRoi(new RegionOfInterest(0, 0, 2, 2));
            int notified = 0;
            viewport.RoiChanged += (_, _) => notified++;

            viewport.SetDisplayMode(to);

            Assert.Null(viewport.Roi);
            Assert.Equal(1, notified); // 解析側が旧ROIの統計を消せるよう通知する
        }
        finally
        {
            await viewport.ClearImageAsync();
            image.Dispose();
        }
    });

    [Fact]
    public Task SwitchingBetweenSourceCoordinateModes_KeepsRoi() => WpfTestHost.Run(async () =>
    {
        // Raw / Bayerカラー / 現像はどれも元画像座標で表示するのでROIはそのまま使える
        (ImageViewport viewport, RawImage image) = CreateBayerViewport(BayerPattern.Rggb);
        try
        {
            var roi = new RegionOfInterest(1, 1, 3, 2);
            viewport.SetRoi(roi);

            viewport.SetDisplayMode(ViewportDisplayMode.BayerColor);
            viewport.SetDisplayMode(ViewportDisplayMode.ColorDevelop);
            viewport.SetDisplayMode(ViewportDisplayMode.Raw);

            Assert.Equal(roi, viewport.Roi);
            Assert.False(viewport.IsChannelSplitLayout);
        }
        finally
        {
            await viewport.ClearImageAsync();
            image.Dispose();
        }
    });

    [Fact]
    public Task BayerNoneInChannelSplitMode_FallsBackToSourceLayoutAndDiscardsRoi() =>
        WpfTestHost.Run(async () =>
    {
        // 分割表示中にBayerを「なし」にすると描画はRawへ落ちる(表示座標=元画像座標)。
        // タイル座標で選んだROIを元画像座標として読み替えない
        (ImageViewport viewport, RawImage image) = CreateBayerViewport(BayerPattern.Rggb);
        try
        {
            viewport.SetDisplayMode(ViewportDisplayMode.ChannelSplit);
            Assert.True(viewport.IsChannelSplitLayout);
            viewport.SetRoi(new RegionOfInterest(0, 0, 2, 2));

            viewport.UpdateFormat(image.Format with { Bayer = BayerPattern.None });

            Assert.False(viewport.IsChannelSplitLayout);
            Assert.Null(viewport.Roi);
        }
        finally
        {
            await viewport.ClearImageAsync();
            image.Dispose();
        }
    });

    private static (ImageViewport Viewport, RawImage Image) CreateBayerViewport(BayerPattern pattern)
    {
        var format = new RawFormat { Width = 8, Height = 8, BitDepth = 12, Bayer = pattern };
        RawImage image = RawImage.FromPixels(format, new ushort[64]);
        var viewport = new ImageViewport();
        viewport.Measure(new Size(240, 180));
        viewport.Arrange(new Rect(0, 0, 240, 180));
        viewport.SetImage(image, format);
        return (viewport, image);
    }

    private static List<string> CollectTexts(Drawing? drawing)
    {
        var texts = new List<string>();
        Collect(drawing);
        return texts;

        void Collect(Drawing? node)
        {
            switch (node)
            {
                case DrawingGroup group:
                    foreach (Drawing child in group.Children)
                    {
                        Collect(child);
                    }

                    break;
                case GlyphRunDrawing { GlyphRun.Characters: { } characters }:
                    texts.Add(new string(characters.ToArray()));
                    break;
            }
        }
    }
}

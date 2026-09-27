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

    [Fact]
    public Task ChannelSplit_DrawsDefectMarkersWhereTheirPixelsAreShown() => WpfTestHost.Run(async () =>
    {
        // 欠陥の座標は元画像の座標。分割表示では各画素はそのチャネルの象限に並ぶので、
        // マーカーもそこに描く(以前は元画像座標のまま描き、別チャネルの別の画素を指していた)
        (ImageViewport viewport, RawImage image) = CreateBayerViewport(BayerPattern.Rggb);
        try
        {
            viewport.SetDisplayMode(ViewportDisplayMode.ChannelSplit);
            viewport.SetDefectMarkers(new[]
            {
                new DefectPixel(3, 5, 4095, DefectType.Hot), // (奇,奇) → 右下象限の (1,2)
                new DefectPixel(6, 2, 0, DefectType.Dead),   // (偶,偶) → 左上象限の (3,1)
            });
            viewport.UpdateLayout();

            Assert.Equal(
                new[] { ScreenCenter(viewport, 4 + 1, 4 + 2), ScreenCenter(viewport, 3, 1) },
                MarkerCenters(viewport));

            // 元画像座標で表示するモードでは元の座標のまま
            viewport.SetDisplayMode(ViewportDisplayMode.Raw);
            viewport.UpdateLayout();

            Assert.Equal(
                new[] { ScreenCenter(viewport, 3, 5), ScreenCenter(viewport, 6, 2) },
                MarkerCenters(viewport));
        }
        finally
        {
            await viewport.ClearImageAsync();
            image.Dispose();
        }
    });

    [Fact]
    public Task ChannelSplit_OddSizedImage_SkipsDefectMarkersNotShownInTiles() =>
        WpfTestHost.Run(async () =>
    {
        // 9×7 は 8×6 のタイルとして表示される。最終列・最終行の画素はどの象限にも並ばないので、
        // 他の画素の位置に描いてしまわないよう描かない
        (ImageViewport viewport, RawImage image) = CreateBayerViewport(BayerPattern.Rggb, 9, 7);
        try
        {
            viewport.SetDisplayMode(ViewportDisplayMode.ChannelSplit);
            viewport.SetDefectMarkers(new[]
            {
                new DefectPixel(8, 1, 4095, DefectType.Hot), // 最終列
                new DefectPixel(1, 6, 4095, DefectType.Hot), // 最終行
                new DefectPixel(5, 3, 4095, DefectType.Hot), // 右下象限の (2,1)
            });
            viewport.UpdateLayout();

            Assert.Equal(new[] { ScreenCenter(viewport, 4 + 2, 3 + 1) }, MarkerCenters(viewport));
        }
        finally
        {
            await viewport.ClearImageAsync();
            image.Dispose();
        }
    });

    [Fact]
    public Task ChannelSplit_CenterOnSourcePixel_CentersTheTileShowingThatPixel() =>
        WpfTestHost.Run(async () =>
    {
        // 欠陥一覧・「ここを拡大」は元画像の座標で移動先を渡す。分割表示ではその画素が並ぶ
        // 象限上の位置を中央に置く(以前は元画像座標をそのまま表示座標として中央に置いていた)
        (ImageViewport viewport, RawImage image) = CreateBayerViewport(BayerPattern.Rggb);
        try
        {
            viewport.SetDisplayMode(ViewportDisplayMode.ChannelSplit);

            Assert.True(viewport.CenterOnSourcePixel(3, 5, 32));
            AssertAtViewCenter(viewport, ScreenCenter(viewport, 4 + 1, 4 + 2));

            viewport.SetDisplayMode(ViewportDisplayMode.Raw);

            Assert.True(viewport.CenterOnSourcePixel(3, 5, 32));
            AssertAtViewCenter(viewport, ScreenCenter(viewport, 3, 5));
        }
        finally
        {
            await viewport.ClearImageAsync();
            image.Dispose();
        }
    });

    [Fact]
    public Task ChannelSplit_CenterOnSourcePixelNotShownInTiles_KeepsView() =>
        WpfTestHost.Run(async () =>
    {
        // 9×7 の最終列の画素は分割表示に並ばない。別の画素へ移動しない
        (ImageViewport viewport, RawImage image) = CreateBayerViewport(BayerPattern.Rggb, 9, 7);
        try
        {
            viewport.SetDisplayMode(ViewportDisplayMode.ChannelSplit);
            (double zoom, double originX, double originY) =
                (viewport.Zoom, viewport.OriginX, viewport.OriginY);

            Assert.False(viewport.CenterOnSourcePixel(8, 1, 32));
            Assert.Equal((zoom, originX, originY), (viewport.Zoom, viewport.OriginX, viewport.OriginY));
        }
        finally
        {
            await viewport.ClearImageAsync();
            image.Dispose();
        }
    });

    [Fact]
    public Task ProfileMarker_StaysOnItsSourceRowAndColumnAcrossLayouts() =>
        WpfTestHost.Run(async () =>
    {
        // プロファイル窓に出ているのは元画像の行 y=5 と列 x=3。マーカーはその行・列が
        // いま並んでいる位置に描く。分割表示では元画像の1行はタイルの1行(左右の象限)に、
        // 1列はタイルの1列(上下の象限)に並ぶ。以前は表示座標で持っていたため、
        // 分割⇔非分割を切り替えるとプロファイルとは別の行・列の上に残った
        (ImageViewport viewport, RawImage image) = CreateBayerViewport(BayerPattern.Rggb);
        try
        {
            viewport.SetDisplayMode(ViewportDisplayMode.ChannelSplit);
            viewport.SetProfileMarker(3, 5, horizontalActive: true);
            viewport.UpdateLayout();

            Assert.Equal(
                (ScreenCenter(viewport, 0, 4 + 2).Y, ScreenCenter(viewport, 4 + 1, 0).X),
                ProfileMarkerLines(viewport));

            viewport.SetDisplayMode(ViewportDisplayMode.Raw);
            viewport.UpdateLayout();

            Assert.Equal(
                (ScreenCenter(viewport, 0, 5).Y, ScreenCenter(viewport, 3, 0).X),
                ProfileMarkerLines(viewport));

            viewport.SetDisplayMode(ViewportDisplayMode.ChannelSplit);
            viewport.UpdateLayout();

            Assert.Equal(
                (ScreenCenter(viewport, 0, 4 + 2).Y, ScreenCenter(viewport, 4 + 1, 0).X),
                ProfileMarkerLines(viewport));
        }
        finally
        {
            await viewport.ClearImageAsync();
            image.Dispose();
        }
    });

    [Fact]
    public Task ChannelSplit_OddSizedImage_OmitsProfileLineNotShownInTiles() =>
        WpfTestHost.Run(async () =>
    {
        // 9×7 の最終列 x=8 は分割表示のどの象限にも並ばないので、その縦線は描かない
        // (別の列の上に描かない)。行 y=3 は右下・左下の象限の行 1 に並ぶ
        (ImageViewport viewport, RawImage image) = CreateBayerViewport(BayerPattern.Rggb, 9, 7);
        try
        {
            viewport.SetDisplayMode(ViewportDisplayMode.ChannelSplit);
            viewport.SetProfileMarker(8, 3, horizontalActive: false);
            viewport.UpdateLayout();

            Assert.Equal(((double?)ScreenCenter(viewport, 0, 3 + 1).Y, (double?)null), ProfileMarkerLines(viewport));

            viewport.SetDisplayMode(ViewportDisplayMode.Raw);
            viewport.UpdateLayout();

            Assert.Equal(
                (ScreenCenter(viewport, 0, 3).Y, ScreenCenter(viewport, 8, 0).X),
                ProfileMarkerLines(viewport));
        }
        finally
        {
            await viewport.ClearImageAsync();
            image.Dispose();
        }
    });

    /// <summary>描画されたプロファイルマーカーの横線のY・縦線のX(描かれていなければnull)。</summary>
    private static (double? RowY, double? ColumnX) ProfileMarkerLines(ImageViewport viewport)
    {
        List<LineGeometry> lines =
            CollectGeometries<LineGeometry>(VisualTreeHelper.GetDrawing(viewport));
        double? rowY = lines
            .Where(line => line.StartPoint.Y == line.EndPoint.Y)
            .Select(line => (double?)line.StartPoint.Y)
            .SingleOrDefault();
        double? columnX = lines
            .Where(line => line.StartPoint.X == line.EndPoint.X)
            .Select(line => (double?)line.StartPoint.X)
            .SingleOrDefault();
        return (rowY, columnX);
    }

    private static void AssertAtViewCenter(ImageViewport viewport, Point screen)
    {
        Assert.Equal(viewport.ActualWidth / 2, screen.X, 9);
        Assert.Equal(viewport.ActualHeight / 2, screen.Y, 9);
    }

    /// <summary>表示座標の画素の中心の画面座標(ビューポートの描画と同じ式)。</summary>
    private static Point ScreenCenter(ImageViewport viewport, int x, int y)
    {
        return new Point(
            (x + 0.5 - viewport.OriginX) * viewport.Zoom,
            (y + 0.5 - viewport.OriginY) * viewport.Zoom);
    }

    /// <summary>描画された欠陥マーカー(円)の中心。</summary>
    private static Point[] MarkerCenters(ImageViewport viewport)
    {
        return CollectGeometries<EllipseGeometry>(VisualTreeHelper.GetDrawing(viewport))
            .Select(ellipse => ellipse.Center)
            .ToArray();
    }

    private static List<T> CollectGeometries<T>(Drawing? drawing)
        where T : Geometry
    {
        var geometries = new List<T>();
        Collect(drawing);
        return geometries;

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
                case GeometryDrawing { Geometry: T geometry }:
                    geometries.Add(geometry);
                    break;
            }
        }
    }

    private static (ImageViewport Viewport, RawImage Image) CreateBayerViewport(
        BayerPattern pattern, int width = 8, int height = 8)
    {
        var format = new RawFormat { Width = width, Height = height, BitDepth = 12, Bayer = pattern };
        RawImage image = RawImage.FromPixels(format, new ushort[width * height]);
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

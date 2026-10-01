using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

    [Theory]
    [InlineData(0.125, 8, 12)] // 継ぎ目の算術は ChannelSplitRenderTests。ここは配線とチャネル幅1のレベルの端
    public Task ChannelSplit_ZoomedOut_QuadrantBoundaryMatchesActualSize(
        double zoom, int factor, int center) => WpfTestHost.Run(async () =>
    {
        // 22×22 のタイルは象限 11×11 で、ROIの象限判定もこの境目(11)で行う。
        // Bayer縮小レベルの象限は 11 を縮小率で割った端数を切り捨てた幅(L2: 5、L4: 2、L8: 1)
        // しかなく、以前はそのタイルを一様に並べていたため、右・下の象限が等倍の境目より手前
        // (L2: 10、L4/L8: 8)から始まり、画像の右端・下端も欠けていた。
        // 画面の各画素は、その中心のタイル座標が属する象限のチャネルを示さなければならない
        const int size = 22;
        const int half = size / 2;
        ushort[] channelValues = { 8000, 24000, 40000, 56000 }; // RGGB の位相 (0,0)(1,0)(0,1)(1,1)
        var codes = new ushort[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                codes[y * size + x] = channelValues[(y & 1) * 2 + (x & 1)];
            }
        }

        var format = new RawFormat
        {
            Width = size, Height = size, BitDepth = 16, Bayer = BayerPattern.Rggb,
        };
        using RawImage image = TestImages.FromCodes(codes, format);
        using BayerPyramid pyramid =
            BayerPyramid.Create(image, format, maxLevelPixels: long.MaxValue);
        var viewport = new ImageViewport();
        viewport.Measure(new Size(240, 180));
        viewport.Arrange(new Rect(0, 0, 240, 180));
        viewport.SetImage(image, format);
        viewport.SetBayerPyramid(pyramid);
        viewport.SetDisplayMode(ViewportDisplayMode.ChannelSplit);

        var presented = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewport.ViewportStateChanged += (_, e) =>
        {
            if (e.Zoom == zoom && e.RenderedFactor == factor)
            {
                presented.TrySetResult(); // 縮小レベルから描いた結果が表示された
            }
        };

        try
        {
            viewport.CenterOn(center, center, zoom);
            await presented.Task.WaitAsync(TimeSpan.FromSeconds(10));

            byte[] pixels = RenderedPixels(viewport, out int width, out int height);
            DisplayLut lut = DisplayLut.Create(new DisplayParameters());
            int levelHalf = half / factor * factor; // 以前の境目
            var mismatches = new List<string>();
            bool checkedBandX = false;
            bool checkedBandY = false;
            for (int dy = 0; dy < height; dy++)
            {
                double tiledY = viewport.OriginY + (dy + 0.5) / zoom;
                if (tiledY < 0 || tiledY >= size)
                {
                    continue;
                }

                for (int dx = 0; dx < width; dx++)
                {
                    double tiledX = viewport.OriginX + (dx + 0.5) / zoom;
                    if (tiledX < 0 || tiledX >= size)
                    {
                        continue;
                    }

                    int channel = (tiledY < half ? 0 : 2) + (tiledX < half ? 0 : 1);
                    byte expected = lut.Map(channelValues[channel]);
                    byte actual = pixels[(dy * width + dx) * 4];
                    if (actual != expected)
                    {
                        mismatches.Add($"({tiledX},{tiledY}): {actual} (期待 {expected})");
                    }

                    checkedBandX |= tiledX >= levelHalf && tiledX < half;
                    checkedBandY |= tiledY >= levelHalf && tiledY < half;
                }
            }

            Assert.Empty(mismatches);

            // 以前ずれていた帯(縮小レベルの境目〜等倍の境目)の画素を実際に確かめている
            Assert.True(checkedBandX && checkedBandY);
        }
        finally
        {
            await viewport.ClearImageAsync();
        }
    });

    [Fact]
    public Task DetachBayerPyramidThenSetFrameInSameTurn_DrawsNewFrameAndNeverUsesDetachedPyramid() =>
        WpfTestHost.Run(async () =>
    {
        // フレーム送りは、送りを決めたUIターンで Bayer ピラミッドの切り離しとフレームの切り替えを行い、
        // 切り離しの完了(切り離す前に始まっていた描画の停止)を待ってからピラミッドを破棄する
        // (先に切り離して停止を待つと、その間に始まった操作に譲って送りをやめたとき、表示中のフレームの
        // ピラミッドだけを失う)。この順で、新しいフレームの描画は止まらずに表示され、
        // 破棄したピラミッドは元のフレームへ戻っても使われないこと
        const int size = 64;
        const ushort frame0Value = 8000;
        const ushort frame1Value = 56000;
        var format = new RawFormat
        {
            Width = size, Height = size, BitDepth = 16, Bayer = BayerPattern.Rggb, FrameCount = 2,
        };
        var codes = new ushort[size * size * 2];
        codes.AsSpan(0, size * size).Fill(frame0Value);
        codes.AsSpan(size * size).Fill(frame1Value);
        using RawImage image = TestImages.FromCodes(codes, format);
        BayerPyramid pyramid = BayerPyramid.Create(image, format, frame: 0, maxLevelPixels: long.MaxValue);
        DisplayLut lut = DisplayLut.Create(new DisplayParameters());
        var viewport = new ImageViewport();
        viewport.Measure(new Size(240, 180));
        viewport.Arrange(new Rect(0, 0, 240, 180));
        viewport.SetImage(image, format);
        viewport.SetBayerPyramid(pyramid, frame: 0);
        viewport.SetDisplayMode(ViewportDisplayMode.BayerColor);

        // 表示されたフレームと縮小率を記録する(描画結果の表示は UI スレッドで行われる)
        var frame0FromPyramid = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frame1Shown = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var frame0ShownAgain = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool returned = false;
        viewport.ViewportStateChanged += (_, e) =>
        {
            if (viewport.Frame == 0 && !returned && e.Zoom == 0.25 && e.RenderedFactor == 4)
            {
                frame0FromPyramid.TrySetResult(); // 縮小表示をフレーム0のピラミッドから描いた
            }
            else if (viewport.Frame == 1)
            {
                frame1Shown.TrySetResult(e.RenderedFactor);
            }
            else if (viewport.Frame == 0 && returned)
            {
                frame0ShownAgain.TrySetResult(e.RenderedFactor);
            }
        };

        try
        {
            viewport.CenterOn(size / 2, size / 2, 0.25);
            await frame0FromPyramid.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(lut.Map(frame0Value), CenterValue(viewport));

            // 読み込みの完了後に動くアイドル時の描き直しを済ませ、以降の表示が下の操作による描画だけになるようにする
            await Task.Delay(400);

            // ピラミッドを読む描画を走らせたまま、同じUIターンで切り離してフレーム1へ移す
            viewport.SetBayerPyramid(pyramid, frame: 0);
            Task detached = viewport.DetachBayerPyramidAsync();
            viewport.SetFrame(1);
            await detached.WaitAsync(TimeSpan.FromSeconds(10));
            pyramid.Dispose();

            // 新しいフレームの描画は切り離しで止まらず、等倍データから表示される
            Assert.Equal(1, await frame1Shown.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(lut.Map(frame1Value), CenterValue(viewport));

            // 元のフレームへ戻っても、破棄したピラミッドは使わない(使うと読み出しが破棄済みで失敗し、表示されない)
            returned = true;
            viewport.SetFrame(0);
            Assert.Equal(1, await frame0ShownAgain.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(lut.Map(frame0Value), CenterValue(viewport));
        }
        finally
        {
            await viewport.ClearImageAsync();
            pyramid.Dispose();
        }
    });

    [Fact]
    public Task ClearRoiDuringRoiDrag_ReleasesMouseCaptureWhenButtonIsReleased() =>
        WpfTestHost.Run(async () =>
    {
        // ROI のドラッグ中に画像の差し替え(HDR 分割の計算完了など)・分割⇔非分割の切り替え・
        // Ctrl+G で ROI が消されることがある。以前はドラッグの状態だけを落としてマウスキャプチャを残し、
        // ボタンを離しても外れなかったため、ビューポートの外のマウス移動と次の1クリックが
        // ビューポートに吸われていた
        (ImageViewport viewport, RawImage image) = CreateBayerViewport(BayerPattern.None);
        using HwndSource host = HostInHiddenWindow(viewport);
        try
        {
            // キャプチャを取るとWPFが実際のカーソル位置・ボタン状態でマウス移動を合成する。
            // 実際のボタンは押されていないので、そのまま届くとドラッグ自体が終わってしまう。
            // ボタンを押したまま動かさない操作にするため、実際のマウス移動は届けない
            viewport.PreviewMouseMove += (_, e) => e.Handled = true;
            viewport.InteractionMode = ViewportInteractionMode.RoiSelect;
            RaiseLeftButton(viewport, UIElement.MouseDownEvent);
            Assert.True(viewport.IsMouseCaptured); // ROI のドラッグを始めた

            viewport.ClearRoi();
            RaiseLeftButton(viewport, UIElement.MouseUpEvent);

            Assert.False(viewport.IsMouseCaptured);
        }
        finally
        {
            viewport.ReleaseMouseCapture();
            host.RootVisual = null;
            await viewport.ClearImageAsync();
            image.Dispose();
        }
    });

    [Theory]
    [InlineData(1.0)]  // 等倍: 以前は交点が4画素右下の画素を指していた
    [InlineData(0.25)] // 縮小表示: 以前は18画素右下
    [InlineData(16.0)] // 枠が画素と同じ大きさになる拡大(以前から正しい)
    public Task KeyboardPixelCursor_CrossesAtCenterOfItsPixel(double zoom) => WpfTestHost.Run(async () =>
    {
        // Ctrl+矢印の画素カーソルは、ステータスバーに値を出す画素の中心で十字が交わり、枠もその画素を
        // 中心に描く(ゴーストカーソル・プロファイルマーカーと同じ規約)。以前は最小 9px の枠を画素の
        // 左上から描いて交点を枠の中心に置いていたため、ズーム 9 未満では右下へ最大 4.5 画面px ずれた
        // 別の画素を指していた
        (ImageViewport viewport, RawImage image) = CreateBayerViewport(BayerPattern.None, 64, 64);
        var reported = new List<CursorPixelEventArgs>();
        viewport.CursorPixelChanged += (_, e) => reported.Add(e);
        try
        {
            viewport.CenterOn(20, 30, zoom);
            PressWithControl(viewport, Key.Right); // 最初の Ctrl+矢印でビュー中央の画素にカーソルが出る
            CursorPixelEventArgs pixel = Assert.Single(reported);
            viewport.UpdateLayout();

            Point center = ScreenCenter(viewport, pixel.X, pixel.Y);
            (double? rowY, double? columnX) = ProfileMarkerLines(viewport); // ガイド線の横線・縦線
            Assert.Equal(center.Y, rowY!.Value, 9);
            Assert.Equal(center.X, columnX!.Value, 9);

            Rect frame = Assert.Single(
                CollectDrawings<GeometryDrawing>(VisualTreeHelper.GetDrawing(viewport))
                    .Where(drawing => drawing.Brush is null && drawing.Geometry is RectangleGeometry)
                    .Select(drawing => ((RectangleGeometry)drawing.Geometry).Rect));
            Assert.Equal(center.X, frame.X + frame.Width / 2, 9);
            Assert.Equal(center.Y, frame.Y + frame.Height / 2, 9);
            Assert.True(frame.Width >= zoom && frame.Height >= zoom); // 画素全体を囲む
        }
        finally
        {
            await viewport.ClearImageAsync();
            image.Dispose();
        }
    });

    /// <summary>
    /// Ctrl を押したまま矢印キーを押す。ビューポートは Ctrl の状態を <see cref="Keyboard.Modifiers"/>
    /// (このスレッドのキーボード状態)から読むので、その間だけこのスレッドのキーボード状態で Ctrl を押した扱いにする
    /// (ほかのスレッド・アプリの入力には影響しない)。
    /// </summary>
    private static void PressWithControl(UIElement target, Key key)
    {
        const int VkControl = 0x11;
        const int VkLeftControl = 0xA2;
        var saved = new byte[256];
        Assert.True(GetKeyboardState(saved));
        var pressed = (byte[])saved.Clone();
        pressed[VkControl] |= 0x80;
        pressed[VkLeftControl] |= 0x80;
        Assert.True(SetKeyboardState(pressed));
        try
        {
            Assert.Equal(ModifierKeys.Control, Keyboard.Modifiers);
            var source = new TestInputSource();
            var preview = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            };
            target.RaiseEvent(preview);
            target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
            {
                RoutedEvent = Keyboard.KeyDownEvent,
                Handled = preview.Handled,
            });
        }
        finally
        {
            SetKeyboardState(saved);
        }
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKeyboardState(byte[] keyState);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKeyboardState(byte[] keyState);

    /// <summary>ウィンドウを表示せずにキー入力イベントを作るための入力元。</summary>
    private sealed class TestInputSource : PresentationSource
    {
        private Visual? _root;

        public override Visual RootVisual
        {
            get => _root!;
            set => _root = value;
        }

        public override bool IsDisposed => false;

        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }

    /// <summary>
    /// マウスキャプチャは PresentationSource に載った要素でしか取れないので、表示しない HWND に載せる。
    /// </summary>
    private static HwndSource HostInHiddenWindow(ImageViewport viewport)
    {
        const int WsPopup = unchecked((int)0x80000000); // WS_VISIBLE を付けない
        const int WsExToolWindow = 0x00000080;
        const int WsExNoActivate = 0x08000000;
        return new HwndSource(new HwndSourceParameters(nameof(ImageViewportTests))
        {
            WindowStyle = WsPopup,
            ExtendedWindowStyle = WsExToolWindow | WsExNoActivate,
            PositionX = -32000,
            PositionY = -32000,
            Width = 240,
            Height = 180,
        })
        {
            RootVisual = viewport,
        };
    }

    /// <summary>左ボタンの押下または解放を、入力と同じく Preview → 本体の順に送る。</summary>
    private static void RaiseLeftButton(ImageViewport viewport, RoutedEvent bubbling)
    {
        RoutedEvent preview = bubbling == UIElement.MouseDownEvent
            ? UIElement.PreviewMouseDownEvent
            : UIElement.PreviewMouseUpEvent;
        foreach (RoutedEvent routed in new[] { preview, bubbling })
        {
            viewport.RaiseEvent(new MouseButtonEventArgs(
                Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = routed });
        }
    }

    /// <summary>
    /// 表示中の描画結果の中央の画素の最も明るいチャネル。Bayerカラー表示では、その画素のチャネルの値
    /// (他のチャネルは暗く描く)。
    /// </summary>
    private static byte CenterValue(ImageViewport viewport)
    {
        byte[] pixels = RenderedPixels(viewport, out int width, out int height);
        int offset = ((height / 2) * width + width / 2) * 4;
        return Math.Max(pixels[offset], Math.Max(pixels[offset + 1], pixels[offset + 2]));
    }

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
        return CollectDrawings<GeometryDrawing>(drawing)
            .Select(geometryDrawing => geometryDrawing.Geometry)
            .OfType<T>()
            .ToList();
    }

    private static List<T> CollectDrawings<T>(Drawing? drawing)
        where T : Drawing
    {
        var drawings = new List<T>();
        Collect(drawing);
        return drawings;

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
                case T match:
                    drawings.Add(match);
                    break;
            }
        }
    }

    /// <summary>直近に表示された描画結果(BGRA、ビューポートと同じ大きさ)。</summary>
    private static byte[] RenderedPixels(ImageViewport viewport, out int width, out int height)
    {
        viewport.UpdateLayout(); // OnRender → 描画結果のビットマップを描く
        ImageDrawing drawing =
            Assert.Single(CollectDrawings<ImageDrawing>(VisualTreeHelper.GetDrawing(viewport)));
        var bitmap = (BitmapSource)drawing.ImageSource;
        width = bitmap.PixelWidth;
        height = bitmap.PixelHeight;
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
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

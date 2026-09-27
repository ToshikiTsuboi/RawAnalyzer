using System.Windows;
using System.Windows.Media;
using RawAnalyzer.App.Controls;
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

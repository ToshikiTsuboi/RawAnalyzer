using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Rendering;

/// <summary>ヒストグラムを固定サイズの表示画像へ変換する。ウィンドウや解析状態には依存しない。</summary>
internal static class HistogramRenderer
{
    private const int Width = 210;
    private const int Height = 70;
    private const int BytesPerPixel = 4;

    // チャネル色 (BGR順): R, Gr, Gb, B
    private static readonly (byte B, byte G, byte R)[] ChannelColors =
    {
        (0x4E, 0x52, 0xE0), (0x72, 0xCB, 0x7E), (0x4E, 0x9A, 0x4E), (0xD9, 0x9D, 0x5B),
    };

    /// <summary>全画素のヒストグラムを棒グラフとして描く。</summary>
    internal static BitmapSource Render(long[] bins, bool logScale, bool cumulative)
    {
        // cumulative時は縦軸=累積頻度(そのcode以下の画素数)。非累積は列ごとの担当コード数の違いで
        // 偽の櫛にならないよう、1コードあたりの度数で描く
        double[] columns = HistogramTools.AggregateForDisplay(bins, Width, cumulative);
        double maxScale = Scale(columns.Max(), logScale);
        var pixels = new byte[Width * Height * BytesPerPixel];
        for (int x = 0; x < Width; x++)
        {
            int barHeight = BarHeight(columns[x], maxScale, logScale);
            for (int y = Height - barHeight; y < Height; y++)
            {
                int offset = (y * Width + x) * BytesPerPixel;
                pixels[offset] = cumulative ? (byte)0xD9 : (byte)0x85;
                pixels[offset + 1] = cumulative ? (byte)0x9D : (byte)0x8A;
                pixels[offset + 2] = cumulative ? (byte)0x5B : (byte)0x8A;
                pixels[offset + 3] = 0xFF;
            }
        }

        return CreateBitmap(pixels);
    }

    /// <summary>R・Gr・Gb・Bのヒストグラムを共通の縦軸スケールで重ねて描く。</summary>
    internal static BitmapSource RenderChannels(
        IReadOnlyList<ChannelHistogram> channels, bool logScale, bool cumulative)
    {
        var curves = new double[channels.Count][];
        double maxValue = 0;
        for (int c = 0; c < channels.Count; c++)
        {
            double[] columns = HistogramTools.AggregateForDisplay(channels[c].Bins, Width, cumulative);
            curves[c] = columns;
            maxValue = Math.Max(maxValue, columns.Max());
        }

        double maxScale = Scale(maxValue, logScale);
        var pixels = new byte[Width * Height * BytesPerPixel];
        for (int c = 0; c < curves.Length; c++)
        {
            for (int x = 0; x < Width; x++)
            {
                int barHeight = BarHeight(curves[c][x], maxScale, logScale);
                int top = Math.Clamp(Height - 1 - barHeight, 0, Height - 2);
                for (int y = top; y < Math.Min(Height, top + 2); y++)
                {
                    int offset = (y * Width + x) * BytesPerPixel;
                    pixels[offset] = ChannelColors[c].B;
                    pixels[offset + 1] = ChannelColors[c].G;
                    pixels[offset + 2] = ChannelColors[c].R;
                    pixels[offset + 3] = 0xFF;
                }
            }
        }

        return CreateBitmap(pixels);
    }

    private static double Scale(double value, bool logScale) => logScale ? Math.Log(1 + value) : value;

    private static int BarHeight(double value, double maxScale, bool logScale) =>
        maxScale > 0 ? (int)(Scale(value, logScale) / maxScale * (Height - 2)) : 0;

    private static BitmapSource CreateBitmap(byte[] pixels)
    {
        var bitmap = new WriteableBitmap(Width, Height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, Width, Height), pixels, Width * BytesPerPixel, 0);
        bitmap.Freeze();
        return bitmap;
    }
}

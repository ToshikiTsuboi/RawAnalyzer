using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Rendering;

internal sealed record NavigatorThumbnail(
    PyramidLevel GrayLevel, RawImage? BayerLevel, int BayerFactor, int ImageWidth, int ImageHeight,
    ViewportDisplayMode Mode, BayerPattern Pattern, DisplayLut Lut, DevelopLuts? Develop,
    IReadOnlyList<DisplayLut>? SegmentLuts, int SegmentWidth, ColorImage? Color)
{
    internal const int LongSide = 160;

    internal Task<BitmapSource> RenderAsync(double dpi, CancellationToken token) =>
        Task.Run(() => Render(dpi, token), token);

    private BitmapSource Render(double dpi, CancellationToken token)
    {
        int longest = Math.Max(ImageWidth, ImageHeight);
        double scale = Math.Round(LongSide * dpi) / longest;
        int width = Math.Max(1, (int)Math.Round(ImageWidth * scale));
        int height = Math.Max(1, (int)Math.Round(ImageHeight * scale));
        byte[] pixels = new byte[width * height * 4];

        if (Mode == ViewportDisplayMode.TrueColor && Color is { } rgb)
        {
            // RGB に縮小ピラミッドはない。グレー最粗段の格子から位置を取り、RGB は出力1画素につき1点だけ読む。
            for (int y = 0; y < height; y++)
            {
                token.ThrowIfCancellationRequested();
                int cy = CoarseColorCoordinate(y, height, GrayLevel.Factor, rgb.Height);
                for (int x = 0; x < width; x++)
                {
                    int cx = CoarseColorCoordinate(x, width, GrayLevel.Factor, rgb.Width);
                    rgb.GetPixel(cx, cy, out ushort r, out ushort g, out ushort b);
                    int at = (y * width + x) * 4;
                    pixels[at] = Lut.Map(b);
                    pixels[at + 1] = Lut.Map(g);
                    pixels[at + 2] = Lut.Map(r);
                    pixels[at + 3] = 255;
                }
            }
        }
        else
        {
            RenderSource source = new PyramidLevelRenderSource(GrayLevel, ImageWidth, ImageHeight);
            double zoom = scale;
            if (BayerLevel is { } level)
            {
                if (Mode == ViewportDisplayMode.ChannelSplit)
                {
                    source = new ChannelSplitRenderSource(level, 0, BayerFactor, ImageWidth, ImageHeight);
                }
                else
                {
                    source = new BayerLevelRenderSource(level, BayerFactor, ImageWidth, ImageHeight);
                    zoom *= BayerFactor;
                }
            }

            var request = new RenderRequest
            {
                Source = source, Mode = Mode, Pattern = Pattern, Lut = Lut, DevelopLuts = Develop,
                SegmentLuts = SegmentLuts, SegmentWidth = SegmentWidth,
            };
            // 丸めた出力寸法の中心を画像の中心に合わせ、縦長・横長でも端に余白が出ないようにする。
            double originX = (source.ExtentWidth - width / zoom) / 2;
            double originY = (source.ExtentHeight - height / zoom) / 2;
            ViewportRenderer.Render(request, zoom, originX, originY, width, height, pixels, token);
        }

        BitmapSource bitmap = BitmapSource.Create(width, height, 96 * dpi, 96 * dpi,
            PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static int CoarseColorCoordinate(int coordinate, int thumbnailExtent, int factor, int extent)
    {
        int pixel = (int)((coordinate + 0.5) / thumbnailExtent * extent);
        int first = Math.Min(pixel / factor * factor, extent - 1);
        return first + (Math.Min(factor, extent - first) - 1) / 2;
    }
}

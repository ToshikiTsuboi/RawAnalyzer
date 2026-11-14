using System.Windows.Media;
using System.Windows.Media.Imaging;
using RawAnalyzer.App.Views;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

internal static class ProcessingPreview
{
    internal readonly record struct Plan(RegionOfInterest Input, RegionOfInterest Before,
        RegionOfInterest After, int OutputX, int OutputY);

    internal static Plan SelectRegion(int width, int height, BayerPattern bayer, ImageProcessingChoice choice,
        int limit = 512)
    {
        int step = bayer == BayerPattern.None ? 1 : 2;
        int factor = choice.Filter is null ? choice.Factor : 1;
        int alignment = step * factor;
        int halo = choice.Filter is { } filter ? step * (filter.Kind == ImageFilterKind.Sobel ? 1 : filter.Radius) : 0;
        (int x, int w) = Center(width);
        (int y, int h) = Center(height);
        int inputX = Math.Max(0, x - halo);
        int inputY = Math.Max(0, y - halo);
        return new Plan(
            new(inputX, inputY, Math.Min(width - inputX, x - inputX + w + halo),
                Math.Min(height - inputY, y - inputY + h + halo)),
            new(x - inputX, y - inputY, w, h),
            new((x - inputX) / factor, (y - inputY) / factor, w / factor, h / factor),
            x / factor, y / factor);

        (int Start, int Length) Center(int length)
        {
            if (choice.Filter is null)
            {
                int available = length / alignment;
                int blocks = Math.Min(available, Math.Max(1, limit / alignment));
                return ((available - blocks) / 2 * alignment, blocks * alignment);
            }

            int size = Math.Min(length, limit);
            return ((length - size) / 2 / alignment * alignment, size);
        }
    }

    internal static (BitmapSource Before, BitmapSource After) Render(
        Plan plan, BayerPattern pattern, ImageProcessingChoice choice,
        Func<RegionOfInterest, CancellationToken, RawImage> read,
        Func<RegionOfInterest, CancellationToken, ColorImage>? readColor, DisplayLut lut, CancellationToken token)
    {
        if (readColor is not null)
        {
            ColorImage input = readColor(plan.Input, token);
            ColorImage output = Apply(input, choice, null, token);
            return (RenderRegion(null, input, plan.Before), RenderRegion(null, output, plan.After));
        }

        using RawImage raw = read(plan.Input, token);
        using RawImage processed = Apply(raw, choice, 0, pattern, null, token);
        return (RenderRegion(raw, null, plan.Before), RenderRegion(processed, null, plan.After));

        BitmapSource RenderRegion(RawImage? gray, ColorImage? color, RegionOfInterest region)
        {
            int channels = color is null ? 1 : 3;
            int stride = region.Width * channels;
            var codes = new ushort[stride];
            var bytes = new byte[stride * region.Height];
            for (int y = 0; y < region.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                if (color is not null)
                    color.CopyRow(region.Y + y, region.X, region.Width, codes);
                else
                    gray!.CopyRegion(0, region.X, region.Y + y, region.Width, 1, codes);
                lut.Apply(codes, bytes.AsSpan(y * stride, stride));
            }

            var image = BitmapSource.Create(region.Width, region.Height, 96, 96,
                color is null ? PixelFormats.Gray8 : PixelFormats.Rgb24, null, bytes, stride);
            image.Freeze();
            return image;
        }
    }

    internal static RawImage ReadRaw(RawImage source, int frame, RegionOfInterest area, CancellationToken token)
    {
        var pixels = new ushort[area.Width * area.Height];
        for (int y = 0; y < area.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            source.CopyRegion(frame, area.X, area.Y + y, area.Width, 1, pixels.AsSpan(y * area.Width, area.Width));
        }

        return RawImage.FromPixels(new RawFormat { Width = area.Width, Height = area.Height, BitDepth = source.Format.BitDepth }, pixels);
    }

    internal static ColorImage ReadColor(ColorImage source, RegionOfInterest area, CancellationToken token)
    {
        int stride = area.Width * 3;
        var pixels = new ushort[stride * area.Height];
        for (int y = 0; y < area.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            source.CopyRow(area.Y + y, area.X, area.Width, pixels.AsSpan(y * stride, stride));
        }

        return ColorImage.FromInterleaved(area.Width, area.Height, source.BitDepth, pixels);
    }

    internal static RawImage Apply(RawImage source, ImageProcessingChoice choice, int frame, BayerPattern pattern,
        IProgress<double>? progress, CancellationToken token) => choice.Filter is { } filter
        ? ImageFilters.Apply(source, filter, frame, pattern, progress, token)
        : ImageBinning.Apply(source, choice.Factor, choice.Mode, frame, pattern, progress, token);

    internal static ColorImage Apply(ColorImage source, ImageProcessingChoice choice,
        IProgress<double>? progress, CancellationToken token) => choice.Filter is { } filter
        ? ImageFilters.Apply(source, filter, progress, token)
        : ImageBinning.Apply(source, choice.Factor, choice.Mode, progress, token);
}

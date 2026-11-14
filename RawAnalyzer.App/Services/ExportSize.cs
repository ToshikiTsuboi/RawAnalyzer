using System.IO;
using System.Windows;
using RawAnalyzer.App.Views;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

internal static class ExportSize
{
    internal static long? ForBatchTiff(int width, int height, long frames)
    {
        if (width <= 0 || height <= 0 || frames <= 0)
            return null;

        long bytes = TiffWriter.Gray16FileSize(width, height);
        return bytes <= uint.MaxValue && frames <= long.MaxValue / bytes ? bytes * frames : null;
    }

    internal static (int? FirstPageCount, long? TiffBytes) ReadBatchSizes(IReadOnlyList<string> targets,
        int? firstPageCount, bool needTotal, CancellationToken token)
    {
        long total = 0;
        try
        {
            for (int i = 0; i < targets.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var pages = ImageFileLoader.ReadPageDimensions(targets[i], token);
                if (i == 0)
                {
                    firstPageCount = pages.Count;
                    if (!needTotal)
                        return (firstPageCount, null);
                }

                foreach (var page in pages)
                {
                    token.ThrowIfCancellationRequested();
                    long? pageSize = ForBatchTiff(page.Width, page.Height, 1);
                    if (pageSize is not long bytes || total > long.MaxValue - bytes)
                        return (firstPageCount, null);
                    total += bytes;
                }
            }
            return (firstPageCount, total);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                   or InvalidDataException or FileFormatException or NotSupportedException
                                   or System.Runtime.InteropServices.COMException)
        {
            return (firstPageCount, null);
        }
    }

    internal static (long Bytes, string Label)? ForSave(SaveFormat format, long pixels, int frames,
        int rawBytesPerPixel, bool color, bool demosaic)
    {
        try
        {
            return format switch
            {
                SaveFormat.Raw => (checked(pixels * frames * rawBytesPerPixel), "出力サイズ"),
                SaveFormat.FloatRaw => (checked(pixels * 4), "出力サイズ"),
                SaveFormat.Tiff16 => (checked(pixels * (color ? 6 : 2)), "非圧縮の画素データ (TIFF ヘッダ等を除く)"),
                SaveFormat.Png16 => (checked(pixels * (color ? 6 : 2)), "圧縮前の画素データ上限"),
                SaveFormat.Png8 or SaveFormat.Jpeg8 => (checked(pixels * (color || demosaic ? 3 : 1)), "圧縮前の画素データ上限"),
                _ => null,
            };
        }
        catch (OverflowException)
        {
            return null;
        }
    }
}

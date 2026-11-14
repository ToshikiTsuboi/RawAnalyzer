using RawAnalyzer.App.Views;

namespace RawAnalyzer.App.Services;

internal static class ExportSize
{
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

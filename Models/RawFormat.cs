// Models/RawFormat.cs
namespace ImgProcessWpfApp.Models
{
    public enum Endianness { Little, Big }
    public enum BitAlignment { MSB, LSB }
    public enum DemosaicAlgorithm { Bilinear, Bicubic }

    // ★ 追加: Bayer カラーフィルター配列
    public enum CfaPattern
    {
        RGGB,
        GRBG,
        GBRG,
        BGGR
    }

    public enum ExportFormat
    {
        Png,
        Jpeg,
        Tiff,
        Bmp
    }
}

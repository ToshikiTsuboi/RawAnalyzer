// Models/RawFormat.cs
namespace ImgProcessWpfApp.Models
{
    public enum Endianness { Little, Big }
    public enum BitAlignment { MSB, LSB } // 上詰め=MSB, 下詰め=LSB

    public enum DemosaicAlgorithm
    {
        Bilinear,
        Bicubic // ※現在はバイリニアと同等のロジック。後で本物の双三次へ差し替えやすい設計です
    }

    public enum ExportFormat
    {
        Png,
        Jpeg,
        Tiff,
        Bmp
    }
}

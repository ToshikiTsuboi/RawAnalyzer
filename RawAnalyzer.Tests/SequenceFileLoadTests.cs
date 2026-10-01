using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// ファイル連番の送りで送り先のファイルを読む処理(MainWindow の ShowSequenceIndexAsync)。
/// </summary>
public class SequenceFileLoadTests
{
    private static readonly RawFormat Raw8x4 = new()
    {
        Width = 8, Height = 4, BitDepth = 16, Bayer = BayerPattern.Rggb,
    };

    private static TempTiff WriteRaw(ushort value)
    {
        var bytes = new byte[8 * 4 * 2];
        for (int i = 0; i < bytes.Length; i += 2)
        {
            bytes[i] = (byte)value;
            bytes[i + 1] = (byte)(value >> 8);
        }

        return TempTiff.Write(bytes, ".raw");
    }

    private static TempTiff WriteGrayTiff(int width, int height)
    {
        var samples = new byte[width * height];
        return TempTiff.Write(new TiffBuilder().Build(TiffBuilder.GrayPage(width, height, 8, samples)));
    }

    [Fact]
    public void Raw_IsReadWithTheDisplayedFormat()
    {
        using TempTiff file = WriteRaw(1234);

        DecodedImage loaded = SequenceFileLoad.Load(file.Path, isRaw: true, Raw8x4, CancellationToken.None);
        using RawImage image = loaded.Luminance;

        Assert.Equal(Raw8x4, image.Format);
        Assert.Equal(1234, image.GetPixel(7, 3));
        Assert.Null(loaded.Color);
    }

    [Fact]
    public void ImageFile_IsReadWithItsOwnFormat()
    {
        using TempTiff file = WriteGrayTiff(6, 5);

        DecodedImage loaded = SequenceFileLoad.Load(file.Path, isRaw: false, Raw8x4, CancellationToken.None);
        using RawImage image = loaded.Luminance;

        Assert.Equal(6, image.Width);
        Assert.Equal(5, image.Height);
        Assert.Equal(8, image.Format.BitDepth);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CanceledLoad_Throws(bool isRaw)
    {
        // 送り先を読む間に別ファイルを開く・操作を始める・ウィンドウを閉じると取り消す。以前はトークンを渡さず、
        // 結果を捨てるだけで大きなファイルの読み込み(ネットワーク上の raw の一時コピー、TIFF のデコード)が
        // 最後まで走り、新しい読み込みや処理と I/O・メモリを奪い合っていた
        using TempTiff file = isRaw ? WriteRaw(1) : WriteGrayTiff(6, 5);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => SequenceFileLoad.Load(file.Path, isRaw, Raw8x4, cts.Token));
    }
}

using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 画像ファイル(TIFF 等)を送るときに、右パネルで指定した Bayer を引き継ぐ規則の検証
/// (ファイル連番の送りと TIFF のページ送りで共通)。
/// </summary>
public class ImageFileBayerTests
{
    private const int W = 4;
    private const int H = 4;

    [Theory]
    [InlineData(BayerPattern.None)] // 指定なしも CFAPattern に負けない(指定ありは TiffPages_FollowTheSameRule)
    public void CfaTiff_DesignationTakesPrecedenceOverCfaPattern(BayerPattern designated)
    {
        // CFAPattern(GRBG)を持つ TIFF でも、引き継いだ指定を優先する(TIFF のページ送りと同じ規約)。
        // 指定の初期値は開いた画像の配列なので、変えなければ開いたファイルの CFAPattern が続く
        using var file = TempTiff.Write(new TiffBuilder().Build(CfaPage()));
        DecodedImage decoded = ImageFileLoader.Load(file.Path);
        using RawImage image = decoded.Luminance;
        Assert.Null(decoded.Color);
        Assert.Equal(BayerPattern.Grbg, image.Format.Bayer);

        RawFormat format = ImageFileBayer.Apply(image.Format, isColor: false, designated);

        Assert.Equal(image.Format with { Bayer = designated }, format);
    }

    [Fact]
    public async Task TiffPages_FollowTheSameRule()
    {
        // TIFF のページ送りの既存規約(TiffStackSource.BayerOverride)と同じ結果になること:
        // グレーのページには CFA ページも含めて指定を、RGB ページには None を付ける
        using var file = TempTiff.Write(new TiffBuilder().Build(GrayPage(), CfaPage(), RgbPage()));
        var source = new TiffStackSource(file.Path, 3) { BayerOverride = BayerPattern.Rggb };
        foreach ((int page, BayerPattern expected) in new[]
        {
            (0, BayerPattern.Rggb), (1, BayerPattern.Rggb), (2, BayerPattern.None),
        })
        {
            DecodedImage decoded = await source.LoadPageAsync(page, CancellationToken.None);
            using RawImage image = decoded.Luminance;
            RawFormat format = source.GetPageFormat(decoded);

            Assert.Equal(expected, format.Bayer);
            Assert.Equal(
                ImageFileBayer.Apply(image.Format, decoded.Color is not null, source.BayerOverride),
                format);
        }
    }

    private static TiffBuilder.Page GrayPage()
    {
        return TiffBuilder.GrayPage(W, H, 16, Samples16());
    }

    private static TiffBuilder.Page CfaPage()
    {
        // CFAPattern 1,0,2,1 = G R / B G(GRBG)
        var page = TiffBuilder.GrayPage(W, H, 16, Samples16(), photometric: TiffLoader.PhotometricCfa);
        page.Tags[33421] = (3, new long[] { 2, 2 });
        page.Tags[33422] = (1, new byte[] { 1, 0, 2, 1 });
        return page;
    }

    private static TiffBuilder.Page RgbPage()
    {
        return TiffBuilder.RgbPage(W, H, Enumerable.Range(0, W * H * 3).Select(i => (byte)i).ToArray());
    }

    private static byte[] Samples16()
    {
        return TiffBuilder.SampleBytes(
            Enumerable.Range(0, W * H).Select(i => (ushort)(i * 1000)).ToArray(), 16);
    }
}

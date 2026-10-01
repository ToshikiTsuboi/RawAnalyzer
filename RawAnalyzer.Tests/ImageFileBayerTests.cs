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

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task BatchOfTiffStack_DevelopsGrayPageWithDesignation_WhicheverPageIsShown(int shownPage)
    {
        // 全体レビュー 2026-10-01 B18。1ページ目が RGB、2ページ目がグレーのスタックで、2ページ目で Bayer を
        // RGGB に指定してから RGB ページへ戻って一括書き出しすると、表示中の RGB ページのフォーマット
        // (Bayer=None)で全ページを焼き、グレーページが指定を無視してモノクロで出ていた。
        // 書き出しの Bayer はスタックが保持する指定から取り、どのページを表示中でも同じ結果にする
        using var file = TempTiff.Write(new TiffBuilder().Build(RgbPage(), GrayPage()));
        var stack = new TiffStackSource(file.Path, 2) { BayerOverride = BayerPattern.Rggb };
        DecodedImage decoded = await stack.LoadPageAsync(shownPage, CancellationToken.None);
        using (decoded.Luminance)
        {
            RawFormat shown = stack.GetPageFormat(decoded);
            Assert.Equal(shownPage == 0 ? BayerPattern.None : BayerPattern.Rggb, shown.Bayer);

            BayerPattern pattern = ImageFileBayer.ForBatch(
                rawTargets: false, shown.Bayer, stack, sequenceOverride: BayerPattern.Rggb);

            Assert.Equal(BayerPattern.Rggb, pattern);
            AssertGrayPageDeveloped(file.Path, pattern);
        }
    }

    [Fact]
    public void BatchOfImageSequence_TakesSequenceDesignation_WhileColorFileIsShown()
    {
        // 画像ファイルの連番(TIFF スタックなし、またはファイル連番で到達してページ送りしない TIFF)では、
        // 送りで引き継いでいる指定を使う。カラーのファイルの表示中でも次のグレーのファイルを現像する
        Assert.Equal(
            BayerPattern.Gbrg,
            ImageFileBayer.ForBatch(false, BayerPattern.None, stack: null, sequenceOverride: BayerPattern.Gbrg));
        var arrived = new TiffStackSource("seq_0001.tif", 3, pageNavigationEnabled: false)
        {
            BayerOverride = BayerPattern.None,
        };
        Assert.Equal(
            BayerPattern.Gbrg,
            ImageFileBayer.ForBatch(false, BayerPattern.None, arrived, sequenceOverride: BayerPattern.Gbrg));
    }

    [Fact]
    public void BatchOfRaw_TakesShownFormat()
    {
        // raw は常にグレーで、表示中のフォーマット(右パネルの指定を含む)で全ファイルを読む
        Assert.Equal(
            BayerPattern.Bggr,
            ImageFileBayer.ForBatch(true, BayerPattern.Bggr, stack: null, sequenceOverride: BayerPattern.Rggb));
    }

    private static void AssertGrayPageDeveloped(string path, BayerPattern pattern)
    {
        // WB ゲインを R だけ上げて焼き込む。現像すれば R と G がずれ、現像しなければ全画素 R=G=B のグレー
        var renderer = new BatchFrameRenderer(
            pattern, new DevelopParameters(GainR: 2.0, Gamma: 1.0), DisplayLut.Create(new DisplayParameters()));
        bool sawGray = false;
        foreach (FileFrame entry in FileFrameReader.Read(path, new RawFormat { Width = 1, Height = 1 }))
        {
            if (entry.Color is not null)
            {
                continue;
            }

            sawGray = true;
            byte[] rgb = renderer.RenderRgb24(entry, CancellationToken.None);
            Assert.Contains(Enumerable.Range(0, W * H), p => rgb[p * 3] != rgb[(p * 3) + 1]);
        }

        Assert.True(sawGray);
    }

    private static TiffBuilder.Page GrayPage()
    {
        return TiffBuilder.GrayPage(W, H, 16, Samples16());
    }

    private static TiffBuilder.Page CfaPage()
    {
        // CFAPattern 1,0,2,1 = G R / B G(GRBG)。TiffSpecTests.Cfa_IsReadAsBayerRaw の Grbg の対応
        // (ReadCfaPattern の表)を兼ねているので、パターンを変えるときは向こうへ Grbg の確認を戻す
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

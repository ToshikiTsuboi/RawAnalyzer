using RawAnalyzer.App.Rendering;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// HDR分割表示(露光ごとの画像を横に並べた表示)の段別LUT。各列は、その列に描く画素が属する段のLUTで描く。
/// </summary>
public class HdrSplitRenderTests
{
    [Theory]
    [InlineData(1648)] // 1/段幅 を掛けると 段幅×fl(1/段幅) が1未満になる段幅(段幅のおよそ13%がこうなる)
    [InlineData(49)]
    [InlineData(1920)] // 掛けても丸めの起きない段幅
    public void EachColumnUsesLutOfTheSegmentOfItsPixel(int segmentWidth)
    {
        // 段の判定を 1/段幅 の乗算にしていたため、段幅によっては描く位置がちょうど段の境目(段幅の整数倍)
        // に乗った列が、前の段のLUTで描かれていた(等倍で原点が x.5 のときなど、描く位置は整数になる)
        const ushort value = 40000;
        const int segments = 3;
        int width = segmentWidth * segments;
        var codes = new ushort[width];
        Array.Fill(codes, value);
        using RawImage image = TestImages.FromCodes(codes, width, 1);
        DisplayLut[] luts =
        {
            DisplayLut.Create(new DisplayParameters(Gain: 0.25)),
            DisplayLut.Create(new DisplayParameters(Gain: 0.5)),
            DisplayLut.Create(new DisplayParameters(Gain: 1.0)),
        };
        Assert.Equal(segments, luts.Select(lut => lut.Map(value)).Distinct().Count());
        var request = new RenderRequest
        {
            Source = new RawImageRenderSource(image, 0),
            Lut = luts[0],
            SegmentLuts = luts,
            SegmentWidth = segmentWidth,
        };

        for (int boundary = 1; boundary < segments; boundary++)
        {
            // 等倍で原点を x.5 に置くと、列 dx を描く位置は 原点 + dx + 0.5 で整数になる
            const int destWidth = 40;
            double originX = boundary * segmentWidth - (destWidth / 2) - 0.5;
            var pixels = new byte[destWidth * 4];
            ViewportRenderer.Render(request, 1.0, originX, 0, destWidth, 1, pixels, CancellationToken.None);
            for (int dx = 0; dx < destWidth; dx++)
            {
                int x = (int)(originX + dx + 0.5);
                byte expected = luts[x / segmentWidth].Map(value);
                Assert.True(
                    expected == pixels[dx * 4],
                    $"段幅 {segmentWidth} の列 x={x}: {pixels[dx * 4]}(期待 {expected}、段 {x / segmentWidth})");
            }
        }
    }
}

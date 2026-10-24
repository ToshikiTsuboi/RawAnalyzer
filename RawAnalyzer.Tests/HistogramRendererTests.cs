using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RawAnalyzer.App.Rendering;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

[Collection("WPF UI")]
public class HistogramRendererTests
{
    // 抽出前のMainWindowの描画結果から記録したBGRA画素のSHA-256。
    // 色・線幅・スケール・重ね描き順を含め、通常/累積と線形/対数の見た目を維持する。
    [Theory]
    [InlineData(false, false, false, "8DA0E6DB566463BD513BAB84A77416A853FFDB64295EA36B19105691F28BC3ED")]
    [InlineData(false, false, true, "BC052B6DDC1CF353D8B6343D9367C9156BBAF433E548050B8E9235EDDAB39F8E")]
    [InlineData(false, true, false, "C79D037FA133CB54A59030310CB53B373F36FD7F614CF2248ED32630CB7211B9")]
    [InlineData(false, true, true, "1F6395AD38EAB9D9DEC6C107DED1F87A7269467815D788F13113F7DE2F77C34F")]
    [InlineData(true, false, false, "6C74261547701850D211075C8184C307AB697402D97589EAF7B9CAF0596A7465")]
    [InlineData(true, false, true, "A32D309C81E207C808BB1699C2EC9CFF0033E295BD6D8F154AA20EDD0BB36951")]
    [InlineData(true, true, false, "0EB106202FB30AF024879B29800E95DA8F9E34F6D0E7B7FB2278451198FC6D5C")]
    [InlineData(true, true, true, "20001FDCE8D76359785EAAA3FD145F1CFB14C7AD70A333A03E6EA71FD10756BE")]
    public Task Render_PreservesExistingPixels(bool byChannel, bool logScale, bool cumulative, string expectedHash) =>
        WpfTestHost.Run(() =>
        {
            long[] bins = MakeBins(0);
            ChannelHistogram[] channels = MakeChannels();
            long[] originalBins = (long[])bins.Clone();
            long[][] originalChannels = channels.Select(c => (long[])c.Bins.Clone()).ToArray();

            BitmapSource bitmap = byChannel
                ? HistogramRenderer.RenderChannels(channels, logScale, cumulative)
                : HistogramRenderer.Render(bins, logScale, cumulative);

            Assert.Equal(210, bitmap.PixelWidth);
            Assert.Equal(70, bitmap.PixelHeight);
            Assert.Equal(PixelFormats.Bgra32, bitmap.Format);
            Assert.Equal(96, bitmap.DpiX);
            Assert.Equal(96, bitmap.DpiY);
            Assert.True(bitmap.IsFrozen);
            Assert.Equal(expectedHash, Convert.ToHexString(SHA256.HashData(ReadPixels(bitmap))));
            Assert.Equal(originalBins, bins);
            for (int i = 0; i < channels.Length; i++)
            {
                Assert.Equal(originalChannels[i], channels[i].Bins);
            }
        });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task Render_EmptyHistogramIsTransparent(bool logScale, bool cumulative) =>
        WpfTestHost.Run(() =>
        {
            BitmapSource bitmap = HistogramRenderer.Render(new long[256], logScale, cumulative);
            Assert.All(ReadPixels(bitmap), value => Assert.Equal((byte)0, value));
        });

    [Fact]
    public async Task Render_ResultCanBeReadFromAnotherThread()
    {
        BitmapSource? bitmap = null;
        await WpfTestHost.Run(() => { bitmap = HistogramRenderer.Render(MakeBins(0), false, false); });
        byte[] pixels = await Task.Run(() => ReadPixels(bitmap!));
        Assert.Contains((byte)0xFF, pixels);
    }

    private static byte[] ReadPixels(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    private static long[] MakeBins(int channel)
    {
        var bins = Enumerable.Range(0, 256).Select(code => (long)((code * (channel + 3) + 17) % 101)).ToArray();
        bins[0] = 0;
        bins[37 + channel * 51] = 5_000_000_000L;
        bins[255] = 9_000_000_000L;
        return bins;
    }

    private static ChannelHistogram[] MakeChannels() => new[] { BayerChannel.R, BayerChannel.Gr, BayerChannel.Gb, BayerChannel.B }
        .Select((channel, index) => new ChannelHistogram
        {
            Channel = channel,
            Bins = MakeBins(index),
            Statistics = new RegionStatistics(0, 0, 0, 0, 0),
        }).ToArray();
}

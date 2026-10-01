using System.Buffers.Binary;
using System.Text;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// ネットワーク上のTIFFをマップせずにストリームから読む経路の回帰テスト(全体レビュー 2026-10-01 B13)。
/// </summary>
/// <remarks>
/// ネットワーク上のファイルを直接マップすると、NAS の切断・SMB の再接続でページインが
/// EXCEPTION_IN_PAGE_ERROR になり、.NET では捕捉できずプロセスごと落ちる(RawLoader が写してから
/// マップするのと同じ理由)。ストリームから読めば IOException(理由付きの失敗)になる。実際の切断は
/// テストで起こせないので、ネットワーク上と判定されることと、ストリームの経路がマップと同じ結果に
/// なることを確かめる。
/// </remarks>
public class TiffStreamAccessTests
{
    private const int W = 37;
    private const int H = 9;

    [Theory]
    [InlineData(@"\\nas\share\stack.tif", true)]
    [InlineData(@"C:\Temp\stack.tif", false)]
    public void NetworkTiff_IsReadWithoutMapping(string path, bool expected)
    {
        Assert.Equal(expected, TiffLoader.ReadsWithoutMapping(path));
    }

    [Theory]
    [InlineData("packed12")]
    [InlineData("float32")]
    [InlineData("bigTiffRgbFloat32")]
    [InlineData("bigTiffTiled16")]
    [InlineData("imageJStack")]
    [InlineData("stripOutOfFile")]
    public void StreamAccess_ReadsTheSameAsMapping(string kind)
    {
        (byte[] bytes, int pageIndex) = Build(kind);
        using var file = TempTiff.Write(bytes);

        string mapped = Snapshot(file.Path, pageIndex);
        Assert.Contains(kind == "stripOutOfFile" ? "error " : "image ", mapped);
        TiffLoader.ForceStreamAccess.Value = true;
        string streamed;
        try
        {
            Assert.True(TiffLoader.ReadsWithoutMapping(file.Path));
            streamed = Snapshot(file.Path, pageIndex);
        }
        finally
        {
            TiffLoader.ForceStreamAccess.Value = false;
        }

        Assert.Equal(mapped, streamed);
    }

    /// <summary>ヘッダの読み取り・直接読み出しの判定・読み込み結果(画素とその注記、または例外)を文字列にする。</summary>
    private static string Snapshot(string path, int pageIndex)
    {
        var text = new StringBuilder();
        bool infoOk = TiffLoader.TryReadSampleInfo(path, out TiffSampleInfo? info, out string infoReason, pageIndex);
        text.AppendLine($"info {infoOk} {info} {infoReason}");
        bool probeOk = TiffLoader.TryProbePixelLayout(path, out TiffPixelLayout? layout, out string probeReason, pageIndex);
        text.AppendLine($"probe {probeOk} {layout} {probeReason}");
        try
        {
            DecodedImage decoded = ImageFileLoader.Load(path, pageIndex: pageIndex);
            using RawImage image = decoded.Luminance;
            text.AppendLine($"image {image.Format} {decoded.PageCount} {decoded.ValueNote}");
            var pixels = new ushort[image.Width * image.Height];
            image.CopyRegion(0, 0, 0, image.Width, image.Height, pixels);
            text.AppendLine(string.Join(",", pixels));
            if (decoded.Color is { } color)
            {
                for (int i = 0; i < color.Width * color.Height; i++)
                {
                    color.GetPixel(i % color.Width, i / color.Width, out ushort r, out ushort g, out ushort b);
                    text.Append($"{r}/{g}/{b},");
                }

                text.AppendLine();
            }
        }
        catch (InvalidDataException ex)
        {
            text.AppendLine($"error {ex.Message}");
        }

        return text.ToString();
    }

    private static (byte[] Bytes, int PageIndex) Build(string kind)
    {
        switch (kind)
        {
            case "packed12":
            {
                int[] values = Enumerable.Range(0, W * H).Select(i => i * 4095 / ((W * H) - 1)).ToArray();
                var page = TiffBuilder.GrayPage(W, H, 12, TiffBuilder.PackRows(values, W, 12), rowsPerStrip: 2);
                return (new TiffBuilder().Build(page), 0);
            }

            case "float32":
            {
                var samples = new byte[W * H * 4];
                for (int i = 0; i < W * H; i++)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(samples.AsSpan(i * 4), (i * 0.25f) - 3f);
                }

                return (new TiffBuilder().Build(TiffBuilder.GrayPage(W, H, 32, samples, sampleFormat: 3, rowsPerStrip: 4)), 0);
            }

            case "bigTiffRgbFloat32":
            {
                var samples = new byte[W * H * 3 * 4];
                for (int i = 0; i < W * H * 3; i++)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(samples.AsSpan(i * 4), (i % 97) * 0.5f);
                }

                var page = TiffBuilder.GrayPage(W, H, 32, samples, photometric: 2, sampleFormat: 3, samplesPerPixel: 3);
                return (new TiffBuilder(bigTiff: true).Build(page), 0);
            }

            case "bigTiffTiled16":
                return (new TiffBuilder(bigTiff: true).Build(TiffBuilder.TiledGrayPage(W, H, 16, Ramp16(), 16, 8)), 0);

            case "imageJStack":
            {
                const int frames = 3;
                var data = new byte[frames * W * H * 2];
                for (int i = 0; i < frames * W * H; i++)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(i * 2), (ushort)(i * 7));
                }

                var page = TiffBuilder.GrayPage(W, H, 16, data.AsSpan(0, W * H * 2).ToArray());
                page.Tags[270] = (2, Encoding.ASCII.GetBytes($"ImageJ=1.53t\nimages={frames}\n\0"));
                page.Trailer = data.AsSpan(W * H * 2).ToArray();
                return (new TiffBuilder().Build(page), 2);
            }

            case "stripOutOfFile":
            {
                // 壊れたファイルはどちらの経路でも同じ理由で読めない
                var page = TiffBuilder.GrayPage(W, H, 12, new byte[((W * 12) + 7) / 8 * H]);
                page.Blocks.Clear();
                page.Tags[273] = (4, new long[] { 1 << 20 });
                page.Tags[279] = (4, new long[] { ((W * 12) + 7) / 8 * H });
                return (new TiffBuilder().Build(page), 0);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    private static byte[] Ramp16()
    {
        var bytes = new byte[W * H * 2];
        for (int i = 0; i < W * H; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), (ushort)(i * 65535 / ((W * H) - 1)));
        }

        return bytes;
    }
}

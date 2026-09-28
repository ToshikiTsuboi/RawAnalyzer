using RawAnalyzer.App.Services;
using RawAnalyzer.App.Views;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 品質設定がH.264の出力に効いていることを、実際の書き出しで確かめる。
/// </summary>
/// <remarks>
/// 実測(640x480・純ノイズ・20フレーム)では、標準〜ほぼ無劣化で
/// 8.2→17.5 Mbps、復号後のPSNRが33.3→42.6 dBまで改善する。
/// 出力タイプのMF_MT_AVG_BITRATEだけでは環境により無視されるため、
/// エンコード設定をSetInputMediaTypeへ渡す経路が要る。
/// </remarks>
public class Mp4QualityTests
{
    private const int Width = 640;
    private const int Height = 480;
    private const int Frames = 20;
    private const int Fps = 15;

    /// <summary>
    /// センサ評価らしい「ノイズだらけ」のフレームを作る。
    /// 高周波成分が多く、H.264にとって最も圧縮しにくい入力になる。
    /// </summary>
    private static byte[] MakeNoisyFrame(int index)
    {
        var rgb = new byte[Width * Height * 3];
        uint state = (uint)(index * 2654435761u) | 1u;
        for (int i = 0; i < rgb.Length; i += 3)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            byte v = (byte)(110 + (state % 40));
            rgb[i] = v;
            rgb[i + 1] = v;
            rgb[i + 2] = v;
        }

        return rgb;
    }

    /// <summary>
    /// 書き出し先の一時ファイル名(呼ぶたびに一意。他のテストと同じ %TEMP%\RawAnalyzerTests の下)。
    /// </summary>
    /// <remarks>
    /// 以前は固定の名前(quality\standard.mp4 など)で、別の作業フォルダのテストが同時に走ると
    /// 同じファイルを開き合い、「別のプロセスが使用中」で失敗していた。
    /// </remarks>
    private static string TempPath()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RawAnalyzerTests");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Guid.NewGuid().ToString("N") + ".mp4");
    }

    private static long WriteAt(string path, int quality)
    {
        using (var writer = new Mp4H264Writer(path, Width, Height, Fps, quality))
        {
            for (int i = 0; i < Frames; i++)
            {
                writer.AddFrameRgb24(MakeNoisyFrame(i));
            }

            writer.Finish();
        }

        return new FileInfo(path).Length;
    }

    [Mp4Fact]
    public void HigherQuality_ProducesLargerFile()
    {
        string low = TempPath();
        string high = TempPath();
        try
        {
            long lowSize = WriteAt(low, VideoQualitySettings.EncoderQuality(VideoQuality.Standard));
            long highSize = WriteAt(
                high, VideoQualitySettings.EncoderQuality(VideoQuality.NearLossless));

            // ビットレート指定が効いていれば、同じ素材でも明確に容量が増える
            Assert.True(
                highSize > lowSize * 2,
                $"品質がビットレートに効いていない (標準 {lowSize} / 最高 {highSize})");
        }
        finally
        {
            File.Delete(low);
            File.Delete(high);
        }
    }
}

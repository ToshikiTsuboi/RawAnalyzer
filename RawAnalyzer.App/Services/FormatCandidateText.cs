using System.Globalization;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>
/// フォーマット候補を一覧(インポートダイアログ)・ツールチップに出す文。
/// </summary>
internal static class FormatCandidateText
{
    /// <summary>候補の由来の表示名。</summary>
    /// <param name="source">候補の由来。</param>
    /// <returns>表示名。</returns>
    internal static string SourceLabel(FormatCandidateSource source)
    {
        return source switch
        {
            FormatCandidateSource.SameSizeMemory => "記憶(同じサイズ)",
            FormatCandidateSource.DisplayedImage => "表示中の画像",
            FormatCandidateSource.MemoryFrameCount
                or FormatCandidateSource.MemoryHeight
                or FormatCandidateSource.MemoryResolution => "記憶から推定",
            FormatCandidateSource.FileName => "ファイル名",
            _ => "解像度表",
        };
    }

    /// <summary>候補を一覧の1行にする(「由来: 1920×1080 12bit RGGB …」)。</summary>
    /// <param name="candidate">候補。</param>
    /// <returns>表示する文。</returns>
    internal static string Display(FormatCandidate candidate)
    {
        return $"{SourceLabel(candidate.Source)}: {Describe(candidate.Format)}";
    }

    /// <summary>
    /// フォーマットを短く表す。寸法・ビット深度・Bayer は常に、詰め・エンディアン・ヘッダ・
    /// フレーム数・HDR は既定と違うときだけ出す。
    /// </summary>
    /// <param name="format">フォーマット。</param>
    /// <returns>表示する文。</returns>
    internal static string Describe(RawFormat format)
    {
        var parts = new List<string>
        {
            $"{format.Width}×{format.Height}",
            $"{format.BitDepth}bit",
            format.Bayer switch
            {
                BayerPattern.Rggb => "RGGB",
                BayerPattern.Bggr => "BGGR",
                BayerPattern.Grbg => "GRBG",
                BayerPattern.Gbrg => "GBRG",
                _ => "モノクロ",
            },
        };
        if (format.Packing == BitPacking.Msb)
        {
            parts.Add("上詰め");
        }

        if (format.Endianness == Endianness.Big)
        {
            parts.Add("Big");
        }

        if (format.HeaderOffset != 0)
        {
            parts.Add($"ヘッダ{format.HeaderOffset.ToString(CultureInfo.InvariantCulture)}B");
        }

        if (format.FrameCount != 1)
        {
            parts.Add($"{format.FrameCount.ToString(CultureInfo.InvariantCulture)}fr");
        }

        if (format.Hdr != HdrMode.None)
        {
            string layout = format.Hdr switch
            {
                HdrMode.LineInterleaved => "行交互",
                HdrMode.FrameSequential => "フレーム連結",
                _ => "自動",
            };
            parts.Add($"HDR {layout} {format.HdrStages.ToString(CultureInfo.InvariantCulture)}段");
        }

        return string.Join(" ", parts);
    }
}

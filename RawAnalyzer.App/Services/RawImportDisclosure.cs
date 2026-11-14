using System.Globalization;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

internal static class RawImportDisclosure
{
    internal static bool ShouldExpandDetails(bool applyingFormat, bool hasInvalidField,
        BitPacking packing, Endianness endianness, bool hasHeader, bool hasMultipleFrames) =>
        hasInvalidField || (applyingFormat &&
            (packing != BitPacking.Lsb || endianness != Endianness.Little || hasHeader || hasMultipleFrames));

    internal static bool ShowHdrDetails(HdrMode mode, bool hasInvalidField) =>
        mode != HdrMode.None || hasInvalidField;

    internal static string DetailsHeading(bool expanded, BitPacking packing, Endianness endianness,
        long headerOffset, int frameCount)
    {
        if (expanded)
        {
            return "詳細";
        }

        var parts = new List<string> { "詳細" };
        if (packing == BitPacking.Msb)
        {
            parts.Add("上詰め");
        }
        if (endianness == Endianness.Big)
        {
            parts.Add("Big");
        }
        if (headerOffset > 0)
        {
            parts.Add($"ヘッダ{headerOffset.ToString(CultureInfo.InvariantCulture)}B");
        }
        if (frameCount > 1)
        {
            parts.Add($"{frameCount.ToString(CultureInfo.InvariantCulture)}fr");
        }

        return string.Join(" · ", parts);
    }
}

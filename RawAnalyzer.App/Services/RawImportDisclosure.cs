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
}

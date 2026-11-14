using System.Globalization;
using RawAnalyzer.App.Compare;
using RawAnalyzer.App.ViewModels;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

internal static class FileListDetails
{
    internal static string Describe(FileEntry entry, IReadOnlyFormatHistory history)
    {
        if (ComparePane.IsRawFile(entry.Name) && entry.Length > 0)
        {
            var candidates = FormatCandidates.Build(entry.Length, FormatHistory.ExtensionOf(entry.Name),
                entry.Name, history, displayedFormat: null, maxCount: 1);
            if (candidates.Count > 0)
            {
                return FormatCandidateText.Describe(candidates[0].Format, separator: " · ");
            }
        }

        return FormatSize(entry.Length);
    }

    internal static string FormatSize(long length)
    {
        if (length < 0) return "サイズ不明";
        if (length >= 1L << 30) return (length / (double)(1L << 30)).ToString("0.##", CultureInfo.InvariantCulture) + " GiB";
        if (length >= 1L << 20) return (length / (double)(1L << 20)).ToString("0.##", CultureInfo.InvariantCulture) + " MiB";
        if (length >= 1L << 10) return (length / (double)(1L << 10)).ToString("0.##", CultureInfo.InvariantCulture) + " KiB";
        return length.ToString(CultureInfo.InvariantCulture) + " B";
    }
}

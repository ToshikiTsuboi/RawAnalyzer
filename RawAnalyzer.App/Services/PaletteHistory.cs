namespace RawAnalyzer.App.Services;

internal static class PaletteHistory
{
    internal const string PaletteId = "palette";

    internal static string[] Combine(IEnumerable<string> newer, IEnumerable<string> older) =>
        newer.Concat(older).Where(id => !string.IsNullOrWhiteSpace(id) && id != PaletteId)
            .Distinct(StringComparer.Ordinal).Take(8).ToArray();
}

using System.Globalization;

namespace RawAnalyzer.App.Compare;

/// <summary>比較ファイル名の共通部分と、色を付けて残す差分。</summary>
internal readonly record struct PaneNameDisplay(string Prefix, string Difference, string Suffix)
{
    internal string FullName => Prefix + Difference + Suffix;

    internal PaneNameDisplay ForWidth(double availableWidth, double fullWidth) =>
        fullWidth <= availableWidth || (Difference.Length == 0 && Suffix.Length == 0)
            ? this
            : new(Prefix.Length > 0 ? "…" : "", Difference, Suffix.Length > 0 ? "…" : "");

    internal PaneNameDisplay DifferenceOnly() => Difference.Length > 0 ? new("", Difference, "") : this;

    internal static PaneNameDisplay[] ForFiles(IReadOnlyList<string> names)
    {
        if (names.Count <= 1 || names.All(name => name == names[0]))
        {
            return names.Select(name => new PaneNameDisplay(name, "", "")).ToArray();
        }

        // 拡張子まで一致する場合も、共通の先頭と末尾が重ならない範囲で切る。
        // 書記素単位で比較し、サロゲートペアや結合文字の途中で分けない。
        string[][] elements = names.Select(name =>
            StringInfo.ParseCombiningCharacters(name)
                .Select(start => StringInfo.GetNextTextElement(name, start)).ToArray()).ToArray();
        int shortest = elements.Min(name => name.Length);
        int prefix = 0;
        while (prefix < shortest && elements.All(name => name[prefix] == elements[0][prefix]))
        {
            prefix++;
        }

        int suffix = 0;
        while (suffix < shortest - prefix
               && elements.All(name => name[^(suffix + 1)] == elements[0][^(suffix + 1)]))
        {
            suffix++;
        }

        return elements.Select(name => new PaneNameDisplay(
            string.Concat(name[..prefix]), string.Concat(name[prefix..(name.Length - suffix)]),
            string.Concat(name[(name.Length - suffix)..]))).ToArray();
    }
}

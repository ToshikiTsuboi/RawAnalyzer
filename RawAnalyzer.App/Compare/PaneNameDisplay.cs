using System.Globalization;

namespace RawAnalyzer.App.Compare;

/// <summary>比較ファイル名の共通部分と、色を付けて残す差分。</summary>
internal readonly record struct PaneNameDisplay(string Prefix, string Difference, string Suffix)
{
    internal string FullName => Prefix + Difference + Suffix;

    internal PaneNameDisplay ForWidth(double availableWidth, double fullWidth, Func<string, double> measure)
    {
        if (fullWidth <= availableWidth || Difference.Length == 0)
        {
            return this;
        }

        var (prefixText, difference, suffixText) = this;
        int[] prefixStarts = StringInfo.ParseCombiningCharacters(prefixText);
        int[] suffixStarts = StringInfo.ParseCombiningCharacters(suffixText);
        const string ellipsis = "…";
        double ellipsisWidth = measure(ellipsis);
        int prefixCount = 0;
        int suffixCount = 0;
        PaneNameDisplay shown = Slice(0, 0);
        bool expanded;
        do
        {
            expanded = false;
            // 差分に隣接する書記素を前後から交互に足す。先頭側の文脈を先に試す。
            if (prefixCount < prefixStarts.Length && Fits(Slice(prefixCount + 1, suffixCount)))
            {
                prefixCount++;
                expanded = true;
            }

            if (suffixCount < suffixStarts.Length && Fits(Slice(prefixCount, suffixCount + 1)))
            {
                suffixCount++;
                expanded = true;
            }
        }
        while (expanded);

        return shown;

        bool Fits(PaneNameDisplay candidate)
        {
            if (measure(candidate.FullName) > availableWidth)
            {
                return false;
            }

            shown = candidate;
            return true;
        }

        PaneNameDisplay Slice(int left, int right)
        {
            int prefixEnd = left == 0 ? prefixText.Length : prefixStarts[^left];
            int suffixStart = right == suffixStarts.Length ? suffixText.Length : suffixStarts[right];
            string prefix = prefixEnd > 0 && measure(prefixText[..prefixEnd]) > ellipsisWidth
                ? ellipsis + prefixText[prefixEnd..] : prefixText;
            string suffix = suffixStart < suffixText.Length && measure(suffixText[suffixStart..]) > ellipsisWidth
                ? suffixText[..suffixStart] + ellipsis : suffixText;
            return new PaneNameDisplay(prefix, difference, suffix);
        }
    }

    internal static PaneNameDisplay[] ForFiles(IReadOnlyList<string> names)
    {
        // 拡張子まで一致する場合も、共通の先頭と末尾が重ならない範囲で切る。
        // 書記素単位で比較し、サロゲートペアや結合文字の途中で分けない。
        string[][] elements = names.Select(name =>
            StringInfo.ParseCombiningCharacters(name)
                .Select(start => StringInfo.GetNextTextElement(name, start)).ToArray()).ToArray();
        var displays = new PaneNameDisplay[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            string[] name = elements[i];
            (int Prefix, int Suffix)? best = null;
            for (int j = 0; j < names.Count; j++)
            {
                // 同名ペイン同士では差分が取れないため、異なる名前との比較に使う。
                if (names[i] == names[j])
                {
                    continue;
                }

                var shared = SharedEnds(name, elements[j]);
                if (best is not { } previous
                    || shared.Prefix + shared.Suffix > previous.Prefix + previous.Suffix
                    || (shared.Prefix + shared.Suffix == previous.Prefix + previous.Suffix
                        && shared.Prefix > previous.Prefix))
                {
                    best = shared;
                }
            }

            displays[i] = best is { } ends && ends.Prefix + ends.Suffix < name.Length
                ? new PaneNameDisplay(string.Concat(name[..ends.Prefix]),
                    string.Concat(name[ends.Prefix..(name.Length - ends.Suffix)]),
                    string.Concat(name[(name.Length - ends.Suffix)..]))
                : new PaneNameDisplay(names[i], "", "");
        }

        return displays;
    }

    private static (int Prefix, int Suffix) SharedEnds(string[] name, string[] other)
    {
        int shortest = Math.Min(name.Length, other.Length);
        int prefix = 0;
        while (prefix < shortest && name[prefix] == other[prefix])
        {
            prefix++;
        }

        int suffix = 0;
        while (suffix < shortest - prefix && name[^(suffix + 1)] == other[^(suffix + 1)])
        {
            suffix++;
        }

        return (prefix, suffix);
    }
}

using System.IO;
using System.Text.RegularExpressions;

namespace RawAnalyzer.App.Services;

/// <summary>
/// ファイル一覧の絞り込み条件。
/// </summary>
/// <remarks>
/// 書式は次のとおり(いずれも大文字小文字を区別しない)。
/// <list type="bullet">
/// <item><description>空文字: すべて表示。</description></item>
/// <item><description><c>/pattern/</c>: 正規表現をファイル名(パスを含まない)に適用する。
/// 発展的な用途向けで、不正なパターンは <see cref="Error"/> に説明を入れ、絞り込みは行わない。</description></item>
/// <item><description>それ以外: 空白・<c>;</c>・<c>,</c> で区切った語のいずれかに一致すれば表示(OR)。
/// IME をオンのまま区切ったときの全角スペース・全角の <c>；</c>・<c>，</c> も区切りとみなす。
/// <c>*</c> か <c>?</c> を含む語はワイルドカード(ファイル名全体に一致)、
/// <c>.</c> で始まる語は拡張子の一致、その他の語は部分一致。</description></item>
/// </list>
/// </remarks>
internal sealed class FileNameFilter
{
    // ファイル名は短いので通常は一瞬で終わる。破滅的なバックトラックを起こす
    // パターンを入力されても UI スレッドを長く止めないための上限
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly char[] WildcardChars = { '*', '?' };

    private readonly Func<string, bool>[] _terms;
    private readonly Regex? _regex;

    private FileNameFilter(string text, Func<string, bool>[] terms, Regex? regex, string? error)
    {
        Text = text;
        _terms = terms;
        _regex = regex;
        Error = error;
    }

    /// <summary>何も絞り込まない条件。</summary>
    public static FileNameFilter Empty { get; } = new("", Array.Empty<Func<string, bool>>(), null, null);

    /// <summary>解釈した入力(前後の空白を除いたもの)。</summary>
    public string Text { get; }

    /// <summary>正規表現として解釈されたか。</summary>
    public bool IsRegex => _regex is not null;

    /// <summary>入力が不正な場合の説明。正常なら null。</summary>
    public string? Error { get; }

    /// <summary>条件が空(すべて表示)か。不正な入力は空ではない。</summary>
    public bool IsEmpty => _terms.Length == 0 && _regex is null && Error is null;

    /// <summary>入力文字列を解釈する。不正な正規表現でも例外にはしない。</summary>
    /// <param name="text">入力文字列(null 可)。</param>
    /// <returns>解釈した条件。</returns>
    public static FileNameFilter Parse(string? text)
    {
        string trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return Empty;
        }

        if (trimmed.Length >= 2 && trimmed[0] == '/' && trimmed[^1] == '/')
        {
            string pattern = trimmed[1..^1];
            if (pattern.Length == 0)
            {
                return Empty;
            }

            try
            {
                var regex = new Regex(
                    pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
                return new FileNameFilter(trimmed, Array.Empty<Func<string, bool>>(), regex, null);
            }
            catch (ArgumentException ex)
            {
                return new FileNameFilter(
                    trimmed, Array.Empty<Func<string, bool>>(), null, $"正規表現が不正です: {ex.Message}");
            }
        }

        Func<string, bool>[] terms = SplitTerms(trimmed).Select(BuildTerm).ToArray();
        return new FileNameFilter(trimmed, terms, null, null);
    }

    /// <summary>入力を語に分ける。続いた区切りは1つとみなす。</summary>
    /// <remarks>
    /// 日本語のファイル名の語を並べるときは IME がオンのままなので、スペースは全角(U+3000)になる。
    /// 以前は半角スペース・タブ・<c>;</c>・<c>,</c> だけで区切り、「暗室　フラット」を1語の部分一致として探して
    /// 何にも一致しなかった。空白はすべて(全角スペースを含む)、<c>;</c>・<c>,</c> は全角の形も区切りとする。
    /// </remarks>
    private static IEnumerable<string> SplitTerms(string text)
    {
        int start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && !IsSeparator(text[i]))
            {
                continue;
            }

            if (i > start)
            {
                yield return text[start..i];
            }

            start = i + 1;
        }
    }

    private static bool IsSeparator(char c) => char.IsWhiteSpace(c) || c is ';' or ',' or '；' or '，';

    /// <summary>ファイル名(パスを含まない)が条件に一致するか。不正な条件はすべて一致とみなす。</summary>
    /// <param name="fileName">判定するファイル名。</param>
    /// <returns>表示すべきなら true。</returns>
    public bool IsMatch(string fileName)
    {
        if (Error is not null)
        {
            return true;
        }

        if (_regex is not null)
        {
            try
            {
                return _regex.IsMatch(fileName);
            }
            catch (RegexMatchTimeoutException)
            {
                return true;
            }
        }

        if (_terms.Length == 0)
        {
            return true;
        }

        foreach (Func<string, bool> term in _terms)
        {
            if (term(fileName))
            {
                return true;
            }
        }

        return false;
    }

    private static Func<string, bool> BuildTerm(string token)
    {
        if (token.IndexOfAny(WildcardChars) >= 0)
        {
            string pattern = "^" + Regex.Escape(token).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
            var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return name => regex.IsMatch(name);
        }

        if (token.Length > 1 && token[0] == '.')
        {
            return name => string.Equals(
                Path.GetExtension(name), token, StringComparison.OrdinalIgnoreCase);
        }

        return name => name.Contains(token, StringComparison.OrdinalIgnoreCase);
    }
}

namespace RawAnalyzer.App.Services;

/// <summary>
/// アクセスキーを解釈する見出し(メニュー項目など)へ、ファイル名などの文字列を文字どおりに出すための変換。
/// </summary>
/// <remarks>
/// WPF のメニュー項目は見出しの文字列を AccessText として表示し(テーマのテンプレートが RecognizesAccessKey)、
/// 最初の「_」を消して次の文字をアクセスキーにする。センサ評価のファイル名(dark_10ms_001.raw など)は「_」を
/// 含むことが多く、「最近使ったファイル」で dark10ms_001.raw と表示され、img_a.raw と imga.raw が区別できなかった。
/// </remarks>
internal static class AccessKeyText
{
    /// <summary>「_」を「__」にして、アクセスキーとして解釈されないようにする。</summary>
    /// <param name="text">そのまま表示したい文字列。</param>
    /// <returns>見出しに渡す文字列。</returns>
    internal static string Escape(string text) => text.Replace("_", "__", StringComparison.Ordinal);
}

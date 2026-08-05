using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace RawAnalyzer.App.Services;

/// <summary>
/// 実行中のバイナリがいつ・どのコミットから作られたかを示すビルド情報。
/// 値は csproj の EmbedBuildInfo ターゲットが AssemblyMetadata として埋め込む。
/// </summary>
/// <remarks>
/// 不具合報告のときに「どのビルドで起きたか」を特定できるようにするのが目的。
/// git が無い環境でビルドされた場合は各項目が空になる(表示側で「不明」を出す)。
/// </remarks>
internal static class BuildInfo
{
    private static readonly Dictionary<string, string> Metadata = ReadMetadata();

    /// <summary>アセンブリのバージョン(リリース時はタグの値)。</summary>
    public static string Version { get; } =
        typeof(BuildInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>ビルド日時(ローカル時刻)。取得できなければnull。</summary>
    public static DateTimeOffset? BuildTime { get; } = ParseBuildTime(Get("BuildTimeUtc"));

    /// <summary>ビルド構成(Debug / Release)。</summary>
    public static string Configuration { get; } = Get("BuildConfiguration");

    /// <summary>コミットハッシュ(40桁)。取得できなければ空。</summary>
    public static string Commit { get; } = Get("GitCommit");

    /// <summary>ブランチ名。取得できなければ空。</summary>
    public static string Branch { get; } = Get("GitBranch");

    /// <summary>直近タグからの相対位置(例 v1.2.0 / v1.2.0-3-gabc1234)。</summary>
    public static string Describe { get; } = Get("GitDescribe");

    /// <summary>ビルド時に未コミットの変更があったか。</summary>
    public static bool IsDirty { get; } =
        string.Equals(Get("GitDirty"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>リポジトリのURL。</summary>
    public static string RepositoryUrl { get; } = Get("RepositoryUrl");

    /// <summary>ライセンス名。</summary>
    public const string License = "MIT License";

    /// <summary>著作権表示。</summary>
    public static string Copyright { get; } =
        typeof(BuildInfo).Assembly
            .GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "";

    /// <summary>コミットハッシュの短縮形(12桁)。</summary>
    public static string ShortCommit => ShortenCommit(Commit);

    /// <summary>
    /// バージョン表示。タグ上のビルドならバージョンだけ、
    /// タグから進んでいる/未コミット変更ありなら開発ビルドである旨を添える。
    /// </summary>
    public static string VersionLabel => FormatVersionLabel(Version, Describe, IsDirty);

    /// <summary>ランタイムの表示文字列。</summary>
    public static string Runtime =>
        $".NET {Environment.Version} / WPF ({RuntimeInformation.ProcessArchitecture})";

    /// <summary>
    /// 「タグと一致し未コミット変更もない」= 配布物と同一のビルドか。
    /// </summary>
    public static bool IsTaggedBuild => IsTagged(Version, Describe, IsDirty);

    /// <summary>コミットハッシュを短縮する。</summary>
    /// <param name="commit">コミットハッシュ。</param>
    /// <returns>先頭12桁。空なら空文字。</returns>
    internal static string ShortenCommit(string commit)
    {
        return commit.Length > 12 ? commit[..12] : commit;
    }

    /// <summary>
    /// バージョン表示を組み立てる。
    /// </summary>
    /// <param name="version">アセンブリバージョン。</param>
    /// <param name="describe">git describe の出力(空可)。</param>
    /// <param name="dirty">未コミットの変更があるか。</param>
    /// <returns>表示用の文字列。</returns>
    internal static string FormatVersionLabel(string version, string describe, bool dirty)
    {
        if (IsTagged(version, describe, dirty))
        {
            return version;
        }

        // タグから進んでいる場合、アセンブリバージョンは csproj の既定値(1.0.0)のままで
        // 実体を表さない。git describe の方が「何を動かしているか」を正しく示す
        string basis = describe.Length > 0 ? describe : version;
        return dirty ? $"{basis} (開発ビルド・未コミットの変更あり)" : $"{basis} (開発ビルド)";
    }

    private static bool IsTagged(string version, string describe, bool dirty)
    {
        // リリースワークフローは v1.2.0 のタグから Version=1.2.0 を渡すため、
        // タグ上のビルドでは describe が "v" + version と完全一致する
        return !dirty && describe.Length > 0
            && string.Equals(describe, "v" + version, StringComparison.Ordinal);
    }

    /// <summary>不具合報告に貼り付けられる形式でビルド情報を組み立てる。</summary>
    /// <returns>複数行のテキスト。</returns>
    public static string ToReportText()
    {
        var sb = new StringBuilder();
        sb.Append("RawAnalyzer ").AppendLine(VersionLabel);
        sb.Append("コミット: ").AppendLine(Commit.Length > 0
            ? Commit + (Branch.Length > 0 ? $" ({Branch})" : "")
            : "不明");
        sb.Append("ビルド: ").AppendLine(FormatBuild());
        sb.Append("ランタイム: ").AppendLine(Runtime);
        sb.Append("OS: ").AppendLine(RuntimeInformation.OSDescription);
        return sb.ToString();
    }

    /// <summary>ビルド構成と日時の表示文字列。</summary>
    /// <returns>「Release / 2026-08-04 19:32:11 +09:00」形式。</returns>
    public static string FormatBuild()
    {
        string configuration = Configuration.Length > 0 ? Configuration : "不明";
        if (BuildTime is not { } time)
        {
            return configuration;
        }

        return $"{configuration} / " +
            time.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset? ParseBuildTime(string value)
    {
        return DateTimeOffset.TryParse(
            value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out DateTimeOffset parsed)
            ? parsed.ToLocalTime()
            : null;
    }

    private static string Get(string key)
    {
        return Metadata.TryGetValue(key, out string? value) ? value : "";
    }

    private static Dictionary<string, string> ReadMetadata()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (AssemblyMetadataAttribute attribute in
            typeof(BuildInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            // 同名キーは最初の1つを採用する(SDKが重複を出すことがある)
            map.TryAdd(attribute.Key, attribute.Value ?? "");
        }

        return map;
    }
}

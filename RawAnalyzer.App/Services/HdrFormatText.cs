using System.Globalization;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>フォーマットのHDR設定の文字列(右パネルのフォーマット欄の要約)。</summary>
/// <remarks>
/// <para>
/// ライン単位と行オフセットは行交互の分割だけが使う(フレーム連結は各フレームが1つの露光で、整列しない)。
/// 出すかどうかは指定ではなく解決したレイアウト(<see cref="HdrSplitter.ResolveLayout"/>)で決める。以前は指定で
/// 判定しており、自動でフレーム連結に解決したときや、レイアウトを決められないときも、使わないライン単位・
/// 行オフセットを適用中のように出していた。
/// </para>
/// <para>
/// 露光比は整数に丸めない(F0 では 2.5 が 3、1.4 が 1 に見えた)。HDR派生ビューの来歴と同じ 0.### で書く。
/// </para>
/// </remarks>
internal static class HdrFormatText
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>フォーマット欄のHDR設定の要約。</summary>
    /// <param name="format">表示中の画像のフォーマット(右パネルで指定した Bayer を含む)。</param>
    /// <returns>要約(HDRでなければ「なし」)。</returns>
    internal static string DescribePanel(RawFormat format)
    {
        if (format.Hdr == HdrMode.None)
        {
            return "なし";
        }

        HdrMode? layout = TryResolveLayout(format);
        string layoutText = layout switch
        {
            HdrMode.LineInterleaved => "行交互",
            HdrMode.FrameSequential => "フレーム連結",
            _ => "レイアウト不明",
        };
        if (layout is not null && format.Hdr == HdrMode.Auto)
        {
            layoutText += "(自動)";
        }

        string detail = layout == HdrMode.LineInterleaved
            ? $" / {format.EffectiveHdrLineBlock}行単位"
                + (format.HdrRowOffset != 0 ? $" / 行オフセット{FormatRowOffset(format.HdrRowOffset)}" : "")
            : "";
        return $"{layoutText} {format.HdrStages}段 (露光比 {FormatRatio(format.ExposureRatio)}){detail}";
    }

    /// <summary>露光比(整数に丸めない。HDR派生ビューの来歴と同じ書式)。</summary>
    private static string FormatRatio(double ratio) => ratio.ToString("0.###", Invariant);

    /// <summary>行オフセット(符号付き。0は「0」)。</summary>
    private static string FormatRowOffset(int offset) => offset.ToString("+#;-#;0", Invariant);

    /// <summary>実際に使う格納レイアウト。自動で決められないときは null。</summary>
    private static HdrMode? TryResolveLayout(RawFormat format)
    {
        try
        {
            return HdrSplitter.ResolveLayout(format, format.HdrStages);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}

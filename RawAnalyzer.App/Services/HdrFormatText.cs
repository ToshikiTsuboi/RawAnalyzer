using System.Globalization;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>フォーマットのHDR設定の文字列(右パネルのフォーマット欄の要約と、保存の付随テキストのHDR行)。</summary>
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

    /// <summary>
    /// 保存の付随テキストの[入力フォーマット]のHDR行の値(HDR方式・段数・露光比と、行交互ならライン単位・行オフセット)。
    /// </summary>
    /// <remarks>
    /// 行交互ではライン単位が行の振り分けを、行オフセットが各段の切り出し位置と Bayer 位相を決める。書かないと、
    /// ライン単位1と2や行オフセット+2と−2のように派生画像の寸法が同じになる分割・合成を後から区別できない。
    /// ライン単位は実効値(未指定なら Bayer で決まる値)、行オフセットは0でも書く。
    /// </remarks>
    /// <param name="format">元画像のフォーマット(右パネルで指定した Bayer を含む。分割・合成に使ったもの)。</param>
    /// <returns>HDR行の値(HDRでなければ「None」)。</returns>
    internal static string DescribeSidecar(RawFormat format)
    {
        if (format.Hdr == HdrMode.None)
        {
            return format.Hdr.ToString();
        }

        string text = $"{format.Hdr} {format.HdrStages}段 露光比{FormatRatio(format.ExposureRatio)}";
        return TryResolveLayout(format) == HdrMode.LineInterleaved
            ? text + $" ライン単位{format.EffectiveHdrLineBlock} 行オフセット{FormatRowOffset(format.HdrRowOffset)}"
            : text;
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

using System.Globalization;
using System.Text;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>
/// HDR派生ビュー(分割・合成)の表示中に保存したとき、保存の付随テキストへ書く来歴の節を作る。
/// </summary>
/// <remarks>
/// <para>
/// 付随テキストは、保存した画像に何が適用されたかを実体と食い違わずに残すためのもの。派生ビューから保存した
/// 画像は元ファイルの画素ではなく、HDR分割の並置画像やHDR合成の16bit量子化画像(float raw なら量子化前の合成値)
/// になる。以前はそのことが書かれず、入力フォーマットも派生画像の形式(HDR: None)になっていた。
/// </para>
/// <para>
/// 合成ビューの表示黒点は減算済みの0から始まるので、[適用処理]の黒点は合成で減算した黒点とは別物になる。
/// 合成で減算した黒点・露光比・段数は、合成結果(<see cref="HdrImage.Parameters"/>)が持つ計算に使った値を書く
/// (計算中や計算後に動かしたスライダーの値と取り違えない)。
/// </para>
/// </remarks>
internal static class HdrViewSidecar
{
    private const string Header = "[HDR派生ビュー]";

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>HDR分割の派生ビュー(長秒→短秒の各段を左から並置した画像)の来歴。</summary>
    /// <param name="derived">派生画像(並置画像)のフォーマット。</param>
    /// <param name="stages">段数。</param>
    /// <param name="source">元画像のフォーマット(HDR方式を含む)。</param>
    /// <param name="sourceFrame">分割した元画像のフレーム番号(行交互の複数フレームの画像のときだけ書く)。</param>
    /// <returns>付随テキストの節(改行で終わる)。</returns>
    internal static string DescribeSplit(RawFormat derived, int stages, RawFormat source, int sourceFrame)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Header);
        sb.Append("  種類: HDR分割 (左: 長秒 → 右: 短秒, ").Append(stages).AppendLine("段)");
        AppendSourceFrame(sb, source, sourceFrame);
        sb.Append("  派生画像: ").Append(DescribeImage(derived))
            .Append(" (各段 ").Append(derived.Width / Math.Max(1, stages)).Append('×').Append(derived.Height)
            .AppendLine(" を左から並置)");
        return sb.ToString();
    }

    /// <summary>HDR合成の派生ビュー(黒レベルを減算して合成し、16bitへ量子化した画像)の来歴。</summary>
    /// <param name="derived">派生画像(16bit量子化画像)のフォーマット。</param>
    /// <param name="merged">合成結果(合成に使ったパラメータを持つ)。</param>
    /// <param name="source">元画像のフォーマット(HDR方式を含む)。</param>
    /// <param name="sourceFrame">合成した元画像のフレーム番号(行交互の複数フレームの画像のときだけ書く)。</param>
    /// <param name="floatRawOutput">float raw(量子化前の合成値)で保存したか。</param>
    /// <returns>付随テキストの節(改行で終わる)。</returns>
    internal static string DescribeMerge(
        RawFormat derived, HdrImage merged, RawFormat source, int sourceFrame, bool floatRawOutput)
    {
        HdrMergeParameters parameters = merged.Parameters;
        int bitDepth = merged.SourceBitDepth;
        var sb = new StringBuilder();
        sb.AppendLine(Header);
        sb.Append("  種類: HDR合成 (").Append(merged.Stages).Append("段, 露光比 ")
            .Append(parameters.ExposureRatio.ToString("0.###", Invariant)).AppendLine(")");
        AppendSourceFrame(sb, source, sourceFrame);
        sb.Append("  合成で減算した黒点: ").Append(parameters.BlackLevel)
            .Append(" (").Append(bitDepth).Append("bitのcode ")
            .Append(parameters.BlackLevel >> (16 - bitDepth)).AppendLine(")");
        sb.Append("  合成域のフルスケール: ").AppendLine(merged.FullScale.ToString("F0", Invariant));
        if (floatRawOutput)
        {
            sb.Append("  保存した値: 量子化前の合成値 (float32・").Append(merged.Width).Append('×')
                .Append(merged.Height).Append("・Bayer ").Append(merged.Bayer).AppendLine("・黒点減算済み)");
            return sb.ToString();
        }

        sb.Append("  派生画像: ").Append(DescribeImage(derived))
            .AppendLine(" (合成域のフルスケールを65535へ量子化)");
        sb.Append("  量子化の1LSB: ").Append(merged.QuantizationStep.ToString("F1", Invariant))
            .Append(merged.LostBits >= 0.5
                ? $" (元素材比 約{merged.LostBits.ToString("F0", Invariant)}bit損失)"
                : " (元素材比で損失なし)")
            .AppendLine();
        return sb.ToString();
    }

    private static string DescribeImage(RawFormat format) =>
        $"{format.Width}×{format.Height} · {format.BitDepth}bit · Bayer {format.Bayer}";

    /// <summary>
    /// 行交互の複数フレームの画像なら、派生ビューの元にしたフレームを書く。
    /// </summary>
    /// <remarks>
    /// 行交互は1フレームが全露光を含む1回の撮影で、表示中のフレームから派生ビューを作る。フレーム連結はフレームそのものが
    /// 各露光で全フレームを使う(入力フォーマットのHDR方式で分かる)ので書かない。
    /// </remarks>
    private static void AppendSourceFrame(StringBuilder sb, RawFormat source, int sourceFrame)
    {
        if (source.FrameCount <= 1)
        {
            return;
        }

        HdrMode layout;
        try
        {
            layout = HdrSplitter.ResolveLayout(source, source.HdrStages);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        if (layout == HdrMode.LineInterleaved)
        {
            sb.Append("  元画像のフレーム: ").Append(sourceFrame + 1).Append('/').Append(source.FrameCount)
                .AppendLine();
        }
    }
}

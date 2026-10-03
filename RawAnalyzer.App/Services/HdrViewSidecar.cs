using System.Globalization;
using System.Text;
using RawAnalyzer.App.Views;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>
/// HDR派生ビュー(分割・合成)の表示中に保存したとき、保存の付随テキストへ書く来歴の節を作る
/// ([HDR派生ビュー]と、分割ビューから表示LUTを焼き込んだときの[適用処理]の段ごとの表示調整)。
/// </summary>
/// <remarks>
/// <para>
/// 付随テキストは、保存した画像に何が適用されたかを実体と食い違わずに残すためのもの。派生ビューから保存した
/// 画像は元ファイルの画素ではなく、HDR分割の並置画像やHDR合成の16bit量子化画像(float raw なら量子化前の合成値)
/// になる。以前はそのことが書かれず、入力フォーマットも派生画像の形式(HDR: None)になっていた。
/// </para>
/// <para>
/// 合成ビューの表示黒点・白点は合成画像の値域で黒0(減算済み)・白65535から始まる(MergedViewLevels)ので、
/// [適用処理]の黒点は合成で減算した黒点とは別物になる。
/// 合成で減算した黒点・露光比・段数は、合成結果(<see cref="HdrImage.Parameters"/>)が持つ計算に使った値を書く
/// (計算中や計算後に動かしたスライダーの値と取り違えない)。
/// </para>
/// <para>
/// 分割ビューから表示LUTを焼き込んだ画像は、各段をその段の表示調整で焼き込んでいる(<see cref="HdrSplitAdjustments"/>)。
/// [適用処理]の表示LUTもスライダーの値の1組ではなく段ごとに書く(<see cref="DescribeSplitDisplayLut"/>)。
/// </para>
/// </remarks>
internal static class HdrViewSidecar
{
    private const string Header = "[HDR派生ビュー]";

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>HDR分割の派生ビュー(長秒→短秒の各段を左から並置した画像)の来歴。</summary>
    /// <remarks>
    /// 保存した並置画像は開き直すと1枚の画像に見えるので、段の構成(段の数・段の幅・各段の露光)と、各段の Bayer は
    /// 段の左端を列0として数えること(<see cref="RawImage.SegmentWidth"/>。段の幅が奇数だと、後ろの段では並置画像の
    /// 列の偶奇と位相が逆になる)を書く。露光比はフォーマットで指定した1段あたりの値(長秒:短秒)。付随テキストを
    /// 読んで段を復元する機能はない(記録のため)。
    /// </remarks>
    /// <param name="derived">派生画像(並置画像)のフォーマット。</param>
    /// <param name="stages">段数。</param>
    /// <param name="source">元画像のフォーマット(HDR方式・露光比を含む)。</param>
    /// <param name="sourceFrame">分割した元画像のフレーム番号(行交互の複数フレームの画像のときだけ書く)。</param>
    /// <param name="segmentWidth">段の幅(並置画像の区画の幅)。0 なら派生画像の幅を段数で割った幅。</param>
    /// <returns>付随テキストの節(改行で終わる)。</returns>
    internal static string DescribeSplit(
        RawFormat derived, int stages, RawFormat source, int sourceFrame, int segmentWidth = 0)
    {
        stages = Math.Max(1, stages);
        int width = segmentWidth > 0 ? segmentWidth : derived.Width / stages;
        var sb = new StringBuilder();
        sb.AppendLine(Header);
        sb.Append("  種類: HDR分割 (左: 長秒 → 右: 短秒, ").Append(stages).AppendLine("段)");
        AppendSourceFrame(sb, source, sourceFrame);
        sb.Append("  派生画像: ").Append(DescribeImage(derived))
            .Append(" (各段 ").Append(width).Append('×').Append(derived.Height)
            .AppendLine(" を左から並置)");
        sb.Append("  段の数: ").Append(stages).AppendLine();
        sb.Append("  段の幅: ").Append(width).Append(" px (各段 ").Append(width).Append('×').Append(derived.Height)
            .AppendLine(")");
        sb.AppendLine("  各段の露光 (左から):");
        for (int stage = 0; stage < stages; stage++)
        {
            // 最後の段は並置画像の右端まで(区画の規約と同じ)
            int first = stage * width;
            int last = stage == stages - 1 ? derived.Width - 1 : first + width - 1;
            sb.Append("    段").Append(stage + 1).Append(" (x ").Append(first).Append('〜').Append(last).Append("): ")
                .Append(StageName(stage, stages));
            if (stage > 0)
            {
                sb.Append(" (長秒の 1/").Append(FormatRatio(Math.Pow(source.ExposureRatio, stage)))
                    .Append("。フォーマットの露光比 ").Append(FormatRatio(source.ExposureRatio)).Append(')');
            }

            sb.AppendLine();
        }

        if (derived.Bayer != BayerPattern.None)
        {
            sb.Append("  各段の Bayer: 段の左端を列0として数える (段の幅が奇数でも、各段の左上が ")
                .Append(derived.Bayer).AppendLine(" の並びの始まり)");
        }

        return sb.ToString();
    }

    /// <summary>
    /// HDR派生ビューの表示中に保存したときの[HDR派生ビュー]の節(派生ビューでなければ null)。保存の形式によらず書く
    /// (raw だけでなく TIFF/PNG/JPEG に保存した並置画像にも段の構成を残す)。
    /// </summary>
    /// <param name="derived">派生画像のフォーマット(派生ビューでなければ null)。</param>
    /// <param name="segmentWidth">分割ビューの段の幅(<see cref="RawImage.SegmentWidth"/>)。</param>
    /// <param name="merged">合成ビューなら合成結果(分割ビューなら null)。</param>
    /// <param name="stages">分割ビューの段数。</param>
    /// <param name="source">元画像のフォーマット(HDR方式・露光比を含む)。</param>
    /// <param name="sourceFrame">派生ビューの元にした元画像のフレーム番号。</param>
    /// <param name="output">保存の形式。</param>
    /// <returns>付随テキストの節(改行で終わる)。派生ビューでなければ null。</returns>
    internal static string? DescribeDerivedView(
        RawFormat? derived, int segmentWidth, HdrImage? merged, int stages, RawFormat source, int sourceFrame,
        SaveFormat output)
    {
        if (derived is null)
        {
            return null;
        }

        return merged is not null
            ? DescribeMerge(derived, merged, source, sourceFrame, floatRawOutput: output == SaveFormat.FloatRaw)
            : DescribeSplit(derived, stages, source, sourceFrame, segmentWidth);
    }

    /// <summary>
    /// HDR合成の派生ビュー(黒レベルを減算して合成し、16bitへ量子化した画像)の来歴。
    /// 合成値は黒点未満が負になる。float raw は負値のまま、16bitの派生画像は0に切り詰めた値なので、そのことも書く。
    /// </summary>
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
                .Append(merged.Height).Append("・Bayer ").Append(merged.Bayer)
                .AppendLine("・黒点減算済み・黒点未満は負値のまま)");
            return sb.ToString();
        }

        sb.Append("  派生画像: ").Append(DescribeImage(derived))
            .AppendLine(" (合成域のフルスケールを65535へ量子化・黒点未満は0に切り詰め)");
        sb.Append("  量子化の1LSB: ").Append(merged.QuantizationStep.ToString("F1", Invariant))
            .Append(merged.LostBits >= 0.5
                ? $" (元素材比 約{merged.LostBits.ToString("F0", Invariant)}bit損失)"
                : " (元素材比で損失なし)")
            .AppendLine();
        return sb.ToString();
    }

    /// <summary>
    /// HDR分割ビューから表示LUTを焼き込んで保存したときの、[適用処理]の表示LUTの記録(段ごとの表示調整)。
    /// </summary>
    /// <remarks>
    /// 分割ビューは表示調整を段ごとに持ち、焼き込みも段ごとに行う。スライダーが示すのは最後に調整した段の値だけで、
    /// それを1組だけ書くと保存した画像と食い違う。値の書式は分割ビューでないときの表示LUTの記録と同じ。
    /// </remarks>
    /// <param name="stages">段ごとの表示パラメータ(左の段から。長秒 → 短秒)。</param>
    /// <returns>「表示LUT: 適用」の行から始まる記録(改行で終わる)。</returns>
    internal static string DescribeSplitDisplayLut(IReadOnlyList<DisplayParameters> stages)
    {
        var sb = new StringBuilder();
        sb.AppendLine("  表示LUT: 適用 (HDR分割の段ごと)");
        for (int i = 0; i < stages.Count; i++)
        {
            DisplayParameters stage = stages[i];
            sb.Append("    ").Append(StageName(i, stages.Count)).AppendLine(":");
            sb.Append("      黒点/白点: ").Append(stage.BlackPoint).Append(" / ")
                .AppendLine(stage.WhitePoint.ToString(Invariant));
            sb.Append("      ゲイン: ")
                .Append(DisplayLevels.ToGainDb(stage.Gain).ToString("F1", Invariant))
                .Append(" dB (×")
                .Append(stage.Gain.ToString("F3", Invariant))
                .AppendLine(")");
            sb.Append("      ガンマ: ").AppendLine(stage.Gamma.ToString("F3", Invariant));
            sb.Append("      コントラスト: ").AppendLine(stage.Contrast.ToString("F3", Invariant));
        }

        return sb.ToString();
    }

    /// <summary>段の名前(表示調整の対象の選択肢と同じ。左端の段が長秒、右端の段が短秒、その間が中秒)。</summary>
    private static string StageName(int stage, int stages) =>
        stage == 0 ? "長秒" : stage == stages - 1 ? "短秒" : "中秒";

    private static string FormatRatio(double ratio) => ratio.ToString("0.###", Invariant);

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

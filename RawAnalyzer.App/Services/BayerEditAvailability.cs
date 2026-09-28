namespace RawAnalyzer.App.Services;

/// <summary>
/// 右パネルの Bayer の選択を操作できるかと、操作できないときにツールチップで示す理由。
/// </summary>
/// <remarks>
/// <para>
/// デコード済みのカラー画像は RGB のまま表示し、Bayer を適用しない(TIFF のページ送り・ファイル連番の送り・
/// 表示モードの選択と同じ規約)。カラー画像で指定できると、表示はカラーのままチャネル別統計などが輝度へ Bayer を
/// 当ててしまう。指定そのものはカラー画像を挟んでも保持され、次のグレーの画像に付く。
/// </para>
/// <para>
/// HDR分割・合成の派生ビューは、計算を始めたときの Bayer(と、Bayer で決まる行交互の既定の行単位)で元画像から作る。
/// 以前は派生ビューの表示中に Bayer を変えても派生ビューは古いパターンのままで、右パネルの表示と食い違った。
/// 分割・合成の計算中(モーダルではない)に変えた場合も、計算は開始時のフォーマットで行い、適用時に照合しなかった。
/// そこで派生ビューの表示中と計算中は選択を無効にし、理由をツールチップで示す(「フォーマット変更…」と同じく、
/// 見える操作は無効にして理由を示す規約)。Raw 表示へ戻ったとき、計算が採用されずに終わったとき(取り消し・
/// 失敗・元画像の差し替え)は操作できる。計算結果の適用時にも、開始時のフォーマットのままか照合する(MainWindow)。
/// </para>
/// </remarks>
internal static class BayerEditAvailability
{
    /// <summary>Bayer の選択を操作できない理由。</summary>
    internal enum Refusal
    {
        /// <summary>操作できる。</summary>
        None,

        /// <summary>画像を開いていない。</summary>
        NoImage,

        /// <summary>デコード済みのカラー画像を表示している(Bayer を適用しない)。</summary>
        ColorImage,

        /// <summary>HDR分割・合成の計算中(計算は始めたときのフォーマットで行う)。</summary>
        HdrComputing,

        /// <summary>HDR分割・合成の派生ビューを表示している(計算を始めたときの Bayer で作った画像)。</summary>
        HdrView,
    }

    /// <summary>操作できるとき(と画像を開いていないとき)のツールチップ。項目の説明。</summary>
    internal const string Description = "Bayerパターンはここで変更できます(再読込不要)";

    /// <summary>Bayer の選択を操作できるか判定する。</summary>
    /// <remarks>
    /// HDR分割ビューの表示中に合成を計算しているときは、計算中の理由を示す(計算が採用されずに終わっても、
    /// 分割ビューのままなので派生ビューの理由で無効のまま)。カラー画像では HDR 表示を使わない。
    /// </remarks>
    /// <param name="hasImage">画像を開いているか。</param>
    /// <param name="isColorImage">デコード済みのカラー画像を表示しているか。</param>
    /// <param name="hdrViewShown">HDR分割・合成の派生ビューを表示しているか。</param>
    /// <param name="hdrComputing">HDR分割・合成の計算中か。</param>
    /// <returns>操作できない理由。操作できれば <see cref="Refusal.None"/>。</returns>
    internal static Refusal Check(bool hasImage, bool isColorImage, bool hdrViewShown, bool hdrComputing)
    {
        if (!hasImage)
        {
            return Refusal.NoImage;
        }

        if (hdrComputing)
        {
            return Refusal.HdrComputing;
        }

        if (hdrViewShown)
        {
            return Refusal.HdrView;
        }

        return isColorImage ? Refusal.ColorImage : Refusal.None;
    }

    /// <summary>Bayer の選択に出すツールチップ(無効のときも出す)。</summary>
    /// <param name="refusal">操作できない理由。</param>
    /// <returns>操作できないときはその理由と戻し方、操作できるとき・画像を開いていないときは項目の説明。</returns>
    internal static string ToolTip(Refusal refusal) => refusal switch
    {
        Refusal.ColorImage => "カラー画像(RGB)には Bayer を適用せず、カラーのまま表示します。",
        Refusal.HdrComputing =>
            "HDR分割・合成の計算中は Bayer を変更できません(計算は始めたときの Bayer で行います)。\n" +
            "計算の完了後に、Raw表示へ戻してから変更してください。",
        Refusal.HdrView =>
            "HDR表示(分割・合成)の間は Bayer を変更できません" +
            "(表示中の画像は、HDR表示を始めたときの Bayer で作っています)。\n" +
            "Raw表示に戻してから変更してください。",
        _ => Description,
    };
}

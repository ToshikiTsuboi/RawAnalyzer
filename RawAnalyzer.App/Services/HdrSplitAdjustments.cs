using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>
/// HDR分割ビュー(各段を同じ幅で左から並置した画像)で段ごとに持つ表示調整(黒点/白点・ゲイン・ガンマ・コントラスト)。
/// 焼き込み保存で、画面と同じく各段をその段の表示調整で焼き込むのに使う。
/// </summary>
/// <remarks>
/// <para>
/// 分割ビューの画面は、表示調整の対象(全体/長秒/中秒/短秒)ごとに変えた表示パラメータから段ごとの表示LUTを作り、
/// 並置画像のX座標を段の幅で区切って描く。スライダーが示すのは最後に調整した値だけなので、それで作った1本のLUTを
/// 全段に掛けて保存すると、画面と保存結果が食い違う。
/// </para>
/// <para>
/// 分割ビューの画面はRaw表示だけ(カラー現像などを選ぶと分割ビューを抜ける)だが、保存ではデモザイクも選べる。
/// そのときも段の表示調整を現像の黒点/白点・ゲイン・ガンマ・コントラストへ当て、画面で段ごとに合わせた明るさを保つ。
/// WB・カラーマトリクスは段によらず共通。
/// </para>
/// <para>
/// 表示LUTを焼き込まないときも各段は別の画像として焼き込み(表示調整は恒等)、デモザイクで隣の段の画素を補間に
/// 混ぜない(<see cref="ForSave"/>)。
/// </para>
/// </remarks>
/// <param name="Stages">段ごとの表示パラメータ(左の段から。長秒 → 短秒)。</param>
/// <param name="SegmentWidth">各段の幅(並置画像の画素数)。</param>
internal sealed record HdrSplitAdjustments(IReadOnlyList<DisplayParameters> Stages, int SegmentWidth)
{
    /// <summary>
    /// HDR分割ビューから保存するときに使う、段ごとの表示調整を控える。
    /// </summary>
    /// <remarks>
    /// 並置画像の各段は別の画像なので、表示LUTを焼き込まないときも段ごとに焼き込む(デモザイクで隣の段の画素を
    /// 補間に混ぜない)。そのときの表示調整は各段とも恒等にする(焼き込まないと選んだ表示調整を当てない)。
    /// 段ごとの値は写しを取り、保存と付随テキストに同じ値を使う。
    /// </remarks>
    /// <param name="stages">分割ビューの段ごとの表示パラメータ。分割ビューでなければ null。</param>
    /// <param name="segmentWidth">各段の幅(並置画像の画素数)。</param>
    /// <param name="applyDisplayLut">表示LUTを焼き込むか。</param>
    /// <returns>保存に使う段ごとの表示調整。分割ビューでなければ null。</returns>
    internal static HdrSplitAdjustments? ForSave(
        IReadOnlyList<DisplayParameters>? stages, int segmentWidth, bool applyDisplayLut)
    {
        if (stages is null)
        {
            return null;
        }

        var copy = new DisplayParameters[stages.Count];
        for (int i = 0; i < copy.Length; i++)
        {
            copy[i] = applyDisplayLut ? stages[i] : new DisplayParameters();
        }

        return new HdrSplitAdjustments(copy, segmentWidth);
    }

    /// <summary>画面(分割ビューの段ごとの表示LUT)と同じ、段ごとの表示LUTを作る。</summary>
    /// <returns>段ごとの表示LUT(左の段から)。</returns>
    internal DisplayLut[] CreateDisplayLuts()
    {
        var luts = new DisplayLut[Stages.Count];
        for (int i = 0; i < luts.Length; i++)
        {
            luts[i] = DisplayLut.Create(Stages[i]);
        }

        return luts;
    }

    /// <summary>
    /// 段ごとの現像LUTを作る。保存に使う現像パラメータの表示調整の値だけを、段の表示調整に替える。
    /// </summary>
    /// <param name="common">保存に使う現像パラメータ(WB・カラーマトリクス・素材のビット深度はそのまま使う)。</param>
    /// <returns>段ごとの現像LUT(左の段から)。</returns>
    internal DevelopLuts[] CreateDevelopLuts(DevelopParameters common)
    {
        var luts = new DevelopLuts[Stages.Count];
        for (int i = 0; i < luts.Length; i++)
        {
            DisplayParameters stage = Stages[i];

            // 画面の表示調整から現像パラメータを作るとき(MainWindow の CurrentDevelopParameters)と同じ対応
            luts[i] = DevelopLuts.Create(common with
            {
                BlackLevel = stage.BlackPoint,
                WhitePoint = stage.WhitePoint,
                Gain = stage.Gain,
                Gamma = stage.Gamma > 0 ? stage.Gamma : 1.0,
                Contrast = stage.Contrast,
            });
        }

        return luts;
    }
}

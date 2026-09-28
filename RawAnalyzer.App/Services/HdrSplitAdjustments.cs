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
/// </remarks>
/// <param name="Stages">段ごとの表示パラメータ(左の段から。長秒 → 短秒)。</param>
/// <param name="SegmentWidth">各段の幅(並置画像の画素数)。</param>
internal sealed record HdrSplitAdjustments(IReadOnlyList<DisplayParameters> Stages, int SegmentWidth)
{
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

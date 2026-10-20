using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>
/// 黒/白レベルの raw code(UIのスライダー値)と、16bitフルスケールの内部値
/// (表示LUTの黒点・白点)の換算。表示ゲインの線形倍率と dB の換算も持つ。
/// </summary>
/// <remarks>
/// 表示LUTは16bit正規化値で黒点・白点を持ち、UIは表示中の画像のビット深度のコード値で示す。
/// 表示中の画像のビット深度が変わったとき(HDR合成の16bit化、元画像への復帰、
/// ビット深度の異なるファイル連番など)は、内部値を保ったままUIのコード値と上限を
/// 新しいビット深度で表し直す。上限だけ更新すると、古いビット深度のコード値が次の操作で
/// 新しいシフト量のまま解釈され、白点が 65535 → 4094 のように急落する。
/// </remarks>
internal static class DisplayLevels
{
    /// <summary>表示ゲインのスライダーの下限[dB](×0.1)。</summary>
    internal const double MinGainDb = -20;

    /// <summary>
    /// 線形の表示ゲインを dB(20·log10(倍率))で表す。ゲインのスライダーと付随テキストで同じ換算を使う。
    /// </summary>
    /// <param name="gain">線形の表示ゲイン。</param>
    /// <returns>dB 値。ゲインが0以下ならスライダーの下限。</returns>
    internal static double ToGainDb(double gain) => gain > 0 ? 20 * Math.Log10(gain) : MinGainDb;

    /// <summary>ビット深度の最大コード値(黒/白レベルの上限)。</summary>
    /// <param name="bitDepth">ビット深度。</param>
    /// <returns>最大コード値。</returns>
    internal static int MaxCode(int bitDepth) => (1 << bitDepth) - 1;

    /// <summary>内部値(16bit)を、指定ビット深度のコード値へ換算する。</summary>
    /// <param name="blackPoint">黒点(16bit)。</param>
    /// <param name="whitePoint">白点(16bit)。</param>
    /// <param name="bitDepth">表示中の画像のビット深度。</param>
    /// <returns>黒/白レベルのコード値。</returns>
    internal static (int BlackCode, int WhiteCode) ToCodes(
        ushort blackPoint, ushort whitePoint, int bitDepth)
    {
        int shift = 16 - bitDepth;
        return (blackPoint >> shift, whitePoint >> shift);
    }

    /// <summary>コード値を内部値(16bit)へ換算する。</summary>
    /// <param name="blackCode">黒レベル(コード値)。</param>
    /// <param name="whiteCode">白レベル(コード値)。</param>
    /// <param name="bitDepth">表示中の画像のビット深度。</param>
    /// <returns>
    /// 黒点・白点(16bit)。どちらもそのコードの画素と同じ値(code&lt;&lt;(16−N)、コードの区間の下端)に置く。
    /// </returns>
    /// <remarks>
    /// Nbit の画素は code&lt;&lt;(16−N) に正規化されている。白点をコードの区間の上端(下位ビットを1で埋めた値)に
    /// 置くと、白レベルに指定したコードの画素が白点に届かず 255 にならない(12bit 黒100/白101 で 132、
    /// 8bit の既定表示で 255 が 254)。区間の下端に置けば、白レベルのコードの画素はどのビット深度でも 255 になり、
    /// 8bit の既定表示は恒等になる。ピラミッドの平均などコードの間の値も、白点以上なら白になる。
    /// </remarks>
    internal static (ushort BlackPoint, ushort WhitePoint) ToPoints(
        double blackCode, double whiteCode, int bitDepth)
    {
        int shift = 16 - bitDepth;
        var black = (ushort)Math.Clamp((long)blackCode << shift, 0, 65535);
        var white = (ushort)Math.Clamp((long)whiteCode << shift, 0, 65535);
        return (black, white);
    }

    /// <summary>既定の黒点・白点(黒0・白=そのビット深度の最大コード)。</summary>
    /// <param name="bitDepth">表示中の画像のビット深度。</param>
    /// <returns>黒点・白点(16bit)。</returns>
    internal static (ushort BlackPoint, ushort WhitePoint) DefaultPoints(int bitDepth) =>
        ToPoints(0, MaxCode(bitDepth), bitDepth);

    /// <summary>
    /// 表示調整をしない(既定の黒点・白点、ゲイン・ガンマ・コントラストが等倍の)表示パラメータ。
    /// </summary>
    /// <param name="bitDepth">表示中の画像のビット深度。</param>
    /// <returns>表示パラメータ。</returns>
    internal static DisplayParameters IdentityParameters(int bitDepth)
    {
        (ushort black, ushort white) = DefaultPoints(bitDepth);
        return new DisplayParameters(black, white);
    }
}

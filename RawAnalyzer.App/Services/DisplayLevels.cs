namespace RawAnalyzer.App.Services;

/// <summary>
/// 黒/白レベルの raw code(UIのスライダー値)と、16bitフルスケールの内部値
/// (表示LUTの黒点・白点)の換算。
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
    /// <returns>黒点・白点(16bit)。白点はそのコードの上端まで含める(下位ビットを立てる)。</returns>
    internal static (ushort BlackPoint, ushort WhitePoint) ToPoints(
        double blackCode, double whiteCode, int bitDepth)
    {
        int shift = 16 - bitDepth;
        var black = (ushort)Math.Clamp((long)blackCode << shift, 0, 65535);
        var white = (ushort)Math.Clamp(
            ((long)whiteCode << shift) | ((1L << shift) - 1), 0, 65535);
        return (black, white);
    }
}

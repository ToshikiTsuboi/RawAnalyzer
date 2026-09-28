namespace RawAnalyzer.App.Services;

/// <summary>
/// HDR合成ビューの表示黒点・白点と、合成ビューの外(元画像・分割ビュー)の黒点・白点を受け渡す。
/// </summary>
/// <remarks>
/// <para>
/// HDR合成は元画像の黒点を減算して合成し(HdrMergeParameters.BlackLevel)、合成域のフルスケールを16bitの65535へ
/// 量子化する。合成画像は黒レベル減算済みで、値域も元画像とは別物になる。元画像の黒点のまま合成ビューの表示LUT・
/// 現像LUTを作ると黒を二重に引き、黒点を十分上回る信号まで黒つぶれする(元12bitコード1000・黒コード64・2段・
/// 露光比16で、表示ゲイン16の出力が59ではなく0になり、ゲインを上げても戻らない)。元画像で下げた白点のままだと、
/// 合成で短秒から取り戻した高輝度まで白飛びとして切る(同じ条件で元画像の白コード2000のまま、短秒コード3000の
/// 画素の出力が186ではなく255になる)。
/// </para>
/// <para>
/// そこで合成ビューへ入るときに外の黒点・白点を控えて、合成ビューの表示黒点を0(減算済み)、表示白点を65535
/// (合成域のフルスケール)にし、合成ビューを抜けるとき(Raw表示への復帰・分割ビューへの切替。分割フレームは
/// 黒レベル未減算で元画像と同じ値域)に控えた黒点・白点へ戻す。合成ビューで動かした黒/白レベルは合成ビューの
/// 表示だけのもので、抜けるときに捨てる。合成ビューの表示中は現像・WBの推定・保存で焼き込むLUTも同じ表示黒点・
/// 白点を使う。ゲイン・ガンマ・コントラストは合成ビューの内外で共有する。
/// </para>
/// <para>UIスレッド専用(排他制御はしない)。</para>
/// </remarks>
internal sealed class MergedViewLevels
{
    /// <summary>合成ビューへ入ったときの表示黒点(合成画像は黒レベル減算済み)。</summary>
    internal const ushort MergedBlackPoint = 0;

    /// <summary>合成ビューへ入ったときの表示白点(合成域のフルスケールを65535へ量子化している)。</summary>
    internal const ushort MergedWhitePoint = 65535;

    // 合成ビューの表示中だけ、合成ビューへ入る前の黒点・白点を持つ
    private (ushort BlackPoint, ushort WhitePoint)? _outside;

    /// <summary>
    /// 合成ビューへ入る(合成画像を表示する)。合成ビューの外の黒点・白点を控え、合成ビューの表示黒点・白点を返す。
    /// </summary>
    /// <param name="currentBlackPoint">いまの表示黒点(16bit)。</param>
    /// <param name="currentWhitePoint">いまの表示白点(16bit)。</param>
    /// <returns>
    /// 合成ビューの表示黒点(0)・白点(65535)。合成ビューの表示中に入り直した場合も同じで、控えは合成ビューへ
    /// 入る前の値のまま。
    /// </returns>
    internal (ushort BlackPoint, ushort WhitePoint) Enter(ushort currentBlackPoint, ushort currentWhitePoint)
    {
        _outside ??= (currentBlackPoint, currentWhitePoint);
        return (MergedBlackPoint, MergedWhitePoint);
    }

    /// <summary>
    /// 合成ビューを抜ける(元画像・分割ビューを表示する)。合成ビューへ入る前の黒点・白点を返す。
    /// </summary>
    /// <param name="currentBlackPoint">いまの表示黒点(16bit)。</param>
    /// <param name="currentWhitePoint">いまの表示白点(16bit)。</param>
    /// <returns>
    /// 控えていた黒点・白点。合成ビューの表示中でなければ(分割ビュー → Raw表示など)いまの表示黒点・白点のまま。
    /// </returns>
    internal (ushort BlackPoint, ushort WhitePoint) Leave(ushort currentBlackPoint, ushort currentWhitePoint)
    {
        (ushort BlackPoint, ushort WhitePoint) restored = _outside ?? (currentBlackPoint, currentWhitePoint);
        _outside = null;
        return restored;
    }

    /// <summary>
    /// 控えを捨てる。合成ビューの表示中に別の画像を開いたとき(黒点・白点は開いた画像の既定へ戻す)に呼ぶ。
    /// </summary>
    internal void Reset()
    {
        _outside = null;
    }
}

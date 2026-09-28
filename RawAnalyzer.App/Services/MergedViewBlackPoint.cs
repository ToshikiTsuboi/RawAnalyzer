namespace RawAnalyzer.App.Services;

/// <summary>
/// HDR合成ビューの表示黒点と、合成ビューの外(元画像・分割ビュー)の黒点を受け渡す。
/// </summary>
/// <remarks>
/// <para>
/// HDR合成は元画像の黒点を減算して合成し(HdrMergeParameters.BlackLevel)、16bitへ量子化した合成画像は
/// 黒レベル減算済みになる。元画像の黒点のまま合成ビューの表示LUT・現像LUTを作ると黒を二重に引き、黒点を
/// 十分上回る信号まで黒つぶれする(元12bitコード1000・黒コード64・2段・露光比16で、表示ゲイン16の出力が
/// 59ではなく0になり、ゲインを上げても戻らない)。
/// </para>
/// <para>
/// そこで合成ビューへ入るときに外の黒点を控えて表示黒点を0(減算済み)にし、合成ビューを抜けるとき
/// (Raw表示への復帰・分割ビューへの切替。分割フレームは黒レベル未減算)に控えた黒点へ戻す。合成ビューで
/// 動かした黒レベルは合成ビューの表示だけのもので、抜けるときに捨てる。合成ビューの表示中は現像・WBの推定・
/// 保存で焼き込むLUTも同じ表示黒点(減算済みの画像の黒点)を使う。
/// </para>
/// <para>UIスレッド専用(排他制御はしない)。</para>
/// </remarks>
internal sealed class MergedViewBlackPoint
{
    /// <summary>合成ビューへ入ったときの表示黒点(合成画像は黒レベル減算済み)。</summary>
    internal const ushort MergedBlackPoint = 0;

    // 合成ビューの表示中だけ、合成ビューへ入る前の黒点を持つ
    private ushort? _outsideBlackPoint;

    /// <summary>
    /// 合成ビューへ入る(合成画像を表示する)。合成ビューの外の黒点を控え、合成ビューの表示黒点を返す。
    /// </summary>
    /// <param name="currentBlackPoint">いまの表示黒点(16bit)。</param>
    /// <returns>
    /// 合成ビューの表示黒点(0)。合成ビューの表示中に入り直した場合も0で、控えは合成ビューへ入る前の黒点のまま。
    /// </returns>
    internal ushort Enter(ushort currentBlackPoint)
    {
        _outsideBlackPoint ??= currentBlackPoint;
        return MergedBlackPoint;
    }

    /// <summary>
    /// 合成ビューを抜ける(元画像・分割ビューを表示する)。合成ビューへ入る前の黒点を返す。
    /// </summary>
    /// <param name="currentBlackPoint">いまの表示黒点(16bit)。</param>
    /// <returns>控えていた黒点。合成ビューの表示中でなければ(分割ビュー → Raw表示など)いまの表示黒点のまま。</returns>
    internal ushort Leave(ushort currentBlackPoint)
    {
        ushort restored = _outsideBlackPoint ?? currentBlackPoint;
        _outsideBlackPoint = null;
        return restored;
    }

    /// <summary>
    /// 控えを捨てる。合成ビューの表示中に別の画像を開いたとき(黒点は開いた画像の既定へ戻す)に呼ぶ。
    /// </summary>
    internal void Reset()
    {
        _outsideBlackPoint = null;
    }
}

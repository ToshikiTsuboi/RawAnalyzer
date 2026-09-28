using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>
/// カラー現像表示の現像LUTと、それを作ったパラメータ(素材のビット深度を含む)を控え、表示の前に
/// 現在のパラメータと照合する。
/// </summary>
/// <remarks>
/// <para>
/// 現像LUTは白飛びの判定に表示中の画像のビット深度(<see cref="DevelopParameters.SourceBitDepth"/>)を使う。
/// ビット深度はHDR合成(16bit)の表示・元画像への復帰、ビット深度の異なる連番・TIFFのページの送り、処理結果
/// (16bit)での差し替えで変わる。以前は作り直しの印(dirty)を立てた経路だけが作り直しの対象で、HDR派生ビューへの
/// 出入りでは印が立たず、旧ビット深度のLUTのまま描いていた(HDR出力 65520・WB(2,1,1.5)・典型的なCCMで、
/// 12bitのLUTは白 (255,255,255)、正しい16bitのLUTは (255,178,255))。保存は現在のパラメータでLUTを作るので、
/// 画面と保存の色も食い違った。
/// </para>
/// <para>
/// 印ではなく、作ったときのパラメータと現在のパラメータを照合して、違えば作り直す(同じなら65536エントリの
/// LUTを作り直さない)。UIスレッド専用(排他制御はしない)。
/// </para>
/// </remarks>
internal sealed class DevelopLutCache
{
    private DevelopLuts? _luts;

    /// <summary>
    /// 控えている現像LUTが指定のパラメータで作ったものでなければ作り直す。
    /// </summary>
    /// <param name="parameters">現在の現像パラメータ(表示中の画像のビット深度を含む)。</param>
    /// <returns>作り直したLUT(ビューポートへ渡す)。控えているLUTがそのパラメータのものなら null。</returns>
    internal DevelopLuts? Refresh(DevelopParameters parameters)
    {
        if (_luts is { } luts && luts.Parameters == parameters)
        {
            return null;
        }

        _luts = DevelopLuts.Create(parameters);
        return _luts;
    }
}

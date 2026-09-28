using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>
/// 欠陥検出の結果が前提にしている条件(検出した画像・フレーム・Bayerパターン)。
/// 結果を採用するときと補正に使うときに、表示中の画像の条件のままか確かめる。
/// </summary>
/// <remarks>
/// <para>
/// 一覧の座標と画素値は、検出した画像・フレームでのみ有効。別の画像(HDR派生ビュー・送り後の画像)や
/// 別のフレームへ適用すると、配列の範囲内に収まるので例外にならず、無関係な画素を黙って書き換える。
/// </para>
/// <para>
/// Bayerパターンも検出の前提になる。検出はパターンのチャネル別の統計で閾値を決め(なしなら全画素の統計)、
/// 補正はパターンで選んだ同じチャネルの近傍から値を補う。右パネルでパターンを変えた後に前の一覧で
/// 補正すると、変えた後のパターンでは欠陥ではない画素を、その近傍の値で書き換えてしまう
/// (なしで検出した「値の高い B 画素」は、RGGB では正常な B 画素)。
/// </para>
/// </remarks>
/// <param name="Image">検出した画像。</param>
/// <param name="Frame">検出したフレーム。</param>
/// <param name="Pattern">検出に使ったBayerパターン。</param>
internal sealed record DefectDetectionSource(RawImage Image, int Frame, BayerPattern Pattern)
{
    /// <summary>
    /// 検出結果が、いまの表示の条件(画像・フレーム・Bayerパターン)で検出したものか判定する。
    /// </summary>
    /// <param name="image">いまの画像(検出結果の採用では表示中の画像、補正では元画像)。</param>
    /// <param name="frame">いまの表示フレーム。</param>
    /// <param name="pattern">いまのBayerパターン。</param>
    /// <returns>検出したときと同じならtrue。</returns>
    internal bool IsCurrent(RawImage? image, int frame, BayerPattern pattern)
    {
        return ReferenceEquals(Image, image) && Frame == frame && Pattern == pattern;
    }
}

namespace RawAnalyzer.App.Services;

/// <summary>
/// シーケンス(マルチフレーム・ファイル連番)の送りを使えるか判定する。
/// </summary>
/// <remarks>
/// <para>
/// HDR分割・合成の派生ビューは元画像から作った別の画像で、表示中は送りを使わない
/// (Raw表示へ戻すと送りを検出し直す)。派生ビューのまま送ると、ファイル連番では派生ビュー
/// (ActiveImage。解析・保存の対象)を残したまま元画像だけがビューポートへ入り、フレーム送りでは
/// 派生ビューの Bayer ピラミッドがビューポートから外れる。
/// </para>
/// <para>
/// 派生ビューに入るときに送りの UI を隠すだけでは足りない。それより前に始まったファイル連番の送りが
/// 派生ビューの表示の後で終わると、送りをやめたときの後始末(スライダーと表示を実際の内容へ戻す)が
/// 送りの件数だけを見て UI を有効に戻してしまう。送りの UI の状態を作るときと送りを受け付けるときに、
/// 毎回この判定を通す。
/// </para>
/// </remarks>
internal static class SequenceNavigation
{
    /// <summary>
    /// 送り(シーケンスバー・送りのショートカット・再生)を使えるか。
    /// </summary>
    /// <param name="count">シーケンスのフレーム数・ファイル数(派生ビューの表示中も元画像の件数が残る)。</param>
    /// <param name="derivedViewShown">HDR分割・合成の派生ビューを表示しているか。</param>
    /// <returns>使えるならtrue。</returns>
    internal static bool IsAvailable(int count, bool derivedViewShown) => count > 1 && !derivedViewShown;
}

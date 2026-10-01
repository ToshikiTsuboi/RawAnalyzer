using System.IO;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>
/// ファイル連番の送りで、送り先のファイルを読む。
/// </summary>
/// <remarks>
/// 取り消せる読み込みを使う。送りの結果は、読む間に別ファイルを開いた・操作を始めた・ウィンドウを閉じたら
/// 差し替えずに捨てるが、読み込みそのものを取り消さないと、大きなファイルの読み込み(ネットワーク上の raw の
/// 一時コピー、TIFF のデコード)が最後まで走り、新しい読み込みや処理と I/O・CPU・メモリを奪い合う
/// (その間は次の送りも断られる)。
/// </remarks>
internal static class SequenceFileLoad
{
    /// <summary>
    /// 送り先のファイルを読む。raw は表示中のフォーマットで、画像ファイル(TIFF 等)はファイル自身の
    /// フォーマットで読む(複数ページなら1ページ目)。
    /// </summary>
    /// <param name="path">送り先のファイル。</param>
    /// <param name="isRaw">raw(.raw/.bin)か。</param>
    /// <param name="rawFormat">raw を読むフォーマット(表示中の画像のフォーマット)。画像ファイルでは使わない。</param>
    /// <param name="cancellationToken">取り消しのトークン。</param>
    /// <returns>読み込んだ画像。呼び出し側で <see cref="DecodedImage.Luminance"/> を破棄すること。</returns>
    /// <exception cref="OperationCanceledException">取り消された場合。</exception>
    internal static DecodedImage Load(
        string path, bool isRaw, RawFormat rawFormat, CancellationToken cancellationToken)
    {
        return isRaw
            ? new DecodedImage(RawLoader.Load(path, rawFormat, cancellationToken), null)
            : ImageFileLoader.Load(path, cancellationToken);
    }

    /// <summary>
    /// 読み込んだ送り先を表示中の画像と差し替えられるか、寸法を確かめる(ファイル連番は同じ寸法の画像だけを送る)。
    /// </summary>
    /// <remarks>
    /// raw の連番はファイルサイズで集めるので寸法がそろうが、画像ファイル(TIFF 等)の連番は名前だけで集めるので
    /// 寸法の違う1枚が混じり得る。差し替えないときは理由を返し、呼び出し側は再生を止めて知らせる
    /// (黙って送りをやめると、再生は毎ティック同じファイルを読み直して捨て続け、前のファイルで止まって見える)。
    /// </remarks>
    /// <param name="path">送り先のファイル。</param>
    /// <param name="loaded">読み込んだ送り先の画像。</param>
    /// <param name="current">表示中の画像。</param>
    /// <returns>差し替えないときは理由(利用者に知らせる文)。差し替えられるときは null。</returns>
    internal static string? CheckSize(string path, RawImage loaded, RawImage current)
    {
        return loaded.Width == current.Width && loaded.Height == current.Height
            ? null
            : $"{Path.GetFileName(path)} は {loaded.Width}×{loaded.Height} で、表示中の画像 " +
              $"{current.Width}×{current.Height} と寸法が違うため送れません";
    }

    /// <summary>
    /// 送り先を読み込めなかった理由を、利用者に知らせる文にする。
    /// </summary>
    /// <param name="path">送り先のファイル。</param>
    /// <param name="error">読み込みで起きた例外。</param>
    /// <returns>ステータスバーに出す文。</returns>
    internal static string ExplainUnreadable(string path, Exception error)
    {
        return $"{Path.GetFileName(path)} を読み込めないため送れません: {error.Message}";
    }
}

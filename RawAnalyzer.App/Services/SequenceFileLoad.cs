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
}

using System.IO;

namespace RawAnalyzer.App.Services;

/// <summary>
/// ファイル・フォルダ選択ダイアログ(「参照…」)の初期フォルダを、UI スレッドを止めずに確かめる。
/// </summary>
/// <remarks>
/// <para>
/// 初期フォルダ(表示中の画像のフォルダ・出力先の入力)はネットワーク上のことがある。UI スレッドで
/// Directory.Exists を呼ぶと、切断した NAS では SMB のタイムアウト(十数秒〜数十秒)までウィンドウが固まる。
/// 確かめずに渡すと、選択ダイアログ自身が UI スレッドでそのパスを解決して同じく固まり、届かないパスでは例外を出す。
/// </para>
/// <para>
/// 確認は UI スレッドの外で行い、<see cref="Timeout"/> のうちに実在を確かめられなければ初期フォルダなしで
/// ダイアログを出す(タイムアウトまで待つと、ボタンを押しても何も起きないように見える)。
/// </para>
/// </remarks>
internal static class DialogInitialFolder
{
    /// <summary>実在を確かめるのを待つ時間の上限。届くフォルダならこれより十分早く確かめられる。</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// フォルダの実在を UI スレッドの外で確かめ、時間内に確かめられたらそのフォルダを返す。
    /// </summary>
    /// <param name="folder">初期フォルダの候補(null・空白なら確かめない)。</param>
    /// <param name="timeout">待つ時間の上限。</param>
    /// <param name="exists">実在の確認(UI スレッドの外で呼ぶ)。null なら <see cref="Directory.Exists"/>。</param>
    /// <returns>実在を確かめられたフォルダ。無い・時間内に確かめられない・確認に失敗したら null。</returns>
    internal static async Task<string?> ConfirmAsync(
        string? folder, TimeSpan timeout, Func<string, bool>? exists = null)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        Func<string, bool> probe = exists ?? Directory.Exists;
        Task<bool> check = Task.Run(() => probe(folder));
        Task first = await Task.WhenAny(check, Task.Delay(timeout));
        return first == check && check.IsCompletedSuccessfully && check.Result ? folder : null;
    }
}

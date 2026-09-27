namespace RawAnalyzer.App.Services;

/// <summary>
/// 画像の読み込み・操作の実行中で、操作やフレーム送りを受け付けなかった理由。
/// </summary>
internal enum BusyReason
{
    /// <summary>通常の読み込み(ファイルを開く)が確定待ち。</summary>
    Loading,

    /// <summary>表示画像を使う操作(HDR分割・合成の計算など)の実行中。</summary>
    Operation,

    /// <summary>読み込みは確定したが、開く処理の仕上げ(縮小表示の作成)中。</summary>
    Finishing,
}

/// <summary>
/// 実行中のため受け付けなかった操作について、利用者に示す理由の文言を作る。
/// </summary>
/// <remarks>
/// 画像を処理する明示的な操作(メニュー・コマンド)はダイアログで、フレーム送りのように
/// 続けて起こり得る操作(ボタン・キー・スライダー・再生)はステータスバーで知らせる。
/// どちらも同じ見分け方の理由を示し、黙って捨てない。
/// </remarks>
internal static class BusyNotice
{
    /// <summary>
    /// 実行中(MainWindow の busy スコープの内側)である理由を見分ける。
    /// </summary>
    /// <remarks>
    /// busy スコープへ操作として数えずに入るのは通常の読み込みだけなので、確定待ちでも
    /// 操作の実行中でもなければ、確定後の縮小表示(ピラミッド)の作成中とみなす。
    /// </remarks>
    /// <param name="loadPending">通常の読み込みが確定待ちか。</param>
    /// <param name="operationRunning">表示画像を使う操作の実行中か。</param>
    /// <returns>理由。</returns>
    internal static BusyReason Classify(bool loadPending, bool operationRunning)
    {
        if (loadPending)
        {
            return BusyReason.Loading;
        }

        return operationRunning ? BusyReason.Operation : BusyReason.Finishing;
    }

    /// <summary>画像を処理する操作を始めなかった理由(ダイアログ用)。</summary>
    /// <param name="reason">理由。</param>
    /// <param name="operation">操作名(「保存」など)。</param>
    /// <returns>表示する文言。</returns>
    internal static string ForOperation(BusyReason reason, string operation)
    {
        return $"{Describe(reason)}は{operation}を開始できません。{Until(reason)}にもう一度実行してください。";
    }

    /// <summary>フレームを送らなかった理由(ステータスバー用)。</summary>
    /// <param name="reason">理由。</param>
    /// <returns>表示する文言。</returns>
    internal static string ForSequence(BusyReason reason)
    {
        return $"{Describe(reason)}のためフレームを送れません({Until(reason)}にもう一度操作してください)";
    }

    private static string Describe(BusyReason reason) => reason switch
    {
        BusyReason.Loading => "画像の読み込み中",
        BusyReason.Operation => "他の処理の実行中",
        _ => "縮小表示(ピラミッド)の作成中",
    };

    private static string Until(BusyReason reason) => reason switch
    {
        BusyReason.Loading => "読み込みの完了後",
        BusyReason.Operation => "処理の完了後",
        _ => "作成の完了後",
    };
}

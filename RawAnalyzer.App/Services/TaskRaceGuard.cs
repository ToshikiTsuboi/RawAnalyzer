namespace RawAnalyzer.App.Services;

/// <summary>
/// 画像の差し替え・キャンセルと競合して「結果が不要になった」処理の例外を見分ける。
/// </summary>
/// <remarks>
/// 解析・描画・ピラミッド生成は <see cref="System.Threading.Tasks.Parallel"/> を使う。
/// Parallel.For は本体で起きた例外を <see cref="AggregateException"/> に包んで投げ直すため、
/// <c>catch (ObjectDisposedException)</c> だけでは競合由来の例外がすり抜け、
/// 未処理例外ダイアログとしてユーザーに見えてしまう。
/// </remarks>
internal static class TaskRaceGuard
{
    /// <summary>
    /// 破棄・キャンセルとの競合による例外(結果を捨ててよい)か判定する。
    /// </summary>
    /// <param name="exception">判定する例外。</param>
    /// <returns>結果を捨ててよければtrue。</returns>
    internal static bool IsAbandoned(Exception exception)
    {
        return exception switch
        {
            OperationCanceledException or ObjectDisposedException => true,
            AggregateException aggregate =>
                aggregate.InnerExceptions.Count > 0
                && aggregate.InnerExceptions.All(IsAbandoned),
            _ => false,
        };
    }
}

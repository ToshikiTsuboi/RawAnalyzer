using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>
/// HDR分割・合成の派生画像の縮小ピラミッドを生成し、派生画像を差し替える・Raw表示へ戻るときに
/// 旧派生画像用の生成を取り消す。
/// </summary>
/// <remarks>
/// <para>
/// 生成は派生ビューを表示してから投げっぱなしで始める。分割⇔合成を素早く切り替える・Raw表示へ戻すと、
/// 旧派生画像は生成の途中で破棄される。取り消さないと生成は最後まで走り続け、破棄と競合した読み出しの
/// <see cref="ObjectDisposedException"/>(Parallel.For が <see cref="AggregateException"/> に包む)が
/// 投げっぱなしのタスクから漏れて、未監視のタスク例外としてログに残る。
/// </para>
/// <para>
/// 呼び出し側は派生画像を破棄する前に <see cref="Cancel"/> で取り消す。破棄・取り消しと競合した例外は
/// <see cref="TaskRaceGuard"/> の規約どおり打ち切りとして扱い、結果を返さない
/// (画像を開き直すときなど、取り消さずに破棄する経路もこの扱いで例外を漏らさない)。
/// </para>
/// <para>UIスレッド専用(排他制御はしない)。</para>
/// </remarks>
internal sealed class DerivedPyramidBuild
{
    private CancellationTokenSource? _cts;

    /// <summary>
    /// 生成中の縮小ピラミッドを取り消す。派生画像を破棄する前(Raw表示への復帰・派生画像の差し替え)に呼ぶ。
    /// </summary>
    internal void Cancel()
    {
        // 旧トークンは生成中の Parallel.For が参照しているので破棄しない(MainWindow.ReplaceLoadCts と同じ)
        CancellationTokenSource? previous = _cts;
        _cts = null;
        previous?.Cancel();
    }

    /// <summary>
    /// 旧派生画像用の生成を取り消し、派生画像の縮小ピラミッドを作る。
    /// </summary>
    /// <param name="derived">派生画像。</param>
    /// <returns>
    /// 生成したピラミッド。取り消された(生成が取り消しより先に終わっていた場合を含む)・派生画像の破棄と
    /// 競合した場合は null。
    /// </returns>
    internal async Task<TilePyramid?> CreateAsync(RawImage derived)
    {
        Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            TilePyramid pyramid = await TilePyramid.CreateAsync(derived, cancellationToken: cts.Token);

            // 取り消しの前に生成が終わっていても、差し替え・復帰の後に受け取った結果は捨てる
            return cts.IsCancellationRequested ? null : pyramid;
        }
        catch (Exception ex) when (TaskRaceGuard.IsAbandoned(ex))
        {
            return null;
        }
    }
}

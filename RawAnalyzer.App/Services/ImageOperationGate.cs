namespace RawAnalyzer.App.Services;

/// <summary>
/// 表示画像を差し替える通常の読み込みと、表示画像を使う操作
/// (保存・画像演算・測定・欠陥処理・HDR分割/合成・ビニング/フィルタ・バッチ)の排他を管理する。
/// </summary>
/// <remarks>
/// <para>
/// 操作はダイアログや進捗ウィンドウを出している間も対象の画像を握り続ける。
/// ShowDialog は Dispatcher の入れ子ポンプなので、その間に読み込みの継続(await の続き)が
/// 走って確定すると、操作のために作った画面が別の画像を処理したり、処理中の画像が破棄されたりする。
/// </para>
/// <para>
/// そこで読み込みは「開始から確定(または破棄)まで」を、操作は「開始から終了まで」を数える。
/// 呼び出し側は、読み込みの確定待ちがある間は操作を始めず(<see cref="IsLoadPending"/>)、
/// 読み込みは実行中の操作が終わるまで確定を待つ(<see cref="WhenOperationsIdleAsync"/>)。
/// </para>
/// <para>
/// ファイル連番の送りは逆に操作・読み込みへ譲る(操作の終了を待たず、送りをやめる)。
/// 次のファイルを読んでいる間に操作・読み込みが始まっていないことを
/// <see cref="ActivityStamp"/> で確かめ、差し替えは表示と状態の交換を同じUIターンで済ませる。
/// </para>
/// <para>UIスレッド専用(排他制御はしない)。</para>
/// </remarks>
internal sealed class ImageOperationGate
{
    private int _operationDepth;
    private int _pendingLoads;
    private int _activityStamp;
    private TaskCompletionSource? _idle;

    /// <summary>表示画像を使う操作の実行中か(入れ子を含む)。</summary>
    internal bool IsOperationRunning => _operationDepth > 0;

    /// <summary>開始から確定(または破棄)までの途中にある読み込みがあるか。</summary>
    internal bool IsLoadPending => _pendingLoads > 0;

    /// <summary>
    /// 操作・読み込みを始めるたびに進む通し番号(終了では変わらない)。
    /// </summary>
    /// <remarks>
    /// 操作に譲る差し替え(ファイル連番の送り)が、準備の間に操作・読み込みが始まったかを
    /// 見分けるために使う。準備中に始まって差し替えまでに終わった操作(HDR分割の派生ビュー、
    /// 開き直した画像など)は実行中かどうかでは見分けられず、送りがその結果を上書きしてしまう。
    /// </remarks>
    internal int ActivityStamp => _activityStamp;

    /// <summary>
    /// 表示画像を使う操作に入る。戻り値を Dispose すると抜ける(二重の Dispose は無視する)。
    /// </summary>
    /// <remarks>
    /// 開始してよいか(読み込みの確定待ちでないか)は呼び出し側が先に判定する。
    /// 実行中の操作の内側(ダイアログ→実行など)の入れ子はそのまま入れる。
    /// </remarks>
    /// <returns>操作を抜けるためのスコープ。</returns>
    internal IDisposable EnterOperation()
    {
        _operationDepth++;
        _activityStamp++;
        return new Scope(ExitOperation);
    }

    /// <summary>
    /// 読み込みの開始を登録する。確定または破棄したら戻り値を Dispose する
    /// (二重の Dispose は無視する)。
    /// </summary>
    /// <returns>読み込みの登録を外すスコープ。</returns>
    internal IDisposable BeginLoad()
    {
        _pendingLoads++;
        _activityStamp++;
        return new Scope(() => _pendingLoads--);
    }

    /// <summary>
    /// 実行中の操作がすべて終わるのを待つ。実行中でなければ完了済みのタスクを返す。
    /// </summary>
    /// <remarks>
    /// 待っている側は、最後の操作を抜ける Dispose の中ではなく、その後に非同期で再開する
    /// (操作の後始末の途中へ画像の差し替えが割り込まないようにするため)。
    /// 再開までに別の操作が始まっていることもあるので、呼び出し側は
    /// <see cref="IsOperationRunning"/> を確かめ直すこと。
    /// </remarks>
    /// <returns>実行中の操作がなくなったら完了するタスク。</returns>
    internal Task WhenOperationsIdleAsync()
    {
        if (_operationDepth == 0)
        {
            return Task.CompletedTask;
        }

        _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _idle.Task;
    }

    private void ExitOperation()
    {
        _operationDepth--;
        if (_operationDepth == 0 && _idle is { } idle)
        {
            _idle = null;
            idle.SetResult();
        }
    }

    /// <summary>一度だけ抜け処理を行うスコープ。</summary>
    private sealed class Scope(Action exit) : IDisposable
    {
        private Action? _exit = exit;

        /// <summary>スコープを抜ける(2回目以降は何もしない)。</summary>
        public void Dispose()
        {
            Action? exit = _exit;
            _exit = null;
            exit?.Invoke();
        }
    }
}

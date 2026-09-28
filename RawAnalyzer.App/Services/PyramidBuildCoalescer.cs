namespace RawAnalyzer.App.Services;

/// <summary>
/// 縮小ピラミッドの生成を、同じ生成元の画像・フレーム・世代について1本にまとめる。
/// </summary>
/// <remarks>
/// <para>
/// ピラミッドは取り付け済みかどうかを見て作るが、生成中はまだ取り付けていない。完了の前に同じ画像・
/// フレームのピラミッドを重ねて求めると(表示モードを Bayer カラーから現像へ切り替える、再生中に
/// 「次」を押して停止するなど)、同じ全走査が二重に走り、Bayer ピラミッドでは後から終わった方が
/// 先に取り付けた方を描画中に破棄する。同じ生成が進行中なら新たに始めず、その完了を返す。
/// </para>
/// <para>
/// 世代は表示中の画像・フレームの世代のトークン(MainWindow の読み込み用 CTS)で表す。フレームの
/// 送り・画像の差し替えは世代を進めて旧トークンを取り消すので、旧世代の生成には相乗りせず新しく
/// 始める(旧世代の生成は完了時に結果を捨てる)。完了した生成は手放し(生成元の画像を持ち続けない)、
/// その後の要求は取り付け済みかどうかを見る呼び出し側の判断に任せる。
/// </para>
/// <para>UIスレッド専用(排他制御はしない)。</para>
/// </remarks>
internal sealed class PyramidBuildCoalescer
{
    private readonly List<Build> _running = new();

    /// <summary>進行中の生成の数。</summary>
    internal int RunningCount => _running.Count;

    /// <summary>
    /// 同じ生成元・フレーム・世代の生成が進行中ならその完了を返し、なければ生成を始める。
    /// </summary>
    /// <param name="source">生成元の画像。</param>
    /// <param name="frame">生成元のフレーム番号。</param>
    /// <param name="generation">生成の世代。生成を取り消すトークン。</param>
    /// <param name="start">生成を始め、完了(取り付け、または結果の破棄)までを表すタスクを返す処理。</param>
    /// <returns>生成の完了を表すタスク。進行中の生成に相乗りした場合はその生成のタスク。</returns>
    internal Task RunAsync(object source, int frame, CancellationToken generation, Func<Task> start)
    {
        foreach (Build running in _running)
        {
            if (running.Matches(source, frame, generation))
            {
                return running.Completion;
            }
        }

        var build = new Build(source, frame, generation);
        _running.Add(build);
        build.Completion = TrackAsync(build, start);
        return build.Completion;
    }

    private async Task TrackAsync(Build build, Func<Task> start)
    {
        try
        {
            await start();
        }
        finally
        {
            _running.Remove(build);
        }
    }

    /// <summary>進行中の生成1本。</summary>
    private sealed class Build(object source, int frame, CancellationToken generation)
    {
        /// <summary>生成の完了。進行中に来た同じ要求にも返す。</summary>
        internal Task Completion { get; set; } = Task.CompletedTask;

        /// <summary>同じ生成元・フレーム・世代の生成か。</summary>
        /// <param name="otherSource">生成元の画像。</param>
        /// <param name="otherFrame">生成元のフレーム番号。</param>
        /// <param name="otherGeneration">生成の世代。</param>
        /// <returns>同じ生成ならtrue。</returns>
        internal bool Matches(object otherSource, int otherFrame, CancellationToken otherGeneration)
        {
            return ReferenceEquals(source, otherSource)
                && frame == otherFrame
                && generation == otherGeneration;
        }
    }
}

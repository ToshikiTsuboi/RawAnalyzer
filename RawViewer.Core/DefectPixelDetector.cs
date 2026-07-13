namespace RawViewer.Core;

/// <summary>欠陥画素の種別。</summary>
public enum DefectType
{
    /// <summary>白点(mean + Nσ を超える)。</summary>
    Hot,

    /// <summary>黒点(mean - Nσ を下回る)。</summary>
    Dead,
}

/// <summary>検出された欠陥画素。</summary>
/// <param name="X">X座標。</param>
/// <param name="Y">Y座標。</param>
/// <param name="Code">raw code値。</param>
/// <param name="Type">種別。</param>
public readonly record struct DefectPixel(int X, int Y, int Code, DefectType Type);

/// <summary>
/// 欠陥画素検出の結果。
/// </summary>
public sealed class DefectDetectionResult
{
    internal DefectDetectionResult(
        IReadOnlyList<DefectPixel> defects, RegionStatistics statistics,
        double hotThreshold, double deadThreshold, bool truncated)
    {
        Defects = defects;
        Statistics = statistics;
        HotThreshold = hotThreshold;
        DeadThreshold = deadThreshold;
        Truncated = truncated;
    }

    /// <summary>検出された欠陥画素(y→x順)。</summary>
    public IReadOnlyList<DefectPixel> Defects { get; }

    /// <summary>判定に使った全体統計(raw code値域)。</summary>
    public RegionStatistics Statistics { get; }

    /// <summary>白点判定閾値(これを超えるとHot)。</summary>
    public double HotThreshold { get; }

    /// <summary>黒点判定閾値(これを下回るとDead)。</summary>
    public double DeadThreshold { get; }

    /// <summary>上限件数で打ち切られたかどうか。</summary>
    public bool Truncated { get; }

    /// <summary>白点の数。</summary>
    public int HotCount => Defects.Count(d => d.Type == DefectType.Hot);

    /// <summary>黒点の数。</summary>
    public int DeadCount => Defects.Count(d => d.Type == DefectType.Dead);
}

/// <summary>
/// 欠陥画素(白点/黒点)の検出。全体統計(mean±Nσ)を閾値として全画素を走査する。
/// ダークフレーム(白点検出)やフラットフィールド(黒点検出)への適用を想定。
/// </summary>
public static class DefectPixelDetector
{
    /// <summary>
    /// 欠陥画素を検出する。
    /// </summary>
    /// <param name="image">対象画像。</param>
    /// <param name="frame">フレーム番号。</param>
    /// <param name="sigmaFactor">閾値のσ係数(mean ± N×σ)。</param>
    /// <param name="detectHot">白点を検出するか。</param>
    /// <param name="detectDead">黒点を検出するか。</param>
    /// <param name="maxResults">収集する最大件数(超過分は打ち切り)。</param>
    /// <param name="progress">進捗通知(0〜1)。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>検出結果。</returns>
    /// <exception cref="ArgumentOutOfRangeException">σ係数が正でない場合。</exception>
    public static DefectDetectionResult Detect(
        RawImage image,
        int frame = 0,
        double sigmaFactor = 6.0,
        bool detectHot = true,
        bool detectDead = true,
        int maxResults = 100_000,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (sigmaFactor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sigmaFactor), "σ係数は正の値である必要があります。");
        }

        // パス1: 全体統計
        RegionStatistics stats = ImageAnalysis.ComputeStatistics(
            image, frame, new RegionOfInterest(0, 0, image.Width, image.Height),
            cancellationToken);
        progress?.Report(0.5);

        double hotThreshold = stats.Mean + sigmaFactor * stats.Sigma;
        double deadThreshold = stats.Mean - sigmaFactor * stats.Sigma;
        int shift = 16 - image.Format.BitDepth;
        int width = image.Width;
        int height = image.Height;

        // パス2: 閾値超過画素の収集(行並列)
        object gate = new();
        var defects = new List<DefectPixel>();
        bool truncated = false;
        long rowsDone = 0;

        Parallel.For(
            0,
            height,
            () => (Buffer: new ushort[width], Local: new List<DefectPixel>()),
            (y, state, local) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Volatile.Read(ref truncated))
                {
                    state.Stop();
                    return local;
                }

                image.CopyRegion(frame, 0, y, width, 1, local.Buffer);
                for (int x = 0; x < width; x++)
                {
                    int code = local.Buffer[x] >> shift;
                    if (detectHot && code > hotThreshold)
                    {
                        local.Local.Add(new DefectPixel(x, y, code, DefectType.Hot));
                    }
                    else if (detectDead && code < deadThreshold)
                    {
                        local.Local.Add(new DefectPixel(x, y, code, DefectType.Dead));
                    }
                }

                long done = Interlocked.Increment(ref rowsDone);
                if ((done & 1023) == 0)
                {
                    progress?.Report(0.5 + 0.5 * done / height);
                }

                return local;
            },
            local =>
            {
                lock (gate)
                {
                    int space = maxResults - defects.Count;
                    if (space <= 0)
                    {
                        truncated |= local.Local.Count > 0;
                        return;
                    }

                    if (local.Local.Count > space)
                    {
                        defects.AddRange(local.Local.Take(space));
                        truncated = true;
                    }
                    else
                    {
                        defects.AddRange(local.Local);
                    }
                }
            });

        cancellationToken.ThrowIfCancellationRequested();
        defects.Sort((a, b) => a.Y != b.Y ? a.Y - b.Y : a.X - b.X);
        progress?.Report(1.0);
        return new DefectDetectionResult(defects, stats, hotThreshold, deadThreshold, truncated);
    }
}

namespace RawAnalyzer.Core;

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

/// <summary>チャネル別の判定閾値(Bayer画像での検出に使用)。</summary>
/// <param name="Channel">対象チャネル。</param>
/// <param name="Mean">チャネルの平均(raw code)。</param>
/// <param name="Sigma">チャネルの標準偏差(raw code)。</param>
/// <param name="HotThreshold">白点判定閾値。</param>
/// <param name="DeadThreshold">黒点判定閾値。</param>
public readonly record struct DefectChannelThreshold(
    BayerChannel Channel, double Mean, double Sigma, double HotThreshold, double DeadThreshold);

/// <summary>
/// 欠陥画素検出の結果。
/// </summary>
public sealed class DefectDetectionResult
{
    internal DefectDetectionResult(
        IReadOnlyList<DefectPixel> defects, RegionStatistics statistics,
        double hotThreshold, double deadThreshold, bool truncated,
        IReadOnlyList<DefectChannelThreshold> channelThresholds)
    {
        Defects = defects;
        Statistics = statistics;
        HotThreshold = hotThreshold;
        DeadThreshold = deadThreshold;
        Truncated = truncated;
        ChannelThresholds = channelThresholds;
    }

    /// <summary>検出された欠陥画素(y→x順)。</summary>
    public IReadOnlyList<DefectPixel> Defects { get; }

    /// <summary>判定に使った全体統計(raw code値域)。</summary>
    public RegionStatistics Statistics { get; }

    /// <summary>白点判定閾値(これを超えるとHot)。Bayer検出時はNaN(チャネル別を参照)。</summary>
    public double HotThreshold { get; }

    /// <summary>黒点判定閾値(これを下回るとDead)。Bayer検出時はNaN(チャネル別を参照)。</summary>
    public double DeadThreshold { get; }

    /// <summary>上限件数で打ち切られたかどうか。</summary>
    public bool Truncated { get; }

    /// <summary>
    /// チャネル別の判定閾値。モノクロ検出では空。
    /// Bayer画像はチャネル間の感度差が大きく、全画素混合のmean±Nσでは
    /// 閾値が値域外へ出て欠陥を見逃すため、チャネルごとに判定する。
    /// </summary>
    public IReadOnlyList<DefectChannelThreshold> ChannelThresholds { get; }

    /// <summary>白点の数。</summary>
    public int HotCount => Defects.Count(d => d.Type == DefectType.Hot);

    /// <summary>黒点の数。</summary>
    public int DeadCount => Defects.Count(d => d.Type == DefectType.Dead);
}

/// <summary>
/// 欠陥画素(白点/黒点)の検出。mean±Nσ を閾値として全画素を走査する。
/// Bayerパターン指定時はチャネルごとに統計を取り、チャネル別の閾値で判定する
/// (混合統計ではチャネル間の感度差がσに乗り、カラーのフラットフィールドで
/// 閾値が値域外へ出て欠陥を一切検出できなくなるため)。
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
    /// <param name="pattern">Bayerパターン。None以外でチャネル別判定になる。</param>
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
        BayerPattern pattern = BayerPattern.None,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // NaN は比較を素通りするため、有限性を先に確かめる
        if (!double.IsFinite(sigmaFactor) || sigmaFactor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sigmaFactor), "σ係数は正の値である必要があります。");
        }

        // パス1: 統計(Bayerはチャネル別、モノクロは全体)
        RegionStatistics stats;
        double scalarHot = double.NaN;
        double scalarDead = double.NaN;
        IReadOnlyList<DefectChannelThreshold> channelThresholds =
            Array.Empty<DefectChannelThreshold>();

        // 判定パスで毎画素分岐しないよう、2x2位相→閾値のテーブルにしておく
        Span<double> hotByParity = stackalloc double[4];
        Span<double> deadByParity = stackalloc double[4];

        if (pattern == BayerPattern.None)
        {
            stats = ImageAnalysis.ComputeStatistics(
                image, frame, new RegionOfInterest(0, 0, image.Width, image.Height),
                cancellationToken);
            scalarHot = stats.Mean + sigmaFactor * stats.Sigma;
            scalarDead = stats.Mean - sigmaFactor * stats.Sigma;
            hotByParity.Fill(scalarHot);
            deadByParity.Fill(scalarDead);
        }
        else
        {
            // 閾値の元になる統計なのでサンプリングせず全画素から取る
            ChannelAnalysisResult analysis = ImageAnalysis.ComputeChannelAnalysis(
                image, frame, pattern, region: null,
                maxSamples: long.MaxValue, cancellationToken);
            stats = analysis.Total.Statistics;

            var thresholds = new DefectChannelThreshold[analysis.Channels.Count];
            for (int i = 0; i < analysis.Channels.Count; i++)
            {
                ChannelHistogram channel = analysis.Channels[i];
                thresholds[i] = new DefectChannelThreshold(
                    channel.Channel,
                    channel.Statistics.Mean,
                    channel.Statistics.Sigma,
                    channel.Statistics.Mean + sigmaFactor * channel.Statistics.Sigma,
                    channel.Statistics.Mean - sigmaFactor * channel.Statistics.Sigma);
            }

            channelThresholds = thresholds;
            for (int py = 0; py < 2; py++)
            {
                for (int px = 0; px < 2; px++)
                {
                    BayerChannel ch = BayerHelper.GetChannel(pattern, px, py);
                    DefectChannelThreshold t = thresholds.First(x => x.Channel == ch);
                    hotByParity[py * 2 + px] = t.HotThreshold;
                    deadByParity[py * 2 + px] = t.DeadThreshold;
                }
            }
        }

        progress?.Report(0.5);

        int shift = 16 - image.Format.BitDepth;
        int width = image.Width;
        int height = image.Height;
        double hot0 = hotByParity[0];
        double hot1 = hotByParity[1];
        double hot2 = hotByParity[2];
        double hot3 = hotByParity[3];
        double dead0 = deadByParity[0];
        double dead1 = deadByParity[1];
        double dead2 = deadByParity[2];
        double dead3 = deadByParity[3];

        // パス2: 閾値超過画素の収集(行並列)
        object gate = new();
        var defects = new List<DefectPixel>();
        bool truncated = false;
        long rowsDone = 0;

        // スレッドローカルのListを最後まで貯めると、ヒット率が高い画像で
        // (ワーカ数 × 全ヒット数)ぶんが同時生存しOOMになる。
        // 一定件数ごとにグローバルへ吸い上げてローカルを空にする。
        const int FlushThreshold = 4096;

        void Flush(List<DefectPixel> local)
        {
            if (local.Count == 0)
            {
                return;
            }

            lock (gate)
            {
                int space = maxResults - defects.Count;
                if (space <= 0)
                {
                    truncated = true;
                }
                else if (local.Count > space)
                {
                    defects.AddRange(local.Take(space));
                    truncated = true;
                }
                else
                {
                    defects.AddRange(local);
                }
            }

            local.Clear();
        }

        Parallel.For(
            0,
            height,
            new ParallelOptions { CancellationToken = cancellationToken },
            () => (Buffer: new ushort[width], Local: new List<DefectPixel>()),
            (y, state, local) =>
            {
                if (Volatile.Read(ref truncated))
                {
                    state.Stop();
                    return local;
                }

                image.CopyRegion(frame, 0, y, width, 1, local.Buffer);
                double hotEvenX = (y & 1) == 0 ? hot0 : hot2;
                double hotOddX = (y & 1) == 0 ? hot1 : hot3;
                double deadEvenX = (y & 1) == 0 ? dead0 : dead2;
                double deadOddX = (y & 1) == 0 ? dead1 : dead3;
                for (int x = 0; x < width; x++)
                {
                    int code = local.Buffer[x] >> shift;
                    bool evenX = (x & 1) == 0;
                    if (detectHot && code > (evenX ? hotEvenX : hotOddX))
                    {
                        local.Local.Add(new DefectPixel(x, y, code, DefectType.Hot));
                    }
                    else if (detectDead && code < (evenX ? deadEvenX : deadOddX))
                    {
                        local.Local.Add(new DefectPixel(x, y, code, DefectType.Dead));
                    }
                }

                if (local.Local.Count >= FlushThreshold)
                {
                    Flush(local.Local);
                }

                long done = Interlocked.Increment(ref rowsDone);
                if ((done & 1023) == 0)
                {
                    progress?.Report(0.5 + 0.5 * done / height);
                }

                return local;
            },
            local => Flush(local.Local));

        cancellationToken.ThrowIfCancellationRequested();
        defects.Sort((a, b) => a.Y != b.Y ? a.Y - b.Y : a.X - b.X);
        progress?.Report(1.0);
        return new DefectDetectionResult(
            defects, stats, scalarHot, scalarDead, truncated, channelThresholds);
    }
}

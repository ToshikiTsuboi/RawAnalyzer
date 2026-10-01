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

/// <summary>チャネル別の判定閾値(Bayer画像・区画別の検出に使用)。</summary>
/// <param name="Channel">対象チャネル。区画別のモノクロ検出では <see cref="BayerChannel.None"/>。</param>
/// <param name="Mean">チャネルの平均(raw code)。</param>
/// <param name="Sigma">チャネルの標準偏差(raw code)。</param>
/// <param name="HotThreshold">白点判定閾値。</param>
/// <param name="DeadThreshold">黒点判定閾値。</param>
/// <param name="Segment">区画の番号(左から0起点)。区画に分けない検出では0。</param>
public readonly record struct DefectChannelThreshold(
    BayerChannel Channel, double Mean, double Sigma, double HotThreshold, double DeadThreshold,
    int Segment = 0);

/// <summary>
/// 欠陥画素検出の結果。
/// </summary>
public sealed class DefectDetectionResult
{
    internal DefectDetectionResult(
        IReadOnlyList<DefectPixel> defects, RegionStatistics statistics,
        double hotThreshold, double deadThreshold, bool truncated,
        IReadOnlyList<DefectChannelThreshold> channelThresholds, int segmentCount = 1)
    {
        Defects = defects;
        Statistics = statistics;
        HotThreshold = hotThreshold;
        DeadThreshold = deadThreshold;
        Truncated = truncated;
        ChannelThresholds = channelThresholds;
        SegmentCount = segmentCount;
    }

    /// <summary>
    /// 統計と閾値を別々に求めた区画(列方向に並ぶ帯)の数。区画に分けない検出では1。
    /// </summary>
    public int SegmentCount { get; }

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
    /// <param name="segmentWidth">
    /// 統計と閾値を別々に求める区画の幅(画素)。0以下(または画像の幅以上)なら画像全体で1組。
    /// 正なら左から segmentWidth ごとの列の帯(右端は残りの幅)を区画とし、区画ごと(Bayerは区画×チャネルごと)に
    /// mean±Nσ を求めてその区画の画素を判定する。HDR分割ビュー(各露光の段を左右に並べた1枚)では段の幅を渡す。
    /// 露光の違う段を1つの母集団にすると σ に露光差が乗り、閾値が値域の外へ出て欠陥を見逃すため。
    /// </param>
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
        CancellationToken cancellationToken = default,
        int segmentWidth = 0)
    {
        // NaN は比較を素通りするため、有限性を先に確かめる
        if (!double.IsFinite(sigmaFactor) || sigmaFactor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sigmaFactor), "σ係数は正の値である必要があります。");
        }

        int width = image.Width;
        int height = image.Height;
        bool segmented = segmentWidth > 0 && segmentWidth < width;
        int segmentSize = segmented ? segmentWidth : width;
        int segments = segmented ? (int)(((long)width + segmentWidth - 1) / segmentWidth) : 1;

        // パス1: 統計(Bayerはチャネル別、モノクロは全体)。区画に分けるときは区画ごと
        RegionStatistics stats = default;
        double scalarHot = double.NaN;
        double scalarDead = double.NaN;
        var channelThresholds = new List<DefectChannelThreshold>();

        // 判定パスで毎画素分岐しないよう、区画×2x2位相→閾値のテーブルにしておく
        var hotTable = new double[segments * 4];
        var deadTable = new double[segments * 4];

        if (segmented)
        {
            // 判定には区画ごとの統計を使う。結果の全体統計は従来どおり全画素から取る
            stats = ImageAnalysis.ComputeStatistics(
                image, frame, new RegionOfInterest(0, 0, width, height), cancellationToken);
        }

        for (int segment = 0; segment < segments; segment++)
        {
            int left = segment * segmentSize;
            var region = new RegionOfInterest(left, 0, Math.Min(segmentSize, width - left), height);
            if (pattern == BayerPattern.None)
            {
                RegionStatistics regionStats = ImageAnalysis.ComputeStatistics(
                    image, frame, region, cancellationToken);
                double hot = regionStats.Mean + sigmaFactor * regionStats.Sigma;
                double dead = regionStats.Mean - sigmaFactor * regionStats.Sigma;
                hotTable.AsSpan(segment * 4, 4).Fill(hot);
                deadTable.AsSpan(segment * 4, 4).Fill(dead);
                if (segmented)
                {
                    channelThresholds.Add(new DefectChannelThreshold(
                        BayerChannel.None, regionStats.Mean, regionStats.Sigma, hot, dead, segment));
                }
                else
                {
                    stats = regionStats;
                    scalarHot = hot;
                    scalarDead = dead;
                }

                continue;
            }

            // 閾値の元になる統計なのでサンプリングせず全画素から取る
            ChannelAnalysisResult analysis = ImageAnalysis.ComputeChannelAnalysis(
                image, frame, pattern, region: segmented ? region : null,
                maxSamples: long.MaxValue, cancellationToken);
            if (!segmented)
            {
                stats = analysis.Total.Statistics;
            }

            var thresholds = new DefectChannelThreshold[analysis.Channels.Count];
            for (int i = 0; i < analysis.Channels.Count; i++)
            {
                ChannelHistogram channel = analysis.Channels[i];
                thresholds[i] = new DefectChannelThreshold(
                    channel.Channel,
                    channel.Statistics.Mean,
                    channel.Statistics.Sigma,
                    channel.Statistics.Mean + sigmaFactor * channel.Statistics.Sigma,
                    channel.Statistics.Mean - sigmaFactor * channel.Statistics.Sigma,
                    segment);
            }

            channelThresholds.AddRange(thresholds);
            for (int py = 0; py < 2; py++)
            {
                for (int px = 0; px < 2; px++)
                {
                    BayerChannel ch = BayerHelper.GetChannel(pattern, px, py);
                    DefectChannelThreshold t = thresholds.First(x => x.Channel == ch);
                    hotTable[segment * 4 + py * 2 + px] = t.HotThreshold;
                    deadTable[segment * 4 + py * 2 + px] = t.DeadThreshold;
                }
            }
        }

        progress?.Report(0.5);

        int shift = 16 - image.Format.BitDepth;

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
                for (int segment = 0; segment < segments; segment++)
                {
                    int t = segment * 4 + (y & 1) * 2;
                    double hotEvenX = hotTable[t];
                    double hotOddX = hotTable[t + 1];
                    double deadEvenX = deadTable[t];
                    double deadOddX = deadTable[t + 1];
                    int right = Math.Min(width, (segment + 1) * segmentSize);
                    for (int x = segment * segmentSize; x < right; x++)
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
            defects, stats, scalarHot, scalarDead, truncated, channelThresholds, segments);
    }
}

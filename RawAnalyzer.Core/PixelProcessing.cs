namespace RawAnalyzer.Core;

/// <summary>ビニング・フィルタで共通の確保上限と丸め。</summary>
internal static class PixelProcessing
{
    private const long MaxWorkingBytes = 1L << 30;

    internal static int WorkerCount(int width, int height, int channels, long scratchPerWorker)
    {
        long pixels = (long)width * height;
        // RGBは呼び出し側で解析用の輝度画像も作るため、その分も予約する。
        long outputBytes = pixels * (channels == 3 ? 8 : 2);
        if (pixels > RawLoader.DefaultInMemoryPixelThreshold
            || scratchPerWorker <= 0 || outputBytes + scratchPerWorker > MaxWorkingBytes)
        {
            throw new NotSupportedException(
                "処理結果は1億画素以下、出力と作業領域は合計1GiB以下にしてください。");
        }

        return (int)Math.Min(Math.Clamp(Environment.ProcessorCount, 1, 8),
            (MaxWorkingBytes - outputBytes) / scratchPerWorker);
    }

    internal static ushort Clamp(double value) =>
        (ushort)Math.Clamp(Math.Floor(value + 0.5), 0, ushort.MaxValue);

    /// <summary>
    /// Nbit 素材の 16bit 値を、16bit にする処理(平均ビニング・フィルタ)の結果の値域へ写す倍率。
    /// 最大 code の 16bit 値 (2^N−1)&lt;&lt;(16−N) を 65535 にする(code c は c×65535/(2^N−1) になる)。
    /// 16bit の素材は 1。
    /// </summary>
    /// <remarks>
    /// Nbit 素材の白飛びは (2^N−1)&lt;&lt;(16−N)(12bit は 65520)で、16bit を名乗る結果に写さずに残すと、
    /// 16bit の最大 code(65535)を飽和の基準にする飽和率やカラー現像の白飛びの判定から漏れる。
    /// </remarks>
    /// <param name="bitDepth">素材のビット深度。</param>
    /// <returns>倍率。</returns>
    internal static double FullScaleFactor(int bitDepth) =>
        65535.0 / (((1 << bitDepth) - 1) << (16 - bitDepth));

    /// <summary>
    /// 16bit 域の値を Nbit の code へ四捨五入(0.5 は切り上げ)し、0〜最大コードへ収めて code&lt;&lt;(16−N) で返す。
    /// 結果を元の Nbit のまま返す処理(画像演算・欠陥補正)で使う。端数を残したまま Nbit を名乗ると、
    /// 統計・raw 保存の &gt;&gt;(16−N) で切り捨てられ、平均が約 0.5 code 下がる。
    /// </summary>
    /// <param name="value16">16bit 域の値。</param>
    /// <param name="bitDepth">結果のビット深度(1〜16)。</param>
    /// <returns>code の格子に乗った 16bit 値。</returns>
    internal static ushort RoundToCodeGrid(double value16, int bitDepth)
    {
        int shift = 16 - bitDepth;
        double code = Math.Floor((value16 / (1 << shift)) + 0.5);
        return (ushort)((int)Math.Clamp(code, 0, (1 << bitDepth) - 1) << shift);
    }

    internal static void ValidatePattern(BayerPattern pattern)
    {
        if (!Enum.IsDefined(pattern))
        {
            throw new ArgumentOutOfRangeException(nameof(pattern));
        }
    }

    /// <summary>並列ワーカーからの進捗を1%刻みにまとめ、逆行を防ぐ。</summary>
    internal sealed class RowProgress(int rows, IProgress<double>? target)
    {
        private readonly object _gate = new();
        private int _completed;
        private int _reportedPercent;

        internal void CompleteRow()
        {
            if (target is null)
            {
                return;
            }

            int done = Interlocked.Increment(ref _completed);
            int percent = (int)((long)done * 100 / rows);
            lock (_gate)
            {
                if (percent > _reportedPercent)
                {
                    _reportedPercent = percent;
                    target.Report((double)done / rows);
                }
            }
        }
    }

    internal static RawFormat OutputFormat(RawImage source, int width, int height, BayerPattern pattern) =>
        source.Format with
        {
            Width = width,
            Height = height,
            BitDepth = 16,
            FrameCount = 1,
            HeaderOffset = 0,
            Packing = BitPacking.Lsb,
            Endianness = Endianness.Little,
            Bayer = pattern,
            Hdr = HdrMode.None,
            HdrLineBlock = 0,
            HdrRowOffset = 0,
        };
}

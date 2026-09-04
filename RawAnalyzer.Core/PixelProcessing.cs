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

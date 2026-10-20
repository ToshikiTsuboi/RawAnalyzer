namespace RawAnalyzer.Core;

/// <summary>画像演算の種類。</summary>
public enum ImageOperation
{
    /// <summary>減算: out = clamp(A − B)。ダークフレーム減算用。</summary>
    Subtract,

    /// <summary>絶対差: out = |A − B|。2条件比較用。</summary>
    AbsoluteDifference,

    /// <summary>
    /// ゲイン補正: out = clamp(A × mean(B) / B)。フラットフィールド補正用。mean(B) は B の16bit値の平均。
    /// </summary>
    DivideGain,
}

/// <summary>
/// 2画像間の演算(ImageJのImage Calculator相当のサブセット)。
/// ダーク減算・フラットフィールド補正・差分比較に使う。
/// 演算は正規化済み16bit値域で行い、結果は A のビット深度の code へ四捨五入(0.5 は切り上げ)して0〜最大コードへ
/// 収め、code&lt;&lt;(16−N) で置く(端数を残さないので、統計・raw 保存で code へ戻しても切り捨てで偏らない)。
/// </summary>
public static class ImageCalculator
{
    /// <summary>
    /// 演算を適用した新しい画像を生成する。
    /// </summary>
    /// <param name="source">対象画像(A)。</param>
    /// <param name="reference">参照画像(B)。同一サイズであること。</param>
    /// <param name="operation">演算の種類。</param>
    /// <param name="frame">Aのフレーム番号。</param>
    /// <param name="referenceFrame">Bのフレーム番号。</param>
    /// <param name="pattern">
    /// 結果に付けるCFA。省略時はAのCFA。画面で変更したCFAも指定可能
    /// (画素ごとの演算なので計算自体はCFAに依らない)。
    /// </param>
    /// <param name="progress">進捗通知(0〜1)。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>
    /// 演算結果(Aと同フォーマット・指定CFA、単一フレーム)。行交互HDRは HDR のレイアウト(方式・段数・
    /// 露光比・ライン単位・行オフセット)を保ち、フレーム連結HDRは1露光だけの結果なので HDR 方式なしになる。
    /// </returns>
    /// <exception cref="ArgumentException">サイズが一致しない場合。</exception>
    /// <exception cref="InvalidOperationException">結果が大きすぎてヒープ展開できない場合。</exception>
    public static RawImage Apply(
        RawImage source,
        RawImage reference,
        ImageOperation operation,
        int frame = 0,
        int referenceFrame = 0,
        BayerPattern? pattern = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (source.Width != reference.Width || source.Height != reference.Height)
        {
            throw new ArgumentException(
                $"サイズが一致しません: {source.Width}×{source.Height} と " +
                $"{reference.Width}×{reference.Height}", nameof(reference));
        }

        // ビニング・フィルタと同じく、処理に使ったCFAを結果のフォーマットにする
        BayerPattern cfa = pattern ?? source.Format.Bayer;
        PixelProcessing.ValidatePattern(cfa);

        int width = source.Width;
        int height = source.Height;
        if ((long)width * height > RawLoader.DefaultInMemoryPixelThreshold)
        {
            throw new InvalidOperationException(
                "1億画素を超える画像の演算はサポートされていません。");
        }

        // ゲイン補正は参照画像の平均が必要
        double referenceMean = 0;
        if (operation == ImageOperation.DivideGain)
        {
            referenceMean = SixteenBitMean(reference, referenceFrame, cancellationToken);
            if (referenceMean <= 0)
            {
                throw new InvalidOperationException("参照画像の平均が0のためゲイン補正できません。");
            }
        }

        // 結果は A の Nbit の code の格子へ四捨五入して返す(16bit 域の端数を残したまま Nbit を名乗ると、
        // 統計・raw 保存の >>shift で切り捨てられ、平均が約 0.5 code 下がる)
        int bitDepth = source.Format.BitDepth;
        var pixels = new ushort[(long)width * height];
        long rowsDone = 0;
        Parallel.For(
            0,
            height,
            new ParallelOptions { CancellationToken = cancellationToken },
            () => (RowA: new ushort[width], RowB: new ushort[width]),
            (y, state, buffers) =>
            {
                source.CopyRegion(frame, 0, y, width, 1, buffers.RowA);
                reference.CopyRegion(referenceFrame, 0, y, width, 1, buffers.RowB);
                int offset = y * width;
                switch (operation)
                {
                    case ImageOperation.Subtract:
                        for (int x = 0; x < width; x++)
                        {
                            int v = buffers.RowA[x] - buffers.RowB[x];
                            pixels[offset + x] = PixelProcessing.RoundToCodeGrid(Math.Max(0, v), bitDepth);
                        }

                        break;
                    case ImageOperation.AbsoluteDifference:
                        for (int x = 0; x < width; x++)
                        {
                            pixels[offset + x] = PixelProcessing.RoundToCodeGrid(
                                Math.Abs(buffers.RowA[x] - buffers.RowB[x]), bitDepth);
                        }

                        break;
                    default:
                        for (int x = 0; x < width; x++)
                        {
                            double b = Math.Max(1, (int)buffers.RowB[x]);
                            double v = buffers.RowA[x] * referenceMean / b;
                            pixels[offset + x] = PixelProcessing.RoundToCodeGrid(v, bitDepth);
                        }

                        break;
                }

                long done = Interlocked.Increment(ref rowsDone);
                if ((done & 511) == 0)
                {
                    progress?.Report((double)done / height);
                }

                return buffers;
            },
            _ => { });

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(1.0);
        // 行交互HDRは1フレームに全露光を含み、画素ごとの演算は寸法も行の並びも変えないので、HDR のレイアウトを
        // 保つ(ダーク減算してから HDR 分割・合成できる)。フレーム連結は1フレーム=1露光なので、演算した
        // 1フレームは HDR ではない
        HdrMode hdr = IsLineInterleaved(source.Format) ? source.Format.Hdr : HdrMode.None;
        RawFormat format = source.Format with { FrameCount = 1, Hdr = hdr, Bayer = cfa };
        return RawImage.FromPixels(format, pixels);
    }

    /// <summary>
    /// 参照画像の 16bit 値の平均。code の統計から「code 平均×2^shift」で戻すと、下位ビットの立った画素
    /// (16bit 値に端数を持つ参照)の分が落ちるので、16bit 値をそのまま合計する。
    /// 1億画素の上限があるので long の合計で足りる(65535×1e8 ≈ 6.6e12)。
    /// </summary>
    private static double SixteenBitMean(RawImage reference, int frame, CancellationToken cancellationToken)
    {
        int width = reference.Width;
        int height = reference.Height;
        long total = 0;
        Parallel.For(
            0,
            height,
            new ParallelOptions { CancellationToken = cancellationToken },
            () => (Row: new ushort[width], Sum: 0L),
            (y, state, local) =>
            {
                reference.CopyRegion(frame, 0, y, width, 1, local.Row);
                long sum = local.Sum;
                foreach (ushort v in local.Row)
                {
                    sum += v;
                }

                return (local.Row, sum);
            },
            local => Interlocked.Add(ref total, local.Sum));
        cancellationToken.ThrowIfCancellationRequested();
        return (double)total / ((long)width * height);
    }

    private static bool IsLineInterleaved(RawFormat format)
    {
        if (format.Hdr == HdrMode.None)
        {
            return false;
        }

        try
        {
            return HdrSplitter.ResolveLayout(format, format.HdrStages) == HdrMode.LineInterleaved;
        }
        catch (InvalidOperationException)
        {
            // 格納レイアウトを決められない指定(Auto でフレーム数が1でも段数でもない)は分割もできない
            return false;
        }
    }
}

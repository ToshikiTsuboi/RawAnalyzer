namespace RawViewer.Core;

/// <summary>欠陥画素の補正方法。</summary>
public enum DefectCorrectionMethod
{
    /// <summary>同色近傍のメディアン(外れ値に強く、クラスタ欠陥でも破綻しにくい)。</summary>
    Median,

    /// <summary>同色近傍の平均。</summary>
    Mean,
}

/// <summary>
/// 欠陥画素(白点/黒点)の補間補正。
/// Bayerパターン指定時は同色画素(2画素飛び)のみを参照し、
/// 近傍が同じく欠陥の場合は参照から除外する(クラスタ欠陥対応)。
/// </summary>
public static class DefectCorrector
{
    /// <summary>有効な近傍が足りない場合に広げる最大探索半径(近傍ステップ単位)。</summary>
    public const int MaxSearchRadius = 3;

    /// <summary>
    /// 欠陥画素を近傍補間で補正した新しい画像を生成する。
    /// </summary>
    /// <param name="image">対象画像。</param>
    /// <param name="defects">補正する欠陥画素。</param>
    /// <param name="pattern">Bayerパターン(Noneなら隣接画素を参照)。</param>
    /// <param name="method">補正方法。</param>
    /// <param name="frame">フレーム番号。</param>
    /// <param name="progress">進捗通知(0〜1)。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>補正済み画像(単一フレーム)。</returns>
    /// <exception cref="InvalidOperationException">画像が大きすぎてヒープ展開できない場合。</exception>
    public static RawImage Correct(
        RawImage image,
        IReadOnlyList<DefectPixel> defects,
        BayerPattern pattern,
        DefectCorrectionMethod method = DefectCorrectionMethod.Median,
        int frame = 0,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        int width = image.Width;
        int height = image.Height;
        if ((long)width * height > RawLoader.DefaultInMemoryPixelThreshold)
        {
            throw new InvalidOperationException(
                "1億画素を超える画像の欠陥補正はサポートされていません。");
        }

        // 全画素をコピー
        var pixels = new ushort[(long)width * height];
        Parallel.For(0, height, y =>
        {
            image.CopyRegion(frame, 0, y, width, 1, pixels.AsSpan(y * width, width));
        });
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(0.5);

        if (defects.Count == 0)
        {
            return RawImage.FromPixels(
                image.Format with { FrameCount = 1, Hdr = HdrMode.None }, pixels);
        }

        // 欠陥座標の集合(近傍参照から除外するため)
        var defectSet = new HashSet<long>(defects.Count);
        foreach (DefectPixel defect in defects)
        {
            defectSet.Add((long)defect.Y * width + defect.X);
        }

        // Bayerでは同色画素を参照するため2画素飛びで探索する
        int step = pattern == BayerPattern.None ? 1 : 2;
        var corrected = new ushort[defects.Count];
        var neighbors = new List<ushort>(24);

        for (int i = 0; i < defects.Count; i++)
        {
            if ((i & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            DefectPixel defect = defects[i];
            neighbors.Clear();
            for (int radius = 1; radius <= MaxSearchRadius && neighbors.Count < 2; radius++)
            {
                CollectNeighbors(
                    pixels, width, height, defectSet, defect.X, defect.Y, step, radius, neighbors);
            }

            corrected[i] = neighbors.Count > 0
                ? Combine(neighbors, method)
                : pixels[(long)defect.Y * width + defect.X];
        }

        // 参照は補正前の値で行い、最後に一括で書き戻す
        for (int i = 0; i < defects.Count; i++)
        {
            DefectPixel defect = defects[i];
            pixels[(long)defect.Y * width + defect.X] = corrected[i];
        }

        progress?.Report(1.0);
        return RawImage.FromPixels(
            image.Format with { FrameCount = 1, Hdr = HdrMode.None }, pixels);
    }

    private static void CollectNeighbors(
        ushort[] pixels, int width, int height, HashSet<long> defectSet,
        int x, int y, int step, int radius, List<ushort> neighbors)
    {
        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                // 外周のみ(内側は前の半径で収集済み)
                if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius)
                {
                    continue;
                }

                int nx = x + dx * step;
                int ny = y + dy * step;
                if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                {
                    continue;
                }

                long index = (long)ny * width + nx;
                if (defectSet.Contains(index))
                {
                    continue;
                }

                neighbors.Add(pixels[index]);
            }
        }
    }

    private static ushort Combine(List<ushort> values, DefectCorrectionMethod method)
    {
        if (method == DefectCorrectionMethod.Mean)
        {
            long sum = 0;
            foreach (ushort v in values)
            {
                sum += v;
            }

            return (ushort)(sum / values.Count);
        }

        values.Sort();
        int middle = values.Count / 2;
        return values.Count % 2 == 1
            ? values[middle]
            : (ushort)((values[middle - 1] + values[middle]) / 2);
    }
}

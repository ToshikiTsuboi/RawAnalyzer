using System.Buffers.Binary;

namespace RawViewer.Core;

/// <summary>
/// HDR合成パラメータ。
/// </summary>
/// <param name="ExposureRatio">1段あたりの露光比(長:短)。</param>
/// <param name="BlackLevel">黒レベル(16bitフルスケール値域)。線形化時に減算する。</param>
/// <param name="SaturationThreshold">長秒側を飽和とみなすフルスケール比の閾値(既定0.9)。</param>
/// <param name="BlendWidth">飽和閾値手前のスムーズブレンド帯域幅(フルスケール比、既定0.05)。</param>
public sealed record HdrMergeParameters(
    double ExposureRatio = 16.0,
    ushort BlackLevel = 0,
    double SaturationThreshold = 0.9,
    double BlendWidth = 0.05);

/// <summary>
/// HDR合成結果のfloat32広ダイナミックレンジ画像。
/// </summary>
public sealed class HdrImage
{
    internal HdrImage(int width, int height, float[] pixels, float fullScale, BayerPattern bayer)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
        FullScale = fullScale;
        Bayer = bayer;
    }

    /// <summary>幅(画素数)。</summary>
    public int Width { get; }

    /// <summary>高さ(画素数)。</summary>
    public int Height { get; }

    /// <summary>画素値(線形、黒レベル減算済み)。</summary>
    public float[] Pixels { get; }

    /// <summary>合成域のフルスケール値((65535-black)×露光比^(段数-1))。</summary>
    public float FullScale { get; }

    /// <summary>元フレームのBayerパターン(合成後もモザイク構造は保存される)。</summary>
    public BayerPattern Bayer { get; }

    /// <summary>
    /// <see cref="ToRawImage16"/> の1codeが表す合成域の値(量子化ステップ)。
    /// 1.0 なら無損失、2.0 なら1bit失われている。
    /// </summary>
    public double QuantizationStep => FullScale > 0 ? FullScale / 65535.0 : 0;

    /// <summary>
    /// <see cref="ToRawImage16"/> で失われるビット数(0なら無損失)。
    /// 例: 14bit素材・2段・露光比16 では2bit、3段・露光比16 では4bit失われる。
    /// </summary>
    public double LostBits => QuantizationStep > 1 ? Math.Log2(QuantizationStep) : 0;

    /// <summary>
    /// フルスケールを65535へスケーリングした16bit画像へ量子化する
    /// (表示・16bit TIFF保存用)。
    /// </summary>
    /// <remarks>
    /// 合成域が16bitに収まらない構成では情報が落ちる。落ちる量は
    /// <see cref="LostBits"/> で確認できる。無損失のデータが必要な場合は
    /// <see cref="Pixels"/>(float)または float raw 保存を使うこと。
    /// </remarks>
    /// <returns>量子化された画像(16bit、Bayer付きフォーマット)。</returns>
    public RawImage ToRawImage16()
    {
        var pixels = new ushort[(long)Width * Height];
        float scale = FullScale > 0 ? 65535f / FullScale : 0f;
        Parallel.For(0, Height, y =>
        {
            int offset = y * Width;
            for (int x = 0; x < Width; x++)
            {
                float v = Pixels[offset + x] * scale;
                pixels[offset + x] = (ushort)Math.Clamp((int)MathF.Round(v), 0, 65535);
            }
        });

        var format = new RawFormat
        {
            Width = Width,
            Height = Height,
            BitDepth = 16,
            Bayer = Bayer,
        };
        return new RawImage(format, pixels);
    }

    /// <summary>
    /// float32リトルエンディアンのrawバイナリとして保存する(行単位ストリーミング)。
    /// </summary>
    /// <param name="path">出力先パス。</param>
    /// <param name="progress">進捗通知(0〜1)。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    public void SaveFloatRaw(
        string path, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 20);
        var row = new byte[Width * 4];
        for (int y = 0; y < Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < Width; x++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(
                    row.AsSpan(x * 4, 4), Pixels[y * Width + x]);
            }

            stream.Write(row, 0, row.Length);
            if ((y & 255) == 0 || y == Height - 1)
            {
                progress?.Report((double)(y + 1) / Height);
            }
        }
    }
}

/// <summary>
/// 長秒/短秒フレームのHDR合成。
/// 黒レベル減算→線形化→短秒×露光比スケール→長秒の飽和閾値近傍でスムーズブレンド。
/// 3段は長→中→短の順に再帰適用する。
/// </summary>
public static class HdrMerger
{
    /// <summary>
    /// HDRフレームを合成する。
    /// </summary>
    /// <param name="frames">長秒→短秒の順のフレーム(2〜3枚、同一サイズ)。</param>
    /// <param name="parameters">合成パラメータ。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>float32の合成画像。</returns>
    /// <exception cref="ArgumentException">フレーム数・サイズが不正な場合。</exception>
    public static HdrImage Merge(
        IReadOnlyList<RawImage> frames,
        HdrMergeParameters parameters,
        CancellationToken cancellationToken = default)
    {
        if (frames.Count is < 2 or > 3)
        {
            throw new ArgumentException("HDR合成は2〜3フレームに対応します。", nameof(frames));
        }

        int width = frames[0].Width;
        int height = frames[0].Height;
        foreach (RawImage frame in frames)
        {
            if (frame.Width != width || frame.Height != height)
            {
                throw new ArgumentException("フレームのサイズが一致しません。", nameof(frames));
            }
        }

        float black = parameters.BlackLevel;
        float linearFullScale = 65535f - black;

        // 長秒フレームを線形化して初期値とする
        float[] current = ToLinear(frames[0], black, cancellationToken);
        float currentFullScale = linearFullScale;
        double ratio = parameters.ExposureRatio;

        for (int stage = 1; stage < frames.Count; stage++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            float[] shorter = ToLinear(frames[stage], black, cancellationToken);
            float shortScale = (float)Math.Pow(ratio, stage);
            float t1 = (float)(parameters.SaturationThreshold * currentFullScale);
            float t0 = (float)(t1 - parameters.BlendWidth * currentFullScale);
            if (t0 <= 0)
            {
                t0 = t1 * 0.5f;
            }

            float[] merged = current;
            Parallel.For(0, height, y =>
            {
                int offset = y * width;
                for (int x = 0; x < width; x++)
                {
                    float longer = merged[offset + x];
                    float scaled = shorter[offset + x] * shortScale;
                    if (longer <= t0)
                    {
                        continue;
                    }

                    if (longer >= t1)
                    {
                        merged[offset + x] = scaled;
                        continue;
                    }

                    // smoothstepブレンドで飽和閾値前後を連続に繋ぐ
                    float t = (longer - t0) / (t1 - t0);
                    float w = t * t * (3f - 2f * t);
                    merged[offset + x] = longer + (scaled - longer) * w;
                }
            });

            currentFullScale = (float)(linearFullScale * Math.Pow(ratio, stage));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new HdrImage(
            width, height, current, currentFullScale, frames[0].Format.Bayer);
    }

    private static float[] ToLinear(RawImage frame, float black, CancellationToken ct)
    {
        int width = frame.Width;
        int height = frame.Height;
        var linear = new float[(long)width * height];
        Parallel.For(
            0,
            height,
            () => new ushort[width],
            (y, state, row) =>
            {
                if (ct.IsCancellationRequested)
                {
                    state.Stop();
                    return row;
                }

                frame.CopyRegion(0, 0, y, width, 1, row);
                int offset = y * width;
                for (int x = 0; x < width; x++)
                {
                    linear[offset + x] = Math.Max(0f, row[x] - black);
                }

                return row;
            },
            _ => { });
        ct.ThrowIfCancellationRequested();
        return linear;
    }
}

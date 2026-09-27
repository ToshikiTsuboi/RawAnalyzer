using System.Globalization;

namespace RawAnalyzer.Core;

/// <summary>32bitサンプルの解釈方法(TIFFのSampleFormatに対応)。</summary>
public enum SampleInterpretation
{
    /// <summary>符号なし整数(SampleFormat=1)。</summary>
    UnsignedInteger,

    /// <summary>符号あり整数(SampleFormat=2)。</summary>
    SignedInteger,

    /// <summary>IEEE浮動小数点(SampleFormat=3)。</summary>
    Float,

    /// <summary>IEEE半精度浮動小数点(16bit、SampleFormat=3)。</summary>
    HalfFloat,
}

/// <summary>32bitサンプルの値域。</summary>
/// <param name="Minimum">有限値の最小。有限値が1つもなければ0。</param>
/// <param name="Maximum">有限値の最大。有限値が1つもなければ0。</param>
/// <param name="NonFiniteCount">NaN・無限大の個数。</param>
public readonly record struct SampleRange(double Minimum, double Maximum, long NonFiniteCount);

/// <summary>
/// 値域を1サンプルずつ集計する(ストリーミング版の <see cref="SampleScaling.Scan"/>)。
/// </summary>
public struct SampleRangeAccumulator
{
    private double _min;
    private double _max;
    private long _nonFinite;
    private bool _any;

    /// <summary>1値を加える。非有限値は個数だけ数える。</summary>
    /// <param name="value">サンプル値。</param>
    public void Add(double value)
    {
        if (!double.IsFinite(value))
        {
            _nonFinite++;
            return;
        }

        if (!_any)
        {
            _min = value;
            _max = value;
            _any = true;
            return;
        }

        if (value < _min)
        {
            _min = value;
        }

        if (value > _max)
        {
            _max = value;
        }
    }

    /// <summary>集計結果。有限値が1つもなければ 0〜0。</summary>
    /// <returns>値域。</returns>
    public readonly SampleRange ToRange()
    {
        return _any ? new SampleRange(_min, _max, _nonFinite) : new SampleRange(0, 0, _nonFinite);
    }
}

/// <summary>
/// 32bit(浮動小数点・整数)のサンプルを、内部表現の16bitコードへ写す係数。
/// </summary>
/// <remarks>
/// 32bit実数のTIFFは「0〜1の正規化値」と「ADU等の実スケール値」の両方が使われ、
/// ファイル自身は区別を持たない。値域から慣習を推定し、適用した対応関係を
/// 呼び出し側が表示できるようにする(1codeが表す値が分かれば絶対値へ戻せる)。
/// HDR合成の <see cref="HdrImage.ToRawImage16"/> と同じ考え方で、
/// 表示は常にフルレンジを使い、量子化幅を明示する。
/// 対応関係は両端の値(ともに有限)で持つ。64bit実数の −1e308〜1e308 のように
/// 幅(最大−最小)が double で表せない値域でも、桁あふれせずに写せる。
/// </remarks>
/// <param name="Offset">コード0が表す値。負値を含む場合のみ0以外になる。</param>
/// <param name="Upper">コード65535が表す値(Offsetより大きい)。</param>
/// <param name="Range">元データの値域。</param>
public sealed record SampleScaling(double Offset, double Upper, SampleRange Range)
{
    /// <summary>
    /// コード0〜65535が表す値の幅(Upper−Offset)。幅が double の範囲を超える値域では +∞。
    /// </summary>
    public double Span => Upper - Offset;

    /// <summary>1コードが表す元の値の刻み。幅が double の範囲を超える値域でも有限。</summary>
    public double ValuePerCode => HalfSpan / (65535.0 * 0.5);

    /// <summary>正規化(0〜1)の慣習として扱ったか。</summary>
    public bool IsNormalized => Offset == 0 && Upper == 1;

    /// <summary>
    /// 幅の半分。両端を半分にしてから差を取るので桁あふれしない。2倍・半分は誤差なしのため、
    /// 通常の値域では (Upper−Offset)/2 と同じ値になる。
    /// </summary>
    private double HalfSpan => (Upper * 0.5) - (Offset * 0.5);

    /// <summary>
    /// 32bitのビット列を実値として解釈する。
    /// </summary>
    /// <param name="bits">サンプルの32bitビット列。</param>
    /// <param name="interpretation">解釈方法。</param>
    /// <returns>実値。</returns>
    public static double ToValue(int bits, SampleInterpretation interpretation)
    {
        return interpretation switch
        {
            SampleInterpretation.Float => BitConverter.Int32BitsToSingle(bits),
            SampleInterpretation.HalfFloat => (double)BitConverter.UInt16BitsToHalf((ushort)bits),
            SampleInterpretation.SignedInteger => bits,
            _ => (uint)bits,
        };
    }

    /// <summary>
    /// サンプル列の値域を調べる。
    /// </summary>
    /// <param name="bits">32bitビット列の配列。</param>
    /// <param name="count">走査する要素数。</param>
    /// <param name="interpretation">解釈方法。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>値域。</returns>
    public static SampleRange Scan(
        int[] bits, long count, SampleInterpretation interpretation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bits);
        if (count < 0 || count > bits.LongLength)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        object gate = new();
        double totalMin = double.PositiveInfinity;
        double totalMax = double.NegativeInfinity;
        long totalNonFinite = 0;

        // 行単位ではなく区画分割にして、区画あたりの確保をなくす
        int partitions = (int)Math.Clamp(
            Environment.ProcessorCount, 1, Math.Max(1, count / 65536));
        Parallel.For(
            0,
            partitions,
            new ParallelOptions { CancellationToken = cancellationToken },
            partition =>
            {
                long first = count * partition / partitions;
                long last = count * (partition + 1) / partitions;
                double min = double.PositiveInfinity;
                double max = double.NegativeInfinity;
                long nonFinite = 0;
                for (long i = first; i < last; i++)
                {
                    if ((i & 0xFFFFF) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    double value = ToValue(bits[i], interpretation);
                    if (!double.IsFinite(value))
                    {
                        nonFinite++;
                        continue;
                    }

                    if (value < min)
                    {
                        min = value;
                    }

                    if (value > max)
                    {
                        max = value;
                    }
                }

                lock (gate)
                {
                    totalMin = Math.Min(totalMin, min);
                    totalMax = Math.Max(totalMax, max);
                    totalNonFinite += nonFinite;
                }
            });

        cancellationToken.ThrowIfCancellationRequested();
        return double.IsFinite(totalMin) && double.IsFinite(totalMax)
            ? new SampleRange(totalMin, totalMax, totalNonFinite)
            : new SampleRange(0, 0, totalNonFinite);
    }

    /// <summary>
    /// 値域から対応関係を決める。
    /// </summary>
    /// <remarks>
    /// 最大値が1以下なら0〜1の正規化データとみなす(実数TIFFの一般的な慣習)。
    /// それ以外は実データの最大までを使い切る。負値があるとコード0が0を指さなく
    /// なるが、暗電流減算後の負のすそを切り捨てるとノイズ評価が狂うため残す。
    /// </remarks>
    /// <param name="range">値域。</param>
    /// <returns>対応関係。</returns>
    public static SampleScaling FromRange(SampleRange range)
    {
        double minimum = double.IsFinite(range.Minimum) ? range.Minimum : 0;
        double maximum = double.IsFinite(range.Maximum) ? range.Maximum : 0;
        double offset = Math.Min(0, minimum);
        double upper = maximum;
        if (offset == 0 && upper <= 1)
        {
            upper = 1; // 0〜1の正規化データ(非有限のみ・全画素0も含む)
        }

        if (upper <= offset)
        {
            // 全画素が同じ負値。幅1にして破綻させない。桁落ちで offset+1 が同じ値に
            // なるほど大きい値なら、次に大きい表現可能な値を上端にする
            upper = offset + 1 > offset ? offset + 1 : Math.BitIncrement(offset);
        }

        return new SampleScaling(offset, upper, range);
    }

    /// <summary>
    /// 実値を16bitコードへ写す。非有限値と範囲外はコード0/65535へ丸める。
    /// </summary>
    /// <param name="value">実値。</param>
    /// <returns>16bitコード。</returns>
    public ushort ToCode(double value)
    {
        if (!double.IsFinite(value))
        {
            return 0;
        }

        // 値も半分にしてから差を取り、幅が double を超える値域でも桁あふれさせない
        double code = Math.Round(((value * 0.5) - (Offset * 0.5)) / HalfSpan * 65535.0);
        return (ushort)Math.Clamp(code, 0, 65535);
    }

    /// <summary>
    /// サンプル列を16bitコードへ変換する。
    /// </summary>
    /// <param name="bits">32bitビット列の配列。</param>
    /// <param name="count">変換する要素数。</param>
    /// <param name="interpretation">解釈方法。</param>
    /// <param name="destination">出力先(count以上)。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <exception cref="ArgumentException">出力先が小さい場合。</exception>
    public void Convert(
        int[] bits, long count, SampleInterpretation interpretation, ushort[] destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bits);
        ArgumentNullException.ThrowIfNull(destination);
        if (count < 0 || count > bits.LongLength)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if (destination.LongLength < count)
        {
            throw new ArgumentException("出力バッファが小さすぎます。", nameof(destination));
        }

        int partitions = (int)Math.Clamp(
            Environment.ProcessorCount, 1, Math.Max(1, count / 65536));
        Parallel.For(
            0,
            partitions,
            new ParallelOptions { CancellationToken = cancellationToken },
            partition =>
            {
                long first = count * partition / partitions;
                long last = count * (partition + 1) / partitions;
                for (long i = first; i < last; i++)
                {
                    if ((i & 0xFFFFF) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    destination[i] = ToCode(ToValue(bits[i], interpretation));
                }
            });

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// 適用した対応関係の説明(画像情報欄への表示用)。
    /// </summary>
    /// <param name="bitsPerSample">元データの1サンプルのビット数(表示用)。</param>
    /// <returns>表示用テキスト。</returns>
    public string Describe(int bitsPerSample = 32)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        string body = IsNormalized
            ? $"{bitsPerSample}bit実数 0〜1 → 16bit"
            : $"{bitsPerSample}bit値 {Offset.ToString("G6", culture)}〜" +
              $"{Upper.ToString("G6", culture)} → 16bit " +
              $"(1code≈{ValuePerCode.ToString("G3", culture)})";
        return Range.NonFiniteCount > 0
            ? $"{body}・非数{Range.NonFiniteCount.ToString("N0", culture)}画素は0"
            : body;
    }
}

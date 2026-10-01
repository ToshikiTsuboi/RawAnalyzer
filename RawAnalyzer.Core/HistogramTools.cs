using System.Globalization;
using System.Text;

namespace RawAnalyzer.Core;

/// <summary>自動コントラストで決定した黒点・白点(raw code値域)。</summary>
/// <param name="BlackCode">黒点のraw code。</param>
/// <param name="WhiteCode">白点のraw code。</param>
public readonly record struct AutoLevels(int BlackCode, int WhiteCode);

/// <summary>
/// ヒストグラムから導かれる補助アルゴリズム(自動コントラスト・表示用集約・CSV出力)。
/// </summary>
/// <remarks>
/// 元々はApp層のイベントハンドラ内に直書きされておりテストできなかった。
/// UI非依存の計算はここへ置く。
/// </remarks>
public static class HistogramTools
{
    /// <summary>自動コントラストで両端を切り捨てる既定割合(0.35%)。</summary>
    public const double DefaultClipRatio = 0.0035;

    /// <summary>
    /// 上下を一定割合クリップした黒点・白点を求める。
    /// </summary>
    /// <param name="bins">ヒストグラムのビン(raw code値域)。</param>
    /// <param name="clipRatio">両端で切り捨てる割合(0〜0.5未満)。</param>
    /// <returns>
    /// 黒点 &lt; 白点となるレベル。全画素が同一値など決定できない場合はnull。
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">clipRatioが範囲外の場合。</exception>
    public static AutoLevels? ComputeAutoLevels(
        ReadOnlySpan<long> bins, double clipRatio = DefaultClipRatio)
    {
        if (clipRatio < 0 || clipRatio >= 0.5)
        {
            throw new ArgumentOutOfRangeException(
                nameof(clipRatio), "クリップ割合は0以上0.5未満である必要があります。");
        }

        long total = 0;
        foreach (long count in bins)
        {
            total += count;
        }

        if (total == 0)
        {
            return null;
        }

        long clip = (long)(total * clipRatio);

        int blackCode = 0;
        long accumulated = 0;
        for (int i = 0; i < bins.Length; i++)
        {
            accumulated += bins[i];
            if (accumulated > clip)
            {
                blackCode = i;
                break;
            }
        }

        int whiteCode = bins.Length - 1;
        accumulated = 0;
        for (int i = bins.Length - 1; i >= 0; i--)
        {
            accumulated += bins[i];
            if (accumulated > clip)
            {
                whiteCode = i;
                break;
            }
        }

        return whiteCode > blackCode ? new AutoLevels(blackCode, whiteCode) : null;
    }

    /// <summary>
    /// ヒストグラムを表示列数へ集約する(各列は担当ビンの度数合計)。
    /// </summary>
    /// <param name="bins">ヒストグラムのビン。</param>
    /// <param name="columns">出力列数(1以上)。</param>
    /// <param name="cumulative">trueなら累積(左から積み上げ)にする。</param>
    /// <returns>長さcolumnsの集約結果。</returns>
    /// <exception cref="ArgumentOutOfRangeException">columnsが1未満の場合。</exception>
    public static double[] Aggregate(long[] bins, int columns, bool cumulative = false)
    {
        if (columns < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(columns), "列数は1以上である必要があります。");
        }

        // 比例写像 i*columns/len で全列へ均等に配分する。
        // 「列あたりビン数」の固定値で割ると端数のぶん右端の列が使われず、
        // ヒストグラム全体が左へ圧縮される(8bit/210列では右39%が常に空白で、
        // 飽和スパイクが中央付近に描画されていた)
        var result = new double[columns];
        for (int i = 0; i < bins.Length; i++)
        {
            result[(int)((long)i * columns / bins.Length)] += bins[i];
        }

        if (cumulative)
        {
            double running = 0;
            for (int x = 0; x < columns; x++)
            {
                running += result[x];
                result[x] = running;
            }
        }

        return result;
    }

    /// <summary>
    /// ヒストグラムを表示列数へ集約する(棒の高さ用)。累積は <see cref="Aggregate"/> と同じ。
    /// 非累積は、各列の度数合計をその列が担当するビン数で割った1ビンあたりの度数に、
    /// 名目の列あたりビン数(ビン数÷列数)を掛けた値にする。
    /// </summary>
    /// <remarks>
    /// ビン数が列数の整数倍でないと、比例写像では列ごとの担当ビン数が揃わない(8bit・210列では164列が1ビン、
    /// 46列が2ビン。10bitでは184列が5ビン、26列が4ビン)。合計のまま描くと、平らな分布でも担当ビンの多い列が周期的に
    /// 高く(少ない列が低く)なり、ADCのミッシングコードやDNLのような偽の櫛に見える。1ビンあたりにすれば揃い、
    /// 欠けたコードは担当ビン数に応じた割合で低く描かれる。名目の列あたりビン数を掛けるのは、割り切れるとき
    /// (全列の担当ビン数が同じ)に合計と同じ値にするため(対数表示の形も変えない)。累積は各列の最後のビンまでの
    /// 累積値で櫛にならない。担当ビンのない列(ビン数が列数より少ないとき)は0。
    /// </remarks>
    /// <param name="bins">ヒストグラムのビン。</param>
    /// <param name="columns">出力列数(1以上)。</param>
    /// <param name="cumulative">trueなら累積(左から積み上げ)にする。</param>
    /// <returns>長さcolumnsの集約結果。</returns>
    /// <exception cref="ArgumentOutOfRangeException">columnsが1未満の場合。</exception>
    public static double[] AggregateForDisplay(long[] bins, int columns, bool cumulative)
    {
        if (cumulative)
        {
            return Aggregate(bins, columns, cumulative: true);
        }

        if (columns < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(columns), "列数は1以上である必要があります。");
        }

        // 写像は Aggregate と同じ比例写像
        var result = new double[columns];
        var binsPerColumn = new int[columns];
        for (int i = 0; i < bins.Length; i++)
        {
            int column = (int)((long)i * columns / bins.Length);
            result[column] += bins[i];
            binsPerColumn[column]++;
        }

        double nominal = (double)bins.Length / columns;
        for (int x = 0; x < columns; x++)
        {
            result[x] = binsPerColumn[x] > 0 ? result[x] / binsPerColumn[x] * nominal : 0;
        }

        return result;
    }

    /// <summary>
    /// ヒストグラムを表計算ソフトへ貼り付け可能なテキストにする。
    /// </summary>
    /// <remarks>
    /// 大きな画像・領域のヒストグラムは間引いて集計するので(<see cref="HistogramResult.IsSampled"/>)、count は
    /// 画素数ではなくサンプル数になる。<paramref name="populationCount"/> がサンプル数(ビンの合計)より多いときは、
    /// 表の前に「# sampled: …」の1行でサンプル数・対象の画素数・おおよその間引き率を書く(表の列は変えない)。
    /// </remarks>
    /// <param name="bins">全体のヒストグラム。</param>
    /// <param name="separator">区切り文字(タブまたはカンマ)。</param>
    /// <param name="channels">Bayerチャネル別ヒストグラム(R,Gr,Gb,Bの順)。nullなら出力しない。</param>
    /// <param name="populationCount">
    /// 集計の対象とした画素数(間引く前。ROIならROIの画素数)。0以下なら間引きの注記を書かない。
    /// </param>
    /// <returns>ヘッダ付きのテキスト。</returns>
    /// <exception cref="ArgumentException">チャネル別ビンの長さが一致しない場合。</exception>
    public static string BuildTable(
        long[] bins, char separator, IReadOnlyList<ChannelHistogram>? channels = null, long populationCount = 0)
    {
        bool byChannel = channels is { Count: 4 };
        if (byChannel)
        {
            foreach (ChannelHistogram channel in channels!)
            {
                if (channel.Bins.Length != bins.Length)
                {
                    throw new ArgumentException(
                        "チャネル別ヒストグラムのビン数が全体と一致しません。", nameof(channels));
                }
            }
        }

        var builder = new StringBuilder();
        long sampleCount = 0;
        foreach (long count in bins)
        {
            sampleCount += count;
        }

        if (sampleCount > 0 && populationCount > sampleCount)
        {
            double ratio = (double)populationCount / sampleCount;
            builder.Append("# sampled: count is the number of sampled pixels (")
                .Append(sampleCount.ToString(CultureInfo.InvariantCulture)).Append(" of ")
                .Append(populationCount.ToString(CultureInfo.InvariantCulture)).Append(" pixels, about 1/")
                .Append(ratio.ToString("F1", CultureInfo.InvariantCulture)).AppendLine(")");
        }

        builder.Append("raw_code").Append(separator).Append("count")
            .Append(separator).Append("cumulative");
        if (byChannel)
        {
            builder.Append(separator).Append("count_R").Append(separator).Append("count_Gr")
                .Append(separator).Append("count_Gb").Append(separator).Append("count_B");
        }

        builder.AppendLine();

        long cumulative = 0;
        for (int i = 0; i < bins.Length; i++)
        {
            cumulative += bins[i];
            builder.Append(i.ToString(CultureInfo.InvariantCulture)).Append(separator)
                .Append(bins[i].ToString(CultureInfo.InvariantCulture)).Append(separator)
                .Append(cumulative.ToString(CultureInfo.InvariantCulture));
            if (byChannel)
            {
                foreach (ChannelHistogram channel in channels!)
                {
                    builder.Append(separator)
                        .Append(channel.Bins[i].ToString(CultureInfo.InvariantCulture));
                }
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }
}

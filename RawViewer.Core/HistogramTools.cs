using System.Globalization;
using System.Text;

namespace RawViewer.Core;

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
        ReadOnlySpan<uint> bins, double clipRatio = DefaultClipRatio)
    {
        if (clipRatio < 0 || clipRatio >= 0.5)
        {
            throw new ArgumentOutOfRangeException(
                nameof(clipRatio), "クリップ割合は0以上0.5未満である必要があります。");
        }

        long total = 0;
        foreach (uint count in bins)
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
    public static double[] Aggregate(uint[] bins, int columns, bool cumulative = false)
    {
        if (columns < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(columns), "列数は1以上である必要があります。");
        }

        var result = new double[columns];
        int binsPerColumn = bins.Length / columns + 1;
        for (int i = 0; i < bins.Length; i++)
        {
            result[Math.Min(i / binsPerColumn, columns - 1)] += bins[i];
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
    /// ヒストグラムを表計算ソフトへ貼り付け可能なテキストにする。
    /// </summary>
    /// <param name="bins">全体のヒストグラム。</param>
    /// <param name="separator">区切り文字(タブまたはカンマ)。</param>
    /// <param name="channels">Bayerチャネル別ヒストグラム(R,Gr,Gb,Bの順)。nullなら出力しない。</param>
    /// <returns>ヘッダ付きのテキスト。</returns>
    /// <exception cref="ArgumentException">チャネル別ビンの長さが一致しない場合。</exception>
    public static string BuildTable(
        uint[] bins, char separator, IReadOnlyList<ChannelHistogram>? channels = null)
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

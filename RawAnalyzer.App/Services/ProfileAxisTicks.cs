using System.Globalization;

namespace RawAnalyzer.App.Services;

/// <summary>
/// グラフの軸の目盛り(主・補助)の間隔と位置。表示だけに使い、データ・統計は変えない。
/// 目盛りは間隔の整数倍の値に置く(軸の最小値からではなく 0 から数えるので、移動・拡大しても同じ値に並ぶ)。
/// </summary>
/// <param name="MajorStep">主目盛りの間隔(描いていなければ 0)。</param>
/// <param name="Major">主目盛りの値(小さい順)。</param>
/// <param name="MinorStep">補助目盛りの間隔(描いていなければ 0)。</param>
/// <param name="Minor">補助目盛りの値(主目盛りと重なるものは除く。小さい順)。</param>
/// <param name="MajorSpacing">主目盛りの間隔の画面上の長さ(px)。</param>
/// <param name="Decimals">ラベルの小数の桁数(主目盛りの間隔を表せる桁数)。</param>
/// <param name="ManualMajorTooFine">指定の主目盛りの間隔が細かすぎて、自動の間隔で描いたか。</param>
/// <param name="ManualMinorOmitted">指定の補助目盛りの間隔を、細かすぎるか主目盛りの間隔以上で描かなかったか。</param>
internal readonly record struct ProfileAxisTicks(
    double MajorStep,
    IReadOnlyList<double> Major,
    double MinorStep,
    IReadOnlyList<double> Minor,
    double MajorSpacing,
    int Decimals,
    bool ManualMajorTooFine,
    bool ManualMinorOmitted)
{
    /// <summary>主目盛りの画面上の最小の間隔(px)。指定の間隔がこれより詰まる拡大率では自動の間隔で描く。</summary>
    internal const double MinimumMajorSpacing = 4;

    /// <summary>補助目盛りの画面上の最小の間隔(px)。これより詰まる拡大率では描かない。</summary>
    internal const double MinimumMinorSpacing = 3;

    private const int MaxDecimals = 12;

    /// <summary>目盛りを描かない(データがない・軸の長さがない)とき。</summary>
    internal static ProfileAxisTicks Empty { get; } =
        new(0, Array.Empty<double>(), 0, Array.Empty<double>(), 0, 0, false, false);

    /// <summary>軸の目盛りを決める。</summary>
    /// <param name="range">軸の範囲(表示する値で)。</param>
    /// <param name="length">軸の画面上の長さ(px)。</param>
    /// <param name="targetSpacing">自動の主目盛りの画面上の間隔の目安(px)。</param>
    /// <param name="integerSteps">間隔を 1 以上の整数に限るか(画素の座標の軸)。</param>
    /// <param name="majorStep">指定の主目盛りの間隔(自動なら null)。</param>
    /// <param name="minorStep">指定の補助目盛りの間隔(自動なら null、0 なら描かない)。</param>
    /// <returns>目盛り。</returns>
    internal static ProfileAxisTicks Layout(ProfileAxisRange range, double length, double targetSpacing,
        bool integerSteps, double? majorStep, double? minorStep)
    {
        double span = range.Maximum - range.Minimum;
        if (!(span > 0) || !double.IsFinite(span) || !(length >= 1)) return Empty;

        double major = AutoStep(span, length, targetSpacing, integerSteps);
        bool tooFine = false;
        if (majorStep is { } manual && manual > 0 && double.IsFinite(manual))
        {
            if (manual / span * length >= MinimumMajorSpacing) major = manual;
            else tooFine = true;
        }

        int maxCount = (int)Math.Min(100_000, length / MinimumMajorSpacing) + 2;
        IReadOnlyList<double> majors = Generate(range, major, maxCount);

        double minor = minorStep ?? AutoMinorStep(major, integerSteps);
        bool minorOmitted = false;
        IReadOnlyList<double> minors = Array.Empty<double>();
        if (minor > 0 && double.IsFinite(minor))
        {
            if (minor < major && minor / span * length >= MinimumMinorSpacing)
            {
                minors = Generate(range, minor, (int)Math.Min(100_000, length / MinimumMinorSpacing) + 2)
                    .Where(value => !IsMultiple(value, major)).ToArray();
            }
            else
            {
                minorOmitted = minorStep is not null;
                minor = 0;
            }
        }
        else
        {
            minor = 0;
        }

        return new ProfileAxisTicks(major, majors, minor, minors, major / span * length,
            DecimalsOf(major), tooFine, minorOmitted);
    }

    /// <summary>
    /// 自動の主目盛りの間隔。画面上でおよそ <paramref name="targetSpacing"/> px ごとになる 1・2・5×10ⁿ の値。
    /// </summary>
    /// <param name="span">軸の範囲の幅。</param>
    /// <param name="length">軸の画面上の長さ(px)。</param>
    /// <param name="targetSpacing">画面上の間隔の目安(px)。</param>
    /// <param name="integerSteps">1 以上の整数に限るか。</param>
    /// <returns>間隔。</returns>
    internal static double AutoStep(double span, double length, double targetSpacing, bool integerSteps)
    {
        double target = span / Math.Max(1, Math.Floor(length / targetSpacing));
        if (!(target > 0) || !double.IsFinite(target)) return 1;
        double power = Math.Pow(10, Math.Floor(Math.Log10(integerSteps ? Math.Max(1, target) : target)));
        return new[] { 1.0, 2.0, 5.0, 10.0 }.First(value => value * power >= target) * power;
    }

    /// <summary>
    /// 自動の補助目盛りの間隔。主目盛りの間隔の仮数が 2 なら 4 分割、それ以外は 5 分割する。
    /// 整数に限る軸で割り切れなければ 2 分割し、それも割り切れなければ描かない(0)。
    /// </summary>
    /// <param name="majorStep">主目盛りの間隔。</param>
    /// <param name="integerSteps">1 以上の整数に限るか。</param>
    /// <returns>間隔(描かなければ 0)。</returns>
    internal static double AutoMinorStep(double majorStep, bool integerSteps)
    {
        if (!(majorStep > 0) || !double.IsFinite(majorStep)) return 0;
        double mantissa = majorStep / Math.Pow(10, Math.Floor(Math.Log10(majorStep)));
        double minor = majorStep / (Math.Abs(mantissa - 2) < 1e-6 ? 4 : 5);
        if (!integerSteps) return minor;
        if (IsInteger(minor) && minor >= 1) return Math.Round(minor);
        double half = majorStep / 2;
        return IsInteger(half) && half >= 1 ? Math.Round(half) : 0;
    }

    /// <summary>範囲に入る、間隔の整数倍の値(小さい順)。数が上限を超えるときは描かない(空)。</summary>
    /// <param name="range">範囲。</param>
    /// <param name="step">間隔。</param>
    /// <param name="maxCount">数の上限。</param>
    /// <returns>値。</returns>
    internal static IReadOnlyList<double> Generate(ProfileAxisRange range, double step, int maxCount)
    {
        if (!(step > 0) || !double.IsFinite(step)) return Array.Empty<double>();
        // 足し合わせると誤差が積もるので、何番目かに間隔を掛けて求める
        double first = Math.Ceiling(range.Minimum / step - 1e-9);
        double last = Math.Floor(range.Maximum / step + 1e-9);
        if (!double.IsFinite(first) || !double.IsFinite(last) || last < first || last - first + 1 > maxCount)
            return Array.Empty<double>();
        var values = new double[(int)(last - first) + 1];
        for (int i = 0; i < values.Length; i++) values[i] = (first + i) * step;
        return values;
    }

    /// <summary>
    /// ラベルを何本おきの主目盛りに付けるか。ラベル同士が重ならない最小の 1・2・5×10ⁿ 本おき。
    /// </summary>
    /// <param name="spacing">主目盛りの画面上の間隔(px)。</param>
    /// <param name="labelExtent">ラベル1つに要る画面上の長さ(px。隙間を含む)。</param>
    /// <returns>何本おきか(1 なら全部)。</returns>
    internal static long LabelEvery(double spacing, double labelExtent)
    {
        if (!(spacing > 0) || spacing >= labelExtent) return 1;
        for (long power = 1; power < long.MaxValue / 10; power *= 10)
        {
            foreach (long factor in new long[] { 1, 2, 5 })
            {
                if (factor * power * spacing >= labelExtent) return factor * power;
            }
        }

        return long.MaxValue;
    }

    /// <summary>
    /// 主目盛りにラベルを付けるか。0 から数えて <paramref name="every"/> 本ごとに付ける
    /// (移動しても同じ値にラベルが残る)。
    /// </summary>
    /// <param name="value">主目盛りの値。</param>
    /// <param name="every">何本おきか。</param>
    /// <returns>付けるなら true。</returns>
    internal bool IsLabeled(double value, long every) =>
        every <= 1 || (MajorStep > 0 && Math.Round(value / MajorStep) % every == 0);

    /// <summary>目盛りのラベル(主目盛りの間隔を表せる小数の桁数で丸める。-0 は 0)。</summary>
    /// <param name="value">値。</param>
    /// <param name="culture">表示の言語。</param>
    /// <returns>ラベル。</returns>
    internal string Format(double value, CultureInfo culture)
    {
        double rounded = Math.Round(value, Decimals);
        if (rounded == 0) rounded = 0;
        return rounded.ToString("F" + Decimals.ToString(CultureInfo.InvariantCulture), culture);
    }

    /// <summary>間隔を表せる小数の桁数(0.25 なら 2、1000 なら 0)。</summary>
    /// <param name="step">間隔。</param>
    /// <returns>桁数。</returns>
    internal static int DecimalsOf(double step)
    {
        if (!(step > 0) || !double.IsFinite(step)) return 0;
        double scaled = step;
        for (int decimals = 0; decimals < MaxDecimals; decimals++, scaled *= 10)
        {
            if (Math.Abs(scaled - Math.Round(scaled)) <= 1e-9 * Math.Max(1, scaled)) return decimals;
        }

        return MaxDecimals;
    }

    /// <summary>
    /// 目盛りの間隔の入力を読む。読めなければ理由を返す(読めたら null)。
    /// 縦軸・横軸の範囲の欄と同じく、全角の数字・記号と3桁区切りも受け付ける。
    /// </summary>
    /// <param name="text">入力。</param>
    /// <param name="culture">表示の言語。</param>
    /// <param name="integerSteps">1 以上の整数に限るか(画素の座標の軸)。</param>
    /// <param name="allowZero">0(描かない)を受け付けるか(補助目盛り)。</param>
    /// <param name="step">読めた間隔。</param>
    /// <returns>読めなければ理由、読めたら null。</returns>
    internal static string? ParseStep(string text, CultureInfo culture, bool integerSteps, bool allowZero,
        out double step)
    {
        if (!ProfileAxisRange.TryParseLimit(text, culture, out step)) return "有限の数値で指定してください。";
        if (allowZero && step == 0) return null;
        if (!(step > 0)) return allowZero ? "0 以上の値を指定してください(0 で補助目盛りなし)。" : "0 より大きい値を指定してください。";
        if (integerSteps && !(IsInteger(step) && step >= 1))
            return "横軸は画素の座標なので、1 以上の整数で指定してください" + (allowZero ? "(0 で補助目盛りなし)。" : "。");
        if (integerSteps) step = Math.Round(step);
        return null;
    }

    private static bool IsInteger(double value) => Math.Abs(value - Math.Round(value)) <= 1e-9 * Math.Max(1, Math.Abs(value));

    private static bool IsMultiple(double value, double step)
    {
        double ratio = value / step;
        return Math.Abs(ratio - Math.Round(ratio)) < 1e-6;
    }
}

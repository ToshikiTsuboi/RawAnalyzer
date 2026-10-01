using System.Globalization;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>
/// 処理(ビニング・フィルタ・欠陥補正・画像演算)が、16bit コードと元データの値の対応に与える変化。
/// </summary>
/// <remarks>
/// 処理はコードの値域で行う。重みの和が1の線形の処理(平均・ガウシアン・アンシャープ・平均ビニング・
/// カラーの輝度化)、順序統計(メディアン・最小・最大)、画素の置き換え(欠陥補正)は「値 = Offset + code × 刻み」の
/// 対応を変えない。重みの和が1でない処理は Offset を、コードを縮める処理は刻みを変える
/// (新しい対応は「値 = Offset × <see cref="OffsetScale"/> + code × 刻み × <see cref="PerCodeScale"/>」)。
/// </remarks>
/// <param name="Quantity">対応が変わったときに説明する量の名前(例: 差、4画素の和)。</param>
/// <param name="OffsetScale">Offset に掛かる係数(重みの和)。差・勾配は0、n 画素の和は n。</param>
/// <param name="PerCodeScale">1code の刻みに掛かる係数(Sobel は結果を1/4にするので4)。</param>
/// <param name="RequiresZeroOffset">
/// Offset が0のときだけ対応を保つ処理か(ゲイン補正はコードの比で補正するので、Offset があると元の値へ戻せない)。
/// </param>
internal sealed record ValueMappingChange(
    string Quantity, double OffsetScale, double PerCodeScale, bool RequiresZeroOffset = false)
{
    /// <summary>対応を変えない処理(フィルタの大半・平均ビニング・欠陥補正)。</summary>
    internal static readonly ValueMappingChange None = new("値", 1, 1);

    /// <summary>2画像の差(減算・絶対差)。Offset は打ち消し合い、コード0が差なしになる。</summary>
    internal static readonly ValueMappingChange Difference = new("差", 0, 1);

    /// <summary>Sobel の勾配の大きさ(結果は1/4スケール。Offset は微分で消える)。</summary>
    internal static readonly ValueMappingChange SobelMagnitude = new("Sobel勾配", 0, 4);

    /// <summary>ゲイン補正(A × mean(B) / B)。Offset が0なら対応を保つ。</summary>
    internal static readonly ValueMappingChange GainCorrection = new("ゲイン補正値", 1, 1, RequiresZeroOffset: true);

    /// <summary>n 画素の和(加算ビニング)。</summary>
    /// <param name="count">足し合わせる画素数。</param>
    /// <returns>変化。</returns>
    internal static ValueMappingChange Sum(int count) => new($"{count}画素の和", count, 1);

    /// <summary>ビニング・フィルタの変化。</summary>
    /// <param name="filter">フィルタ(ビニングならnull)。</param>
    /// <param name="factor">ビニングの各方向の集約数。</param>
    /// <param name="mode">ビニングの集約方法。</param>
    /// <returns>変化。</returns>
    internal static ValueMappingChange ForProcessing(ImageFilterOptions? filter, int factor, BinningMode mode)
    {
        if (filter is not null)
        {
            return filter.Kind == ImageFilterKind.Sobel ? SobelMagnitude : None;
        }

        // Bayer も同じ色の factor×factor 画素を足す
        return mode == BinningMode.Sum ? Sum(factor * factor) : None;
    }

    /// <summary>画像演算の変化。</summary>
    /// <param name="operation">演算の種類。</param>
    /// <returns>変化。</returns>
    internal static ValueMappingChange ForCalculation(ImageOperation operation) => operation switch
    {
        ImageOperation.Subtract or ImageOperation.AbsoluteDifference => Difference,
        _ => GainCorrection,
    };
}

/// <summary>
/// 32bit TIFF などを16bitへ写したときの、コードと元データの値の対応(画像情報欄へ添える説明)。
/// </summary>
/// <remarks>
/// 処理結果へ差し替えても対応が変わらなければ同じ説明を引き継ぎ、変わったら新しい対応で説明し直す
/// (以前は処理結果で説明を消し、1code が表す値が分からなくなっていた)。
/// </remarks>
/// <param name="Text">画像情報欄に添える説明。</param>
/// <param name="Offset">コード0が表す値。元の値へ換算できなければ NaN。</param>
/// <param name="PerCode">1code が表す値の刻み。元の値へ換算できなければ NaN。</param>
internal sealed record ValueMappingNote(string Text, double Offset, double PerCode)
{
    /// <summary>読み込んだファイルの値の対応。</summary>
    /// <param name="text">ローダが作った説明(等倍で読めたならnull)。</param>
    /// <param name="scaling">その係数(等倍で読めたならnull)。</param>
    /// <returns>対応。説明か係数がなければnull。</returns>
    internal static ValueMappingNote? FromFile(string? text, SampleScaling? scaling)
    {
        return text is null || scaling is null ? null : new ValueMappingNote(text, scaling.Offset, scaling.ValuePerCode);
    }

    /// <summary>元の値へ換算できるか(ゲイン補正で失われていないか)。</summary>
    internal bool IsConvertible => !double.IsNaN(PerCode);

    /// <summary>処理の結果の対応を求める。</summary>
    /// <param name="change">処理による変化。</param>
    /// <returns>処理結果の対応。変わらなければこのインスタンス。</returns>
    internal ValueMappingNote After(ValueMappingChange change)
    {
        if (!IsConvertible)
        {
            return this;
        }

        if (change.RequiresZeroOffset && Offset != 0)
        {
            return new ValueMappingNote(
                $"{change.Quantity}のコードは元の値へ換算できません(元の値の対応にオフセットがあるため)",
                double.NaN, double.NaN);
        }

        double offset = Offset * change.OffsetScale;
        double perCode = PerCode * change.PerCodeScale;
        if (offset == Offset && perCode == PerCode)
        {
            return this;
        }

        CultureInfo culture = CultureInfo.CurrentCulture;
        string scale = $"code×{perCode.ToString("G3", culture)}";
        return new ValueMappingNote(
            offset == 0
                ? $"{change.Quantity}≈{scale}"
                : $"{change.Quantity}≈{offset.ToString("G6", culture)}+{scale}",
            offset, perCode);
    }
}

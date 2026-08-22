using RawAnalyzer.Core;

namespace RawAnalyzer.App.Compare;

/// <summary>
/// 比較ペイン1枚ぶんの表示調整(ユーザーがスライダーで扱う単位のまま保持する)。
/// </summary>
/// <remarks>
/// 黒/白レベルは raw code で持ち、表示・比較には %FS(フルスケール比)も使う。
/// ビット深度の違うカメラ間では「黒 137」の意味が違うため、
/// 条件が揃っているかの比較は %FS・dB・γ など無次元の値で行う。
/// </remarks>
internal sealed record DisplaySettings
{
    /// <summary>ゲイン[dB](0 = ×1)。</summary>
    public double GainDb { get; init; }

    /// <summary>ガンマ(1.0 = リニア)。</summary>
    public double Gamma { get; init; } = 1.0;

    /// <summary>コントラスト(1.0 = 等倍)。</summary>
    public double Contrast { get; init; } = 1.0;

    /// <summary>黒レベル(raw code)。</summary>
    public double BlackCode { get; init; }

    /// <summary>白レベル(raw code)。</summary>
    public double WhiteCode { get; init; }

    /// <summary>線形ゲイン(×1基準)。</summary>
    public double GainLinear => Math.Pow(10, GainDb / 20.0);

    /// <summary>ビット深度に応じた既定値(全域表示・調整なし)を作る。</summary>
    /// <param name="bitDepth">対象画像のビット深度。</param>
    /// <returns>既定の表示調整。</returns>
    public static DisplaySettings CreateDefault(int bitDepth)
    {
        return new DisplaySettings { WhiteCode = (1 << bitDepth) - 1 };
    }

    /// <summary>
    /// 内部16bit表現用の表示パラメータへ変換する。
    /// </summary>
    /// <remarks>
    /// 白レベルは code の占める区間の上端((code&lt;&lt;shift) | (2^shift - 1))へ
    /// マップする。メインビューの変換(ApplyLevelCodes)と同じ規約で、
    /// 12bit の 4095 が 65535 になり最大codeが白に届く。
    /// </remarks>
    /// <param name="bitDepth">対象画像のビット深度。</param>
    /// <returns>表示パラメータ。</returns>
    public DisplayParameters ToDisplayParameters(int bitDepth)
    {
        int shift = 16 - bitDepth;
        ushort black = (ushort)Math.Clamp((long)BlackCode << shift, 0, 65535);
        ushort white = (ushort)Math.Clamp(
            ((long)WhiteCode << shift) | ((1L << shift) - 1), 0, 65535);
        return new DisplayParameters(black, white, GainLinear, Gamma, Contrast);
    }

    /// <summary>黒レベルのフルスケール比[%](ビット深度が違うペイン間の比較用)。</summary>
    /// <param name="bitDepth">対象画像のビット深度。</param>
    /// <returns>0〜100の百分率。</returns>
    public double BlackPercent(int bitDepth)
    {
        return BlackCode / ((1 << bitDepth) - 1) * 100.0;
    }

    /// <summary>白レベルのフルスケール比[%](ビット深度が違うペイン間の比較用)。</summary>
    /// <param name="bitDepth">対象画像のビット深度。</param>
    /// <returns>0〜100の百分率。</returns>
    public double WhitePercent(int bitDepth)
    {
        return WhiteCode / ((1 << bitDepth) - 1) * 100.0;
    }
}

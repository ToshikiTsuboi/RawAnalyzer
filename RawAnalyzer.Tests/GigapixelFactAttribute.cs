using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// ギガピクセルRawが用意されているときだけ実行するテスト。
/// </summary>
/// <remarks>
/// 本文の先頭で return すると、ファイルが無い環境では「成功」と数えられてしまい、
/// 実際には一度も検証されていないことが結果から読み取れない。
/// Skip を設定すると結果が「スキップ」として表示される。
/// 環境変数 RAWANALYZER_GIGAPIXEL_PATH に
/// tools/Generate-TestRaw.ps1 で生成したファイルのパスを設定すると実行される。
/// </remarks>
public sealed class GigapixelFactAttribute : FactAttribute
{
    /// <summary>属性を生成し、対象ファイルが無ければスキップ理由を設定する。</summary>
    public GigapixelFactAttribute()
    {
        if (GigapixelSmokeTests.GigapixelPath is null)
        {
            Skip = "RAWANALYZER_GIGAPIXEL_PATH が未設定、またはファイルが存在しません。";
        }
    }
}

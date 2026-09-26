using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 環境変数で指定されたサンプルファイルが存在するときだけ実行するテスト。
/// </summary>
/// <remarks>
/// 本文の先頭で return すると、ファイルが無い環境では「成功」と数えられてしまい、
/// 実際には一度も検証されていないことが結果から読み取れない。
/// Skip を設定すると結果が「スキップ」として表示される。
/// 対象ファイルの生成方法は各テストクラスのドキュメントコメントを参照。
/// </remarks>
public sealed class SampleFileFactAttribute : FactAttribute
{
    /// <summary>属性を生成し、対象ファイルが無ければスキップ理由を設定する。</summary>
    /// <param name="environmentVariable">サンプルファイルのパスを保持する環境変数名。</param>
    public SampleFileFactAttribute(string environmentVariable)
    {
        if (GetPath(environmentVariable) is null)
        {
            Skip = $"{environmentVariable} が未設定、またはファイルが存在しません。";
        }
    }

    /// <summary>環境変数が指す既存ファイルのパスを返す。未設定またはファイルが無ければ null。</summary>
    /// <param name="environmentVariable">サンプルファイルのパスを保持する環境変数名。</param>
    /// <returns>存在するファイルのパス、または null。</returns>
    public static string? GetPath(string environmentVariable)
    {
        string? path = Environment.GetEnvironmentVariable(environmentVariable);
        return !string.IsNullOrEmpty(path) && File.Exists(path) ? path : null;
    }
}

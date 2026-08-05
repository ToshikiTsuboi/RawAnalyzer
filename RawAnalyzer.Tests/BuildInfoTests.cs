using RawAnalyzer.App.Services;
using Xunit;

namespace RawAnalyzer.Tests;

public class BuildInfoTests
{
    [Fact]
    public void FormatVersionLabel_OnTag_ShowsVersionOnly()
    {
        // リリースワークフローは v1.2.0 のタグから Version=1.2.0 を渡す
        Assert.Equal("1.2.0", BuildInfo.FormatVersionLabel("1.2.0", "v1.2.0", dirty: false));
    }

    [Fact]
    public void FormatVersionLabel_AheadOfTag_UsesDescribeAndMarksDevBuild()
    {
        // タグから進んだビルドでは Version は csproj の既定値のままで実体を表さない
        Assert.Equal(
            "v1.2.0-3-gabc1234 (開発ビルド)",
            BuildInfo.FormatVersionLabel("1.0.0", "v1.2.0-3-gabc1234", dirty: false));
    }

    [Fact]
    public void FormatVersionLabel_Dirty_MentionsUncommittedChanges()
    {
        Assert.Equal(
            "v1.2.0-dirty (開発ビルド・未コミットの変更あり)",
            BuildInfo.FormatVersionLabel("1.2.0", "v1.2.0-dirty", dirty: true));
    }

    [Fact]
    public void FormatVersionLabel_TagWithUncommittedChanges_IsNotTreatedAsRelease()
    {
        // ハッシュはタグと同じでも作業ツリーが汚れていれば配布物とは別物
        Assert.Equal(
            "v1.2.0 (開発ビルド・未コミットの変更あり)",
            BuildInfo.FormatVersionLabel("1.2.0", "v1.2.0", dirty: true));
    }

    [Fact]
    public void FormatVersionLabel_NoGit_FallsBackToAssemblyVersion()
    {
        Assert.Equal("1.0.0 (開発ビルド)", BuildInfo.FormatVersionLabel("1.0.0", "", dirty: false));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("abc1234", "abc1234")]
    [InlineData("0123456789abcdef0123456789abcdef01234567", "0123456789ab")]
    public void ShortenCommit_TakesTwelveCharacters(string commit, string expected)
    {
        Assert.Equal(expected, BuildInfo.ShortenCommit(commit));
    }

    [Fact]
    public void EmbeddedMetadata_IsPresentInThisBuild()
    {
        // csproj の EmbedBuildInfo が動いていることの確認。
        // このリポジトリ上のビルドなら git 情報が埋まっているはず
        Assert.NotNull(BuildInfo.BuildTime);
        Assert.NotEmpty(BuildInfo.Configuration);
        Assert.Equal("https://github.com/ToshikiTsuboi/RawAnalyzer", BuildInfo.RepositoryUrl);
        Assert.Equal(40, BuildInfo.Commit.Length);
        Assert.Equal(12, BuildInfo.ShortCommit.Length);
    }

    [Fact]
    public void ToReportText_ContainsVersionAndCommit()
    {
        string text = BuildInfo.ToReportText();

        Assert.Contains("RawAnalyzer", text);
        Assert.Contains(BuildInfo.VersionLabel, text);
        Assert.Contains(BuildInfo.Commit, text);
        Assert.Contains("OS:", text);
    }
}

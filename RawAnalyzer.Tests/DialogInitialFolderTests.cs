using System.Diagnostics;
using RawAnalyzer.App.Services;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 「参照…」の選択ダイアログの初期フォルダを UI スレッドの外で確かめる(残課題 2026-10-02 I1)。
/// </summary>
/// <remarks>
/// 以前は「参照…」のたびに UI スレッドで Directory.Exists を呼び、初期フォルダ(表示中の画像のフォルダ・出力先)が
/// 切断した NAS 上だと SMB のタイムアウトまでダイアログが固まった。
/// </remarks>
public class DialogInitialFolderTests
{
    [Fact]
    public async Task ExistingFolder_IsReturned()
    {
        string folder = Path.GetTempPath();

        Assert.Equal(folder, await DialogInitialFolder.ConfirmAsync(folder, DialogInitialFolder.Timeout));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task NoCandidate_IsNotProbed(string? folder)
    {
        bool probed = false;

        Assert.Null(await DialogInitialFolder.ConfirmAsync(
            folder, DialogInitialFolder.Timeout, _ => probed = true));
        Assert.False(probed);
    }

    [Fact]
    public async Task MissingFolder_IsNotUsed()
    {
        string missing = Path.Combine(Path.GetTempPath(), "RawAnalyzerTests", Guid.NewGuid().ToString("N"));

        Assert.Null(await DialogInitialFolder.ConfirmAsync(missing, DialogInitialFolder.Timeout));
    }

    [Fact]
    public async Task UnreachableFolder_IsGivenUpAfterTheTimeout_WithoutWaitingForTheProbe()
    {
        // 切断した NAS の確認はタイムアウトまで戻らない。確認は呼び出し側で同期に走らせず、待つのは上限まで
        // (同期に走らせると、確認が戻る 10 秒後まで返らず、結果も実在扱いになる)
        using var release = new ManualResetEventSlim();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            string? result = await DialogInitialFolder.ConfirmAsync(
                @"\\nas\share\images", TimeSpan.FromMilliseconds(100), _ =>
                {
                    release.Wait(TimeSpan.FromSeconds(10));
                    return true;
                });

            Assert.Null(result);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"{stopwatch.Elapsed}");
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task FailingProbe_IsTreatedAsNotConfirmed()
    {
        Assert.Null(await DialogInitialFolder.ConfirmAsync(
            @"\\nas\share\images", DialogInitialFolder.Timeout, _ => throw new IOException("ネットワーク名が見つかりません。")));
    }
}

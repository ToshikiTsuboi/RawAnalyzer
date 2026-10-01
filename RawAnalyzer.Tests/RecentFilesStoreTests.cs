using System.Text.Json;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 最近使ったファイルの履歴(recent.json)。複数起動したインスタンスが互いの記録を消さないこと。
/// </summary>
public class RecentFilesStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "RawAnalyzerTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Add_WhileAnotherInstanceIsSaving_WaitsAndKeepsItsEntry()
    {
        // 他のインスタンスと排他せずに読み直して書いていたので、別のインスタンスが保存している最中に足すと、
        // 保存前の内容に足して書き、その保存に上書きされて(または書きかけを読んで空から書き直して)記録が消えた
        Directory.CreateDirectory(_directory);
        string file = Path.Combine(_directory, "recent.json");
        using var holding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task other = Task.Factory.StartNew(() =>
        {
            // ミューテックスは取得したスレッドで解放するので、待たずに同じスレッドで書く
            using (InterProcessFileLock.Acquire(file))
            {
                holding.Set();
                release.Wait();
                File.WriteAllText(file, JsonSerializer.Serialize(new List<string> { @"D:\cap\other.raw" }));
            }
        }, TaskCreationOptions.LongRunning);
        holding.Wait();

        Task add = Task.Run(() => new RecentFilesStore(_directory).Add(@"D:\cap\new.raw"));
        await Task.WhenAny(add, Task.Delay(200));
        release.Set();
        await Task.WhenAll(other, add);

        Assert.Equal([@"D:\cap\new.raw", @"D:\cap\other.raw"], new RecentFilesStore(_directory).Load());
    }
}

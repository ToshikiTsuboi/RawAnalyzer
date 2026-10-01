using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// セッションの保存(session.json)。複数起動したインスタンスが、起動時に読んだ状態で互いの保存を巻き戻さないこと。
/// </summary>
/// <remarks>
/// 2つの SessionStore を同じ一時フォルダで作り、2つのインスタンスを模す(MainWindow は実 %AppData% を使うので作らない)。
/// </remarks>
public class SessionStoreTests : IDisposable
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

    /// <summary>インスタンスの起動(MainWindow のコンストラクタと同じく読み込む)。</summary>
    private SessionStore Start()
    {
        var store = new SessionStore(_directory);
        store.Load();
        return store;
    }

    private static RawFormat Fmt(int width)
    {
        return new RawFormat { Width = width, Height = 4 };
    }

    private static string Key(string path)
    {
        return SessionStore.NormalizeKey(path);
    }

    /// <summary>MainWindow.RememberFileFormat と同じ記憶のしかた。</summary>
    private static void Remember(SessionStore store, string path, RawFormat format)
    {
        string key = Key(path);
        store.Update(session => SessionStore.TouchFileFormat(session, key, format));
    }

    [Fact]
    public void TwoInstances_FileFormatsRememberedInEach_AreKept()
    {
        // B が起動した後に A で記憶したフォーマットは、B の記憶の保存で消えない
        SessionStore a = Start();
        SessionStore b = Start();
        Remember(a, @"D:\cap\a.raw", Fmt(8));
        Remember(b, @"D:\cap\b.raw", Fmt(16));

        Dictionary<string, RawFormat> saved = Start().Current.FileFormats;
        Assert.Equal(Fmt(8), saved[Key(@"D:\cap\a.raw")]);
        Assert.Equal(Fmt(16), saved[Key(@"D:\cap\b.raw")]);
        Assert.Equal(Fmt(16), a.Current.FileFormats[Key(@"D:\cap\b.raw")]); // 動き続けている A も B の記憶で開く
    }

    [Fact]
    public void TwoInstances_CorrectionInOne_IsUsedAndNotRevertedByOther()
    {
        // A と B が同じファイルの記憶(誤った形式)を持つ。A が F2 で直した後は、B もそのファイルを直した形式で開き、
        // B の保存(別のファイルの記憶・ウィンドウを閉じる)でも直した形式が残る
        SessionStore a = Start();
        Remember(a, @"D:\cap\x.raw", Fmt(8));
        SessionStore b = Start();
        Remember(a, @"D:\cap\x.raw", Fmt(16));

        Assert.Equal(Fmt(16), b.Current.FileFormats[Key(@"D:\cap\x.raw")]);
        Remember(b, @"D:\cap\y.raw", Fmt(32));
        b.Update(session => session.WindowLeft = 20);

        Assert.Equal(Fmt(16), Start().Current.FileFormats[Key(@"D:\cap\x.raw")]);
    }

    [Fact]
    public void TwoInstances_ClosingKeepsTheOthersFormatsAndFolder_LastClosedPlacementWins()
    {
        SessionStore a = Start();
        SessionStore b = Start();
        Remember(b, @"D:\cap\b.raw", Fmt(16));
        b.Update(session => session.LastFolder = @"D:\cap");

        // A を閉じる(MainWindow.SaveWindowPlacement と同じく、ウィンドウ配置・パネル・絞り込みだけを当てる)
        a.Update(session =>
        {
            session.WindowLeft = 10;
            session.WindowMaximized = true;
        });
        SessionState afterA = Start().Current;
        Assert.Equal(Fmt(16), Assert.Single(afterA.FileFormats).Value);
        Assert.Equal(@"D:\cap", afterA.LastFolder);
        Assert.Equal(10, afterA.WindowLeft);

        // ウィンドウ配置は後から閉じた B のものが残る
        b.Update(session =>
        {
            session.WindowLeft = 20;
            session.WindowMaximized = false;
        });
        SessionState afterB = Start().Current;
        Assert.Equal(20, afterB.WindowLeft);
        Assert.False(afterB.WindowMaximized);
        Assert.Single(afterB.FileFormats);
        Assert.Equal(@"D:\cap", afterB.LastFolder);
    }

    [Fact]
    public void SaveFailure_IsKeptAndSavedWithTheNextChange()
    {
        // 保存先がフォルダになっていて置き換えられない。失敗は知らせず(従来どおり)、手元の状態には当てておき、
        // 次の変更のときに最新の状態へ当て直して一緒に保存する
        string file = Path.Combine(_directory, "session.json");
        Directory.CreateDirectory(file);
        SessionStore store = Start();
        Remember(store, @"D:\cap\a.raw", Fmt(8));
        Assert.Single(store.Current.FileFormats);

        Directory.Delete(file);
        Remember(store, @"D:\cap\b.raw", Fmt(16));

        Assert.Equal(2, Start().Current.FileFormats.Count);
    }

    [Fact]
    public void ValueThatCannotBeWritten_IsIgnoredWithoutBlockingLaterChanges()
    {
        // JSON に書けない値(非有限の数)は従来どおり黙って保存しない。当て直す変更に残すと、以後の保存がすべて失敗する
        SessionStore store = Start();
        store.Update(session => session.WindowWidth = double.NaN);
        Remember(store, @"D:\cap\a.raw", Fmt(8));

        SessionState saved = Start().Current;
        Assert.Single(saved.FileFormats);
        Assert.Null(saved.WindowWidth);
    }

    [Fact]
    public void CorruptFile_StartsFromDefaultsAndIsReplacedByTheNextChange()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "session.json"), "{ broken");
        SessionStore store = Start();
        Assert.Empty(store.Current.FileFormats);
        Assert.True(store.Current.LeftPanelVisible);

        Remember(store, @"D:\cap\a.raw", Fmt(8));

        Assert.Single(Start().Current.FileFormats);
    }
}

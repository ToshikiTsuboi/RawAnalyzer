using System.Windows.Controls;
using RawAnalyzer.App.Services;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 左パネルのフォルダツリー(子フォルダの列挙、表示中のフォルダまでの展開と選択)。
/// </summary>
[Collection("WPF UI")]
public class FolderTreeNavigatorTests
{
    private static string[] ChildPaths(TreeViewItem item) =>
        item.Items.OfType<TreeViewItem>().Select(i => (string)i.Tag).ToArray();

    private static FolderTreeEntry Entry(string fullPath) => new(Path.GetFileName(fullPath), fullPath);

    [Fact]
    public Task Populate_EnumeratesOffTheUiThread() => WpfTestHost.Run(async () =>
    {
        // NAS や切断中のドライブでは列挙が数秒〜SMB のタイムアウトまでかかる。以前は展開・同期・再読込で
        // UI スレッドのまま列挙し、その間ウィンドウが固まっていた(性能ルール5)
        int uiThread = Environment.CurrentManagedThreadId;
        int? enumerationThread = null;
        using var gate = new ManualResetEventSlim();
        var navigator = new FolderTreeNavigator(path =>
        {
            enumerationThread = Environment.CurrentManagedThreadId;
            gate.Wait(TimeSpan.FromSeconds(10));
            return [Entry(@"Z:\proj\a10"), Entry(@"Z:\proj\a2")];
        });
        TreeViewItem item = FolderTreeNavigator.CreateItem("📁 proj", @"Z:\proj");

        Task populate = navigator.PopulateAsync(item);

        // 列挙の完了を待たずに戻り、列挙中であることを示す
        Assert.False(populate.IsCompleted);
        Assert.Equal(FolderTreeNavigator.LoadingText, Assert.Single(item.Items.Cast<object>()).ToString());

        gate.Set();
        await populate;

        Assert.NotNull(enumerationThread);
        Assert.NotEqual(uiThread, enumerationThread);

        // 子は列挙の順に並び、それぞれ展開できる(ダミーの子を持つ)
        Assert.Equal([@"Z:\proj\a10", @"Z:\proj\a2"], ChildPaths(item));
        Assert.All(item.Items.OfType<TreeViewItem>(), child =>
            Assert.Equal(FolderTreeNavigator.Placeholder, Assert.Single(child.Items.Cast<object>())));
    });

    [Fact]
    public Task Populate_WhileEnumerating_DoesNotEnumerateTwice() => WpfTestHost.Run(async () =>
    {
        // 展開(Expanded)と表示中のフォルダへの同期が同じ項目を続けて求めても、列挙を重ねず子も二重にしない
        int calls = 0;
        using var gate = new ManualResetEventSlim();
        var navigator = new FolderTreeNavigator(path =>
        {
            Interlocked.Increment(ref calls);
            gate.Wait(TimeSpan.FromSeconds(10));
            return [Entry(@"Z:\proj\run03")];
        });
        TreeViewItem item = FolderTreeNavigator.CreateItem("📁 proj", @"Z:\proj");

        Task first = navigator.PopulateAsync(item);
        Task second = navigator.PopulateAsync(item);
        gate.Set();
        await Task.WhenAll(first, second);

        Assert.Equal(1, calls);
        Assert.Equal([@"Z:\proj\run03"], ChildPaths(item));

        // 列挙済みの項目は列挙し直さない
        await navigator.PopulateAsync(item);
        Assert.Equal(1, calls);
    });

    [Fact]
    public Task Refresh_WhileEnumerating_KeepsOnlyTheNewResult() => WpfTestHost.Run(async () =>
    {
        // 列挙中に右クリックの「再読込」で作り直したら、前の列挙の結果は後から終わっても足さない
        using var firstGate = new ManualResetEventSlim();
        int calls = 0;
        var navigator = new FolderTreeNavigator(path =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstGate.Wait(TimeSpan.FromSeconds(10));
                return [Entry(@"Z:\proj\old")];
            }

            return [Entry(@"Z:\proj\new")];
        });
        TreeViewItem item = FolderTreeNavigator.CreateItem("📁 proj", @"Z:\proj");
        item.IsExpanded = true;

        Task first = navigator.PopulateAsync(item);
        await navigator.RefreshAsync(item);
        firstGate.Set();
        await first;

        Assert.Equal(2, calls);
        Assert.Equal([@"Z:\proj\new"], ChildPaths(item));
    });

    [Fact]
    public Task Refresh_OfCollapsedItem_EnumeratesOnNextExpand() => WpfTestHost.Run(async () =>
    {
        int calls = 0;
        var navigator = new FolderTreeNavigator(path =>
        {
            Interlocked.Increment(ref calls);
            return [Entry(@"Z:\proj\run03")];
        });
        TreeViewItem item = FolderTreeNavigator.CreateItem("📁 proj", @"Z:\proj");
        await navigator.PopulateAsync(item);

        await navigator.RefreshAsync(item);

        Assert.Equal(1, calls);
        Assert.Equal(FolderTreeNavigator.Placeholder, Assert.Single(item.Items.Cast<object>()));
    });

    [Fact]
    public Task FailedEnumeration_CanBeRetried() => WpfTestHost.Run(async () =>
    {
        // 想定外の例外で列挙できなかったら、列挙中の表示を残さず展開し直せる状態へ戻す
        int calls = 0;
        var navigator = new FolderTreeNavigator(path =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new InvalidOperationException("boom");
            }

            return [Entry(@"Z:\proj\run03")];
        });
        TreeViewItem item = FolderTreeNavigator.CreateItem("📁 proj", @"Z:\proj");

        await Assert.ThrowsAsync<InvalidOperationException>(() => navigator.PopulateAsync(item));
        Assert.Equal(FolderTreeNavigator.Placeholder, Assert.Single(item.Items.Cast<object>()));

        await navigator.PopulateAsync(item);
        Assert.Equal([@"Z:\proj\run03"], ChildPaths(item));
    });

    [Fact]
    public Task SyncToFolder_ExpandsAncestorsOffTheUiThreadAndSelects() => WpfTestHost.Run(async () =>
    {
        // ファイルを開いたフォルダまでツリーを展開して選択する。祖先の各階層の初回の列挙も UI スレッドの外で行う
        int uiThread = Environment.CurrentManagedThreadId;
        var enumeratedOnUi = new List<string>();
        var children = new Dictionary<string, FolderTreeEntry[]>(StringComparer.OrdinalIgnoreCase)
        {
            [@"Z:\"] = [Entry(@"Z:\proj")],
            [@"Z:\proj"] = [Entry(@"Z:\proj\2026"), Entry(@"Z:\proj\old")],
            [@"Z:\proj\2026"] = [Entry(@"Z:\proj\2026\run03")],
            [@"Z:\proj\2026\run03"] = [],
        };
        var navigator = new FolderTreeNavigator(path =>
        {
            if (Environment.CurrentManagedThreadId == uiThread)
            {
                lock (enumeratedOnUi)
                {
                    enumeratedOnUi.Add(path);
                }
            }

            return children[path];
        });
        var tree = new TreeView();
        tree.Items.Add(FolderTreeNavigator.CreateItem("💽 Z:", @"Z:\"));
        var selectionWhileSyncing = new List<bool>();
        tree.SelectedItemChanged += (_, _) => selectionWhileSyncing.Add(navigator.IsSyncingSelection);

        await navigator.SyncToFolderAsync(tree, @"Z:\proj\2026\run03");

        Assert.Empty(enumeratedOnUi);
        TreeViewItem selected = Assert.IsType<TreeViewItem>(tree.SelectedItem);
        Assert.Equal(@"Z:\proj\2026\run03", selected.Tag);

        // プログラムからの選択は、選択の変更でフォルダを開き直さないよう同期中として知らせる
        Assert.Equal([true], selectionWhileSyncing);
        Assert.False(navigator.IsSyncingSelection);
    });

    [Fact]
    public Task SyncToFolder_Superseded_DoesNotSelectTheOldFolder() => WpfTestHost.Run(async () =>
    {
        // 遅い階層の列挙中に別のフォルダを開いたら、前の同期は後から終わっても選択を奪わない
        using var slowEntered = new ManualResetEventSlim();
        using var slowGate = new ManualResetEventSlim();
        var navigator = new FolderTreeNavigator(path =>
        {
            if (string.Equals(path, @"Z:\slow", StringComparison.OrdinalIgnoreCase))
            {
                slowEntered.Set();
                slowGate.Wait(TimeSpan.FromSeconds(10));
                return [Entry(@"Z:\slow\deep")];
            }

            return path.Equals(@"Z:\", StringComparison.OrdinalIgnoreCase)
                ? [Entry(@"Z:\slow"), Entry(@"Z:\fast")]
                : [];
        });
        var tree = new TreeView();
        tree.Items.Add(FolderTreeNavigator.CreateItem("💽 Z:", @"Z:\"));

        Task slow = navigator.SyncToFolderAsync(tree, @"Z:\slow\deep");

        // 前の同期が遅い階層(Z:\slow)の列挙に入ってから、別のフォルダへ同期する
        await Task.Run(() => slowEntered.Wait(TimeSpan.FromSeconds(10)));
        await navigator.SyncToFolderAsync(tree, @"Z:\fast");
        slowGate.Set();
        await slow;

        TreeViewItem selected = Assert.IsType<TreeViewItem>(tree.SelectedItem);
        Assert.Equal(@"Z:\fast", selected.Tag);
    });

    [Fact]
    public void EnumerateChildFolders_SkipsHiddenAndSortsNaturally()
    {
        string root = Path.Combine(Path.GetTempPath(), "RawAnalyzerTests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "run10"));
            Directory.CreateDirectory(Path.Combine(root, "run2"));
            DirectoryInfo hidden = Directory.CreateDirectory(Path.Combine(root, "hidden"));
            hidden.Attributes |= FileAttributes.Hidden;
            File.WriteAllBytes(Path.Combine(root, "file.raw"), [0]);

            IReadOnlyList<FolderTreeEntry> entries = FolderTreeNavigator.EnumerateChildFolders(root);

            Assert.Equal(["run2", "run10"], entries.Select(e => e.Name));
            Assert.Equal(Path.Combine(root, "run2"), entries[0].FullPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EnumerateChildFolders_MissingFolder_IsEmpty()
    {
        // 消えた・切断されたフォルダは子なしとして扱う(以前と同じく例外にしない)
        string missing = Path.Combine(Path.GetTempPath(), "RawAnalyzerTests", Guid.NewGuid().ToString("N"));

        Assert.Empty(FolderTreeNavigator.EnumerateChildFolders(missing));
    }
}

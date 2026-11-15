using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using RawAnalyzer.App.Controls;
using Xunit.Abstractions;
using System.Windows.Controls;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.Themes;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 左パネルのフォルダツリー(子フォルダの列挙、表示中のフォルダまでの展開と選択)。
/// </summary>
[Collection("WPF UI")]
public class FolderTreeNavigatorTests(ITestOutputHelper output)
{
    private static string[] ChildPaths(FolderTreeNode node) =>
        node.Children.OfType<FolderTreeNode>().Select(child => child.FullPath).ToArray();

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
        FolderTreeNode item = new("proj", @"Z:\proj");

        Task populate = navigator.PopulateAsync(item);

        // 列挙の完了を待たずに戻り、列挙中であることを示す
        Assert.False(populate.IsCompleted);
        Assert.Equal(FolderTreeNode.LoadingText, Assert.Single(item.Children).ToString());

        gate.Set();
        await populate;

        Assert.NotNull(enumerationThread);
        Assert.NotEqual(uiThread, enumerationThread);

        // 子は列挙の順に並び、それぞれ展開できる(ダミーの子を持つ)
        Assert.Equal([@"Z:\proj\a10", @"Z:\proj\a2"], ChildPaths(item));
        Assert.All(item.Children.OfType<FolderTreeNode>(), child =>
        {
            Assert.Equal(FolderTreeNode.Placeholder, Assert.Single(child.Children));
            Assert.Equal(Path.GetFileName(child.FullPath), child.Name);
            Assert.Equal(IconGlyphs.Folder, child.Glyph);
            Assert.Null(child.Container);
        });
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
        FolderTreeNode item = new("proj", @"Z:\proj");

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
        using var firstStarted = new ManualResetEventSlim();
        int calls = 0;
        var navigator = new FolderTreeNavigator(path =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstStarted.Set();
                firstGate.Wait(TimeSpan.FromSeconds(10));
                return [Entry(@"Z:\proj\old")];
            }

            return [Entry(@"Z:\proj\new")];
        });
        FolderTreeNode item = new("proj", @"Z:\proj");
        item.IsExpanded = true;

        Task first = navigator.PopulateAsync(item);

        // 1回目の列挙が走り始めてから再読込する。スレッドプールの開始順は保証されないので、待たないと再読込の
        // 列挙が先に走って「1回目」として止まり、古い結果と新しい結果の役が入れ替わる(遅い環境でまれに起きた)
        Assert.True(await Task.Run(() => firstStarted.Wait(TimeSpan.FromSeconds(10))));
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
        FolderTreeNode item = new("proj", @"Z:\proj");
        await navigator.PopulateAsync(item);

        await navigator.RefreshAsync(item);

        Assert.Equal(1, calls);
        Assert.Equal(FolderTreeNode.Placeholder, Assert.Single(item.Children));
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
        FolderTreeNode item = new("proj", @"Z:\proj");

        await Assert.ThrowsAsync<InvalidOperationException>(() => navigator.PopulateAsync(item));
        Assert.Equal(FolderTreeNode.Placeholder, Assert.Single(item.Children));

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
        var root = new FolderTreeNode("Z:", @"Z:\");
        var tree = CreateTree(navigator, root);
        var window = ShowTree(tree);
        try
        {
            var selectionWhileSyncing = new List<bool>();
            tree.SelectedItemChanged += (_, _) => selectionWhileSyncing.Add(navigator.IsSyncingSelection);

            await navigator.SyncToFolderAsync(tree, @"Z:\proj\2026\run03");

            Assert.Empty(enumeratedOnUi);
            FolderTreeNode selected = Assert.IsType<FolderTreeNode>(tree.SelectedItem);
            Assert.Equal(@"Z:\proj\2026\run03", selected.FullPath);
            Assert.Equal("run03", AutomationProperties.GetName(Assert.IsAssignableFrom<TreeViewItem>(selected.Container)));

            // プログラムからの選択は、選択の変更でフォルダを開き直さないよう同期中として知らせる
            Assert.Equal([true], selectionWhileSyncing);
            Assert.False(navigator.IsSyncingSelection);
        }
        finally
        {
            window.Close();
        }
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
        var root = new FolderTreeNode("Z:", @"Z:\");
        var tree = CreateTree(navigator, root);
        var window = ShowTree(tree);
        try
        {
            Task slow = navigator.SyncToFolderAsync(tree, @"Z:\slow\deep");

            // 前の同期が遅い階層(Z:\slow)の列挙に入ってから、別のフォルダへ同期する
            await Task.Run(() => slowEntered.Wait(TimeSpan.FromSeconds(10)));
            await navigator.SyncToFolderAsync(tree, @"Z:\fast");
            slowGate.Set();
            await slow;

            FolderTreeNode selected = Assert.IsType<FolderTreeNode>(tree.SelectedItem);
            Assert.Equal(@"Z:\fast", selected.FullPath);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task SyncToFolder_UserSelectionWhileWaiting_IsNotTakenBack() => WpfTestHost.Run(async () =>
    {
        // レビュー 2026-10-03 R3。表示中のフォルダ A への同期が A の列挙を待つ間に、利用者がツリーで B をクリックした。
        // 以前は同期の世代が別の同期でしか進まず、後から終わった同期が選択を A へ戻した。B を開く側
        // (MainWindow.OnFolderTreeSelected)は B の実在を確かめた後に B が選択されたままかを照合するので、B へ移れなかった。
        // 利用者が選んだ時点で、待っている同期はやめる
        using var entered = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        var navigator = new FolderTreeNavigator(path =>
        {
            if (string.Equals(path, @"Z:\A", StringComparison.OrdinalIgnoreCase))
            {
                entered.Set();
                gate.Wait(TimeSpan.FromSeconds(10));
                return [Entry(@"Z:\A\run01")];
            }

            return path.Equals(@"Z:\", StringComparison.OrdinalIgnoreCase)
                ? [Entry(@"Z:\A"), Entry(@"Z:\B")]
                : [];
        });
        var root = new FolderTreeNode("Z:", @"Z:\");
        var tree = CreateTree(navigator, root);
        var window = ShowTree(tree);
        try
        {
            root.IsExpanded = true;
            await navigator.PopulateAsync(root);
            FolderTreeNode b = root.Children.OfType<FolderTreeNode>().Single(i => i.FullPath == @"Z:\B");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var userSelections = new List<object>();
            tree.SelectedItemChanged += (_, e) =>
            {
                if (!navigator.IsSyncingSelection)
                {
                    userSelections.Add(e.NewValue);
                }
            };

            Task syncA = navigator.SyncToFolderAsync(tree, @"Z:\A");
            await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.IsAssignableFrom<TreeViewItem>(b.Container).IsSelected = true; // 利用者のクリック
            gate.Set();
            await syncA;

            Assert.Same(b, tree.SelectedItem);
            Assert.Equal([b], userSelections);

            // 次に表示中のフォルダが替わったら(B を開いた)、同期はこれまでどおり選択する
            await navigator.SyncToFolderAsync(tree, @"Z:\A");
            Assert.Equal(@"Z:\A", Assert.IsType<FolderTreeNode>(tree.SelectedItem).FullPath);
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Expand_10000Children_KeepsTheDispatcherResponsive(bool syncToLastChild) => WpfTestHost.Run(async () =>
    {
        const int count = 10_000;
        var navigator = new FolderTreeNavigator(path => path == @"Z:\"
            ? Enumerable.Range(0, count).Select(i => Entry($@"Z:\run{i:D5}")).ToArray() : []);
        var root = new FolderTreeNode("Z:", @"Z:\", IconGlyphs.Drive);
        var tree = CreateTree(navigator, root);
        var window = ShowTree(tree);
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
        long previous = 0;
        TimeSpan longest = TimeSpan.Zero;
        void Sample()
        {
            long now = Stopwatch.GetTimestamp();
            TimeSpan elapsed = Stopwatch.GetElapsedTime(previous, now);
            if (elapsed > longest)
            {
                longest = elapsed;
            }

            previous = now;
        }

        timer.Tick += (_, _) => Sample();
        try
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            previous = Stopwatch.GetTimestamp();
            timer.Start();
            if (syncToLastChild)
            {
                await navigator.SyncToFolderAsync(tree, @"Z:\run09999");
            }
            else
            {
                Assert.IsAssignableFrom<TreeViewItem>(root.Container).IsExpanded = true;
                await navigator.PopulateAsync(root);
            }

            // Children の通知だけでなく、最初のレイアウトと入力が処理されるまでを測る。
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Sample();
            timer.Stop();
            output.WriteLine($"10,000 children, {(syncToLastChild ? "sync" : "expand")}: max input gap {longest.TotalMilliseconds:F1} ms");
            Assert.InRange(longest.TotalMilliseconds, 0, 200);
            Assert.Equal(count, root.Children.Count);
            Assert.InRange(root.Children.OfType<FolderTreeNode>().Count(node => node.Container is not null), 1, 100);
            if (syncToLastChild)
            {
                var selected = Assert.IsType<FolderTreeNode>(tree.SelectedItem);
                Assert.Equal(@"Z:\run09999", selected.FullPath);
                var selectedContainer = Assert.IsAssignableFrom<TreeViewItem>(selected.Container);
                var header = (FrameworkElement)selectedContainer.Template.FindName("PART_Header", selectedContainer);
                var scroll = (ScrollViewer)tree.Template.FindName("_tv_scrollviewer_", tree);
                Rect headerBounds = header.TransformToAncestor(scroll).TransformBounds(new Rect(header.RenderSize));
                Assert.InRange(headerBounds.Bottom, 0, scroll.ViewportHeight + 1);
                WpfTestHost.CaptureIfRequested(tree, "folder-tree-sync-10000");

                // 選択済みの末尾が画面外に出ても、ツリーに存在しない場所へ移ると選択を外せる。
                scroll.ScrollToTop();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                await navigator.SyncToFolderAsync(tree, @"Q:\missing");
                Assert.Null(tree.SelectedItem);
            }
            else
            {
                WpfTestHost.CaptureIfRequested(tree, "folder-tree-expand-10000");
            }
        }
        finally
        {
            timer.Stop();
            window.Close();
        }
    });

    private static FolderTreeView CreateTree(FolderTreeNavigator navigator, FolderTreeNode root)
    {
        var tree = new FolderTreeView
        {
            Style = (Style)Application.Current.FindResource("FolderTree"),
            ItemsSource = new[] { root },
        };
        tree.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is TreeViewItem { DataContext: FolderTreeNode node })
            {
                _ = navigator.PopulateAsync(node);
            }
        }));
        return tree;
    }

    private static Window ShowTree(FolderTreeView tree)
    {
        var window = new Window
        {
            Content = tree, Width = 380, Height = 420, ShowActivated = false,
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("PanelBrush"),
        };
        window.Show();
        return window;
    }

    [Theory]
    [InlineData(@"\\server\share\run03")] // UNC パス(ツリーはドライブだけ)
    [InlineData(@"Q:\data")] // 起動後に接続したドライブ(ツリーは起動時のドライブだけ)
    [InlineData(@"Z:\.hidden\run03")] // 隠し属性のフォルダの下(ツリーに出さない)
    public Task SyncToFolder_NotInTree_ClearsThePreviousSelection(string folder) => WpfTestHost.Run(async () =>
    {
        // 表示中のフォルダがツリーにないときは、前のフォルダの選択を外す。以前は前のフォルダ X が選択された
        // ままになり、TreeView は選択済みの項目をクリックしても選択の変更を出さないので、X をクリックしても
        // 一覧が X に戻らなかった(別の項目を一度選ぶ必要があった)
        var navigator = new FolderTreeNavigator(path =>
            path.Equals(@"Z:\", StringComparison.OrdinalIgnoreCase) ? [Entry(@"Z:\x")] : []);
        var root = new FolderTreeNode("Z:", @"Z:\");
        var tree = CreateTree(navigator, root);
        var window = ShowTree(tree);
        try
        {
            await navigator.SyncToFolderAsync(tree, @"Z:\x");
            FolderTreeNode x = Assert.IsType<FolderTreeNode>(tree.SelectedItem);
            TreeViewItem xContainer = Assert.IsAssignableFrom<TreeViewItem>(x.Container);
            var changes = new List<(object? NewValue, bool Syncing)>();
            tree.SelectedItemChanged += (_, e) => changes.Add((e.NewValue, navigator.IsSyncingSelection));

            await navigator.SyncToFolderAsync(tree, folder);

            Assert.Null(tree.SelectedItem);
            Assert.False(xContainer.IsSelected);

            // 選択を外したのは同期(フォルダを開き直さない)。その後で X をクリックすると、X を開く選択の変更が届く
            xContainer.IsSelected = true;
            Assert.Equal(new (object?, bool)[] { (null, true), (x, false) }, changes);
        }
        finally
        {
            window.Close();
        }
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

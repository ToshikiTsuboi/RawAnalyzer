using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using RawAnalyzer.App.Mvvm;
using RawAnalyzer.App.Themes;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>子フォルダの列挙結果。UI スレッドに依存しない。</summary>
internal readonly record struct FolderTreeEntry(string Name, string FullPath);

/// <summary>フォルダツリーのデータ。WPF は表示範囲の項目だけにコンテナーを作る。</summary>
internal sealed class FolderTreeNode(string name, string fullPath, string glyph = IconGlyphs.Folder) : ObservableObject
{
    internal const string Placeholder = "…";
    internal const string LoadingText = "読み込み中…";

    private string _name = name;
    private IReadOnlyList<object> _children = [Placeholder];
    private bool _isExpanded;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string FullPath { get; } = fullPath;
    public string Glyph { get; } = glyph;

    public IReadOnlyList<object> Children
    {
        get => _children;
        set => SetProperty(ref _children, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    internal Task? PendingLoad { get; set; }
    internal TreeViewItem? Container { get; set; }
}

/// <summary>子フォルダの遅延列挙と、表示中のフォルダへのツリーの同期。</summary>
internal sealed class FolderTreeNavigator
{
    private readonly Func<string, IReadOnlyList<FolderTreeEntry>> _enumerate;
    private int _syncGeneration;

    internal FolderTreeNavigator(Func<string, IReadOnlyList<FolderTreeEntry>>? enumerate = null)
    {
        _enumerate = enumerate ?? EnumerateChildFolders;
    }

    internal bool IsSyncingSelection { get; private set; }

    internal Task PopulateAsync(FolderTreeNode node)
    {
        if (node.PendingLoad is { } pending)
        {
            return pending;
        }

        if (node.Children.Count != 1 || !Equals(node.Children[0], FolderTreeNode.Placeholder))
        {
            return Task.CompletedTask;
        }

        object[] loading = [FolderTreeNode.LoadingText];
        node.Children = loading;
        return node.PendingLoad = PopulateCoreAsync(node, loading);
    }

    internal Task RefreshAsync(FolderTreeNode node)
    {
        node.PendingLoad = null;
        node.Children = [FolderTreeNode.Placeholder];
        return node.IsExpanded ? PopulateAsync(node) : Task.CompletedTask;
    }

    private async Task PopulateCoreAsync(FolderTreeNode node, object[] loading)
    {
        FolderTreeNode[]? children = null;
        try
        {
            children = await Task.Run(() => _enumerate(node.FullPath)
                .Select(entry => new FolderTreeNode(entry.Name, entry.FullPath)).ToArray());
            // 大きな ItemsSource の切り替えと、その直後のレイアウトの前に入力を処理する。
            await Dispatcher.Yield(DispatcherPriority.Background);
        }
        finally
        {
            // 再読込で入れ替えた子に、古い列挙結果を上書きしない。
            if (ReferenceEquals(node.Children, loading))
            {
                node.PendingLoad = null;
                node.Children = children ?? (IReadOnlyList<object>)[FolderTreeNode.Placeholder];
            }
        }
    }

    /// <summary>祖先のデータをたどり、各階層で必要なコンテナーだけを生成して選択する。</summary>
    internal async Task SyncToFolderAsync(TreeView tree, string folder)
    {
        int generation = ++_syncGeneration;
        void OnSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            // 列挙やレイアウトを待つ間に利用者が選んだ項目を優先する。
            if (!IsSyncingSelection && generation == _syncGeneration)
            {
                _syncGeneration++;
            }
        }

        tree.SelectedItemChanged += OnSelectionChanged;
        try
        {
            if (!tree.IsVisible)
            {
                return;
            }

            string? root = Path.GetPathRoot(folder);
            if (string.IsNullOrEmpty(root))
            {
                Select(tree, null);
                return;
            }

            FolderTreeNode? parentNode = null;
            IReadOnlyList<object> children = tree.Items.Cast<object>().ToArray();
            string[] segments = folder[root.Length..].Split(Path.DirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries);
            string path = root;
            for (int depth = 0; depth <= segments.Length; depth++)
            {
                int index = FindChildIndex(children, path);
                if (index < 0 || children[index] is not FolderTreeNode node)
                {
                    Select(tree, null);
                    return;
                }

                // ItemsSource の更新に続く WPF のレイアウトを、入力より優先して連続実行しない。
                await Dispatcher.Yield(DispatcherPriority.Background);
                if (generation != _syncGeneration || !tree.IsVisible)
                {
                    return;
                }

                // 待機中の再読込でインデックスの参照先が変わることがある。
                ItemsControl? parent = parentNode is null ? tree : parentNode.Container;
                if (parent is null || index >= parent.Items.Count || !ReferenceEquals(parent.Items[index], node))
                {
                    return;
                }

                TreeViewItem? container = BringChildIntoView(parent, index);
                if (container is null)
                {
                    return;
                }

                // 選択先は生成したこの UI ターンで選択する。列挙待ちを挟むとコンテナーが再利用され得る。
                if (depth == segments.Length)
                {
                    Select(tree, container);
                    container.BringIntoView();
                }

                node.IsExpanded = true;
                await PopulateAsync(node);
                if (generation != _syncGeneration)
                {
                    return;
                }

                if (depth == segments.Length)
                {
                    // 子の有無で高さが確定した後も、選択行を表示範囲に収める。
                    node.Container?.BringIntoView();
                    return;
                }

                parentNode = node;
                children = node.Children;
                path = Path.Combine(path, segments[depth]);
            }
        }
        finally
        {
            tree.SelectedItemChanged -= OnSelectionChanged;
        }
    }

    private static int FindChildIndex(IReadOnlyList<object> children, string path)
    {
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i] is FolderTreeNode node && string.Equals(node.FullPath, path, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static TreeViewItem? BringChildIntoView(ItemsControl parent, int index)
    {
        if (parent.ItemContainerGenerator.ContainerFromIndex(index) is TreeViewItem visible)
        {
            return visible;
        }

        var presenter = parent.Template?.FindName("ItemsHost", parent) as ItemsPresenter;
        presenter?.ApplyTemplate();
        if (presenter is not null && VisualTreeHelper.GetChildrenCount(presenter) > 0
            && VisualTreeHelper.GetChild(presenter, 0) is VirtualizingPanel panel)
        {
            panel.BringIndexIntoViewPublic(index);
        }

        return parent.ItemContainerGenerator.ContainerFromIndex(index) as TreeViewItem;
    }

    private void Select(TreeView tree, TreeViewItem? node)
    {
        IsSyncingSelection = true;
        try
        {
            if (node is not null)
            {
                node.IsSelected = true;
            }
            else if (tree.SelectedItem is FolderTreeNode selected)
            {
                if (selected.Container is { } selectedContainer)
                {
                    selectedContainer.IsSelected = false;
                }
                else if (FirstVisibleContainer(tree) is { } visible)
                {
                    // WPF は画面外の選択を保持する。表示中の項目を経由して、選択だけを解除する。
                    visible.IsSelected = true;
                    visible.IsSelected = false;
                }
            }
        }
        finally
        {
            IsSyncingSelection = false;
        }
    }

    private static TreeViewItem? FirstVisibleContainer(TreeView tree)
    {
        return tree.Template?.FindName("ItemsHost", tree) is ItemsPresenter presenter
            && VisualTreeHelper.GetChildrenCount(presenter) > 0
            && VisualTreeHelper.GetChild(presenter, 0) is Panel panel
            ? panel.Children.OfType<TreeViewItem>().FirstOrDefault() : null;
    }

    /// <summary>隠し・システム属性のフォルダを除き、子フォルダを名前の自然順で返す。</summary>
    internal static IReadOnlyList<FolderTreeEntry> EnumerateChildFolders(string path)
    {
        try
        {
            // 属性は列挙結果を使い、ネットワーク共有でも各フォルダへ個別に問い合わせない。
            return new DirectoryInfo(path).EnumerateDirectories()
                .Where(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .OrderBy(d => d.FullName, NaturalOrderComparer.Instance)
                .Select(d => new FolderTreeEntry(d.Name, d.FullName))
                .ToList();
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }
}

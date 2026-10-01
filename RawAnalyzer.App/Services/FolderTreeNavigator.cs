using System.IO;
using System.Windows.Controls;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>
/// フォルダツリーの子フォルダ1件。
/// </summary>
/// <param name="Name">フォルダ名(見出しに出す)。</param>
/// <param name="FullPath">フォルダのフルパス。</param>
internal readonly record struct FolderTreeEntry(string Name, string FullPath);

/// <summary>
/// 左パネルのフォルダツリー。項目を展開したときに子フォルダを足し、表示中のフォルダまで展開して選択する。
/// </summary>
/// <remarks>
/// <para>
/// 子フォルダの列挙(とフォルダの属性の取得)は UI スレッドの外で行う。ネットワーク共有では1階層の列挙に
/// 数秒かかることがあり、切断中の割り当てドライブでは SMB のタイムアウトまで戻らない。UI スレッドで列挙すると、
/// 展開・再読込のたび、またファイルを開いたフォルダまでツリーを同期するたび(祖先の各階層の初回)にウィンドウが
/// 固まる(性能ルール5)。列挙中は子に「読み込み中…」を置き、同じ項目の列挙を重ねない。
/// </para>
/// <para>
/// 未展開の項目はダミーの子(<see cref="Placeholder"/>)を1つ持ち、展開できることを示す。列挙の結果は、
/// 列挙中にその項目を作り直していなければ(再読込)足す。
/// </para>
/// </remarks>
internal sealed class FolderTreeNavigator
{
    /// <summary>未展開の項目に置くダミーの子。</summary>
    internal const string Placeholder = "…";

    /// <summary>子フォルダを列挙している間に置く子の表示。</summary>
    internal const string LoadingText = "読み込み中…";

    private readonly Func<string, IReadOnlyList<FolderTreeEntry>> _enumerate;

    // 表示中のフォルダへの同期の世代。新しい同期が始まったら、前の同期は列挙を待った後で何もしない
    private int _syncGeneration;

    /// <summary>
    /// フォルダツリーを作る。
    /// </summary>
    /// <param name="enumerate">
    /// 子フォルダの列挙(UI スレッドの外で呼ぶ)。null なら <see cref="EnumerateChildFolders"/>。
    /// </param>
    internal FolderTreeNavigator(Func<string, IReadOnlyList<FolderTreeEntry>>? enumerate = null)
    {
        _enumerate = enumerate ?? EnumerateChildFolders;
    }

    /// <summary>
    /// 表示中のフォルダへの同期がツリーの選択を変えている間は true。
    /// </summary>
    /// <remarks>
    /// 選択の変更(SelectedItemChanged)でフォルダを開く側は、この間の変更を無視する
    /// (開いたフォルダへ同期しただけなので、開き直さない)。
    /// </remarks>
    internal bool IsSyncingSelection { get; private set; }

    /// <summary>
    /// 未展開の項目(ダミーの子を持つ)を作る。
    /// </summary>
    /// <param name="header">見出し。</param>
    /// <param name="path">フォルダのフルパス(Tag に持つ)。</param>
    /// <returns>作った項目。</returns>
    internal static TreeViewItem CreateItem(string header, string path)
    {
        var item = new TreeViewItem { Header = header, Tag = path };
        item.Items.Add(Placeholder);
        return item;
    }

    /// <summary>
    /// 項目が未展開(ダミーの子だけを持つ)なら子フォルダを列挙して足す。列挙中なら同じ列挙の完了を待つ。
    /// </summary>
    /// <param name="item">フォルダの項目(Tag にフルパス)。</param>
    /// <returns>子を足し終えた(または足さないと決めた)ことを表すタスク。列挙の想定外の失敗は例外で終わる。</returns>
    internal Task PopulateAsync(TreeViewItem item)
    {
        if (item.Items.Count == 1 && item.Items[0] is Loading loading)
        {
            return loading.Completion;
        }

        if (item.Items.Count != 1 || !Equals(item.Items[0], Placeholder) || item.Tag is not string path)
        {
            return Task.CompletedTask;
        }

        var marker = new Loading();
        item.Items.Clear();
        item.Items.Add(marker);
        marker.Completion = PopulateCoreAsync(item, path, marker);
        return marker.Completion;
    }

    /// <summary>
    /// 項目の子フォルダを列挙し直す(右クリックメニューの「再読込」)。展開していなければ次に展開したときに列挙する。
    /// </summary>
    /// <param name="item">フォルダの項目。</param>
    /// <returns>子を足し終えたことを表すタスク。</returns>
    internal Task RefreshAsync(TreeViewItem item)
    {
        // 列挙中の結果は、子を作り直したことで後から終わっても足さない(PopulateCoreAsync)
        item.Items.Clear();
        item.Items.Add(Placeholder);
        return item.IsExpanded ? PopulateAsync(item) : Task.CompletedTask;
    }

    /// <summary>
    /// ツリーを指定のフォルダまで展開して選択する(ベストエフォート)。
    /// </summary>
    /// <remarks>
    /// 祖先の各階層を順に展開し、未列挙なら列挙を待つ。待つ間に次の同期が始まったら(別のフォルダを開いた)、
    /// この同期はそこでやめる。
    /// ツリーにない(UNC パス、起動後に接続したドライブ、隠し・システム属性のフォルダの下など、ルートや途中の階層が
    /// 見つからない)ときは、前のフォルダの選択を外す。残すと、TreeView は選択済みの項目をクリックしても選択の
    /// 変更を出さないので、その項目をクリックしても前のフォルダへ戻れない。
    /// </remarks>
    /// <param name="tree">フォルダツリー。最上位の項目はドライブのルート(Tag にルートのパス)。</param>
    /// <param name="folder">選択するフォルダのフルパス。</param>
    /// <returns>同期の完了を表すタスク。</returns>
    internal async Task SyncToFolderAsync(TreeView tree, string folder)
    {
        int generation = ++_syncGeneration;
        string? root = Path.GetPathRoot(folder);
        if (string.IsNullOrEmpty(root))
        {
            Select(tree, null);
            return;
        }

        TreeViewItem? node = tree.Items
            .OfType<TreeViewItem>()
            .FirstOrDefault(i => string.Equals(i.Tag as string, root, StringComparison.OrdinalIgnoreCase));
        if (node is null)
        {
            Select(tree, null);
            return;
        }

        node.IsExpanded = true;
        await PopulateAsync(node);
        if (generation != _syncGeneration)
        {
            return;
        }

        string relative = folder[root.Length..].Trim('\\');
        if (relative.Length > 0)
        {
            foreach (string segment in relative.Split('\\'))
            {
                TreeViewItem? next = node.Items
                    .OfType<TreeViewItem>()
                    .FirstOrDefault(i => string.Equals(
                        Path.GetFileName(i.Tag as string), segment, StringComparison.OrdinalIgnoreCase));
                if (next is null)
                {
                    Select(tree, null);
                    return;
                }

                next.IsExpanded = true;
                await PopulateAsync(next);
                if (generation != _syncGeneration)
                {
                    return;
                }

                node = next;
            }
        }

        Select(tree, node);
        node.BringIntoView();
    }

    /// <summary>
    /// 同期として項目を選択する。null なら選択中のフォルダの項目の選択を外す。
    /// </summary>
    /// <param name="tree">フォルダツリー。</param>
    /// <param name="node">選択する項目。null なら選択を外す。</param>
    private void Select(TreeView tree, TreeViewItem? node)
    {
        IsSyncingSelection = true;
        try
        {
            if (node is not null)
            {
                node.IsSelected = true;
            }
            else if (tree.SelectedItem is TreeViewItem selected)
            {
                selected.IsSelected = false;
            }
        }
        finally
        {
            IsSyncingSelection = false;
        }
    }

    /// <summary>
    /// 子フォルダを列挙する。隠し・システム属性のフォルダは除き、名前の自然順に並べる。
    /// </summary>
    /// <remarks>
    /// 属性は列挙結果に含まれているものを使う。ディレクトリごとに File.GetAttributes を呼ぶと
    /// SMB では1件ごとに往復が増え、フォルダ数の多いネットワーク共有では列挙1回に数秒かかる。
    /// アクセスできない・消えた・切断されたフォルダは子なしとして扱う。
    /// </remarks>
    /// <param name="path">親フォルダのフルパス。</param>
    /// <returns>子フォルダ。</returns>
    internal static IReadOnlyList<FolderTreeEntry> EnumerateChildFolders(string path)
    {
        try
        {
            return new DirectoryInfo(path).EnumerateDirectories()
                .Where(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .OrderBy(d => d.FullName, NaturalOrderComparer.Instance)
                .Select(d => new FolderTreeEntry(d.Name, d.FullName))
                .ToList();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<FolderTreeEntry>();
        }
        catch (IOException)
        {
            return Array.Empty<FolderTreeEntry>();
        }
    }

    private async Task PopulateCoreAsync(TreeViewItem item, string path, Loading marker)
    {
        IReadOnlyList<FolderTreeEntry>? children = null;
        try
        {
            children = await Task.Run(() => _enumerate(path));
        }
        finally
        {
            // 列挙中に「再読込」で作り直されていたら、この結果は足さない
            if (item.Items.Count == 1 && ReferenceEquals(item.Items[0], marker))
            {
                item.Items.Clear();
                if (children is null)
                {
                    // 想定外の例外で列挙できなかった。列挙中の表示を残さず、展開し直せる状態へ戻す
                    item.Items.Add(Placeholder);
                }
                else
                {
                    foreach (FolderTreeEntry child in children)
                    {
                        item.Items.Add(CreateItem($"📁 {child.Name}", child.FullPath));
                    }
                }
            }
        }
    }

    /// <summary>列挙中に置く子。同じ項目の列挙を重ねないよう、進行中の列挙を持つ。</summary>
    private sealed class Loading
    {
        /// <summary>進行中の列挙(子を足し終えるまで)。</summary>
        internal Task Completion { get; set; } = Task.CompletedTask;

        /// <inheritdoc />
        public override string ToString() => LoadingText;
    }
}

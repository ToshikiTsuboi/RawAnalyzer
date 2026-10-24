using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using System.Windows.Threading;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.ViewModels;
using RawAnalyzer.Core;

namespace RawAnalyzer.App;

public partial class MainWindow
{
    private static readonly string[] SupportedExtensions =
        Compare.ComparePane.RawExtensions
            .Concat(ImageFileLoader.SupportedExtensions).ToArray();
    private readonly RecentFilesStore _recentFiles = new();
    private int _recentMenuGeneration;
    private int _folderGeneration;
    private string? _currentFolder;

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog();
        if (dialog.ShowDialog(this) == true)
        {
            LoadFolder(dialog.FolderName, selectPath: null);
        }
    }

    /// <summary>
    /// フォルダ一覧を読み込む(待つ必要がない場所向けの投げっぱなし版)。
    /// </summary>
    /// <param name="folder">対象フォルダ。</param>
    /// <param name="selectPath">読み込み後に選択するファイル。</param>
    private void LoadFolder(string folder, string? selectPath)
    {
        _ = LoadFolderAsync(folder, selectPath);
    }

    /// <summary>
    /// フォルダ一覧を読み込む。列挙はバックグラウンドで行う。
    /// </summary>
    /// <remarks>
    /// DirectoryInfo.EnumerateFiles はネットワーク共有だと数秒かかることがあり、
    /// UIスレッドで回すとその間ウィンドウが固まる。
    /// </remarks>
    /// <param name="folder">対象フォルダ。</param>
    /// <param name="selectPath">読み込み後に選択するファイル。</param>
    /// <returns>
    /// この読み込みが最新のまま終わったら true(一覧を差し替えた、または列挙に失敗して知らせた)。
    /// 列挙の間に別のフォルダの読み込みが始まって結果を捨てたら false。
    /// </returns>
    private async Task<bool> LoadFolderAsync(string folder, string? selectPath)
    {
        folder = Path.GetFullPath(folder);

        // 遅いフォルダの列挙中に別フォルダを開くと、後から終わった古い結果が
        // 画面とセッションを巻き戻してしまう。ファイル読み込みと同じ世代番号で弾く
        int generation = ++_folderGeneration;
        List<FileEntry> entries;
        try
        {
            entries = await Task.Run(() => EnumerateFolder(folder));
        }
        catch (Exception ex)
        {
            if (generation != _folderGeneration)
            {
                return false;
            }

            MessageBox.Show(this, $"フォルダを読み込めません: {ex.Message}", "RawAnalyzer",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return true;
        }

        if (generation != _folderGeneration)
        {
            return false; // 列挙中に別のフォルダが開かれた
        }

        _currentFolder = folder;
        _vm.FolderPath = $"📂 {folder}";
        _vm.ReplaceFiles(entries);

        // 祖先の各階層の初回の列挙は UI スレッドの外で行い、待たずに一覧の続きへ進む(別のフォルダを開いたら
        // 前の同期はやめる)
        _ = _folderTreeNavigator.SyncToFolderAsync(FolderTree, folder);
        string fileFilter = _vm.FileFilterText;
        _sessionStore.Update(session =>
        {
            session.LastFolder = folder;
            session.FileFilter = fileFilter;
        });

        if (selectPath is not null)
        {
            // 絞り込みで隠れているファイルは選択しない(リストに無い項目は選択できない)
            _vm.SelectedFile = _vm.FilteredFiles.FirstOrDefault(
                f => string.Equals(f.FullPath, selectPath, StringComparison.OrdinalIgnoreCase));
        }

        return true;
    }

    /// <summary>
    /// フォルダ内の対応ファイルをサイズ付きで列挙する。
    /// </summary>
    /// <remarks>
    /// DirectoryInfo.EnumerateFiles は列挙時にサイズを持ってくるので、
    /// あとから1ファイルずつ FileInfo.Length を撃つ必要がない。
    /// ネットワーク共有の5000ファイルで数秒〜十数秒UIが止まっていた原因。
    /// </remarks>
    private static List<FileEntry> EnumerateFolder(string folder)
    {
        var entries = new List<FileEntry>();
        foreach (FileInfo info in new DirectoryInfo(folder).EnumerateFiles()
            .Where(f => SupportedExtensions.Contains(
                f.Extension, StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f.FullName, NaturalOrderComparer.Instance))
        {
            long length;
            try
            {
                length = info.Length;
            }
            catch (Exception)
            {
                length = -1;
            }

            entries.Add(new FileEntry(info.Name, info.FullName, IsDirectory: false, length));
        }

        return entries;
    }

    private void OnFileListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.SelectedFile is not { } entry)
        {
            return;
        }

        if (entry.IsDirectory)
        {
            LoadFolder(entry.FullPath, selectPath: null);
        }
        else if (!AddToCompareInstead(new[] { entry.FullPath }))
        {
            OpenPath(entry.FullPath);
        }
    }

    // ---- メニュー・エクスポート ----

    private void RebuildRecentMenu()
    {
        RecentMenu.Items.Clear();
        List<string> recent = _recentFiles.Load();
        if (recent.Count == 0)
        {
            RecentMenu.Items.Add(new System.Windows.Controls.MenuItem
            {
                Header = "(なし)",
                IsEnabled = false,
            });
            return;
        }

        foreach (string path in recent)
        {
            var item = new System.Windows.Controls.MenuItem
            {
                // 見出しはアクセスキーとして解釈されるので、ファイル名の「_」を消さずに文字どおり出す
                Header = AccessKeyText.Escape(Path.GetFileName(path)),
                ToolTip = path,
            };
            string captured = path;
            item.Click += async (_, _) =>
            {
                // 実在はメニューを作り直したときにしか確かめないので、その後に消えた・接続が切れたファイルの項目も
                // 残っている。開く前に UI スレッドを止めずに確かめ、無ければ一度だけ知らせてメニューから外す
                // (確かめずに開くと、フォルダを読み込めない警告に続けて、実在しない raw にフォーマット指定ダイアログを出していた)
                int deferredRequests = ++_deferredOpenRequests;
                int openRequests = _openRequests;
                int folderGeneration = _folderGeneration;
                bool exists = await Task.Run(() => File.Exists(captured));
                if (deferredRequests != _deferredOpenRequests
                    || openRequests != _openRequests || folderGeneration != _folderGeneration)
                {
                    // 確かめている間に別のファイル・フォルダが開かれた、または確認を待つ別の要求を受け付けた
                    // (後から来た方を優先する)
                    return;
                }

                if (!exists)
                {
                    MessageBox.Show(this,
                        $"ファイルが見つかりません(移動・削除されたか、ネットワークに接続できません)。\n{captured}",
                        "最近使ったファイル", MessageBoxButton.OK, MessageBoxImage.Warning);
                    RemoveRecentMenuItems(new[] { captured });
                    return;
                }

                if (AddToCompareInstead(new[] { captured }))
                {
                    return;
                }

                await LoadFolderAndOpenAsync(captured);
            };
            RecentMenu.Items.Add(item);
        }

        // 存在確認は切断されたNAS/UNCパスでSMBタイムアウトまでブロックするため、
        // メニューは先に出してから裏で確認し、消えていた項目だけ後から取り除く
        int generation = ++_recentMenuGeneration;
        _ = PruneMissingRecentItemsAsync(generation, recent);
    }

    /// <summary>最近使ったファイルのうち実在しないものをメニューから取り除く。</summary>
    /// <param name="generation">起動した時点の世代(後発の再構築があれば破棄する)。</param>
    /// <param name="paths">確認対象のパス。</param>
    /// <returns>取り除きの完了を表すタスク。</returns>
    private async Task PruneMissingRecentItemsAsync(int generation, List<string> paths)
    {
        List<string> missing;
        try
        {
            missing = await Task.Run(() => paths.Where(p => !File.Exists(p)).ToList());
        }
        catch (Exception ex)
        {
            AppLog.Warn($"最近使ったファイルの存在確認に失敗: {ex.Message}");
            return;
        }

        if (generation != _recentMenuGeneration || missing.Count == 0)
        {
            return; // 確認中に作り直された(結果は古い)
        }

        RemoveRecentMenuItems(missing);
    }

    /// <summary>最近使ったファイルのメニューから、指定のファイルの項目を取り除く(履歴の記録は残す)。</summary>
    /// <param name="missing">取り除くファイル(実在しないもの)。</param>
    private void RemoveRecentMenuItems(ICollection<string> missing)
    {
        foreach (System.Windows.Controls.MenuItem item in RecentMenu.Items
                     .OfType<System.Windows.Controls.MenuItem>()
                     .Where(i => i.ToolTip is string path && missing.Contains(path))
                     .ToList())
        {
            RecentMenu.Items.Remove(item);
        }

        if (RecentMenu.Items.Count == 0)
        {
            RecentMenu.Items.Add(new System.Windows.Controls.MenuItem
            {
                Header = "(なし)",
                IsEnabled = false,
            });
        }
    }

    // ---- フォルダツリー ----

    // 子フォルダの列挙は UI スレッドの外で行う(ネットワーク共有・切断中のドライブで固まらない)
    private readonly FolderTreeNavigator _folderTreeNavigator = new();

    private void InitFolderTree()
    {
        FolderTree.Items.Clear();
        var items = new List<(System.Windows.Controls.TreeViewItem Item, DriveInfo Drive)>();
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            System.Windows.Controls.TreeViewItem item = FolderTreeNavigator.CreateItem(
                $"💽 {drive.Name.TrimEnd('\\')}", drive.RootDirectory.FullName);
            FolderTree.Items.Add(item);
            items.Add((item, drive));
        }

        // IsReady/VolumeLabel は切断されたネットワークドライブだと
        // タイムアウトまでブロックする。ツリーは先に出してから名前を足す
        _ = AppendDriveLabelsAsync(items);
    }

    /// <summary>ドライブのボリュームラベルを後から見出しへ足す。</summary>
    /// <param name="items">対象のツリー項目とドライブ。</param>
    /// <returns>更新の完了を表すタスク。</returns>
    private static async Task AppendDriveLabelsAsync(
        List<(System.Windows.Controls.TreeViewItem Item, DriveInfo Drive)> items)
    {
        foreach ((System.Windows.Controls.TreeViewItem item, DriveInfo drive) in items)
        {
            string label;
            try
            {
                label = await Task.Run(() => drive.IsReady ? drive.VolumeLabel : "");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(label))
            {
                item.Header = $"{item.Header} ({label})";
            }
        }
    }

    private void OnFolderTreeExpanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.TreeViewItem item)
        {
            _ = _folderTreeNavigator.PopulateAsync(item);
        }
    }

    private async void OnFolderTreeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        // 開いたフォルダへツリーを同期しただけの選択では開き直さない
        if (_folderTreeNavigator.IsSyncingSelection)
        {
            return;
        }

        if (e.NewValue is System.Windows.Controls.TreeViewItem { Tag: string path } item)
        {
            // 存在の確認も切断されたネットワークドライブではタイムアウトまで戻らないので、UI スレッドの外で行う。
            // 確かめる間に別の項目を選んでいた・別のファイルやフォルダを開いていたら(後から来た方を優先する)開かない。
            // 以前は選択だけを見ていたので、確かめる間に「開く」で別のフォルダのファイルを開くと、その一覧の列挙中に
            // このフォルダを読み込み始めて列挙の結果を捨てさせ、後から開いたファイルが開かれなかった。
            // 確かめ始めた時点で受け付けを数え、確認を待っている前の要求・起動時に開くものに追い越されないようにする
            int deferredRequests = ++_deferredOpenRequests;
            int openRequests = _openRequests;
            int folderGeneration = _folderGeneration;
            bool exists = await Task.Run(() => Directory.Exists(path));
            if (exists && ReferenceEquals(FolderTree.SelectedItem, item) && deferredRequests == _deferredOpenRequests
                && openRequests == _openRequests && folderGeneration == _folderGeneration)
            {
                LoadFolder(path, selectPath: null);
            }
        }
    }

    private void OnFileListPreviewRightClick(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<System.Windows.Controls.ListBoxItem>(
            e.OriginalSource as DependencyObject);
        if (item is not null)
        {
            item.IsSelected = true;
        }
    }

    private void OnFileCtxOpenClick(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedFile is { IsDirectory: false } entry && !AddToCompareInstead(new[] { entry.FullPath }))
        {
            OpenPath(entry.FullPath);
        }
    }

    private void OnFileCtxOpenWithFormatClick(object sender, RoutedEventArgs e)
    {
        // raw 以外(画像ファイル)・フォルダでは項目を無効にしてある(画像ファイルは理由をツールチップで示す)。
        // 以前は画像ファイルをダイアログなしで普通に開き、フォーマットを指定して開くつもりの利用者を驚かせた
        if (!_vm.CanOpenSelectedFileWithFormat || _vm.SelectedFile is not { } entry)
        {
            return;
        }

        // 表示中の raw なら F2 と同じ(開き直せたら同じサイズの記憶の元の形式を置き換える)
        if (_currentPath is not null && IsRawFile(_currentPath)
            && string.Equals(entry.FullPath, _currentPath, StringComparison.OrdinalIgnoreCase))
        {
            ChangeCurrentRawFormat(_currentPath);
            return;
        }

        // 記憶フォーマットをスキップして必ずダイアログを表示する。初期値は同じパスの記憶
        // (なければ候補一覧の先頭 → 表示中の画像のフォーマット → ダイアログ既定の推定)。記憶が使えるかは一覧の
        // 列挙で取ったサイズで確かめる(UI スレッドで FileInfo を撃たない。ダイアログのサイズの照合は開く処理が取り直す)
        RawFormat? initial = TryGetRememberedFormat(entry.FullPath, entry.Length);
        OpenPathChoosingFormat(entry.FullPath, initial, correctFrom: null);
    }

    private async void OnFileCtxRevealClick(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedFile is not { } entry)
        {
            return;
        }

        // 実在の確認(と起動)は UI スレッドの外で行う(切断した NAS ではタイムアウトまで戻らない)
        string path = entry.FullPath;
        await Task.Run(() =>
        {
            if (File.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true,
                });
            }
        });
    }

    private void OnFileCtxCopyPathClick(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedFile is { } entry)
        {
            ClipboardHelper.TrySetText(entry.FullPath);
        }
    }

    private async void OnFileCtxRefreshClick(object sender, RoutedEventArgs e)
    {
        if (_currentFolder is not { } folder)
        {
            return;
        }

        // 実在の確認は UI スレッドの外で行う(切断した NAS ではタイムアウトまで戻らない)。確かめる間に別のフォルダの
        // 読み込みが始まった・確認を待つ別の要求を受け付けたら、後から来た方を優先して読み込み直さない
        int deferredRequests = ++_deferredOpenRequests;
        int folderGeneration = _folderGeneration;
        bool exists = await Task.Run(() => Directory.Exists(folder));
        if (exists && folderGeneration == _folderGeneration && deferredRequests == _deferredOpenRequests)
        {
            LoadFolder(folder, _vm.SelectedFile?.FullPath);
        }
    }

    private void OnFileFilterClearClick(object sender, RoutedEventArgs e)
    {
        _vm.ClearFileFilter();
        FileFilterCombo.Focus();
    }

    /// <summary>絞り込み欄のキー操作。Esc で消去、Enter で一覧へ移動する。</summary>
    private void OnFileFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (FileFilterCombo.IsDropDownOpen)
        {
            return; // ドロップダウン操作中は ComboBox 既定の Esc / Enter に任せる
        }

        if (e.Key == Key.Escape && _vm.FileFilterText.Length > 0)
        {
            _vm.ClearFileFilter();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            FocusFileList();
            e.Handled = true;
        }
    }

    /// <summary>ファイル一覧へフォーカスを移す。選択が無い(または隠れた)場合は先頭を選ぶ。</summary>
    private void FocusFileList()
    {
        if (_vm.SelectedFile is null || !_vm.FilteredFiles.Contains(_vm.SelectedFile))
        {
            _vm.SelectedFile = _vm.FilteredFiles.FirstOrDefault();
        }

        FileListBox.Focus();
        if (_vm.SelectedFile is { } selected)
        {
            FileListBox.ScrollIntoView(selected);
            (FileListBox.ItemContainerGenerator.ContainerFromItem(selected)
                as System.Windows.Controls.ListBoxItem)?.Focus();
        }
    }

    /// <summary>
    /// 絞り込み欄へフォーカスを移す(Ctrl+F)。左パネルが隠れていれば表示し、フルスクリーンなら解除する。
    /// </summary>
    /// <remarks>
    /// フルスクリーンでは左パネルを出さない(UpdatePanelLayout)。以前は解除せずにフォーカスを移そうとして何も起きず、
    /// 続けて打った文字が1文字ショートカットとして実行された。
    /// </remarks>
    private void FocusFileFilter()
    {
        if (_vm.IsFullscreen)
        {
            _vm.IsFullscreen = false;
        }

        if (!_vm.LeftPanelVisible)
        {
            _vm.LeftPanelVisible = true;
        }

        FileFilterCombo.Focus();
        if (!FileFilterCombo.IsKeyboardFocusWithin)
        {
            // 表示したばかりでまだフォーカスを受けられないときは、次のキー入力より先(Loaded 優先度)に移す
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FileFilterCombo.Focus());
        }
    }

    private void OnTreePreviewRightClick(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<System.Windows.Controls.TreeViewItem>(
            e.OriginalSource as DependencyObject);
        if (item is not null)
        {
            item.IsSelected = true;
        }
    }

    private async void OnTreeOpenExplorerClick(object sender, RoutedEventArgs e)
    {
        if (FolderTree.SelectedItem is not System.Windows.Controls.TreeViewItem { Tag: string path })
        {
            return;
        }

        // 実在の確認(と起動)は UI スレッドの外で行う(切断したネットワークドライブではタイムアウトまで戻らない)
        await Task.Run(() =>
        {
            if (Directory.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                });
            }
        });
    }

    private void OnTreeRefreshClick(object sender, RoutedEventArgs e)
    {
        if (FolderTree.SelectedItem is System.Windows.Controls.TreeViewItem item)
        {
            _ = _folderTreeNavigator.RefreshAsync(item);
        }
    }

    // ---- ドラッグ&ドロップ ----

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnFileDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths)
        {
            return;
        }

        // フォルダかファイルかの確認は UI スレッドの外で行う(ネットワーク上のパスは、切断していればタイムアウトまで
        // 戻らない)。確かめる間に別のファイル・フォルダが開かれた・確認を待つ別の要求を受け付けたら、後から来た方を
        // 優先してドロップしたものは開かない
        string path = paths[0];
        int deferredRequests = ++_deferredOpenRequests;
        int openRequests = _openRequests;
        int folderGeneration = _folderGeneration;
        (bool isFolder, bool isFile) = await Task.Run(
            () => Directory.Exists(path) ? (true, false) : (false, File.Exists(path)));
        if (deferredRequests != _deferredOpenRequests
            || openRequests != _openRequests || folderGeneration != _folderGeneration)
        {
            return;
        }

        if (isFolder)
        {
            LoadFolder(path, selectPath: null);
        }
        else if (isFile)
        {
            // 比較モード中は、比較画面の外(左右のパネル・メニュー・ツールバー)へのドロップも、比較画面への
            // ドロップと同じく全ファイルを比較ペインへ追加する
            if (AddToCompareInstead(paths))
            {
                return;
            }

            await LoadFolderAndOpenAsync(path);
        }
    }
}

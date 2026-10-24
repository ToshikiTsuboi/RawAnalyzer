using System.Windows;
using Microsoft.Win32;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.Views;
using RawAnalyzer.Core;

namespace RawAnalyzer.App;

public partial class MainWindow
{
    // ---- 比較モード ----

    private bool _compareMode;

    private async void OnCompareModeClick(object sender, RoutedEventArgs e)
    {
        await ToggleCompareModeAsync();
    }

    /// <summary>比較モードへ入る/抜ける。</summary>
    private async Task ToggleCompareModeAsync()
    {
        if (_compareMode)
        {
            await ExitCompareModeAsync();
        }
        else
        {
            await EnterCompareModeAsync();
        }
    }

    private async Task EnterCompareModeAsync()
    {
        if (_compareMode)
        {
            return;
        }

        CancelTiffPageLoad();
        StopPlayback();
        _compareMode = true;
        _vm.IsCompareMode = true;
        CompareArea.Visibility = Visibility.Visible;

        // 開いている画像があれば最初のペイン(A)として読み直す。
        // メイン表示の画像を共有せず独立にロードするのは、所有権を
        // 比較モードに閉じ、終了時の後始末を単純にするため
        // (補正・HDR適用中はファイルと内容が一致しないため対象外)
        if (CompareArea.PaneCount == 0 && _currentPath is not null
            && _derivedImage is null && _correctionLabel is null)
        {
            await CompareArea.AddPaneFromPathAsync(_currentPath);
        }
    }

    private async Task ExitCompareModeAsync()
    {
        if (!_compareMode)
        {
            return;
        }

        await CompareArea.CloseAllAsync();
        CompareArea.Visibility = Visibility.Collapsed;
        _compareMode = false;
        _vm.IsCompareMode = false;

        // 比較ペインの推定の通知から、表示中のファイルの通知へ戻す
        _vm.FormatNoticeText = _mainFormatNotice;
        _vm.FormatNoticeToolTip = _mainFormatNoticeToolTip;
    }

    /// <summary>ファイル選択ダイアログを出して比較ペイン資源を用意する。</summary>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>読み込んだ資源。キャンセル・失敗はnull。</returns>
    private async Task<Compare.ComparePane?> PickComparePaneAsync(CancellationToken cancellationToken)
    {
        var dialog = new OpenFileDialog { Filter = OpenImageFilter };
        if (dialog.ShowDialog(this) != true)
        {
            return null;
        }

        return await LoadComparePaneAsync(dialog.FileName, cancellationToken);
    }

    /// <summary>
    /// 指定パスから比較ペイン資源を読み込む。rawは通常の「開く」と同じく、同じパスの記憶 →
    /// 同じサイズの記憶(自動適用がちょうど1つ)の順に使い、なければインポートダイアログで確認する。
    /// </summary>
    /// <param name="path">対象ファイル。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>読み込んだ資源。キャンセル・失敗はnull。</returns>
    private async Task<Compare.ComparePane?> LoadComparePaneAsync(
        string path, CancellationToken cancellationToken)
    {
        try
        {
            RawFormat? format = null;
            RawFormatOrigin? origin = null;
            bool? autoOpenChoice = null;
            if (Compare.ComparePane.IsRawFile(path))
            {
                // サイズは UI スレッドの外で取る(通常の「開く」と同じ)。取る間に比較モードを抜けたら打ち切る
                long size = await Task.Run(() => SafeFileSize(path), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                RawOpenPlan plan = RawOpenPlanner.Plan(
                    path, size, TryGetRememberedFormat(path, size), _formatMemory.History,
                    _currentFormat);
                origin = plan.Origin;
                format = plan.Format;
                if (format is null)
                {
                    var dialog = new RawImportDialog(
                        path, size, _presetStore, plan.DialogInitial, _formatMemory, _currentFormat)
                    {
                        Owner = this,
                    };
                    if (dialog.ShowDialog() != true || dialog.Result is null)
                    {
                        return null;
                    }

                    format = dialog.Result;
                    autoOpenChoice = dialog.AutoOpenNextTime;
                    RememberFileFormat(path, format);
                }
            }

            Compare.ComparePane pane = await Compare.ComparePane.LoadAsync(
                path, format, cancellationToken);
            if (format is not null)
            {
                // 読み込めたら同じサイズ・拡張子の記憶へ記録する(同じパスの記憶は従来どおりダイアログの確定時)。
                // サイズは UI スレッドの外で取る。読み込んだペインを返せなくなるので、ここでは打ち切らない
                long loadedSize = await Task.Run(() => SafeFileSize(path));
                _formatMemory.RememberLoaded(path, loadedSize, format, autoOpenChoice);

                // 比較ペインは F2 で開き直せないので、直し方を添えて知らせる(比較モードを抜けたら戻す)
                if (origin == RawFormatOrigin.SizeMemory && _compareMode)
                {
                    _vm.FormatNoticeText = RawOpenPlanner.CompareAutoOpenNotice(path);
                    _vm.FormatNoticeToolTip = RawOpenPlanner.AutoOpenToolTip(path, loadedSize, format);
                }
            }

            return pane;
        }
        catch (OperationCanceledException)
        {
            return null; // 比較モード終了で打ち切られた
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"読み込みに失敗しました: {ex.Message}", "比較モード",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }
}

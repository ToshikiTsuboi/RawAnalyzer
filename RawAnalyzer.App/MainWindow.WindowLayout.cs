using System.IO;
using System.Windows;
using System.Windows.Media;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.Views;

namespace RawAnalyzer.App;

public partial class MainWindow
{
    private readonly SessionStore _sessionStore = new();

    // 起動時に読んだセッション(ウィンドウ配置・パネル・絞り込み・最後のフォルダの復元に使う)。変更は
    // _sessionStore.Update で保存されている最新の状態へ当てる(別のインスタンスの保存を巻き戻さない)
    private readonly SessionState _session;
    private WindowState _preFullscreenState = WindowState.Normal;
    private WindowStyle _preFullscreenStyle = WindowStyle.SingleBorderWindow;
    private ResizeMode _preFullscreenResize = ResizeMode.CanResize;

    // ---- セッション記憶 ----

    private void ApplyWindowPlacement()
    {
        if (_session.WindowWidth is { } width && _session.WindowHeight is { } height
            && _session.WindowLeft is { } left && _session.WindowTop is { } top
            && width >= 400 && height >= 300)
        {
            // 画面外への復元を防ぐ
            double maxLeft = SystemParameters.VirtualScreenLeft
                + SystemParameters.VirtualScreenWidth - 200;
            double maxTop = SystemParameters.VirtualScreenTop
                + SystemParameters.VirtualScreenHeight - 100;
            Left = Math.Clamp(left, SystemParameters.VirtualScreenLeft, maxLeft);
            Top = Math.Clamp(top, SystemParameters.VirtualScreenTop, maxTop);
            Width = Math.Min(width, SystemParameters.VirtualScreenWidth);
            Height = Math.Min(height, SystemParameters.VirtualScreenHeight);
        }

        if (_session.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }

        if (_session.LeftPanelWidth is { } leftWidth && leftWidth >= 120)
        {
            _leftPanelWidth = leftWidth;
        }

        if (_session.RightPanelWidth is { } rightWidth && rightWidth >= 160)
        {
            _rightPanelWidth = rightWidth;
        }
    }

    private void SaveWindowPlacement()
    {
        CapturePanelWidths();
        double leftPanelWidth = _leftPanelWidth;
        double rightPanelWidth = _rightPanelWidth;
        bool leftPanelVisible = _vm.LeftPanelVisible;
        bool rightPanelVisible = _vm.RightPanelVisible;
        string fileFilter = _vm.FileFilterText;
        // フルスクリーン中は(フルスクリーンのための最大化ではなく)フルスクリーンに入る前の状態を保存する
        bool maximized = FullscreenWindowState.IsMaximizedToSave(
            WindowState, _vm.IsFullscreen, _preFullscreenState);
        bool normal = WindowState == WindowState.Normal;
        double left = normal ? Left : RestoreBounds.Left;
        double top = normal ? Top : RestoreBounds.Top;
        double width = normal ? Width : RestoreBounds.Width;
        double height = normal ? Height : RestoreBounds.Height;

        // ウィンドウ配置・パネル・絞り込みは最後に閉じたウィンドウのものを残す。ファイルごとのフォーマットと最後の
        // フォルダは変えたときに保存済みなので書かない(起動時に読んだ内容で、別のインスタンスの保存を巻き戻さない)
        _sessionStore.Update(session =>
        {
            session.LeftPanelWidth = leftPanelWidth;
            session.RightPanelWidth = rightPanelWidth;
            session.LeftPanelVisible = leftPanelVisible;
            session.RightPanelVisible = rightPanelVisible;
            session.FileFilter = fileFilter;
            session.WindowMaximized = maximized;
            session.WindowLeft = left;
            session.WindowTop = top;
            session.WindowWidth = width;
            session.WindowHeight = height;
        });
    }

    private void OnOpenPresetFolderClick(object sender, RoutedEventArgs e)
    {
        string directory = Path.GetDirectoryName(_presetStore.FilePath)!;
        Directory.CreateDirectory(directory);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = directory,
            UseShellExecute = true,
        });
    }

    private void OnUsageGuideClick(object sender, RoutedEventArgs e)
    {
        ShowInfoWindow("操作ガイド",
            "キーボード操作\n" +
            "  Ctrl+Shift+P: コマンドパレット(全機能を検索して実行)\n" +
            "  F1: ショートカット一覧\n" +
            "  矢印: パン / Ctrl+矢印: 画素カーソルを1画素移動\n" +
            "  起動引数にパスを渡すとそのファイルを直接開く\n\n" +
            "マウス操作\n" +
            "  ホイール: カーソル中心ズーム\n" +
            "  ドラッグ: パン / ダブルクリック: 全体表示\n" +
            "  ズーム3200%以上で画素上にraw値を表示\n\n" +
            "解析\n" +
            "  ROI: ツールバーのROIを押してドラッグで矩形選択\n" +
            "  ラインプロファイル: モードを押して画素をクリック\n" +
            "  ヒストグラム/プロファイルは右クリックでデータ保存\n\n" +
            "ホワイトバランス\n" +
            "  AWB(グレーワールド): 全体平均から自動計算\n" +
            "  スポイト: クリック画素を無彩色として計算\n\n" +
            "HDR\n" +
            "  フォーマットでHDR方式を指定 → 表示モードで分割/合成");
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        new AboutWindow { Owner = this }.ShowDialog();
    }

    private void ShowInfoWindow(string title, string message)
    {
        var text = new System.Windows.Controls.TextBlock
        {
            Text = message,
            Margin = new Thickness(20, 16, 20, 8),
            FontSize = 12,
            LineHeight = 19,
            TextWrapping = TextWrapping.Wrap,
        };
        var ok = new System.Windows.Controls.Button
        {
            Content = "OK",
            IsDefault = true,
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(20, 8, 20, 14),
            MinWidth = 80,
        };
        var panel = new System.Windows.Controls.StackPanel();
        panel.Children.Add(text);
        panel.Children.Add(ok);
        var window = new Window
        {
            Title = title,
            Content = panel,
            Owner = this,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = (Brush)FindResource("PanelBrush"),
            Foreground = (Brush)FindResource("TextBrush"),
        };
        ok.Click += (_, _) => window.Close();
        window.ShowDialog();
    }

    // ---- フルスクリーン ----

    private void ApplyFullscreen(bool fullscreen)
    {
        if (fullscreen)
        {
            _preFullscreenState = WindowState;
            _preFullscreenStyle = WindowStyle;
            _preFullscreenResize = ResizeMode;

            MainMenuBar.Visibility = Visibility.Collapsed;
            ToolBarPanel.Visibility = Visibility.Collapsed;
            StatusBarPanel.Visibility = Visibility.Collapsed;
            UpdatePanelLayout();

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal; // 一度戻さないと最大化が効かない場合がある
            WindowState = WindowState.Maximized;
        }
        else
        {
            MainMenuBar.Visibility = Visibility.Visible;
            ToolBarPanel.Visibility = Visibility.Visible;
            StatusBarPanel.Visibility = Visibility.Visible;
            UpdatePanelLayout();

            WindowStyle = _preFullscreenStyle;
            ResizeMode = _preFullscreenResize;

            // 元が最大化でも一度通常へ戻してから最大化し直す(枠のない状態で計算した画面全体の最大化の矩形を残さない)
            foreach (WindowState state in FullscreenWindowState.StatesOnLeaving(_preFullscreenState))
            {
                WindowState = state;
            }
        }

        // フルスクリーンの案内は表示中のあいだオーバーレイに残す(描画のたびの間引きレベルの表示で消さない)
        UpdateLevelOverlay();

        Viewport.Focus();
    }

    // ---- 左右パネルの幅調整・表示切替 ----

    private double _leftPanelWidth = 220;
    private double _rightPanelWidth = 240;

    /// <summary>
    /// パネルの表示状態と幅をグリッドへ反映する。
    /// 非表示時は列幅を0にして端のストリップを出す(フルスクリーン時は両方隠す)。
    /// </summary>
    private void UpdatePanelLayout()
    {
        bool fullscreen = _vm.IsFullscreen;
        bool left = _vm.LeftPanelVisible && !fullscreen;
        bool right = _vm.RightPanelVisible && !fullscreen;

        LeftPanel.Visibility = left ? Visibility.Visible : Visibility.Collapsed;
        LeftSplitter.Visibility = left ? Visibility.Visible : Visibility.Collapsed;
        LeftColumn.Width = left ? new GridLength(_leftPanelWidth) : new GridLength(0);
        LeftColumn.MinWidth = left ? 150 : 0;
        LeftEdgeStrip.Visibility = !left && !fullscreen ? Visibility.Visible : Visibility.Collapsed;

        RightPanel.Visibility = right ? Visibility.Visible : Visibility.Collapsed;
        RightSplitter.Visibility = right ? Visibility.Visible : Visibility.Collapsed;
        RightColumn.Width = right ? new GridLength(_rightPanelWidth) : new GridLength(0);
        RightColumn.MinWidth = right ? 190 : 0;
        RightEdgeStrip.Visibility = !right && !fullscreen ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>ドラッグで変更された現在の幅を控える(折りたたみ後の復元用)。</summary>
    private void CapturePanelWidths()
    {
        if (LeftPanel.Visibility == Visibility.Visible && LeftColumn.ActualWidth > 0)
        {
            _leftPanelWidth = LeftColumn.ActualWidth;
        }

        if (RightPanel.Visibility == Visibility.Visible && RightColumn.ActualWidth > 0)
        {
            _rightPanelWidth = RightColumn.ActualWidth;
        }
    }

    private void OnHideLeftPanelClick(object sender, RoutedEventArgs e)
    {
        _vm.LeftPanelVisible = false;
    }

    private void OnShowLeftPanelClick(object sender, RoutedEventArgs e)
    {
        _vm.LeftPanelVisible = true;
    }

    private void OnHideRightPanelClick(object sender, RoutedEventArgs e)
    {
        _vm.RightPanelVisible = false;
    }

    private void OnShowRightPanelClick(object sender, RoutedEventArgs e)
    {
        _vm.RightPanelVisible = true;
    }
}

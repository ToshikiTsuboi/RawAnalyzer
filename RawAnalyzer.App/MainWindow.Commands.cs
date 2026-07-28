using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using RawAnalyzer.App.Rendering;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.ViewModels;
using RawAnalyzer.App.Views;

namespace RawAnalyzer.App;

/// <summary>
/// キーボード操作の定義とディスパッチ。
/// </summary>
public partial class MainWindow
{
    private IReadOnlyList<AppCommand>? _commands;
    private CommandPaletteWindow? _paletteWindow;
    private ShortcutHelpWindow? _shortcutWindow;

    /// <summary>全コマンド(遅延構築)。</summary>
    private IReadOnlyList<AppCommand> Commands => _commands ??= BuildCommands();

    /// <summary>
    /// ウィンドウ全体のキー入力を捌く。
    /// </summary>
    /// <remarks>
    /// InputBindings ではなく PreviewKeyDown で処理するのは、
    /// 入力欄にフォーカスがあるときに1文字ショートカットが打鍵を奪わないようにするため。
    /// </remarks>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        ModifierKeys modifiers = Keyboard.Modifiers;

        // 入力中は修飾キーなし/Shiftのみのショートカットを無効にする
        bool typing = IsTextEntryFocused();
        foreach (AppCommand command in Commands)
        {
            if (!command.HasGesture || command.Key != key || command.Modifiers != modifiers)
            {
                continue;
            }

            if (typing && (modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0)
            {
                return;
            }

            if (!command.IsEnabled())
            {
                return;
            }

            e.Handled = true;
            RunCommand(command);
            return;
        }
    }

    /// <summary>
    /// フォーカスが文字入力コントロールにあるか。
    /// 編集可能ComboBoxは内部のTextBoxがフォーカスを持つのでTextBoxBaseで拾える。
    /// </summary>
    private static bool IsTextEntryFocused()
    {
        return Keyboard.FocusedElement is TextBoxBase or ComboBox;
    }

    /// <summary>コマンドを実行する(例外はログへ送り、UIは落とさない)。</summary>
    /// <param name="command">実行するコマンド。</param>
    private void RunCommand(AppCommand command)
    {
        try
        {
            command.Execute();
        }
        catch (Exception ex)
        {
            AppLog.Error($"コマンド実行に失敗: {command.Id}", ex);
            MessageBox.Show(this, $"コマンドの実行に失敗しました: {ex.Message}", "RawAnalyzer",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnCommandPaletteClick(object sender, RoutedEventArgs e) => ShowCommandPalette();

    private void OnShortcutHelpClick(object sender, RoutedEventArgs e) => ShowShortcutHelp();

    private void ShowCommandPalette()
    {
        if (_paletteWindow is not null)
        {
            _paletteWindow.Activate();
            return;
        }

        _paletteWindow = new CommandPaletteWindow(Commands) { Owner = this };
        _paletteWindow.Closed += (_, _) =>
        {
            AppCommand? chosen = _paletteWindow?.SelectedCommand;
            _paletteWindow = null;
            if (chosen is not null)
            {
                RunCommand(chosen);
            }
        };
        _paletteWindow.Show();
    }

    private void ShowShortcutHelp()
    {
        if (_shortcutWindow is not null)
        {
            _shortcutWindow.Activate();
            return;
        }

        _shortcutWindow = new ShortcutHelpWindow(Commands) { Owner = this };
        _shortcutWindow.Closed += (_, _) => _shortcutWindow = null;
        _shortcutWindow.Show();
    }

    /// <summary>表示モードを切り替える(コンボボックス経由で既存の検証を通す)。</summary>
    private void SelectDisplayMode(int index)
    {
        if (_vm.HasImage && index >= 0 && index < DisplayModeCombo.Items.Count)
        {
            DisplayModeCombo.SelectedIndex = index;
        }
    }

    private static void Toggle(ToggleButton button)
    {
        if (button.IsEnabled)
        {
            button.IsChecked = button.IsChecked != true;
        }
    }

    private IReadOnlyList<AppCommand> BuildCommands()
    {
        var empty = new RoutedEventArgs();
        var list = new List<AppCommand>
        {
            // ---- 全般 ----
            new()
            {
                Id = "palette",
                Category = "全般",
                Title = "コマンドパレット",
                Description = "すべての操作を検索して実行する",
                Key = Key.P,
                Modifiers = ModifierKeys.Control | ModifierKeys.Shift,
                Execute = ShowCommandPalette,
            },
            new()
            {
                Id = "shortcuts",
                Category = "全般",
                Title = "ショートカット一覧",
                Key = Key.F1,
                Execute = ShowShortcutHelp,
            },
            new()
            {
                Id = "open",
                Category = "ファイル",
                Title = "開く…",
                Key = Key.O,
                Modifiers = ModifierKeys.Control,
                Execute = () => OnOpenFileClick(this, empty),
            },
            new()
            {
                Id = "open-folder",
                Category = "ファイル",
                Title = "フォルダを開く…",
                Key = Key.O,
                Modifiers = ModifierKeys.Control | ModifierKeys.Shift,
                Execute = () => OnOpenFolderClick(this, empty),
            },
            new()
            {
                Id = "save",
                Category = "ファイル",
                Title = "保存…",
                Key = Key.S,
                Modifiers = ModifierKeys.Control,
                CanExecute = () => _vm.HasImage,
                Execute = () => OnSaveClick(this, empty),
            },
            new()
            {
                Id = "batch",
                Category = "ファイル",
                Title = "バッチ現像 / 動画書き出し…",
                Key = Key.B,
                Modifiers = ModifierKeys.Control,
                CanExecute = () => _vm.HasImage,
                Execute = () => OnBatchExportClick(this, empty),
            },
            new()
            {
                Id = "format",
                Category = "ファイル",
                Title = "フォーマットを変更して開き直す…",
                Key = Key.F2,
                CanExecute = () => _vm.HasImage,
                Execute = () => OnChangeFormatClick(this, empty),
            },

            // ---- 表示 ----
            new()
            {
                Id = "zoom-in",
                Category = "表示",
                Title = "ズームイン",
                Key = Key.OemPlus,
                CanExecute = () => _vm.HasImage,
                Execute = Viewport.ZoomIn,
            },
            new()
            {
                Id = "zoom-out",
                Category = "表示",
                Title = "ズームアウト",
                Key = Key.OemMinus,
                CanExecute = () => _vm.HasImage,
                Execute = Viewport.ZoomOut,
            },
            new()
            {
                Id = "zoom-actual",
                Category = "表示",
                Title = "等倍 (1:1)",
                Key = Key.D1,
                CanExecute = () => _vm.HasImage,
                Execute = Viewport.ActualSize,
            },
            new()
            {
                Id = "zoom-fit",
                Category = "表示",
                Title = "全体表示",
                Key = Key.D0,
                CanExecute = () => _vm.HasImage,
                Execute = Viewport.FitToView,
            },
            new()
            {
                Id = "fullscreen",
                Category = "表示",
                Title = "フルスクリーン切替",
                Key = Key.F11,
                Execute = () => _vm.IsFullscreen = !_vm.IsFullscreen,
            },
            new()
            {
                Id = "left-panel",
                Category = "表示",
                Title = "ファイルパネルの表示切替",
                Key = Key.L,
                Modifiers = ModifierKeys.Control,
                Execute = () => _vm.LeftPanelVisible = !_vm.LeftPanelVisible,
            },
            new()
            {
                Id = "right-panel",
                Category = "表示",
                Title = "調整パネルの表示切替",
                Key = Key.R,
                Modifiers = ModifierKeys.Control,
                Execute = () => _vm.RightPanelVisible = !_vm.RightPanelVisible,
            },
            new()
            {
                Id = "zebra",
                Category = "表示",
                Title = "ゼブラ (飽和/黒潰れ警告) 切替",
                Key = Key.Z,
                CanExecute = () => _vm.HasImage,
                Execute = () => _vm.ZebraOn = !_vm.ZebraOn,
            },
            new()
            {
                Id = "mode-raw",
                Category = "表示モード",
                Title = "Raw表示",
                Key = Key.D1,
                Modifiers = ModifierKeys.Control,
                CanExecute = () => _vm.HasImage,
                Execute = () => SelectDisplayMode(0),
            },
            new()
            {
                Id = "mode-bayer",
                Category = "表示モード",
                Title = "Bayerカラー",
                Key = Key.D2,
                Modifiers = ModifierKeys.Control,
                CanExecute = () => _vm.HasImage,
                Execute = () => SelectDisplayMode(1),
            },
            new()
            {
                Id = "mode-develop",
                Category = "表示モード",
                Title = "カラー現像",
                Key = Key.D3,
                Modifiers = ModifierKeys.Control,
                CanExecute = () => _vm.HasImage,
                Execute = () => SelectDisplayMode(2),
            },
            new()
            {
                Id = "mode-split",
                Category = "表示モード",
                Title = "チャネル分割 (R/Gr/Gb/B)",
                Key = Key.D4,
                Modifiers = ModifierKeys.Control,
                CanExecute = () => _vm.HasImage,
                Execute = () => SelectDisplayMode(3),
            },
            new()
            {
                Id = "mode-hdr-split",
                Category = "表示モード",
                Title = "HDR分割表示 (長/短)",
                Key = Key.D5,
                Modifiers = ModifierKeys.Control,
                CanExecute = () => _vm.HasImage,
                Execute = () => SelectDisplayMode(4),
            },
            new()
            {
                Id = "mode-hdr-merge",
                Category = "表示モード",
                Title = "HDR合成表示",
                Key = Key.D6,
                Modifiers = ModifierKeys.Control,
                CanExecute = () => _vm.HasImage,
                Execute = () => SelectDisplayMode(5),
            },

            // ---- 調整 ----
            new()
            {
                Id = "auto-contrast",
                Category = "調整",
                Title = "自動コントラスト (0.35%クリップ)",
                Key = Key.A,
                Modifiers = ModifierKeys.Control,
                CanExecute = () => _vm.HasImage,
                Execute = () => OnAutoContrastClick(this, empty),
            },
            new()
            {
                Id = "reset-display",
                Category = "調整",
                Title = "表示調整をリセット",
                Key = Key.R,
                Modifiers = ModifierKeys.Control | ModifierKeys.Shift,
                CanExecute = () => _vm.HasImage,
                Execute = () => OnResetDisplayClick(this, empty),
            },
            new()
            {
                Id = "awb",
                Category = "調整",
                Title = "AWB (グレーワールド)",
                Key = Key.W,
                Modifiers = ModifierKeys.Control,
                CanExecute = () => _vm.HasImage,
                Execute = () => OnGrayWorldClick(this, empty),
            },
            new()
            {
                Id = "gamma-up",
                Category = "調整",
                Title = "ガンマを上げる (+0.1)",
                Key = Key.OemPeriod,
                Modifiers = ModifierKeys.Shift,
                CanExecute = () => _vm.HasImage,
                Execute = () => _vm.Gamma = Math.Min(3.0, Math.Round(_vm.Gamma + 0.1, 2)),
            },
            new()
            {
                Id = "gamma-down",
                Category = "調整",
                Title = "ガンマを下げる (-0.1)",
                Key = Key.OemComma,
                Modifiers = ModifierKeys.Shift,
                CanExecute = () => _vm.HasImage,
                Execute = () => _vm.Gamma = Math.Max(0.2, Math.Round(_vm.Gamma - 0.1, 2)),
            },
            new()
            {
                Id = "gain-up",
                Category = "調整",
                Title = "ゲインを上げる (×1.25)",
                Key = Key.OemCloseBrackets,
                CanExecute = () => _vm.HasImage,
                Execute = () => _vm.Gain = Math.Min(8.0, Math.Round(_vm.Gain * 1.25, 3)),
            },
            new()
            {
                Id = "gain-down",
                Category = "調整",
                Title = "ゲインを下げる (÷1.25)",
                Key = Key.OemOpenBrackets,
                CanExecute = () => _vm.HasImage,
                Execute = () => _vm.Gain = Math.Max(0.25, Math.Round(_vm.Gain / 1.25, 3)),
            },

            // ---- 操作モード ----
            new()
            {
                Id = "roi-mode",
                Category = "操作モード",
                Title = "ROI選択モード切替",
                Key = Key.R,
                CanExecute = () => _vm.HasImage,
                Execute = () => Toggle(RoiToggle),
            },
            new()
            {
                Id = "profile-mode",
                Category = "操作モード",
                Title = "ラインプロファイルモード切替",
                Key = Key.P,
                CanExecute = () => _vm.HasImage,
                Execute = () => Toggle(ProfileToggle),
            },
            new()
            {
                Id = "wb-pick-mode",
                Category = "操作モード",
                Title = "WBスポイトモード切替",
                Key = Key.I,
                CanExecute = () => _vm.HasImage,
                Execute = () => Toggle(WbPickToggle),
            },
            new()
            {
                Id = "clear-roi",
                Category = "操作モード",
                Title = "ROIを解除",
                Key = Key.G,
                Modifiers = ModifierKeys.Control,
                CanExecute = () => _vm.HasRoi,
                Execute = () => Viewport.ClearRoi(),
            },
            new()
            {
                Id = "roi-center",
                Category = "操作モード",
                Title = "中央に ROI を設定 (画像の1/4)",
                Key = Key.G,
                Modifiers = ModifierKeys.Control | ModifierKeys.Shift,
                CanExecute = () => _vm.HasImage,
                Execute = SetCenterRoi,
            },

            // ---- シーケンス ----
            new()
            {
                Id = "seq-next",
                Category = "シーケンス",
                Title = "次のフレーム",
                Key = Key.Next,
                CanExecute = () => _vm.HasSequence,
                Execute = () => OnSeqNextClick(this, empty),
            },
            new()
            {
                Id = "seq-prev",
                Category = "シーケンス",
                Title = "前のフレーム",
                Key = Key.Prior,
                CanExecute = () => _vm.HasSequence,
                Execute = () => OnSeqPrevClick(this, empty),
            },
            new()
            {
                Id = "seq-first",
                Category = "シーケンス",
                Title = "先頭フレーム",
                Key = Key.Home,
                CanExecute = () => _vm.HasSequence,
                Execute = () => OnSeqFirstClick(this, empty),
            },
            new()
            {
                Id = "seq-last",
                Category = "シーケンス",
                Title = "最終フレーム",
                Key = Key.End,
                CanExecute = () => _vm.HasSequence,
                Execute = () => OnSeqLastClick(this, empty),
            },
            new()
            {
                Id = "seq-play",
                Category = "シーケンス",
                Title = "再生 / 停止",
                Key = Key.Space,
                CanExecute = () => _vm.HasSequence,
                Execute = () => Toggle(PlayToggle),
            },

            // ---- 解析 ----
            new()
            {
                Id = "hist-refresh",
                Category = "解析",
                Title = "ヒストグラムを更新",
                Key = Key.F5,
                CanExecute = () => _vm.HasImage,
                Execute = () => OnHistogramRefreshClick(this, empty),
            },
            new()
            {
                Id = "hist-log",
                Category = "解析",
                Title = "ヒストグラムのlog表示切替",
                Execute = () => _vm.HistogramIsLog = !_vm.HistogramIsLog,
            },
            new()
            {
                Id = "hist-cumulative",
                Category = "解析",
                Title = "累積ヒストグラム切替",
                Execute = () => _vm.HistogramIsCumulative = !_vm.HistogramIsCumulative,
            },
            new()
            {
                Id = "hist-channel",
                Category = "解析",
                Title = "チャネル別ヒストグラム切替",
                Execute = () => _vm.HistogramByChannel = !_vm.HistogramByChannel,
            },
            new()
            {
                Id = "hist-copy",
                Category = "解析",
                Title = "ヒストグラムをクリップボードへコピー",
                CanExecute = () => _vm.HasImage,
                Execute = () => OnHistogramCopyClick(this, empty),
            },
            new()
            {
                Id = "hist-csv",
                Category = "解析",
                Title = "ヒストグラムをCSV保存…",
                CanExecute = () => _vm.HasImage,
                Execute = () => OnHistogramSaveCsvClick(this, empty),
            },
            new()
            {
                Id = "defect",
                Category = "解析",
                Title = "欠陥画素検出…",
                Key = Key.D,
                Modifiers = ModifierKeys.Control,
                CanExecute = () => _vm.HasImage,
                Execute = () => OnDefectDetectClick(this, empty),
            },
            new()
            {
                Id = "noise",
                Category = "解析",
                Title = "ノイズ / ダイナミックレンジ測定…",
                Key = Key.M,
                Modifiers = ModifierKeys.Control,
                CanExecute = () => _vm.HasImage,
                Execute = () => OnNoiseMeasureClick(this, empty),
            },
            new()
            {
                Id = "calc",
                Category = "解析",
                Title = "画像演算 (ダーク減算/フラット補正)…",
                Key = Key.K,
                Modifiers = ModifierKeys.Control,
                CanExecute = () => _vm.HasImage,
                Execute = () => OnImageCalculatorClick(this, empty),
            },

            // ---- クリップボード ----
            new()
            {
                Id = "copy-view",
                Category = "クリップボード",
                Title = "表示をクリップボードへコピー",
                Key = Key.C,
                Modifiers = ModifierKeys.Control | ModifierKeys.Shift,
                CanExecute = () => _vm.HasImage,
                Execute = () => OnCopyViewClick(this, empty),
            },
            new()
            {
                Id = "copy-pixel",
                Category = "クリップボード",
                Title = "カーソル位置の画素値をコピー",
                Key = Key.C,
                Modifiers = ModifierKeys.Control,
                CanExecute = () => _vm.HasImage,
                Execute = () => OnCopyPixelValueClick(this, empty),
            },
            new()
            {
                Id = "copy-pixel-pos",
                Category = "クリップボード",
                Title = "カーソル座標をコピー",
                CanExecute = () => _vm.HasImage,
                Execute = () => OnCopyPixelPosClick(this, empty),
            },
        };

        VerifyNoDuplicateGestures(list);
        return list;
    }

    /// <summary>画像中央に画像の1/4サイズのROIを設定する。</summary>
    private void SetCenterRoi()
    {
        if (ActiveImage is not { } image)
        {
            return;
        }

        int width = Math.Max(1, image.Width / 2);
        int height = Math.Max(1, image.Height / 2);
        Viewport.SetRoi(new Core.RegionOfInterest(
            (image.Width - width) / 2, (image.Height - height) / 2, width, height));
        RoiToggle.IsChecked = true;
    }

    /// <summary>ショートカットの重複を検出する(定義ミスは起動時に気付けるようにする)。</summary>
    private static void VerifyNoDuplicateGestures(IReadOnlyList<AppCommand> commands)
    {
        var seen = new Dictionary<(Key, ModifierKeys), string>();
        foreach (AppCommand command in commands)
        {
            if (!command.HasGesture)
            {
                continue;
            }

            var gesture = (command.Key, command.Modifiers);
            if (seen.TryGetValue(gesture, out string? other))
            {
                throw new InvalidOperationException(
                    $"ショートカット {command.GestureText} が {other} と {command.Id} で重複しています。");
            }

            seen[gesture] = command.Id;
        }
    }
}

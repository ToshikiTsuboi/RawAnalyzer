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
        AppCommand? command = ShortcutRouting.Resolve(
            Commands, key, Keyboard.Modifiers, ShortcutRouting.Classify(Keyboard.FocusedElement));
        if (command is null)
        {
            return;
        }

        e.Handled = true;
        RunCommand(command);
    }

    /// <summary>
    /// 編集できないコンボ(ツールバーの表示モード・右パネルの Bayer と HDR の調整対象)のドロップダウンを閉じたら、
    /// フォーカスをビューポートへ戻す。
    /// </summary>
    /// <remarks>
    /// 項目を選んだ後もフォーカスがコンボに残ると、続けて押した1文字キー(R・B・G など)・Home/End・矢印が
    /// コンボの文字検索・選択の移動に使われ、ショートカットのつもりで Bayer や表示モードを黙って書き換えていた
    /// (Bayer は同じファイル・同じサイズのフォーマットの記憶にも残る)。キーボードだけで閉じたままのコンボを
    /// 操作するとき(Tab で移って矢印で選ぶ)はドロップダウンを開かないので、フォーカスは動かさない。
    /// 比較モード中は通常表示が比較画面に隠れているので戻さない。
    /// </remarks>
    private void OnSelectorDropDownClosed(object? sender, EventArgs e)
    {
        if (!_compareMode && sender is ComboBox { IsKeyboardFocusWithin: true })
        {
            Viewport.Focus();
        }
    }

    /// <summary>コマンドを実行する(例外はログへ送り、UIは落とさない)。</summary>
    /// <param name="command">実行するコマンド。</param>
    private void RunCommand(AppCommand command)
    {
        // ショートカットはフォーカスを動かさないので、数値欄に打ちかけの値があれば先に確定し、
        // 欄に見えている値で処理させる(メニュー・パレットへはフォーカスが移った時点で確定済み)
        Controls.NumericSliderRow.CommitPendingEdit(Keyboard.FocusedElement);
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

    // ---- 実行の前提と、実行できない理由(コマンドパレットで示す。各コマンドの CanExecute が false のときだけ使う) ----
    //
    // 比較モードは通常表示の上に比較画面を重ねるだけなので、メインの画像・ビューポートを対象にするコマンドは
    // 比較モード中は断る(見えている比較ペインではなく隠れた通常表示に効き、Ctrl+C は別の画像の値をコピーしていた)。
    // メニュー・ツールバーの該当項目も同じ条件(MainViewModel.CanUseMainView)で無効にする

    /// <summary>メインの画像・通常表示を対象にするコマンドの前提(画像があり、比較モードでないこと)。</summary>
    private bool MainViewAvailable() => _vm.HasImage && !_compareMode;

    /// <summary>メインの画像・通常表示を対象にするコマンドの理由。比較モードなら画像の有無より先に示す。</summary>
    private string MainViewReason() => CommandDisabledReasons.ForMainView(_compareMode);

    /// <summary>
    /// 右の調整パネルの切替(ヒストグラムの表示の切替)の前提。比較モード中はパネルごと無効にするので、キー・パレットも断る。
    /// 比較モードでなければ、画像がなくても従来どおり切り替えられる。
    /// </summary>
    private bool AdjustPanelAvailable() => _vm.CanUseAdjustPanel;

    /// <summary>右の調整パネルの切替を実行できない理由(比較モード中)。</summary>
    private string AdjustPanelReason() => CommandDisabledReasons.CompareMode;

    /// <summary>フレーム送り・再生(送れるフレームがあり、比較モードでないこと)の理由。</summary>
    private string NoSequenceReason() => CommandDisabledReasons.ForSequence(_vm.HasImage, _compareMode);

    /// <summary>ROI の解除(ROI があり、比較モードでないこと)の理由。</summary>
    private string NoRoiReason() => CommandDisabledReasons.ForRoi(_vm.HasImage, _compareMode);

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
                Id = "compare-mode",
                Category = "表示",
                Title = "比較モード切替",
                Description = "複数画像を並べて比較(最大4面)",
                Execute = () => _ = ToggleCompareModeAsync(),
            },
            new()
            {
                Id = "about",
                Category = "全般",
                Title = "ソフト情報",
                Description = "バージョン・コミット・ビルド日時・ライセンス",
                Execute = () => OnAboutClick(this, empty),
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
                CanExecute = MainViewAvailable,
                DisabledReason = MainViewReason,
                Execute = () => OnSaveClick(this, empty),
            },
            new()
            {
                Id = "batch",
                Category = "ファイル",
                Title = "バッチ現像 / 動画書き出し…",
                Key = Key.B,
                Modifiers = ModifierKeys.Control,
                CanExecute = MainViewAvailable,
                DisabledReason = MainViewReason,
                Execute = () => OnBatchExportClick(this, empty),
            },
            new()
            {
                Id = "format",
                Category = "ファイル",
                Title = "フォーマットを変更して開き直す…",
                Description = "raw(.raw/.bin)の寸法・ビット深度・HDR方式などを指定し直す(画像ファイルでは使えません)",
                Key = Key.F2,

                // raw 以外(画像ファイル)では実行時に理由をダイアログで出す(AppCommand.CanExecute の方針。
                // ビニング・フィルタの実行中の判定と同じ扱い)。ここで無効にすると、パレットは理由を示せても
                // F2 は黙って無視される
                CanExecute = MainViewAvailable,
                DisabledReason = MainViewReason,
                Execute = () => OnChangeFormatClick(this, empty),
            },

            // ---- 表示 ----
            new()
            {
                Id = "zoom-in",
                Category = "表示",
                Title = "ズームイン",
                Description = "+ のキーは Shift の有無を問わず効きます。テンキーの + でも操作できます",
                Key = Key.OemPlus,

                // 一覧の「+」は Shift+;(JIS)・Shift+=(US)で打つので Shift 付きも受け付ける。テンキーの + も同じ
                AlternateGestures = new ShortcutKey[]
                {
                    new(Key.OemPlus, ModifierKeys.Shift),
                    new(Key.Add, ModifierKeys.None),
                },
                CanExecute = MainViewAvailable,
                DisabledReason = MainViewReason,
                Execute = Viewport.ZoomIn,
            },
            new()
            {
                Id = "zoom-out",
                Category = "表示",
                Title = "ズームアウト",
                Description = "テンキーの - でも操作できます",
                Key = Key.OemMinus,
                AlternateGestures = new ShortcutKey[] { new(Key.Subtract, ModifierKeys.None) },
                CanExecute = MainViewAvailable,
                DisabledReason = MainViewReason,
                Execute = Viewport.ZoomOut,
            },
            new()
            {
                Id = "zoom-actual",
                Category = "表示",
                Title = "等倍 (1:1)",
                Key = Key.D1,
                CanExecute = MainViewAvailable,
                DisabledReason = MainViewReason,
                Execute = Viewport.ActualSize,
            },
            new()
            {
                Id = "zoom-fit",
                Category = "表示",
                Title = "全体表示",
                Key = Key.D0,
                CanExecute = MainViewAvailable,
                DisabledReason = MainViewReason,
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

                // フルスクリーンではパネルを表示しないので、見えない表示状態だけを反転して解除後に驚かせない
                CanExecute = () => !_vm.IsFullscreen,
                DisabledReason = () => CommandDisabledReasons.Fullscreen,
                Execute = () => _vm.LeftPanelVisible = !_vm.LeftPanelVisible,
            },
            new()
            {
                Id = "file-filter",
                Category = "ファイル",
                Title = "ファイル一覧を絞り込む",
                Key = Key.F,
                Modifiers = ModifierKeys.Control,
                Description = "拡張子・ワイルドカード・/正規表現/ で左パネルの一覧を絞り込む欄へ移動(フルスクリーンは解除する)",
                Execute = FocusFileFilter,
            },
            new()
            {
                Id = "right-panel",
                Category = "表示",
                Title = "調整パネルの表示切替",
                Key = Key.R,
                Modifiers = ModifierKeys.Control,
                CanExecute = () => !_vm.IsFullscreen,
                DisabledReason = () => CommandDisabledReasons.Fullscreen,
                Execute = () => _vm.RightPanelVisible = !_vm.RightPanelVisible,
            },
            new()
            {
                Id = "zebra",
                Category = "表示",
                Title = "ゼブラ (飽和/黒潰れ警告) 切替",
                Key = Key.Z,
                CanExecute = MainViewAvailable,
                DisabledReason = MainViewReason,
                Execute = () => _vm.ZebraOn = !_vm.ZebraOn,
            },
            new()
            {
                Id = "mode-raw",
                Category = "表示モード",
                Title = "Raw表示",
                Key = Key.D1,
                Modifiers = ModifierKeys.Control,
                CanExecute = MainViewAvailable,
                DisabledReason = MainViewReason,
                Execute = () => SelectDisplayMode(0),
            },
            new()
            {
                Id = "mode-bayer",
                Category = "表示モード",
                Title = "Bayerカラー",
                Key = Key.D2,
                Modifiers = ModifierKeys.Control,
                CanExecute = MainViewAvailable,
                DisabledReason = MainViewReason,
                Execute = () => SelectDisplayMode(1),
            },
            new()
            {
                Id = "mode-develop",
                Category = "表示モード",
                Title = "カラー現像",
                Key = Key.D3,
                Modifiers = ModifierKeys.Control,
                CanExecute = MainViewAvailable,
                DisabledReason = MainViewReason,
                Execute = () => SelectDisplayMode(2),
            },
            new()
            {
                Id = "mode-split",
                Category = "表示モード",
                Title = "チャネル分割 (R/Gr/Gb/B)",
                Key = Key.D4,
                Modifiers = ModifierKeys.Control,
                CanExecute = MainViewAvailable,
                DisabledReason = MainViewReason,
                Execute = () => SelectDisplayMode(3),
            },
            new()
            {
                Id = "mode-hdr-split",
                Category = "表示モード",
                Title = "HDR分割表示 (長/短)",
                Key = Key.D5,
                Modifiers = ModifierKeys.Control,
                CanExecute = MainViewAvailable,
                DisabledReason = MainViewReason,
                Execute = () => SelectDisplayMode(4),
            },
            new()
            {
                Id = "mode-hdr-merge",
                Category = "表示モード",
                Title = "HDR合成表示",
                Key = Key.D6,
                Modifiers = ModifierKeys.Control,
                CanExecute = MainViewAvailable,
                DisabledReason = MainViewReason,
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
                CanExecute = MainViewAvailable,
                DisabledReason = MainViewReason,
                Execute = () => OnAutoContrastClick(this, empty),
            },
            new()
            {
                Id = "reset-display",
                Category = "調整",
                Title = "表示調整をリセット",
                Key = Key.R,
                Modifiers = ModifierKeys.Control | ModifierKeys.Shift,
                CanExecute = MainViewAvailable,
                DisabledReason = MainViewReason,
                Execute = () => OnResetDisplayClick(this, empty),
            },
            new()
            {
                Id = "awb",
                Category = "調整",
                Title = "AWB (グレーワールド)",
                Key = Key.W,
                Modifiers = ModifierKeys.Control,
                CanExecute = MainViewAvailable,
                DisabledReason = MainViewReason,
                Execute = () => OnGrayWorldClick(this, empty),
            },
            new()
            {
                Id = "gamma-up",
                Category = "調整",
                Title = "ガンマを上げる (+0.1)",
                Key = Key.OemPeriod,
                Modifiers = ModifierKeys.Shift,
                CanExecute = MainViewAvailable,
                DisabledReason = MainViewReason,
                Execute = () => _vm.Gamma = Math.Min(3.0, Math.Round(_vm.Gamma + 0.1, 2)),
            },
            new()
            {
                Id = "gamma-down",
                Category = "調整",
                Title = "ガンマを下げる (-0.1)",
                Key = Key.OemComma,
                Modifiers = ModifierKeys.Shift,
                CanExecute = MainViewAvailable,
                DisabledReason = MainViewReason,
                Execute = () => _vm.Gamma = Math.Max(0.2, Math.Round(_vm.Gamma - 0.1, 2)),
            },
            new()
            {
                Id = "gain-up",
                Category = "調整",
                Title = "ゲインを上げる (+6dB = 1段)",
                Key = Key.OemCloseBrackets,
                CanExecute = MainViewAvailable,
                DisabledReason = MainViewReason,
                Execute = () => _vm.GainDb = Math.Min(_vm.MaxGainDb, _vm.GainDb + 6),
            },
            new()
            {
                Id = "gain-down",
                Category = "調整",
                Title = "ゲインを下げる (-6dB = 1段)",
                Key = Key.OemOpenBrackets,
                CanExecute = MainViewAvailable,
                DisabledReason = MainViewReason,
                Execute = () => _vm.GainDb = Math.Max(_vm.MinGainDb, _vm.GainDb - 6),
            },

            // ---- 操作モード ----
            new()
            {
                Id = "roi-mode",
                Category = "操作モード",
                Title = "ROI選択モード切替",
                Key = Key.R,
                CanExecute = MainViewAvailable,
                DisabledReason = MainViewReason,
                Execute = () => Toggle(RoiToggle),
            },
            new()
            {
                Id = "profile-mode",
                Category = "操作モード",
                Title = "ラインプロファイルモード切替",
                Key = Key.P,
                CanExecute = MainViewAvailable,
                DisabledReason = MainViewReason,
                Execute = () => Toggle(ProfileToggle),
            },
            new()
            {
                Id = "wb-pick-mode",
                Category = "操作モード",
                Title = "WBスポイトモード切替",
                Key = Key.I,
                CanExecute = MainViewAvailable,
                DisabledReason = MainViewReason,
                Execute = () => Toggle(WbPickToggle),
            },
            new()
            {
                Id = "clear-roi",
                Category = "操作モード",
                Title = "ROIを解除",
                Key = Key.G,
                Modifiers = ModifierKeys.Control,
                CanExecute = () => _vm.HasRoi && !_compareMode,
                DisabledReason = NoRoiReason,
                Execute = () => Viewport.ClearRoi(),
            },
            new()
            {
                Id = "roi-center",
                Category = "操作モード",
                Title = "中央に ROI を設定 (画像の1/4)",
                Description = "HDR分割表示では調整対象の段(全体なら長秒)の中央に置く",
                Key = Key.G,
                Modifiers = ModifierKeys.Control | ModifierKeys.Shift,
                CanExecute = MainViewAvailable,
                DisabledReason = MainViewReason,
                Execute = SetCenterRoi,
            },

            // ---- シーケンス ----
            new()
            {
                Id = "seq-next",
                Category = "シーケンス",
                Title = "次のフレーム",
                Key = Key.Next,
                CanExecute = () => _vm.HasSequence && !_compareMode,
                DisabledReason = NoSequenceReason,
                Execute = () => OnSeqNextClick(this, empty),
            },
            new()
            {
                Id = "seq-prev",
                Category = "シーケンス",
                Title = "前のフレーム",
                Key = Key.Prior,
                CanExecute = () => _vm.HasSequence && !_compareMode,
                DisabledReason = NoSequenceReason,
                Execute = () => OnSeqPrevClick(this, empty),
            },
            new()
            {
                Id = "seq-first",
                Category = "シーケンス",
                Title = "先頭フレーム",
                Key = Key.Home,
                CanExecute = () => _vm.HasSequence && !_compareMode,
                DisabledReason = NoSequenceReason,
                Execute = () => OnSeqFirstClick(this, empty),
            },
            new()
            {
                Id = "seq-last",
                Category = "シーケンス",
                Title = "最終フレーム",
                Key = Key.End,
                CanExecute = () => _vm.HasSequence && !_compareMode,
                DisabledReason = NoSequenceReason,
                Execute = () => OnSeqLastClick(this, empty),
            },
            new()
            {
                Id = "seq-play",
                Category = "シーケンス",
                Title = "再生 / 停止",
                Key = Key.Space,
                CanExecute = () => _vm.HasSequence && !_compareMode,
                DisabledReason = NoSequenceReason,
                Execute = () => Toggle(PlayToggle),
            },

            // ---- 解析 ----
            new()
            {
                Id = "hist-refresh",
                Category = "解析",
                Title = "ヒストグラムを更新",
                Key = Key.F5,
                CanExecute = MainViewAvailable,
                DisabledReason = MainViewReason,
                Execute = () => OnHistogramRefreshClick(this, empty),
            },
            new()
            {
                Id = "hist-log",
                Category = "解析",
                Title = "ヒストグラムのlog表示切替",
                CanExecute = AdjustPanelAvailable,
                DisabledReason = AdjustPanelReason,
                Execute = () => _vm.HistogramIsLog = !_vm.HistogramIsLog,
            },
            new()
            {
                Id = "hist-cumulative",
                Category = "解析",
                Title = "累積ヒストグラム切替",
                CanExecute = AdjustPanelAvailable,
                DisabledReason = AdjustPanelReason,
                Execute = () => _vm.HistogramIsCumulative = !_vm.HistogramIsCumulative,
            },
            new()
            {
                Id = "hist-channel",
                Category = "解析",
                Title = "チャネル別ヒストグラム切替",
                CanExecute = AdjustPanelAvailable,
                DisabledReason = AdjustPanelReason,
                Execute = () => _vm.HistogramByChannel = !_vm.HistogramByChannel,
            },
            new()
            {
                Id = "hist-copy",
                Category = "解析",
                Title = "ヒストグラムをクリップボードへコピー",
                CanExecute = MainViewAvailable,
                DisabledReason = MainViewReason,
                Execute = () => OnHistogramCopyClick(this, empty),
            },
            new()
            {
                Id = "hist-csv",
                Category = "解析",
                Title = "ヒストグラムをCSV保存…",
                CanExecute = MainViewAvailable,
                DisabledReason = MainViewReason,
                Execute = () => OnHistogramSaveCsvClick(this, empty),
            },
            new()
            {
                Id = "defect",
                Category = "解析",
                Title = "欠陥画素検出…",
                Key = Key.D,
                Modifiers = ModifierKeys.Control,
                CanExecute = MainViewAvailable,
                DisabledReason = MainViewReason,
                Execute = () => OnDefectDetectClick(this, empty),
            },
            new()
            {
                Id = "noise",
                Category = "解析",
                Title = "ノイズ / ダイナミックレンジ測定…",
                Key = Key.M,
                Modifiers = ModifierKeys.Control,
                CanExecute = MainViewAvailable,
                DisabledReason = MainViewReason,
                Execute = () => OnNoiseMeasureClick(this, empty),
            },
            new()
            {
                Id = "calc",
                Category = "解析",
                Title = "画像演算 (ダーク減算/フラット補正)…",
                Key = Key.K,
                Modifiers = ModifierKeys.Control,
                CanExecute = MainViewAvailable,
                DisabledReason = MainViewReason,
                Execute = () => OnImageCalculatorClick(this, empty),
            },

            new()
            {
                Id = "binning",
                Category = "処理",
                Title = "デジタルビニング (2×2 / 4×4)…",
                Description = "平均 / 加算。モノクロ・Bayer CFA・RGBに対応",

                // 実行中かどうかは実行時に判定し、何が実行中かをダイアログで示す(AppCommand.CanExecute の方針)
                CanExecute = MainViewAvailable,
                DisabledReason = MainViewReason,
                Execute = () => OnBinningClick(this, empty),
            },
            new()
            {
                Id = "image-filter",
                Category = "処理",
                Title = "画像フィルタ (平滑化 / シャープ / エッジ)…",
                Description = "平均・ガウシアン・メディアン・アンシャープ・Sobel・最小値・最大値",
                CanExecute = MainViewAvailable,
                DisabledReason = MainViewReason,
                Execute = () => OnFilterClick(this, empty),
            },

            // ---- クリップボード ----
            new()
            {
                Id = "copy-view",
                Category = "クリップボード",
                Title = "表示をクリップボードへコピー",
                Key = Key.C,
                Modifiers = ModifierKeys.Control | ModifierKeys.Shift,
                CanExecute = MainViewAvailable,
                DisabledReason = MainViewReason,
                Execute = () => OnCopyViewClick(this, empty),
            },
            new()
            {
                Id = "copy-pixel",
                Category = "クリップボード",
                Title = "カーソル位置の画素値をコピー",
                Key = Key.C,
                Modifiers = ModifierKeys.Control,
                CanExecute = MainViewAvailable,
                DisabledReason = MainViewReason,
                Execute = () => OnCopyPixelValueClick(this, empty),
            },
            new()
            {
                Id = "copy-pixel-pos",
                Category = "クリップボード",
                Title = "カーソル座標をコピー",
                CanExecute = MainViewAvailable,
                DisabledReason = MainViewReason,
                Execute = () => OnCopyPixelPosClick(this, empty),
            },
        };

        ShortcutRouting.VerifyNoDuplicateGestures(list);
        return list;
    }

    /// <summary>画像中央に画像の1/4サイズのROIを設定する(HDR分割ビューでは調整対象の段の中央)。</summary>
    private void SetCenterRoi()
    {
        if (ActiveImage is not { } image)
        {
            return;
        }

        // チャネル分割表示の中央は4象限(R/Gr/Gb/B)の境目なので、中央のROIは
        // 必ず別チャネルの画素にまたがり解析できない。作ってから断るより先に知らせる
        if (Viewport.IsChannelSplitLayout)
        {
            MessageBox.Show(this,
                "チャネル分割表示では中央にROIを設定できません(4つの象限の境目になるため)。\n" +
                "ROI選択モードで、1つの象限の中をドラッグして選択してください。",
                "ROI", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // HDR分割ビューは段を横に並べた画像で、全体の中央は段の継ぎ目になる(露光の違う画素を混ぜた統計になる)。
        // 調整対象の段(「全体」なら長秒)の中央に置く
        Core.RegionOfInterest roi = _hdrFrameParams is { Length: > 0 } stages && _hdrSegmentWidth > 0
            ? CenterRoi.Compute(image.Width, image.Height, _hdrSegmentWidth,
                CenterRoi.StageForTarget(HdrTargetCombo.SelectedIndex, stages.Length))
            : CenterRoi.Compute(image.Width, image.Height);
        Viewport.SetRoi(roi);
        RoiToggle.IsChecked = true;
    }
}

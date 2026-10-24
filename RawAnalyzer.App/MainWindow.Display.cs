using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using RawAnalyzer.App.Controls;
using RawAnalyzer.App.Rendering;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.ViewModels;
using RawAnalyzer.App.Views;
using RawAnalyzer.Core;

namespace RawAnalyzer.App;

public partial class MainWindow
{
    private ColorMatrix _colorMatrix = ColorMatrix.Identity;
    private bool _updatingMatrixBoxes;
    private ushort _blackPoint;
    private ushort _whitePoint = 65535;
    private bool _updatingSliders;

    // ビューポートへ渡した現像LUTと、それを作ったパラメータ(表示中の画像のビット深度を含む)。
    // カラー現像で描く前に現在のパラメータと照合し、違えば作り直す(カラー現像表示に入るまで再生成を遅延)
    private readonly DevelopLutCache _developLutCache = new();
    private DispatcherTimer? _developLutTimer;

    // ---- 表示調整 (LUT) ----

    /// <summary>
    /// ゼブラのトグルのツールチップを、出す直前にいまの表示モードで作り直す
    /// (Bayer カラー・カラー現像の表示ではゼブラを描かないので、その理由を先頭に出す)。
    /// </summary>
    /// <remarks>表示モードを変える経路は多いので、変わるたびではなく見せるときに決める。</remarks>
    private void OnZebraToolTipOpening(object sender, System.Windows.Controls.ToolTipEventArgs e)
    {
        ZebraToggle.ToolTip = ZebraToolTip.For(Viewport.DisplayMode);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.HistogramIsLog)
            or nameof(MainViewModel.HistogramIsCumulative))
        {
            RedrawHistogram();
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.HistogramByChannel))
        {
            // 行交互HDRの Raw 表示でライン単位か行オフセットが奇数だと、ファイル上の行の偶奇で振り分けたチャネルが
            // センサの色と食い違う。チャネル別にしようとしたら理由を示して分割ビューへ案内し、チェックを戻す
            // (チェックボックスの値の書き込み中に戻さないよう、後で行う)
            if (_vm.HistogramByChannel
                && HdrExposureMix.ChannelHistogramRefusal(BayerPhaseMismatchShown) is { } refusal)
            {
                _ = Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show(this, refusal, "ヒストグラム", MessageBoxButton.OK, MessageBoxImage.Information);
                    _vm.HistogramByChannel = false;
                });
                return;
            }

            RefreshHistogram(Viewport.Roi is { PixelCount: > 0 } roi ? roi : null);
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.ZebraOn))
        {
            Viewport.SetZebra(_vm.ZebraOn);
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.IsFullscreen))
        {
            CapturePanelWidths();
            ApplyFullscreen(_vm.IsFullscreen);
            return;
        }

        if (e.PropertyName is nameof(MainViewModel.LeftPanelVisible)
            or nameof(MainViewModel.RightPanelVisible))
        {
            CapturePanelWidths();
            UpdatePanelLayout();
            return;
        }

        if (_updatingSliders)
        {
            return;
        }

        if (e.PropertyName is nameof(MainViewModel.Gain)
            or nameof(MainViewModel.Gamma)
            or nameof(MainViewModel.Contrast)
            or nameof(MainViewModel.BlackLevel)
            or nameof(MainViewModel.WhiteLevel))
        {
            if (e.PropertyName is nameof(MainViewModel.BlackLevel)
                or nameof(MainViewModel.WhiteLevel))
            {
                ApplyLevelCodes(_vm.BlackLevel, _vm.WhiteLevel);
            }

            ApplyDisplayParametersToViews();
        }

        if (e.PropertyName is nameof(MainViewModel.WbGainR) or nameof(MainViewModel.WbGainG)
            or nameof(MainViewModel.WbGainB)
            && _vm.HasImage)
        {
            UpdateDevelopLuts();
        }
    }

    /// <summary>
    /// 現在の表示調整パラメータを、いま画面に効いているLUT経路すべてへ反映する。
    /// スライダー変更・リセット・自動コントラストで共通に使う。
    /// Raw表示のLUTだけ更新すると、HDR分割(SegmentLuts)やカラー現像(_developLuts)
    /// 表示中に見た目が変わらず「効かないボタン」になる。
    /// </summary>
    private void ApplyDisplayParametersToViews()
    {
        if (!_vm.HasImage)
        {
            return;
        }

        if (_hdrFrameParams is not null)
        {
            // HDR分割表示中は調整対象フレームのLUTのみ更新する
            DisplayParameters parameters = CurrentDisplayParameters();
            int target = HdrTargetCombo.SelectedIndex;
            if (target <= 0)
            {
                for (int i = 0; i < _hdrFrameParams.Length; i++)
                {
                    _hdrFrameParams[i] = parameters;
                }
            }
            else
            {
                int frame = Math.Min(target - 1, _hdrFrameParams.Length - 1);
                _hdrFrameParams[frame] = parameters;
            }

            ApplySplitLuts();
            return;
        }

        Viewport.SetLut(BuildLut());

        // ゲイン・コントラストも現像LUTに影響する。ここを絞ると
        // ColorDevelopモードでスライダーが完全に無反応になる
        UpdateDevelopLuts();
    }

    private DevelopParameters CurrentDevelopParameters()
    {
        double gamma = _vm.Gamma > 0 ? _vm.Gamma : 1.0;
        // 白点(_whitePoint)は CurrentShift と同じビット深度で白レベルcodeの画素と同じ値(code<<(16−N))に置いている。
        // 白飛び(白レベルのcode以上)の判定を code で行えるよう、同じビット深度を渡す
        return new DevelopParameters(
            _blackPoint, _vm.WbGainR, _vm.WbGainG, _vm.WbGainB, gamma,
            _colorMatrix.IsIdentity ? null : _colorMatrix,
            _whitePoint, _vm.Gain, _vm.Contrast,
            SourceBitDepth: ActiveFormat?.BitDepth ?? 16);
    }

    /// <summary>
    /// 現像LUTの再生成を予約する。カラー現像表示中でなければ何もせず、実際の生成
    /// (65536×3回のMath.Powを含む)はカラー現像表示に入るとき(<see cref="EnsureDevelopLuts"/> の照合)まで遅らせる。
    /// 表示中でもスライダー連続操作で毎ティック作り直さないよう間引く。
    /// </summary>
    private void UpdateDevelopLuts()
    {
        if (Viewport.DisplayMode != ViewportDisplayMode.ColorDevelop)
        {
            return;
        }

        _developLutTimer ??= CreateDevelopLutTimer();
        _developLutTimer.Stop();
        _developLutTimer.Start();
    }

    private DispatcherTimer CreateDevelopLutTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            EnsureDevelopLuts();
        };
        return timer;
    }

    /// <summary>
    /// 現像LUTを現在のパラメータ(表示中の画像のビット深度を含む)と照合し、違えば作り直してビューポートへ渡す
    /// (カラー現像表示に入るとき・カラー現像のまま画像を差し替えたとき・間引きの後)。
    /// </summary>
    /// <remarks>
    /// 作り直しの印に頼ると、印を立て忘れた経路(HDR派生ビューへの出入りでビット深度が変わる)で旧ビット深度の
    /// LUTのまま描き、白飛びの判定を誤る(DevelopLutCache)。
    /// </remarks>
    private void EnsureDevelopLuts()
    {
        _developLutTimer?.Stop();
        if (_developLutCache.Refresh(CurrentDevelopParameters()) is { } luts)
        {
            Viewport.SetDevelopLuts(luts);
        }
    }

    // ---- カラーマトリクス ----

    /// <summary>カラーマトリクスの入力欄(M11〜M33 の行優先)。</summary>
    private System.Windows.Controls.TextBox[] MatrixBoxes =>
        new[] { M11Box, M12Box, M13Box, M21Box, M22Box, M23Box, M31Box, M32Box, M33Box };

    private void OnMatrixChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingMatrixBoxes || M33Box is null || MatrixStateText is null)
        {
            return;
        }

        // 読めない欄は赤枠とツールチップの理由で示し、前の行列のまま現像する
        if (!ColorMatrixInput.TryRead(MatrixBoxes, out ColorMatrix? matrix))
        {
            MatrixStateText.Text = "入力エラー";
            return;
        }

        _colorMatrix = matrix;
        MatrixStateText.Text = _colorMatrix.IsIdentity ? "単位行列 (無効)" : "適用中";
        if (_vm.HasImage)
        {
            UpdateDevelopLuts();
        }
    }

    private void OnMatrixResetClick(object sender, RoutedEventArgs e)
    {
        _updatingMatrixBoxes = true;
        M11Box.Text = "1.00";
        M12Box.Text = "0.00";
        M13Box.Text = "0.00";
        M21Box.Text = "0.00";
        M22Box.Text = "1.00";
        M23Box.Text = "0.00";
        M31Box.Text = "0.00";
        M32Box.Text = "0.00";
        M33Box.Text = "1.00";
        _updatingMatrixBoxes = false;
        foreach (System.Windows.Controls.TextBox box in MatrixBoxes)
        {
            InputFeedback.SetError(box, null); // 書き直した欄に前の入力の不正の知らせを残さない
        }

        _colorMatrix = ColorMatrix.Identity;
        MatrixStateText.Text = "単位行列 (無効)";
        if (_vm.HasImage)
        {
            UpdateDevelopLuts();
        }
    }

    private void OnMenuDisplayModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem { Tag: string tag }
            && int.TryParse(tag, out int index))
        {
            DisplayModeCombo.SelectedIndex = index;
        }
    }

    // ---- 表示モード・ホワイトバランス ----

    private async void OnDisplayModeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (Viewport is null || _currentFormat is null || _currentImage is null)
        {
            return;
        }

        int index = DisplayModeCombo.SelectedIndex;
        if (index is 4 or 5)
        {
            // HDR 方式は raw の読み込みダイアログ(フォーマット変更…)でしか指定できない。
            // カラー画像・画像ファイルでは、そこから設定するよう案内せず、実際に取れる手段を示す
            DisplayModeSelection.Refusal hdrRefusal = DisplayModeSelection.ForHdrMode(
                _currentFormat.Hdr, _colorImage is not null,
                isRawFile: _currentPath is not null && IsRawFile(_currentPath));
            if (hdrRefusal != DisplayModeSelection.Refusal.None)
            {
                MessageBox.Show(this, DisplayModeSelection.Explain(hdrRefusal),
                    "RawAnalyzer", MessageBoxButton.OK, MessageBoxImage.Information);
                DisplayModeCombo.SelectedIndex = 0;
                return;
            }

            if (index == 4)
            {
                await EnterHdrSplitAsync();
            }
            else
            {
                await EnterHdrMergeAsync();
            }

            return;
        }

        // HDR合成ビュー中: Bayer/現像/分割は合成結果に適用する(Raw表示で元画像へ復帰)
        if (_hdrFloatImage is not null && index is 1 or 2 or 3)
        {
            if (ActiveFormat?.Bayer is null or BayerPattern.None)
            {
                MessageBox.Show(this,
                    "HDR合成結果にBayerパターンがありません。\n" +
                    "元rawのフォーマットでBayerパターンを指定してからHDR合成してください。",
                    "RawAnalyzer", MessageBoxButton.OK, MessageBoxImage.Information);
                DisplayModeCombo.SelectedIndex = 5;
                return;
            }

            ViewportDisplayMode hdrMode = index switch
            {
                1 => ViewportDisplayMode.BayerColor,
                2 => ViewportDisplayMode.ColorDevelop,
                _ => ViewportDisplayMode.ChannelSplit,
            };
            if (hdrMode == ViewportDisplayMode.ColorDevelop)
            {
                EnsureDevelopLuts();
            }

            Viewport.SetDisplayMode(hdrMode);
            await EnsureBayerPyramidAsync();
            return;
        }

        // 通常モード: HDR派生ビューから復帰
        if (_derivedImage is not null)
        {
            await RestoreMainImageAsync();
        }

        // カラー画像の Raw 表示は RGB のままの表示で、Bayer 系の表示は使わない(開いたとき・送りと同じ)。
        // メニューなどから Bayer 系を選んで断ったあとの戻り先も、グレーの Raw 表示ではなくカラー表示にする。
        // 行交互HDRでファイル上の行の偶奇がセンサの Bayer の位相と一致しない raw も、色を取り違えるので断る
        DisplayModeSelection.Selected selected = DisplayModeSelection.ForSelectedMode(
            index, _colorImage is not null, _currentFormat.Bayer,
            HdrExposureMix.BayerPhaseMismatch(_currentFormat));
        if (selected.Refusal != DisplayModeSelection.Refusal.None)
        {
            MessageBox.Show(this, DisplayModeSelection.Explain(selected.Refusal),
                "RawAnalyzer", MessageBoxButton.OK, MessageBoxImage.Information);
            DisplayModeCombo.SelectedIndex = 0;
            return;
        }

        ViewportDisplayMode mode = selected.ViewportMode;
        if (mode == ViewportDisplayMode.ColorDevelop)
        {
            EnsureDevelopLuts();
        }

        Viewport.SetDisplayMode(mode);
        if (mode is ViewportDisplayMode.BayerColor or ViewportDisplayMode.ColorDevelop
            or ViewportDisplayMode.ChannelSplit)
        {
            await EnsureBayerPyramidAsync();
        }
    }

    /// <summary>
    /// 表示する画像を差し替えた直後に、表示モードの選択とビューポートの表示モードを同じ結果にそろえる。
    /// </summary>
    /// <remarks>
    /// ファイル連番の送り・TIFF のページ送り・処理結果の差し替え・右パネルの Bayer の変更で共通
    /// (DisplayModeSelection の規約)。
    /// 選択中のモードは新しい画像でも成立すれば保ち、成立しないモードだけ選択ごと戻す。
    /// カラー画像は RGB のまま表示し、選択は Raw 表示・操作不可にする。
    /// 選択の変更は OnDisplayModeChanged を通るので、MainWindow の状態(画像・フォーマット・カラー画像)を
    /// 新しい画像へ交換してから呼ぶ。分割⇔非分割の切替で ROI を捨てるのはビューポートが行う。
    /// </remarks>
    /// <param name="isColor">新しい画像がデコード済みのカラー画像か。</param>
    /// <param name="format">
    /// 新しい画像のフォーマット(Bayerパターンと、行交互HDRで Bayer の位相が一致しないかを見る)。
    /// </param>
    /// <returns>適用した表示モード。</returns>
    private DisplayModeSelection.Choice ApplyDisplayModeToNewImage(bool isColor, RawFormat format)
    {
        DisplayModeSelection.Choice display = DisplayModeSelection.ForSequenceImage(
            DisplayModeCombo.SelectedIndex, isColor, format.Bayer, HdrExposureMix.BayerPhaseMismatch(format));
        DisplayModeCombo.IsEnabled = display.ComboEnabled;

        // 再生中の送りでは多くの場合どちらも変わらないので、変わるときだけ設定する
        if (DisplayModeCombo.SelectedIndex != display.ComboIndex)
        {
            DisplayModeCombo.SelectedIndex = display.ComboIndex;
        }

        // 現像LUTは白飛びの判定に表示中の画像のビット深度を使う。カラー現像で表示するなら、このUIターンで
        // 照合して作り直す(間引きのタイマーを待つと、その間はビット深度の違う旧画像のLUTで新しい画像を描く)
        if (display.ViewportMode == ViewportDisplayMode.ColorDevelop)
        {
            EnsureDevelopLuts();
        }

        if (Viewport.DisplayMode != display.ViewportMode)
        {
            Viewport.SetDisplayMode(display.ViewportMode);
        }

        return display;
    }

    private DisplayParameters CurrentDisplayParameters()
    {
        return new DisplayParameters(
            _blackPoint, _whitePoint, _vm.Gain, _vm.Gamma, _vm.Contrast);
    }

    private void ApplySplitLuts()
    {
        if (_hdrFrameParams is null)
        {
            return;
        }

        var luts = new DisplayLut[_hdrFrameParams.Length];
        for (int i = 0; i < luts.Length; i++)
        {
            luts[i] = DisplayLut.Create(_hdrFrameParams[i]);
        }

        Viewport.SetSplitLuts(luts, _hdrSegmentWidth);
    }

    private async void OnGrayWorldClick(object sender, RoutedEventArgs e)
    {
        if (ActiveImage is null || ActiveFormat is null
            || ActiveFormat.Bayer == BayerPattern.None || RefuseWhiteBalanceForHdr())
        {
            return;
        }

        RawImage image = ActiveImage;
        BayerPattern pattern = ActiveFormat.Bayer;

        // フレーム0固定にすると、マルチフレームでフレームN表示中のAWBが
        // 見えていない画素から計算される
        int frame = Viewport.Frame;

        // 現像は黒減算後にWBを掛けるので、推定も現像と同じ黒レベルで行う
        ushort blackLevel = _blackPoint;
        (double R, double G, double B) requestedGains = (_vm.WbGainR, _vm.WbGainG, _vm.WbGainB);
        WhiteBalanceGains gains;
        try
        {
            gains = await Task.Run(
                () => WhiteBalance.ComputeGrayWorld(image, frame, pattern, blackLevel));
        }
        catch (Exception)
        {
            return;
        }

        // 推定中に右パネルでBayerパターンを変えたら、ゲインは変える前のパターンで R・B として平均したもの
        // (RGGB→BGGR では R と B のゲインが入れ替わる)。表示中の画像の条件と違う推定は適用しない。
        // 推定中に WB のゲインを変えた(スポイト・スライダー)ときも、後からの変更を残して推定は捨てる
        // (以前はゲインを照合せず、後から終わった推定がスポイトで合わせたゲインを上書きした)
        if (!ReferenceEquals(image, ActiveImage) || frame != Viewport.Frame
            || pattern != ActiveFormat?.Bayer || blackLevel != _blackPoint || BayerPhaseMismatchShown
            || requestedGains != (_vm.WbGainR, _vm.WbGainG, _vm.WbGainB))
        {
            return;
        }

        _vm.WbGainG = 1.0;
        _vm.WbGainR = Math.Clamp(gains.GainR, 0.5, 4.0);
        _vm.WbGainB = Math.Clamp(gains.GainB, 0.5, 4.0);
    }

    private void OnWbPickToggleChanged(object sender, RoutedEventArgs e)
    {
        if (WbPickToggle.IsChecked == true)
        {
            if (RefuseWhiteBalanceForHdr())
            {
                WbPickToggle.IsChecked = false;
                return;
            }

            RoiToggle.IsChecked = false;
            ProfileToggle.IsChecked = false;
            Viewport.InteractionMode = ViewportInteractionMode.WhiteBalancePick;
        }
        else if (Viewport.InteractionMode == ViewportInteractionMode.WhiteBalancePick)
        {
            Viewport.InteractionMode = ViewportInteractionMode.Pan;
        }
    }

    private void OnWhiteBalancePicked(object? sender, CursorPixelEventArgs e)
    {
        if (ActiveImage is null || ActiveFormat is null
            || ActiveFormat.Bayer == BayerPattern.None || RefuseWhiteBalanceForHdr())
        {
            return;
        }

        // 表示フレーム・元画像座標(チャネル分割はタイル→元座標)で計算する
        if (!TryMapToSourceCoordinates(ActiveImage, e.X, e.Y, out int sourceX, out int sourceY))
        {
            return;
        }

        // 現像と同じ黒レベルで推定しないと、黒レベル設定時にスポイトした色が中性にならない
        WhiteBalanceGains gains = WhiteBalance.ComputeSpotGains(
            ActiveImage, Viewport.Frame, ActiveFormat.Bayer, sourceX, sourceY, _blackPoint);
        _vm.WbGainG = 1.0;
        _vm.WbGainR = Math.Clamp(gains.GainR, 0.5, 4.0);
        _vm.WbGainB = Math.Clamp(gains.GainB, 0.5, 4.0);
    }

    private bool RefuseWhiteBalanceForHdr()
    {
        if (HdrExposureMix.WhiteBalanceRefusal(BayerPhaseMismatchShown) is not { } reason)
        {
            return false;
        }

        MessageBox.Show(this, reason, "ホワイトバランス", MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
    }

    private DisplayLut BuildLut()
    {
        return DisplayLut.Create(new DisplayParameters(
            _blackPoint, _whitePoint, _vm.Gain, _vm.Gamma, _vm.Contrast));
    }

    /// <summary>raw code の黒/白レベルを16bitフルスケールの内部値へ反映する。</summary>
    private void ApplyLevelCodes(double blackCode, double whiteCode)
    {
        // 黒点・白点はそのcodeの画素と同じ値(code<<(16−N)、区間の下端)に置く。白レベルのcodeの画素が255になる
        (_blackPoint, _whitePoint) = DisplayLevels.ToPoints(
            blackCode, whiteCode, ActiveFormat?.BitDepth ?? 16);
    }

    /// <summary>
    /// 黒/白レベルの上限とコード値を、表示中の画像のビット深度で表し直す。
    /// 表示LUTの内部値(黒点・白点)は変えない。
    /// </summary>
    /// <remarks>
    /// 表示中の画像のビット深度が変わる経路(HDR合成の16bit化・元画像への復帰・
    /// ビット深度の異なる連番)で呼ぶ。上限だけ更新すると、旧ビット深度のコード値が
    /// 次のスライダー操作で新しいシフト量のまま内部値へ戻され、白点が急落する。
    /// </remarks>
    private void SyncLevelControlsToActiveBitDepth()
    {
        int bitDepth = ActiveFormat?.BitDepth ?? 16;
        (int blackCode, int whiteCode) = DisplayLevels.ToCodes(_blackPoint, _whitePoint, bitDepth);
        _updatingSliders = true;
        _vm.BlackLevelMax = DisplayLevels.MaxCode(bitDepth);
        _vm.BlackLevel = blackCode;
        _vm.WhiteLevel = whiteCode;
        _updatingSliders = false;
    }

    private void ResetDisplayParameters()
    {
        _updatingSliders = true;
        _vm.Gain = 1.0;
        _vm.Gamma = 1.0;
        _vm.Contrast = 1.0;
        _vm.BlackLevel = 0;
        _vm.WhiteLevel = _vm.BlackLevelMax;
        _updatingSliders = false;

        // 白点は最大コードの画素と同じ値(最大code<<(16−N))。65535 のままだと 8bit の 255(65280)が 254 になる
        (_blackPoint, _whitePoint) = DisplayLevels.DefaultPoints(ActiveFormat?.BitDepth ?? 16);
    }

    private void OnResetDisplayClick(object sender, RoutedEventArgs e)
    {
        ResetDisplayParameters();
        ApplyDisplayParametersToViews();
    }

    private void OnAutoContrastClick(object sender, RoutedEventArgs e)
    {
        if (_currentFormat is null)
        {
            return;
        }

        // 派生ビューへの出入りの直後など、表示中の画像のヒストグラムがまだなければ何もしない
        if (DisplayLevels.AutoLevelsFor(_histogram, ActiveFormat?.BitDepth ?? 16) is not { } levels)
        {
            return;
        }

        _updatingSliders = true;
        _vm.BlackLevel = levels.BlackCode;
        _vm.WhiteLevel = levels.WhiteCode;
        _updatingSliders = false;
        ApplyLevelCodes(levels.BlackCode, levels.WhiteCode);
        ApplyDisplayParametersToViews();
    }

    // ---- フォーマットその場変更 ----

    private void OnFmtBayerChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_updatingFormatPanel || _currentFormat is null)
        {
            return;
        }

        BayerPattern pattern = FmtBayerCombo.SelectedIndex switch
        {
            0 => BayerPattern.Rggb,
            1 => BayerPattern.Bggr,
            2 => BayerPattern.Grbg,
            3 => BayerPattern.Gbrg,
            _ => BayerPattern.None,
        };
        if (_tiffStack is not null)
        {
            _tiffStack.BayerOverride = pattern;
        }

        // ファイル連番の送りでも、次のグレーの画像ファイルへ引き継ぐ(TIFFのページ送りと同じ)
        _sequenceBayerOverride = pattern;

        // 表示中の画像へも送りと同じ規則で付ける。デコード済みのカラー画像には付けない
        // (カラー画像の表示中は右パネルで指定させないが、付けると表示はカラーのまま
        // チャネル別統計・欠陥検出・ノイズ測定が輝度へ Bayer を当てる)。指定は上で保持し、次のグレーの画像に付く
        RawFormat format = ImageFileBayer.Apply(_currentFormat, _colorImage is not null, pattern);
        if (format.Bayer == _currentFormat.Bayer)
        {
            return;
        }

        RawFormat previous = _currentFormat;
        _currentFormat = format;

        // HDR行交互のライン単位は未指定なら Bayer で決まる(なし=1・あり=2)。分割・合成は変えた後のパターンで行うので、
        // フォーマット欄のHDR要約も出し直す(出し直さないと、使わない方のライン単位を示したままになる)
        _vm.FmtHdrText = HdrFormatText.DescribePanel(_currentFormat);
        if (_derivedImage is null && _currentImage is not null)
        {
            Viewport.UpdateFormat(_currentFormat);

            // 表示モードの選択も送り・処理結果の差し替えと同じ規約でパターンにそろえる。Bayer なしでは
            // Bayerカラー・カラー現像・チャネル分割が成立しないので選択ごと Raw 表示へ戻す(残すとツールバーは
            // カラー系のままグレーで描き、同じモードを選び直しても選択が変わらず理由も出ない)。
            // 有効なパターンで Bayer 系の表示を続けるなら Bayer ピラミッドを用意する(作り済みなら何もしない)
            DisplayModeSelection.Choice display = ApplyDisplayModeToNewImage(_colorImage is not null, format);
            if (display.ViewportMode is ViewportDisplayMode.BayerColor
                or ViewportDisplayMode.ColorDevelop or ViewportDisplayMode.ChannelSplit)
            {
                _ = EnsureBayerPyramidAsync();
            }
        }

        // カーソル位置のチャネル名を新しいパターンで出し直す
        RefreshCursorReadout();

        // 欠陥検出の結果は検出したときのパターンのもの(閾値はパターンのチャネル別の統計で決まり、
        // 補正はパターンで選んだ近傍から補う)。表示中の画像のパターンが替わったら、他の差し替え経路と
        // 同じく検出元を手放し、マーカーと欠陥ウィンドウの一覧も破棄して検出し直すよう案内する。
        // 残すと前のパターンで欠陥とした正常な画素を、新しいパターンの近傍で補正できてしまう。
        // HDR表示中は表示中の画像(派生ビュー)のパターンは替わらないので、その検出結果は残す
        if (_defectSource is { } defectSource
            && !defectSource.IsCurrent(ActiveImage, Viewport.Frame, ActiveFormat?.Bayer ?? BayerPattern.None))
        {
            ClearDefectSource();
            Viewport.SetDefectMarkers(null);
            _defectWindow?.DiscardResult(DefectPixelWindow.BayerChangedNotice);
        }

        if (_correctionLabel is null && _currentPath is not null && IsRawFile(_currentPath))
        {
            RememberFileFormat(_currentPath, _currentFormat);

            // 同じサイズの記憶にこのファイルの元の形式があれば直す(F2 で開き直したときと同じく、
            // 次に同じサイズのファイルを記憶から開くときは直した Bayer で開く)。サイズは読み込んだときに取ったもの
            _formatMemory.Correct(_currentPath, _currentFileSize, previous, _currentFormat);
            _openedRawFormat = _currentFormat;
        }

        // チャネル別統計はパターンに依存するため作り直す。
        // 放置すると RGGB→BGGR で R と B を入れ替えた値を表示したままになる
        RefreshHistogram(Viewport.Roi is { PixelCount: > 0 } roi ? roi : null);

        // 射影の窓の対象の説明(チャネル名)も、Bayer なしとの切り替えでは対象(チャネル分割表示)も変わる
        RefreshProjections();
    }
}

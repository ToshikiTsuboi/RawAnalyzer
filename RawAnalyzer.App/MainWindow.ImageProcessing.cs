using System.IO;
using System.Windows;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.Views;
using RawAnalyzer.Core;

namespace RawAnalyzer.App;

public partial class MainWindow
{
    // ---- 画像演算 (ダーク減算/フラット補正) ----

    private void OnImageCalculatorClick(object sender, RoutedEventArgs e)
    {
        if (_currentImage is null || _currentFormat is null || _currentPath is null)
        {
            return;
        }

        // 読み込み中・縮小表示の作成中・他の処理の実行中は始めない(ビニング・フィルタと同じ)。
        // HDR分割・合成の計算中に始めると、ダイアログ・進捗表示の中で派生ビューが表示され、その後で
        // 元画像だけを演算結果へ差し替えてしまう。黙って無視せず理由を知らせる
        if (RejectWhileBusy("画像演算"))
        {
            return;
        }

        using BusyScope busy = EnterBusy();

        // 比較表示中・HDR表示中・カラー画像は断る(見えていない画像を差し替える、カラーを黙って失う)
        if (ImageCalculationAvailability.Refusal(_compareMode, _derivedImage is not null, _colorImage is not null)
            is { } refusal)
        {
            MessageBox.Show(this, refusal, "画像演算", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 演算は1フレーム単位なので、判定もフレーム画素数で行う
        if ((long)_currentFormat.Width * _currentFormat.Height
            > RawLoader.DefaultInMemoryPixelThreshold)
        {
            MessageBox.Show(this, "1億画素を超える画像の演算はサポートされていません。",
                "画像演算", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 参照画像は1フレームだけ読むため、期待サイズも1フレーム分。raw の参照はファイルを読んだ形式で読む
        // (ビニング・フィルタの結果の 16bit 形式で読むと下詰めNbitの参照が正規化されない)
        long expectedSize = ReferenceImage.ExpectedRawSize(
            ReferenceImage.RawReadFormat(_currentFormat, _openedRawFormat));

        // 対象名には演算するフレーム・TIFFページも示す(ビニング・フィルタと同じ。演算は表示中の1フレームだけ)
        var dialog = new ImageCalculatorDialog(
            NoiseSourceName() + (_tiffStack is null ? CalculationFrameNote(_currentImage, Viewport.Frame) : ""),
            _currentFolder, expectedSize, TargetRawFileSize())
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true || dialog.Result is null)
        {
            return;
        }

        ExecuteImageCalculation(dialog.Result);
    }

    private async void ExecuteImageCalculation(ImageCalculatorChoice choice)
    {
        using BusyScope busy = EnterBusy();
        RawImage source = _currentImage!;
        RawFormat format = _currentFormat!;
        RawFormat rawReadFormat = ReferenceImage.RawReadFormat(format, _openedRawFormat);
        SampleScaling? targetScaling = _valueScaling;
        int frame = Viewport.Frame;

        RawImage? corrected = null;
        RawImage? reference = null;
        ProgressWindow result = ProgressWindow.Run(
            this,
            $"画像演算中: {Path.GetFileName(choice.ReferencePath)}",
            (progress, ct) => Task.Run(() =>
            {
                // 読み込み段階からキャンセルを効かせる
                // (NAS等では参照の読み込みだけで数十秒かかることがある)
                // 32bit TIFF などを値域で写した参照は、対象と係数が同じときだけ使う(違えば理由を示して断る)
                reference = ReferenceImage.Load(
                    choice.ReferencePath, IsRawFile(choice.ReferencePath), rawReadFormat, targetScaling, ct);
                // 右パネルで変更したBayerは source.Format に入らないため、結果へ明示的に引き継ぐ
                corrected = ImageCalculator.Apply(
                    source, reference, choice.Operation, frame, 0, format.Bayer, progress, ct);
            }, ct));
        reference?.Dispose();

        if (result.Error is not null)
        {
            corrected?.Dispose();
            MessageBox.Show(this, $"画像演算に失敗しました: {result.Error.Message}", "画像演算",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (result.WasCanceled || corrected is null)
        {
            corrected?.Dispose();
            return;
        }

        string opLabel = choice.Operation switch
        {
            ImageOperation.Subtract => "−",
            ImageOperation.AbsoluteDifference => "|−|",
            _ => "÷",
        };

        // 結果は演算した1フレームだけになるので、どのフレーム・ページの結果かをラベル(タイトル・バッジ・
        // 保存の付随テキスト)に残す(ビニング・フィルタと同じ)
        await ApplyProcessedImageAsync(
            source, corrected,
            $"{opLabel} {Path.GetFileName(choice.ReferencePath)}{CalculationFrameNote(source, frame)}",
            valueChange: ValueMappingChange.ForCalculation(choice.Operation));
    }

    /// <summary>
    /// 演算した元画像のフレーム・TIFFページの注記(ビニング・フィルタのラベルと同じ書式)。単一フレームなら空。
    /// </summary>
    /// <param name="source">演算の元にした画像。</param>
    /// <param name="frame">演算したフレーム。</param>
    /// <returns>注記(先頭に空白を含む)。</returns>
    private string CalculationFrameNote(RawImage source, int frame) =>
        _tiffStack is not null ? TiffPageNote
            : source.FrameCount > 1 ? $" [フレーム {frame + 1}/{source.FrameCount}]" : "";

    /// <summary>
    /// 加工済み画像を現在の画像として差し替える(以後の解析・現像・保存すべてに反映)。
    /// </summary>
    /// <remarks>
    /// 欠陥検出の結果は処理前の画像のものなので、欠陥補正の結果を含めて破棄する
    /// (欠陥ウィンドウは閉じずに「未実行」へ戻す。他の差し替え経路と同じ規約)。
    /// 処理を始めた後で前提が崩れていたら(HDR分割・合成の派生ビューを表示中、元画像が処理の元では
    /// なくなった)、結果を破棄して理由を知らせ、差し替えない(ProcessedImageReplacement)。
    /// </remarks>
    /// <param name="source">処理の元にした画像(処理の開始時の元画像)。</param>
    /// <param name="processed">差し替える画像。差し替えなかったときは破棄する。</param>
    /// <param name="label">タイトル等に表示する処理ラベル。</param>
    /// <param name="color">RGBの処理結果。processedは同じ画像の輝度であること。</param>
    /// <param name="defectNotice">
    /// 欠陥ウィンドウで一覧を破棄したときに出す案内。省略時は画像が替わったことを示す。
    /// 差し替えと同じUIターンで出す(縮小表示の作成を待った後で出すと、その間に検出し直した一覧を消す)。
    /// </param>
    /// <param name="valueChange">
    /// 処理がコードと元の値の対応(32bit TIFF などの説明)に与える変化。省略時は変えない(欠陥補正など)。
    /// </param>
    /// <returns>差し替えた場合はtrue。</returns>
    private async Task<bool> ApplyProcessedImageAsync(
        RawImage source, RawImage processed, string label, ColorImage? color = null,
        string? defectNotice = null, ValueMappingChange? valueChange = null)
    {
        // 処理は実行中の操作・HDR表示の間は始めないが、前提が崩れていたら表示に触れる前に断る
        if (RejectProcessedImage(source, processed, label))
        {
            return false;
        }

        // 旧画像を読んでいる解析タスクを止めてから破棄する。描画の停止を待つ間も操作を受け付けるので、
        // 差し替えるまでは保存などを始めさせない(そのダイアログの中で対象の画像を差し替え・破棄してしまう)
        CancelAnalysis();
        using (_imageGate.BeginReplacement())
        {
            await Viewport.ClearImageAsync();
        }

        if (RejectProcessedImage(source, processed, label))
        {
            return false;
        }

        // 差し替えを確定したこのUIターンで、旧画像の縮小ピラミッド・Bayerピラミッドの生成を打ち切る
        // (ファイル連番・TIFFのページ送りと同じく、表示中の画像の世代を進める)。生成は _loadCts の
        // トークンで走り、結果は差し替え後の画像と照合して捨てられるだけなので、止めないと旧画像を
        // 破棄するまで、また破棄後も元画像を読まない縮小段が CPU とメモリを使い続ける。処理結果の
        // ピラミッドは新しいトークンで作る。上で断ったとき(差し替えないとき)は取り消さない。
        // 通常の読み込みが確定待ち(処理の完了を待つ間に開かれた)のときは進めない。_loadCts はその
        // 読み込みの中止にも使われ(読み込みはフォーマットの確定後に世代を進めて旧画像の生成を取り消し、
        // 処理の完了を待って画像を差し替える)、進めると開く要求を黙って捨ててしまう
        if (!_imageGate.IsLoadPending)
        {
            ReplaceLoadCts(new CancellationTokenSource());
        }

        // 旧画像から作られたBayerピラミッドを残すと、EnsureBayerPyramidAsyncの
        // 早期returnで補正後画像のピラミッドが作られず、縮小カラー表示が
        // 補正前の画素を出し続ける
        _mainBayerPyramid?.Dispose();
        _mainBayerPyramid = null;
        _derivedBayerPyramid?.Dispose();
        _derivedBayerPyramid = null;
        _currentImage?.Dispose();
        _currentImage = processed;
        ClearDefectSource();
        _currentFormat = processed.Format;
        _colorImage = color;
        _vm.IsColorImage = color is not null;

        // 32bit TIFF などの値の対応は、対応を変えない処理(フィルタ・平均ビニング・欠陥補正など)では引き継ぎ、
        // 変える処理(差・加算ビニング・Sobel など)では新しい対応で説明し直す。消すと 1code の値が分からなくなる
        _valueNote = _valueNote?.After(valueChange ?? ValueMappingChange.None);
        _histogram = null;
        _channelHistograms = null;
        _vm.HasRoi = false;
        ClearCursorReadout();
        // 演算結果の16bit化や寸法変更に合わせてUIを更新する。
        // 表示LUTの内部値は維持し、コード値だけ新しいビット深度へ換算する。ただし16bitにする処理(平均ビニング・
        // フィルタ)は Nbit 素材の最大 code を 65535 へ写すので、黒点・白点も同じ倍率で写す(白レベルが最大 code なら
        // 65535。写さないと白点 65520 のまま 16bit の現像で白飛びの判定がずれる)
        (_blackPoint, _whitePoint) = ProcessedImageReplacement.CarryLevels(
            _blackPoint, _whitePoint, source.Format.BitDepth, processed.Format.BitDepth);
        _updatingSliders = true;
        _vm.BlackLevelMax = (1 << processed.Format.BitDepth) - 1;
        _vm.BlackLevel = _blackPoint >> CurrentShift;
        _vm.WhiteLevel = _whitePoint >> CurrentShift;
        _updatingSliders = false;
        UpdateFormatPanel(processed.Format);
        Viewport.SetDefectMarkers(null);
        _defectWindow?.DiscardResult(defectNotice);

        _correctionLabel = _correctionLabel is null ? label : $"{_correctionLabel}, {label}";
        UpdateNoiseWindowSource();
        UpdateProcessingBadge();
        Title = $"RawAnalyzer — {Path.GetFileName(_currentPath!)} [{_correctionLabel}]";
        _vm.ImageInfoText =
            $"{processed.Width}×{processed.Height} · {processed.Format.BitDepth}bit" +
            (color is null ? " · " : " · RGB · ") +
            $"補正: {_correctionLabel}(再読込で元に戻せます)" + ValueNoteSuffix;

        Viewport.SetImage(processed, processed.Format);
        Viewport.SetColorImage(color);

        // 表示モードは処理前の選択を保つ(送りと同じ規約)。カラーの処理結果は RGB のまま表示する
        // (Raw 表示へ戻すとカラー画像のビニング・フィルタの結果がグレーで表示される)
        DisplayModeSelection.Choice display =
            ApplyDisplayModeToNewImage(color is not null, processed.Format);
        Viewport.SetLut(BuildLut());

        // 加工結果はディスク上のファイルと一致しないためシーケンス再生は無効化
        StopPlayback();
        _sequenceMode = SequenceMode.None;
        UpdateSequenceUi();

        RefreshHistogram(roi: null);

        // 開いているラインプロファイル窓は閉じずに、ヒストグラムと同じく処理結果で計算し直す
        // (同じ基準点・方向。ビニングで縮んで範囲外になったら、範囲外であることを示す)
        RefreshLineProfile();
        RefreshProjections();
        _mainPyramid = null;
        await BuildPyramidAsync(processed, _loadCts?.Token ?? CancellationToken.None);
        if (display.ComboIndex != 0 && ReferenceEquals(processed, _currentImage))
        {
            await EnsureBayerPyramidAsync();
        }

        return true;
    }

    /// <summary>
    /// 処理結果で差し替える前提が崩れていたら、結果を破棄して理由を知らせる。
    /// </summary>
    /// <param name="source">処理の元にした画像。</param>
    /// <param name="processed">差し替える予定だった画像。断ったときは破棄する。</param>
    /// <param name="label">処理ラベル。</param>
    /// <returns>断った場合はtrue。</returns>
    private bool RejectProcessedImage(RawImage source, RawImage processed, string label)
    {
        ProcessedImageReplacement.Refusal refusal = ProcessedImageReplacement.Check(
            source, _currentImage, derivedViewShown: _derivedImage is not null);
        if (refusal == ProcessedImageReplacement.Refusal.None)
        {
            return false;
        }

        processed.Dispose();
        MessageBox.Show(this, ProcessedImageReplacement.Explain(refusal, label),
            "RawAnalyzer", MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
    }

    /// <summary>
    /// 「素データではない」ことを示す常設バッジを更新する。
    /// </summary>
    /// <remarks>
    /// 補正・派生の状態を ImageInfoText に埋めていたため、保存完了メッセージ等で
    /// 上書きされて消え、加工済みデータを素データと誤認したままヒストグラムを
    /// 読む危険があった。
    /// </remarks>
    private void UpdateProcessingBadge()
    {
        if (_derivedImage is not null)
        {
            _vm.IsProcessed = true;
            _vm.ProcessingStateText = _hdrFloatImage is not null ? "⚠ HDR合成" : "⚠ HDR分割";
            _vm.ProcessingStateTooltip =
                "表示中の画像はHDR処理後の派生ビューです。統計値も派生ビューに対するものです。";
            return;
        }

        if (_correctionLabel is not null)
        {
            _vm.IsProcessed = true;
            _vm.ProcessingStateText = $"⚠ 加工済: {_correctionLabel}";
            _vm.ProcessingStateTooltip =
                $"適用済み: {_correctionLabel}{Environment.NewLine}" +
                "統計値も加工後のデータに対するものです。再読込で元に戻せます。";
            return;
        }

        _vm.IsProcessed = false;
        _vm.ProcessingStateText = "";
        _vm.ProcessingStateTooltip = "";
    }
    private async void OnBinningClick(object sender, RoutedEventArgs e) => await ProcessImageAsync(binning: true);

    private async void OnFilterClick(object sender, RoutedEventArgs e) => await ProcessImageAsync(binning: false);

    private async Task ProcessImageAsync(bool binning)
    {
        if (_currentImage is null || _currentFormat is null)
        {
            return;
        }

        // 読み込み中・縮小表示の作成中・他の処理の実行中は始めない。黙って無視せず理由を知らせる
        if (RejectWhileBusy(binning ? "ビニング" : "画像フィルタ"))
        {
            return;
        }

        if (_compareMode || _derivedImage is not null || _currentFormat.Hdr != HdrMode.None)
        {
            MessageBox.Show(this,
                "通常の単画像表示で実行してください。HDR素材は先に露光ごとに分割し、" +
                "単独の画像として開いてください。比較表示とHDR表示は直接処理できません。",
                "ビニング・フィルタ", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        using BusyScope busy = EnterBusy();
        RawImage source = _currentImage;
        ColorImage? sourceColor = _colorImage;
        BayerPattern pattern = sourceColor is null ? _currentFormat.Bayer : BayerPattern.None;
        int frame = Viewport.Frame;
        string frameNote = _tiffStack is not null ? TiffPageNote
            : source.FrameCount > 1 ? $" [フレーム {frame + 1}/{source.FrameCount}]" : "";
        var dialog = new ImageProcessingDialog(NoiseSourceName() + (_tiffStack is null ? frameNote : ""), source.Width, source.Height,
            pattern, sourceColor is not null, binning) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } choice)
        {
            return;
        }

        RawImage? processed = null;
        ColorImage? processedColor = null;
        ProgressWindow result = ProgressWindow.Run(this, $"処理中: {choice.Label}", (progress, ct) =>
        {
            IProgress<double> pixelsProgress = new PixelProgress(progress, sourceColor is null ? 1 : 0.9);
            if (sourceColor is not null)
            {
                processedColor = choice.Filter is { } filter
                    ? ImageFilters.Apply(sourceColor, filter, pixelsProgress, ct)
                    : ImageBinning.Apply(sourceColor, choice.Factor, choice.Mode, pixelsProgress, ct);
                processed = processedColor.ToLuminance(ct);
            }
            else
            {
                processed = choice.Filter is { } filter
                    ? ImageFilters.Apply(source, filter, frame, pattern, pixelsProgress, ct)
                    : ImageBinning.Apply(source, choice.Factor, choice.Mode, frame, pattern, pixelsProgress, ct);
            }

            ct.ThrowIfCancellationRequested();
            progress.Report(1);
            return Task.CompletedTask;
        });

        if (!result.Succeeded || processed is null)
        {
            processed?.Dispose();
            if (result.Error is not null)
            {
                MessageBox.Show(this, $"画像処理に失敗しました: {result.Error.Message}", "ビニング・フィルタ",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }

            return;
        }

        try
        {
            await ApplyProcessedImageAsync(source, processed, choice.Label + frameNote,
                color: processedColor,
                valueChange: ValueMappingChange.ForProcessing(choice.Filter, choice.Factor, choice.Mode));
        }
        catch (Exception ex)
        {
            // 所有権移動前だけ破棄する。適用後は表示中の結果を維持する。
            if (!ReferenceEquals(processed, _currentImage))
            {
                processed.Dispose();
            }

            MessageBox.Show(this, $"処理結果の表示更新に失敗しました: {ex.Message}", "ビニング・フィルタ",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private sealed class PixelProgress(IProgress<double> target, double scale) : IProgress<double>
    {
        public void Report(double value) => target.Report(value * scale);
    }
}

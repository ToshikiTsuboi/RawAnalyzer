using System.IO;
using System.Windows;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;

namespace RawAnalyzer.App;

public partial class MainWindow
{
    private TiffStackSource? _tiffStack;
    private int _tiffPageIndex;
    private CancellationTokenSource? _tiffPageLoadCts;

    private string TiffPageNote => _tiffStack is null ? ""
        : $" [TIFFページ {_tiffPageIndex + 1}/{_tiffStack.PageCount}]";

    private void CancelTiffPageLoad() => _tiffPageLoadCts?.Cancel();

    private async Task ShowTiffPageAsync(int index, bool refreshAnalysis)
    {
        TiffStackSource? stack = _tiffStack;
        if (stack is not { PageNavigationEnabled: true }
            || _busyDepth > 0 || _compareMode || _correctionLabel is not null)
        {
            // 実行中で送れないときは黙って戻さず、理由をステータスバーに出す
            NotifySequenceBusy();
            UpdateSequenceUi();
            return;
        }

        index = ((index % stack.PageCount) + stack.PageCount) % stack.PageCount;
        CancelTiffPageLoad();
        if (index == _tiffPageIndex)
        {
            UpdateSequenceUi();
            return;
        }

        // スライダーの新しい要求が来たら古いロードを中止する。表示済み画像は
        // 次ページが完成するまで残し、失敗・中止でもページ番号と画素を一致させる。
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_loadCts?.Token ?? CancellationToken.None);
        _tiffPageLoadCts = cts;
        int generation = _openGeneration;
        _sequenceBusy = true;
        _vm.IsLoading = true;
        _vm.LoadProgress = 0;
        var progress = new Progress<double>(p =>
        {
            if (ReferenceEquals(cts, _tiffPageLoadCts) && generation == _openGeneration)
            {
                _vm.LoadProgress = p * 100;
            }
        });
        DecodedImage? decoded = null;
        bool transferred = false;
        try
        {
            decoded = await stack.LoadPageAsync(index, cts.Token, progress);
            if (cts.IsCancellationRequested || !ReferenceEquals(stack, _tiffStack)
                || !ReferenceEquals(cts, _tiffPageLoadCts) || generation != _openGeneration
                || _busyDepth > 0 || _compareMode || _correctionLabel is not null)
            {
                return;
            }

            transferred = true;
            await ApplyTiffPageAsync(stack, decoded, index);

            // 再生中の送りでも、読み込みの間に再生が止まっていたら送った先のページで作り直す
            // (止めたときの作り直しは前のページに対して始まり、ページの入れ替えで取り消されている)
            if (SequenceNavigation.RefreshesAnalysisAfterMove(refreshAnalysis, _playTimer?.IsEnabled == true)
                && ReferenceEquals(decoded.Luminance, _currentImage))
            {
                RefreshAfterSequenceMove();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if ((!cts.IsCancellationRequested || (transferred && ReferenceEquals(decoded?.Luminance, _currentImage)))
                && ReferenceEquals(stack, _tiffStack)
                && generation == _openGeneration)
            {
                StopPlayback();
                MessageBox.Show(this, $"TIFFの{index + 1}ページ目を読み込めません: {ex.Message}",
                    "TIFFスタック", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            if (decoded is not null && !transferred)
            {
                decoded.Luminance.Dispose();
            }

            if (ReferenceEquals(cts, _tiffPageLoadCts))
            {
                _tiffPageLoadCts = null;
                _sequenceBusy = false;

                // 進捗表示は、後から始まった通常の読み込み(OpenPath)が持っていればそちらに任せる。送りのUIは
                // 世代によらず表示中のページへ戻す。通常の読み込みに取り消された・追い越された後に戻さないと、
                // 読み込みが失敗したときにスライダーが要求したページ、ラベルが前のページのまま残る
                if (generation == _openGeneration)
                {
                    _vm.IsLoading = false;
                }

                UpdateSequenceUi();
            }

            cts.Dispose();
        }
    }

    private async Task ApplyTiffPageAsync(TiffStackSource stack, DecodedImage decoded, int index)
    {
        RawImage image = decoded.Luminance;
        RawImage? oldImage = _currentImage;
        BayerPyramid? oldBayer = _mainBayerPyramid;
        bool sizeChanged = oldImage?.Width != image.Width || oldImage?.Height != image.Height;
        RawFormat format = stack.GetPageFormat(decoded);
        CancelAnalysis();
        // 読込済みページの所有権を移す時点で、元ページの縮小/Bayer計算も打ち切る。
        // 読込要求は完了済みなので、次のページ用のライフタイムへ更新できる。
        ReplaceLoadCts(new CancellationTokenSource());
        _mainPyramid = null;
        _mainBayerPyramid = null;
        _currentImage = image;
        _currentFormat = format;
        _colorImage = decoded.Color;
        _vm.IsColorImage = decoded.Color is not null;
        _valueNote = ValueMappingNote.FromFile(decoded.ValueNote, decoded.Scaling);
        _valueScaling = decoded.Scaling;
        _tiffPageIndex = index;
        _sequenceIndex = index;
        _histogram = null;
        _channelHistograms = null;
        ClearDefectSource();
        Viewport.SetDefectMarkers(null);
        _defectWindow?.DiscardResult();

        // 開いているラインプロファイル窓は閉じない。送りの後(再生中は止めたとき)に RefreshAfterSequenceMove が
        // 同じ基準点で計算し直し、寸法の違うページで範囲外になったら範囲外であることを示す
        if (sizeChanged)
        {
            ClearCursorReadout();
        }

        _updatingSliders = true;
        _vm.BlackLevelMax = (1 << format.BitDepth) - 1;
        _vm.BlackLevel = _blackPoint >> CurrentShift;
        _vm.WhiteLevel = _whitePoint >> CurrentShift;
        _updatingSliders = false;
        UpdateFormatPanel(format);

        // ReplaceImageAsyncは最初のawaitより前にビューポートの参照を交換する。
        // MainWindowの状態も同じUIターンで交換し、次の操作に半更新状態を見せない。
        Task<RawImage?> pending = Viewport.ReplaceImageAsync(image, format, color: decoded.Color);

        // 表示モードはファイル連番の送りと同じ規約でそろえる。カラーのページは RGB のまま表示し
        // (選択は Raw 表示・操作不可)、グレーのページでは成立する選択を保つ。
        // 以前はカラーのページを Raw 表示にしており、2ページ目以降や送りで戻った先頭ページがグレーになった。
        // カラー現像のまま送ったら、現像LUTもページのビット深度で作り直す(ApplyDisplayModeToNewImage)
        ApplyDisplayModeToNewImage(decoded.Color is not null, format.Bayer);
        Viewport.SetLut(BuildLut());
        Title = $"RawAnalyzer — {Path.GetFileName(_currentPath)}{TiffPageNote}";
        _vm.ImageInfoText = $"{image.Width}×{image.Height} · {format.BitDepth}bit"
            + (decoded.Color is null ? "" : " · RGB") + TiffPageNote + ValueNoteSuffix;
        UpdateNoiseWindowSource();

        // カーソル位置の画素値も送った先のページから読み直す(寸法の違うページでは上で消している)
        RefreshCursorReadout();
        try
        {
            await pending;
        }
        finally
        {
            oldBayer?.Dispose();
            oldImage?.Dispose();
        }
    }
}

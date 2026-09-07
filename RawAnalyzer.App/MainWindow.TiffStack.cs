using System.IO;
using System.Windows;
using RawAnalyzer.App.Rendering;
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
            if (refreshAnalysis && ReferenceEquals(decoded.Luminance, _currentImage))
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
                if (generation == _openGeneration)
                {
                    _vm.IsLoading = false;
                    UpdateSequenceUi();
                }
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
        BayerPattern pattern = format.Bayer;
        int mode = pattern == BayerPattern.None ? 0 : Math.Clamp(DisplayModeCombo.SelectedIndex, 0, 3);
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
        _valueNote = decoded.ValueNote;
        _tiffPageIndex = index;
        _sequenceIndex = index;
        _histogram = null;
        _channelHistograms = null;
        ClearDefectSource();
        Viewport.SetDefectMarkers(null);
        _defectWindow?.Close();
        if (sizeChanged)
        {
            _profileWindow?.Close();
            _lastCursorInside = false;
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
        DisplayModeCombo.IsEnabled = decoded.Color is null;
        DisplayModeCombo.SelectedIndex = mode;
        Viewport.SetDisplayMode(mode switch
        {
            1 => ViewportDisplayMode.BayerColor,
            2 => ViewportDisplayMode.ColorDevelop,
            3 => ViewportDisplayMode.ChannelSplit,
            _ => ViewportDisplayMode.Raw,
        });
        Viewport.SetLut(BuildLut());
        UpdateDevelopLuts();
        Title = $"RawAnalyzer — {Path.GetFileName(_currentPath)}{TiffPageNote}";
        _vm.ImageInfoText = $"{image.Width}×{image.Height} · {format.BitDepth}bit"
            + (decoded.Color is null ? "" : " · RGB") + TiffPageNote + ValueNoteSuffix;
        UpdateNoiseWindowSource();
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

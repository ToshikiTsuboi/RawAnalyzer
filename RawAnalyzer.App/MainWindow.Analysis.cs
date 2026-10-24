using System.IO;
using System.Windows;
using Microsoft.Win32;
using RawAnalyzer.App.Controls;
using RawAnalyzer.App.Rendering;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.Views;
using RawAnalyzer.Core;

namespace RawAnalyzer.App;

public partial class MainWindow
{
    private CancellationTokenSource? _analysisCts;

    private void ReplaceAnalysisCts(CancellationTokenSource? next)
    {
        CancellationTokenSource? previous = _analysisCts;
        _analysisCts = next;
        previous?.Cancel();
    }

    // ラインプロファイルはヒストグラムとは独立に走るので別のトークンで打ち切る(射影の窓は ProjectionWindowController)
    private CancellationTokenSource? _profileCts;

    private void ReplaceProfileCts(CancellationTokenSource? next)
    {
        CancellationTokenSource? previous = _profileCts;
        _profileCts = next;
        previous?.Cancel();
    }

    /// <summary>進行中の解析をキャンセルし、参照も外す。</summary>
    private void CancelAnalysis()
    {
        ReplaceAnalysisCts(null);
        ReplaceProfileCts(null);
        CancelProjectionJob();
    }
    private HistogramResult? _histogram;
    private IReadOnlyList<ChannelHistogram>? _channelHistograms;

    // _histogram の集計の対象とした画素数(間引く前)。表(CSV・コピー)に間引いたサンプル数であることを書く
    private long _histogramPopulation;
    private LineProfileWindow? _profileWindow;

    // ---- ヒストグラム・ROI解析 ----

    private async void RefreshHistogram(RegionOfInterest? roi)
    {
        if (ActiveImage is null)
        {
            return;
        }

        RawImage image = ActiveImage;
        int frame = Viewport.Frame;
        var analyzed = new AnalysisSource(image, frame);
        BayerPattern pattern = ActiveFormat?.Bayer ?? BayerPattern.None;

        // 行交互HDRの Raw 表示で Bayer の位相が一致しないときは、ファイル上の行の偶奇で振り分けると色を取り違えるので
        // チャネル別にしない(Bayer なしと同じく全画素の分布だけを出す。チェックした時点で理由を示している)
        bool byChannel = _vm.HistogramByChannel && pattern != BayerPattern.None && !BayerPhaseMismatchShown;

        // ROIは表示座標。チャネル分割表示ではタイル画像の座標なので、
        // 表示されている画素の集合へ対応づけてから集計する
        RoiAnalysisTarget target = ResolveRoiTarget(image, roi);
        if (target is UnsupportedRoiTarget unsupported)
        {
            // 画像全体など別の画素で代わりに集計すると、ROIの統計と誤読される
            ReplaceAnalysisCts(null);
            ShowUnanalyzableRoi(unsupported.Reason);
            return;
        }

        var cts = new CancellationTokenSource();
        ReplaceAnalysisCts(cts);
        RoiHistogram analysis;
        try
        {
            analysis = await Task.Run(
                () => RoiAnalysis.ComputeHistogram(
                    image, frame, target, pattern, byChannel, cts.Token),
                cts.Token);
        }
        catch (Exception ex) when (TaskRaceGuard.IsAbandoned(ex))
        {
            return; // キャンセル、または解析中に画像が差し替わった
        }

        // 解析中に画像を差し替えた・フレームを送ったなら、結果は表示中の画像・フレームのものではない
        // (フレーム送りは画像がそのままなので、画像の照合だけでは前のフレームの統計を表示してしまう)
        if (cts.IsCancellationRequested || !analyzed.IsCurrent(ActiveImage, Viewport.Frame))
        {
            return;
        }

        HistogramResult result = analysis.Histogram;
        RegionStatistics? exactStats = analysis.RoiStatistics;
        _histogram = result;
        _histogramPopulation = RoiAnalysis.PopulationCount(target, image.Width, image.Height);
        _channelHistograms = analysis.Channels;
        UpdateChannelStatsPanel();
        bool statsSampled = exactStats is { } s && s.SampleCount < RoiAnalysis.PixelCount(target);
        _vm.HistogramIsSampled = result.IsSampled || statsSampled;
        RegionStatistics stats = exactStats ?? result.Statistics;
        _vm.HistMeanSigmaText = $"{stats.Mean:F1} / {stats.Sigma:F1}";
        _vm.HistMinMaxText = $"{stats.Min} / {stats.Max}";

        HistogramMetrics metrics = ImageAnalysis.ComputeHistogramMetrics(result);
        _vm.HistMedianModeText = $"{metrics.Median} / {metrics.Mode}";
        _vm.HistPercentileText = $"{metrics.P1} / {metrics.P99}";
        _vm.HistClipText = $"{metrics.SaturatedPercent:F2}% / {metrics.ZeroPercent:F2}%";
        _vm.HistDynamicRangeText = metrics.DynamicRangeDb > 0
            ? $"{metrics.DynamicRangeDb:F1} dB"
            : "—";
        RedrawHistogram();

        if (exactStats is { } es)
        {
            _vm.RoiOverlayText = target switch
            {
                ChannelRoiTarget channel =>
                    $"ROI: {channel.Region.Width}×{channel.Region.Height} " +
                    $"({BayerHelper.GetLabel(channel.Channel)}のみ)  mean {es.Mean:F1}  σ {es.Sigma:F1}",
                SourceRoiTarget source =>
                    $"ROI: {source.Roi.Width}×{source.Roi.Height}  mean {es.Mean:F1}  σ {es.Sigma:F1}",
                _ => "",
            };
            _vm.HasRoi = true;
        }
    }

    /// <summary>
    /// 表示中のROI(表示座標)を、解析で集計する元画像の画素の集合へ対応づける。
    /// </summary>
    /// <param name="image">表示中の画像。</param>
    /// <param name="displayRoi">表示座標のROI(なければnull)。</param>
    /// <returns>集計対象。</returns>
    private RoiAnalysisTarget ResolveRoiTarget(RawImage image, RegionOfInterest? displayRoi)
    {
        // HDR分割ビューでは段(露光)をまたぐROIを断る(合成ビュー・通常表示は段がない)
        return RoiAnalysis.Resolve(
            displayRoi, Viewport.IsChannelSplitLayout, image.Width, image.Height,
            ActiveFormat?.Bayer ?? BayerPattern.None, HdrSplitSegmentWidth);
    }

    /// <summary>ROIが選ばれていて、表示中の画素へ対応づけて解析できるか。</summary>
    private bool HasAnalyzableRoi =>
        ActiveImage is { } image && Viewport.Roi is { PixelCount: > 0 } roi
        && RoiAnalysis.IsAnalyzableRoi(ResolveRoiTarget(image, roi));

    /// <summary>
    /// 解析できないROIであることを示し、ヒストグラム欄を空にする(別の画素の統計を残さない)。
    /// </summary>
    /// <param name="reason">利用者向けの理由。</param>
    private void ShowUnanalyzableRoi(string reason)
    {
        _histogram = null;
        _channelHistograms = null;
        UpdateChannelStatsPanel();
        _vm.HistogramIsSampled = false;
        _vm.HistMeanSigmaText = "— / —";
        _vm.HistMinMaxText = "— / —";
        _vm.HistMedianModeText = "— / —";
        _vm.HistPercentileText = "— / —";
        _vm.HistClipText = "— / —";
        _vm.HistDynamicRangeText = "—";
        RedrawHistogram();
        _vm.RoiOverlayText = reason;

        // 枠は残るので、解除(右クリック / Ctrl+G)できる状態にしておく
        _vm.HasRoi = true;
    }

    private void UpdateChannelStatsPanel()
    {
        if (_channelHistograms is { Count: 4 } channels)
        {
            _vm.ChRText = FormatChannelStats(channels[0].Statistics);
            _vm.ChGrText = FormatChannelStats(channels[1].Statistics);
            _vm.ChGbText = FormatChannelStats(channels[2].Statistics);
            _vm.ChBText = FormatChannelStats(channels[3].Statistics);
            _vm.ChannelStatsVisible = true;
        }
        else
        {
            _vm.ChannelStatsVisible = false;
        }
    }

    private static string FormatChannelStats(RegionStatistics stats)
    {
        return $"{stats.Mean:F1} / {stats.Sigma:F1}";
    }

    private void RedrawHistogram()
    {
        if (_histogram is null)
        {
            _vm.HistogramSource = null;
            return;
        }

        _vm.HistogramSource = _channelHistograms is { Count: 4 } channels
            ? HistogramRenderer.RenderChannels(channels, _vm.HistogramIsLog, _vm.HistogramIsCumulative)
            : HistogramRenderer.Render(_histogram.Bins, _vm.HistogramIsLog, _vm.HistogramIsCumulative);
    }

    private void OnHistogramRefreshClick(object sender, RoutedEventArgs e)
    {
        // 画像の外(余白)だけをドラッグしたROIは画素数0のまま残る。ROI変更時と同じく「ROIなし」として画像全体で
        // 集計する(そのまま渡すと「ROIが画像の範囲外です」で画像全体の統計とヒストグラムを消していた)
        RefreshHistogram(Viewport.Roi is { PixelCount: > 0 } roi ? roi : null);
    }

    private void OnViewportRoiChanged(object? sender, EventArgs e)
    {
        // 象限をまたぐなど解析できないROIでは「ROI内のみ」を選ばせない
        _noiseWindow?.SetRoiAvailability(HasAnalyzableRoi);

        // 射影の窓は ROI があれば ROI、なければ画像全体で計算し直す
        RefreshProjections();
        if (Viewport.Roi is { PixelCount: > 0 } roi)
        {
            RefreshHistogram(roi);
        }
        else
        {
            _vm.HasRoi = false;
            _vm.RoiOverlayText = "";
            RefreshHistogram(roi: null);
        }
    }

    private void OnRoiToggleChanged(object sender, RoutedEventArgs e)
    {
        if (RoiToggle.IsChecked == true)
        {
            ProfileToggle.IsChecked = false;
            WbPickToggle.IsChecked = false;
            Viewport.InteractionMode = ViewportInteractionMode.RoiSelect;
        }
        else if (Viewport.InteractionMode == ViewportInteractionMode.RoiSelect)
        {
            // モードを抜けてもROI選択自体は保持する(射影の窓・統計で使うため)。
            // 解除は右クリックメニューの「ROIを解除」から行う
            Viewport.InteractionMode = ViewportInteractionMode.Pan;
        }
    }

    private void OnProfileToggleChanged(object sender, RoutedEventArgs e)
    {
        if (ProfileToggle.IsChecked == true)
        {
            RoiToggle.IsChecked = false;
            WbPickToggle.IsChecked = false;
            Viewport.InteractionMode = ViewportInteractionMode.LineProfile;
        }
        else if (Viewport.InteractionMode == ViewportInteractionMode.LineProfile)
        {
            Viewport.InteractionMode = ViewportInteractionMode.Pan;
        }
    }

    /// <summary>
    /// 表示上のクリック座標を元画像の座標へ写像する。
    /// チャネル分割表示は R/Gr/Gb/B の2x2タイル並置なので、タイル内座標を
    /// 元のBayer座標に戻さないと、クリックした位置と異なる画素を読むことになる。
    /// </summary>
    /// <returns>写像できない座標(端数行/列)ならfalse。</returns>
    private bool TryMapToSourceCoordinates(
        RawImage image, int x, int y, out int sourceX, out int sourceY)
    {
        sourceX = x;
        sourceY = y;

        // 表示モードが分割でもBayerなしならRawとして描かれている(表示座標=元画像座標)
        if (!Viewport.IsChannelSplitLayout)
        {
            return true;
        }

        int evenW = image.Width & ~1;
        int evenH = image.Height & ~1;
        if (x >= evenW || y >= evenH)
        {
            return false;
        }

        (sourceX, sourceY) = BayerSplit.MapTiledToSource(x, y, evenW, evenH);
        return true;
    }

    private void OnProfilePointClicked(object? sender, CursorPixelEventArgs e)
    {
        if (ActiveImage is not { } image
            || !TryMapToSourceCoordinates(image, e.X, e.Y, out int sourceX, out int sourceY))
        {
            return;
        }

        _ = ShowLineProfileAsync(sourceX, sourceY, fromClick: true);
    }

    /// <summary>
    /// 表示画像を送った・差し替えた後に、開いているラインプロファイル窓を同じ基準点・方向で計算し直す。
    /// </summary>
    /// <remarks>
    /// ヒストグラム・ROI統計と同じ扱い。フレーム・ページ・ファイルの送り(再生中は止めたとき。
    /// <see cref="RefreshAfterSequenceMove"/>)と、別ファイルを開く・処理結果での差し替え・HDR表示の出入りの後に
    /// 呼ぶ。基準点は元画像の座標のまま使い、寸法の違う画像で範囲外になったら前の断面を残さず範囲外であることを示す。
    /// 窓を閉じていれば何もしない(ROI の射影は射影の窓が RefreshProjections で計算し直す)。
    /// </remarks>
    private void RefreshLineProfile()
    {
        if (_profileWindow is not { } window)
        {
            return;
        }

        (int x, int y) = window.CurrentPoint;
        _ = ShowLineProfileAsync(x, y, fromClick: false);
    }

    /// <summary>
    /// 表示中の画像・フレームで、基準点を通る断面を計算し、ラインプロファイル窓に出す。
    /// </summary>
    /// <param name="sourceX">基準点の元画像X座標。</param>
    /// <param name="sourceY">基準点の元画像Y座標。</param>
    /// <param name="fromClick">
    /// 画像上のクリックからか。クリックなら窓を開いて前面に出す。送り・差し替えの後の計算し直しでは、
    /// 計算中に窓が閉じられていたら開き直さない。
    /// </param>
    private async Task ShowLineProfileAsync(int sourceX, int sourceY, bool fromClick)
    {
        if (ActiveImage is not { } image)
        {
            return;
        }

        int frame = Viewport.Frame;
        var analyzed = new AnalysisSource(image, frame);

        // 巨大画像の列の断面は全行を読む。次のクリックや画像切替で確実に打ち切れるようにトークンを渡す
        var cts = new CancellationTokenSource();
        ReplaceProfileCts(cts);
        CancellationToken token = cts.Token;
        LineProfileData? data;
        try
        {
            data = await Task.Run(
                () => LineProfileData.Compute(image, frame, sourceX, sourceY, token), token);
        }
        catch (Exception)
        {
            return;
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        // 計算中に画像を差し替えた・フレームを送ったなら、プロファイルは表示中の画像・フレームのものではない
        // (フレーム送りは画像がそのままなので、画像の照合だけでは前のフレームの値を表示してしまう)
        if (!analyzed.IsCurrent(ActiveImage, Viewport.Frame))
        {
            return;
        }

        if (_profileWindow is null)
        {
            if (!fromClick)
            {
                return;
            }

            _profileWindow = new LineProfileWindow { Owner = this };
            _profileWindow.DirectionChanged += horizontal =>
            {
                // 基準点が範囲外で断面を出していないときは、マーカーを出さない
                if (_profileWindow is { IsOutsideImage: false } window)
                {
                    // マーカーはプロファイルと同じ元画像の列・行で渡す
                    // (表示のどこに並ぶかはビューポートが表示モードに合わせて写す)
                    (int px, int py) = window.CurrentPoint;
                    Viewport.SetProfileMarker(px, py, horizontal);
                }
            };
            _profileWindow.Closed += (_, _) =>
            {
                _profileWindow = null;
                Viewport.ClearProfileMarker();
            };
            _profileWindow.Show();
        }

        int maxCode = (1 << ActiveFormat!.BitDepth) - 1;
        if (data is null)
        {
            // 寸法の違う画像へ送った・差し替えたため、基準点が範囲外になった
            _profileWindow.ShowOutsideImage(sourceX, sourceY, image.Width, image.Height, maxCode);
            Viewport.ClearProfileMarker();
        }
        else
        {
            _profileWindow.SetProfiles(data.Row, data.Column, sourceX, sourceY, maxCode);

            // 表示座標(e.X, e.Y)で渡すと、分割⇔非分割の切替後にプロファイルと別の行・列を指す
            Viewport.SetProfileMarker(sourceX, sourceY, _profileWindow.IsHorizontal);
        }

        if (fromClick)
        {
            _profileWindow.Activate();
        }
    }

    private string? BuildHistogramTable(char separator)
    {
        return _histogram is null
            ? null
            : HistogramTools.BuildTable(_histogram.Bins, separator, _channelHistograms, _histogramPopulation);
    }

    private void OnHistogramCopyClick(object sender, RoutedEventArgs e)
    {
        string? table = BuildHistogramTable('\t');
        if (table is not null)
        {
            _vm.ImageInfoText = ClipboardHelper.TrySetText(table)
                ? "ヒストグラムをクリップボードへコピーしました"
                : "クリップボードを使用できませんでした(他のアプリが使用中の可能性があります)";
        }
    }

    private void OnHistogramSaveCsvClick(object sender, RoutedEventArgs e)
    {
        string? table = BuildHistogramTable(',');
        if (table is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = Path.GetFileNameWithoutExtension(_currentPath ?? "image") + "_hist.csv",
        };
        if (dialog.ShowDialog(this) == true
            && ClipboardHelper.WriteTextOrWarn(this, dialog.FileName, table, "ヒストグラム保存"))
        {
            _vm.ImageInfoText = $"保存完了: {Path.GetFileName(dialog.FileName)}";
        }
    }
}

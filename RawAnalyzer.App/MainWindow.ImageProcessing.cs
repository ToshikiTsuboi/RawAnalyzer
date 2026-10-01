using System.Windows;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.Views;
using RawAnalyzer.Core;

namespace RawAnalyzer.App;

public partial class MainWindow
{
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

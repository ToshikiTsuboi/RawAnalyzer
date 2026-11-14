using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Views;

public partial class RawImportDialog
{
    private const int PreviewLimit = 256;
    // 閉じたダイアログの SMB オープンが戻らない間も、後から開いたダイアログでオープンを積み増さない。
    private static readonly SemaphoreSlim PreviewReadSlot = new(1, 1);

    private DispatcherTimer? _previewDelay;
    private CancellationTokenSource? _previewRunning;
    private RawFormat? _previewRequested;
    private bool _previewReady;

    internal Func<string, RawFormat, int, int, CancellationToken, RawPreview> ReadPreview { get; set; } =
        RawPreviewReader.ReadDecimated;

    private void OnPreviewLoaded(object sender, RoutedEventArgs e)
    {
        if (_previewDelay is not null)
        {
            return;
        }

        _previewDelay = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };
        _previewDelay.Tick += OnPreviewDelayElapsed;
        RawFormat? format = TryBuildFormat(out string error);
        RequestPreview(format, error);
    }

    private void OnPreviewClosed(object? sender, EventArgs e)
    {
        if (_previewDelay is { } delay)
        {
            delay.Stop();
            delay.Tick -= OnPreviewDelayElapsed;
            _previewDelay = null;
        }
        _previewRunning?.Cancel();
    }

    private void RequestPreview(RawFormat? format, string error)
    {
        if (_previewDelay is not { } delay)
        {
            return;
        }
        if (format is not null && format == _previewRequested)
        {
            return;
        }

        _previewRequested = format;
        _previewReady = false;
        delay.Stop();
        _previewRunning?.Cancel();
        PreviewImage.Opacity = 0.4;
        PreviewNote.Text = format is null ? error : "プレビューを更新中…";
        if (format is not null)
        {
            delay.Start();
        }
    }

    private void OnPreviewDelayElapsed(object? sender, EventArgs e)
    {
        _previewDelay!.Stop();
        _previewReady = true;
        StartPreviewIfReady();
    }

    private void StartPreviewIfReady()
    {
        if (_previewDelay is not null && _previewReady && _previewRunning is null && _previewRequested is { } format)
        {
            _previewReady = false;
            _ = UpdatePreviewAsync(format);
        }
    }

    private async Task UpdatePreviewAsync(RawFormat format)
    {
        using var cancellation = new CancellationTokenSource();
        _previewRunning = cancellation;
        CancellationToken token = cancellation.Token;
        try
        {
            await PreviewReadSlot.WaitAsync(token);
            (BitmapSource? Bitmap, string Note) preview;
            try
            {
                var read = ReadPreview;
                preview = await Task.Run(() => CreatePreview(
                    read(_filePath, format, PreviewLimit, PreviewLimit, token), format, token), token);
            }
            finally
            {
                PreviewReadSlot.Release();
            }

            if (!token.IsCancellationRequested)
            {
                PreviewImage.Source = preview.Bitmap;
                PreviewImage.Opacity = 1;
                PreviewNote.Text = preview.Note;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                   or System.Security.SecurityException)
        {
            if (!token.IsCancellationRequested)
            {
                PreviewImage.Source = null;
                PreviewNote.Text = $"プレビューを読み取れませんでした。{ex.Message}";
            }
        }
        finally
        {
            _previewRunning = null;
            StartPreviewIfReady();
        }
    }

    private static (BitmapSource? Bitmap, string Note) CreatePreview(
        RawPreview preview, RawFormat format, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (preview.RowsRead == 0)
        {
            return (null, "ファイルが短く、プレビュー用の行を読み取れませんでした。");
        }

        int shift = 16 - format.BitDepth;
        var bins = new long[1 << format.BitDepth];
        foreach (ushort pixel in preview.Pixels)
        {
            bins[pixel >> shift]++;
        }
        AutoLevels levels = HistogramTools.ComputeAutoLevels(bins) ?? new AutoLevels(0, bins.Length - 1);
        var (black, white) = DisplayLevels.ToPoints(levels.BlackCode, levels.WhiteCode, format.BitDepth);
        DisplayLut lut = DisplayLut.Create(new DisplayParameters(black, white));
        token.ThrowIfCancellationRequested();

        // 読めなかった行の透明部分から下地が見える。短いファイルも予定の縦横比を保つ。
        var pixels = new byte[preview.Width * preview.Height * 4];
        for (int i = 0; i < preview.Pixels.Length; i++)
        {
            byte gray = lut.Map(preview.Pixels[i]);
            pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = gray;
            pixels[i * 4 + 3] = 255;
        }
        BitmapSource bitmap = BitmapSource.Create(preview.Width, preview.Height, 96, 96,
            PixelFormats.Bgra32, null, pixels, preview.Width * 4);
        bitmap.Freeze();
        string sampling = preview.Step == 1 ? "間引きなし" : $"間引き {preview.Step} px";
        string note = $"グレー表示 · {sampling}";
        if (preview.RowsRead < preview.Height)
        {
            note += $"\nファイルが短いため、{preview.RowsRead}/{preview.Height} 行を表示しています。";
        }
        return (bitmap, note);
    }
}

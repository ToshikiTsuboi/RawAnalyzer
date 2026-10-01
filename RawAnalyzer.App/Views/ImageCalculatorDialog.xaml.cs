using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Views;

/// <summary>画像演算ダイアログの選択結果。</summary>
/// <param name="Operation">演算の種類。</param>
/// <param name="ReferencePath">参照画像のパス。</param>
public sealed record ImageCalculatorChoice(ImageOperation Operation, string ReferencePath);

/// <summary>
/// 画像演算(ダーク減算/フラット補正/差分)の設定ダイアログ。
/// </summary>
public partial class ImageCalculatorDialog : Window
{
    private readonly long _expectedSize;

    // 参照パスのサイズ照合は入力が止まってから裏で行う(File.Exists・FileInfo は NAS の UNC パスで
    // 名前解決・SMB のタイムアウトまで止まるため、打鍵ごとに UI スレッドで呼ばない)。
    // 世代は入力のたびに進め、古い入力の照合結果を捨てる
    private readonly DispatcherTimer _referenceCheckTimer;
    private int _referenceCheckGeneration;

    /// <summary>ダイアログを生成する。</summary>
    /// <param name="sourceName">対象画像(A)の表示名。</param>
    /// <param name="initialFolder">参照ファイル選択の初期フォルダ。</param>
    /// <param name="expectedSize">参照ファイルに期待するバイト数(サイズ検証用)。</param>
    public ImageCalculatorDialog(string sourceName, string? initialFolder, long expectedSize)
    {
        InitializeComponent();
        _expectedSize = expectedSize;
        SourceText.Text = $"対象 A: {sourceName}";
        Tag = initialFolder;
        OperationCombo.SelectedIndex = 0;
        _referenceCheckTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(300),
        };
        _referenceCheckTimer.Tick += OnReferenceCheckTick;
        Closed += (_, _) =>
        {
            _referenceCheckTimer.Stop();
            _referenceCheckGeneration++;
        };
    }

    /// <summary>「実行」で確定された選択。</summary>
    public ImageCalculatorChoice? Result { get; private set; }

    private ImageOperation SelectedOperation => OperationCombo.SelectedIndex switch
    {
        1 => ImageOperation.AbsoluteDifference,
        2 => ImageOperation.DivideGain,
        _ => ImageOperation.Subtract,
    };

    private void OnOperationChanged(object sender, RoutedEventArgs e)
    {
        if (OperationHint is null)
        {
            return;
        }

        OperationHint.Text = SelectedOperation switch
        {
            ImageOperation.Subtract =>
                "B にダークフレームを指定すると固定パターン成分を除去できます (負値は0クランプ)",
            ImageOperation.AbsoluteDifference =>
                "補正前後や2条件の差分を可視化します",
            _ =>
                "B にフラットフィールドを指定するとシェーディング/PRNUを補正できます " +
                "(ダーク減算済み推奨)",
        };
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Raw (*.raw;*.bin)|*.raw;*.bin|すべてのファイル (*.*)|*.*",
        };
        if (Tag is string folder && Directory.Exists(folder))
        {
            dialog.InitialDirectory = folder;
        }

        if (dialog.ShowDialog(this) == true)
        {
            ReferenceBox.Text = dialog.FileName;
        }
    }

    private void OnReferenceChanged(object sender, RoutedEventArgs e)
    {
        if (NoteText is null || _referenceCheckTimer is null)
        {
            return;
        }

        // 前の入力の警告は消し、入力が止まってから照合し直す
        _referenceCheckGeneration++;
        NoteText.Visibility = Visibility.Collapsed;
        _referenceCheckTimer.Stop();
        if (_expectedSize > 0 && IsRawPath(ReferenceBox.Text.Trim()))
        {
            _referenceCheckTimer.Start();
        }
    }

    private async void OnReferenceCheckTick(object? sender, EventArgs e)
    {
        _referenceCheckTimer.Stop();
        int generation = _referenceCheckGeneration;
        string path = ReferenceBox.Text.Trim();
        long expectedSize = _expectedSize;
        string? note = await Task.Run(() => DescribeSizeMismatch(path, expectedSize));
        if (generation != _referenceCheckGeneration)
        {
            return; // 照合中に入力が変わった・閉じた
        }

        NoteText.Text = note ?? "";
        NoteText.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>参照ファイルのサイズが期待と違えば警告文を返す(ファイルシステムに触れるので UI スレッドで呼ばない)。</summary>
    private static string? DescribeSizeMismatch(string path, long expectedSize)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        long size = SafeLength(path);

        // raw参照は対象画像のフォーマットで強制解釈されるため、
        // 不足だけでなく超過も警告する(超過分は先頭だけ読まれ無警告で通ってしまう)
        return size >= 0 && size != expectedSize
            ? $"⚠ ファイルサイズが一致しません ({size:N0} / 期待 {expectedSize:N0} バイト)。" +
              "対象画像のフォーマットで解釈されるため結果が正しくない可能性があります"
            : null;
    }

    private static bool IsRawPath(string path)
    {
        string extension = Path.GetExtension(path);
        return string.Equals(extension, ".raw", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".bin", StringComparison.OrdinalIgnoreCase);
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private void OnRunClick(object sender, RoutedEventArgs e)
    {
        string path = ReferenceBox.Text.Trim();
        if (!File.Exists(path))
        {
            MessageBox.Show(this, "参照画像ファイルを指定してください。", "画像演算",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result = new ImageCalculatorChoice(SelectedOperation, path);
        DialogResult = true;
    }
}

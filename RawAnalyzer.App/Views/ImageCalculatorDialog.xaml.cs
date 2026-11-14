using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using RawAnalyzer.App.Services;
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
    private readonly long _targetFileSize;

    // 参照パスのサイズ照合は入力が止まってから裏で行う(File.Exists・FileInfo は NAS の UNC パスで
    // 名前解決・SMB のタイムアウトまで止まるため、打鍵ごとに UI スレッドで呼ばない)。
    // 世代は入力のたびに進め、古い入力の照合結果を捨てる
    private readonly DispatcherTimer _referenceCheckTimer;
    private int _referenceCheckGeneration;
    private CancellationTokenSource? _referenceCheck;
    private bool _closed;
    internal Func<IReadOnlyList<string>>? LoadRecent { get; init; }

    // 「参照…」の初期フォルダを確かめている・選択ダイアログを出している間
    private bool _browsing;

    /// <summary>ダイアログを生成する。</summary>
    /// <param name="sourceName">対象画像(A)の表示名。</param>
    /// <param name="initialFolder">参照ファイル選択の初期フォルダ。</param>
    /// <param name="expectedSize">参照ファイルに期待するバイト数(サイズ検証用)。</param>
    /// <param name="targetFileSize">
    /// 対象(A)の raw ファイルのバイト数(同じ形のファイルも警告しない)。raw でなければ0。
    /// </param>
    /// <param name="sourceDescription">表示中の対象画像の形式。</param>
    /// <param name="sourceBytes">読み込み済みの対象ファイルのサイズ。不明なら0。</param>
    /// <param name="folderFiles">ファイルブラウザーで列挙済みのパス。</param>
    public ImageCalculatorDialog(
        string sourceName, string? initialFolder, long expectedSize, long targetFileSize = 0,
        string? sourceDescription = null, long sourceBytes = 0, IReadOnlyList<string>? folderFiles = null)
    {
        InitializeComponent();
        _expectedSize = expectedSize;
        _targetFileSize = targetFileSize;
        SourceText.Text = sourceName;
        SourceText.ToolTip = sourceName;
        SourceDetails.Text = string.Join("\n", new[] { sourceDescription, sourceBytes > 0 ? $"{sourceBytes:N0} バイト" : null }
            .OfType<string>());
        SetFileChoices(RecentFiles, "最近使ったファイル", []);
        SetFileChoices(FolderFiles, "現在のフォルダのファイル", folderFiles ?? []);
        Tag = initialFolder;
        OperationCombo.SelectedIndex = 0;
        _referenceCheckTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(300),
        };
        _referenceCheckTimer.Tick += OnReferenceCheckTick;
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _closed = true;
            _referenceCheckTimer.Stop();
            _referenceCheck?.Cancel();
            _referenceCheckGeneration++;
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (LoadRecent is { } load)
        {
            IReadOnlyList<string> paths = await Task.Run(load);
            if (!_closed)
                SetFileChoices(RecentFiles, "最近使ったファイル", paths);
        }
    }

    private static void SetFileChoices(ComboBox box, string title, IReadOnlyList<string> paths)
    {
        // Keep the displayed caption and paths as strings so opening the menu requires no file I/O.
        box.ItemsSource = paths.Prepend(title).ToArray();
        box.SelectedIndex = 0;
        box.IsEnabled = paths.Count > 0;
    }

    private void OnFileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedIndex: > 0, SelectedItem: string path } box)
        {
            ReferenceBox.Text = path;
            box.SelectedIndex = 0;
        }
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

    private async void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        if (_browsing)
        {
            return;
        }

        // 初期フォルダ(表示中の画像のフォルダ)の実在は UI スレッドの外で確かめる(DialogInitialFolder)。
        // 確かめる間に重ねて押されても選択ダイアログを重ねて出さず、閉じられたら出さない
        _browsing = true;
        try
        {
            string? folder = await DialogInitialFolder.ConfirmAsync(Tag as string, DialogInitialFolder.Timeout);
            if (!IsVisible)
            {
                return;
            }

            var dialog = new OpenFileDialog
            {
                Filter = "Raw (*.raw;*.bin)|*.raw;*.bin|すべてのファイル (*.*)|*.*",
            };
            if (folder is not null)
            {
                dialog.InitialDirectory = folder;
            }

            if (dialog.ShowDialog(this) == true)
            {
                ReferenceBox.Text = dialog.FileName;
            }
        }
        finally
        {
            _browsing = false;
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
        _referenceCheck?.Cancel();
        NoteText.Visibility = Visibility.Collapsed;
        _referenceCheckTimer.Stop();
        string path = ReferenceBox.Text.Trim();
        ReferenceName.Text = Path.GetFileName(path);
        ReferenceName.ToolTip = path;
        ReferenceDetails.Text = path.Length > 0 ? "サイズを照合中…" : "—";
        if (path.Length > 0)
        {
            _referenceCheckTimer.Start();
        }
    }

    private async void OnReferenceCheckTick(object? sender, EventArgs e)
    {
        _referenceCheckTimer.Stop();
        if (_referenceCheck is not null)
            return;

        int generation = _referenceCheckGeneration;
        string path = ReferenceBox.Text.Trim();
        using var check = new CancellationTokenSource();
        _referenceCheck = check;
        try
        {
            long size = await Task.Run(() => File.Exists(path) ? SafeLength(path) : -1, check.Token);
            if (generation != _referenceCheckGeneration)
                return;

            string? note = size >= 0 && _expectedSize > 0 && IsRawPath(path)
                ? DescribeSizeMismatch(size, _expectedSize, _targetFileSize) : null;
            ReferenceDetails.Text = size < 0 ? "ファイルサイズを取得できません"
                : $"{size:N0} バイト\n" + (!IsRawPath(path) || _expectedSize <= 0
                    ? "形式は読み込み時に確認"
                    : note is null ? "A の参照 raw 形式で読めるサイズ" : "A の参照 raw 形式の想定サイズと不一致");
            NoteText.Text = note ?? "";
            NoteText.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _referenceCheck = null;
            if (!_closed && generation != _referenceCheckGeneration && ReferenceBox.Text.Trim().Length > 0)
                _referenceCheckTimer.Start();
        }
    }

    /// <summary>取得済みの参照ファイルのサイズが期待と違えば警告文を返す。</summary>
    private static string? DescribeSizeMismatch(long size, long expectedSize, long targetFileSize)
    {
        // raw参照は対象画像のフォーマットで強制解釈されるため、
        // 不足だけでなく超過も警告する(超過分は先頭だけ読まれ無警告で通ってしまう)。
        // ただし対象のファイルと同じ大きさ(同じ形の連写ファイル)は先頭フレームを正しく読めるので警告しない
        return !ReferenceImage.IsExpectedRawSize(size, expectedSize, targetFileSize)
            ? $"⚠ ファイルサイズが一致しません ({size:N0} / 期待 " +
              $"{ReferenceImage.DescribeExpectedRawSize(expectedSize, targetFileSize)})。" +
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

    private async void OnRunClick(object sender, RoutedEventArgs e)
    {
        // 参照ファイルの実在は UI スレッドの外で確かめる(切断した NAS 上のパスでは File.Exists がタイムアウトまで
        // 戻らない)。確かめる間は「実行」を受け付けず、閉じられた・参照パスが変わったら確定しない(押し直せる)
        string path = ReferenceBox.Text.Trim();
        RunButton.IsEnabled = false;
        bool exists;
        try
        {
            exists = await Task.Run(() => File.Exists(path));
        }
        finally
        {
            RunButton.IsEnabled = true;
        }

        if (!IsVisible || ReferenceBox.Text.Trim() != path)
        {
            return;
        }

        if (!exists)
        {
            AppMessage.Show(this, "参照画像ファイルを指定してください。", "画像演算",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result = new ImageCalculatorChoice(SelectedOperation, path);
        DialogResult = true;
    }
}

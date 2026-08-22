using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace RawAnalyzer.App.Views;

/// <summary>バッチ出力の形式。</summary>
public enum BatchFormat
{
    /// <summary>PNG 8bit(現像/LUT焼き込み)。</summary>
    Png8,

    /// <summary>JPEG 8bit(現像/LUT焼き込み)。</summary>
    Jpeg8,

    /// <summary>TIFF 16bitグレー(raw値そのまま)。</summary>
    Tiff16,

    /// <summary>MJPEG AVI動画(現像/LUT焼き込み)。</summary>
    AviMjpeg,

    /// <summary>MP4動画(H.264、現像/LUT焼き込み)。</summary>
    Mp4H264,
}

/// <summary>バッチ書き出しの選択結果。</summary>
/// <param name="Format">出力形式。</param>
/// <param name="Fps">動画のフレームレート。</param>
/// <param name="OutputFolder">出力先フォルダ。</param>
/// <param name="ApplyDisplayLut">
/// 表示調整(黒/白点・ゲイン・ガンマ・コントラスト)を焼き込むか。
/// falseでもWB・カラーマトリクス・デモザイクの現像段は適用される。
/// </param>
public sealed record BatchChoice(
    BatchFormat Format, int Fps, string OutputFolder, bool ApplyDisplayLut);

/// <summary>
/// フォルダ内ファイルのバッチ現像/動画書き出し設定ダイアログ。
/// </summary>
public partial class BatchExportDialog : Window
{
    private static readonly int[] FpsValues = { 5, 10, 15, 24, 30 };

    /// <summary>ダイアログを生成する。</summary>
    /// <param name="targetCount">対象ファイル数。</param>
    /// <param name="defaultOutputFolder">出力先の初期値。</param>
    /// <param name="rawTargets">対象がrawか(falseならTIFF等の連番画像)。</param>
    public BatchExportDialog(int targetCount, string defaultOutputFolder, bool rawTargets = true)
    {
        InitializeComponent();
        TargetInfoText.Text = rawTargets
            ? $"対象: フォーマットが一致するrawファイル {targetCount} 件"
            : $"対象: 連番の画像ファイル {targetCount} 件";
        if (!rawTargets)
        {
            // カラー画像は既にRGBなので、現像段(WB/マトリクス/デモザイク)は適用されない
            ProcessingNoteText.Text =
                "※ カラー画像は表示LUT(黒/白点・ゲイン・ガンマ・コントラスト)のみ焼き込みます。" +
                "TIFF 16bitグレーはカラーの場合、輝度(BT.601)を出力します。";
            ProcessingNoteText.Visibility = Visibility.Visible;
        }

        OutputFolderBox.Text = defaultOutputFolder;
        FormatCombo.SelectedIndex = 0;
    }

    /// <summary>「実行」で確定された選択。</summary>
    public BatchChoice? Result { get; private set; }

    private void OnFormatChanged(object sender, RoutedEventArgs e)
    {
        if (FpsPanel is not null)
        {
            FpsPanel.Visibility = FormatCombo.SelectedIndex is 3 or 4
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (DisplayLutCheck is not null)
        {
            // TIFF16はraw値そのままの出力なので表示調整の選択自体がない
            DisplayLutCheck.Visibility = FormatCombo.SelectedIndex == 2
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog();
        if (Directory.Exists(OutputFolderBox.Text))
        {
            dialog.InitialDirectory = OutputFolderBox.Text;
        }

        if (dialog.ShowDialog(this) == true)
        {
            OutputFolderBox.Text = dialog.FolderName;
        }
    }

    private void OnRunClick(object sender, RoutedEventArgs e)
    {
        string folder = OutputFolderBox.Text.Trim();
        if (string.IsNullOrEmpty(folder))
        {
            MessageBox.Show(this, "出力先フォルダを指定してください。", "バッチ書き出し",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        BatchFormat format = FormatCombo.SelectedIndex switch
        {
            1 => BatchFormat.Jpeg8,
            2 => BatchFormat.Tiff16,
            3 => BatchFormat.AviMjpeg,
            4 => BatchFormat.Mp4H264,
            _ => BatchFormat.Png8,
        };
        int fps = FpsValues[Math.Clamp(FpsCombo.SelectedIndex, 0, FpsValues.Length - 1)];
        Result = new BatchChoice(
            format, fps, folder, DisplayLutCheck.IsChecked == true);
        DialogResult = true;
    }
}

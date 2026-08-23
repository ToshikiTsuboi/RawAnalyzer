using System.IO;
using System.Windows;
using Microsoft.Win32;

using RawAnalyzer.App.Services;

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

/// <summary>動画書き出しの品質(圧縮の強さ)。</summary>
public enum VideoQuality
{
    /// <summary>標準。容量は小さいが、ノイズの多い素材ではブロックノイズが出やすい。</summary>
    Standard,

    /// <summary>高。</summary>
    High,

    /// <summary>最高。</summary>
    Highest,

    /// <summary>ほぼ無劣化。容量は大きい。</summary>
    NearLossless,
}

/// <summary>
/// 品質設定を各コーデックのパラメータへ対応づける。
/// </summary>
/// <remarks>
/// H.264のビットレートは画素あたりのビット数で決める。センサ評価のraw素材は
/// ノイズ(高周波成分)が多く、一般的な実写より多くのビットを必要とする。
/// </remarks>
public static class VideoQualitySettings
{
    /// <summary>
    /// H.264エンコーダへ渡す品質(0〜100)。
    /// </summary>
    /// <remarks>
    /// レート制御を品質基準にしているので、実際のビットレートは内容の
    /// 圧縮しにくさで決まる(ノイズが多いほど伸びる)。
    /// </remarks>
    /// <param name="quality">品質。</param>
    /// <returns>0〜100のエンコーダ品質。</returns>
    public static int EncoderQuality(VideoQuality quality)
    {
        return quality switch
        {
            VideoQuality.Standard => 60,
            VideoQuality.High => 75,
            VideoQuality.Highest => 88,
            _ => 98,
        };
    }

    /// <summary>
    /// 品質モードが使えない環境向けの平均ビットレート目安[bit/画素]。
    /// </summary>
    /// <param name="quality">品質。</param>
    /// <returns>画素あたりのビット数。</returns>
    public static double BitsPerPixel(VideoQuality quality)
    {
        return quality switch
        {
            VideoQuality.Standard => 0.15,
            VideoQuality.High => 0.35,
            VideoQuality.Highest => 0.70,
            _ => 1.50,
        };
    }

    /// <summary>MJPEG(AVI)のJPEG品質。</summary>
    /// <param name="quality">品質。</param>
    /// <returns>1〜100のJPEG品質。</returns>
    public static int JpegQuality(VideoQuality quality)
    {
        return quality switch
        {
            VideoQuality.Standard => 85,
            VideoQuality.High => 92,
            VideoQuality.Highest => 96,
            _ => 99,
        };
    }
}

/// <summary>バッチ書き出しの選択結果。</summary>
/// <param name="Format">出力形式。</param>
/// <param name="Fps">動画のフレームレート。</param>
/// <param name="OutputFolder">出力先フォルダ。</param>
/// <param name="ApplyDisplayLut">
/// 表示調整(黒/白点・ゲイン・ガンマ・コントラスト)を焼き込むか。
/// falseでもWB・カラーマトリクス・デモザイクの現像段は適用される。
/// </param>
/// <param name="Quality">動画の品質(静止画形式では未使用)。</param>
public sealed record BatchChoice(
    BatchFormat Format,
    int Fps,
    string OutputFolder,
    bool ApplyDisplayLut,
    VideoQuality Quality = VideoQuality.High);

/// <summary>
/// フォルダ内ファイルのバッチ現像/動画書き出し設定ダイアログ。
/// </summary>
public partial class BatchExportDialog : Window
{
    private const int DefaultFps = 15;

    private readonly int _frameWidth;
    private readonly int _frameHeight;

    /// <summary>ダイアログを生成する。</summary>
    /// <param name="targetCount">対象ファイル数。</param>
    /// <param name="defaultOutputFolder">出力先の初期値。</param>
    /// <param name="rawTargets">対象がrawか(falseならTIFF等の連番画像)。</param>
    /// <param name="frameWidth">1フレームの幅(ビットレート目安の表示用。0なら表示しない)。</param>
    /// <param name="frameHeight">1フレームの高さ。</param>
    public BatchExportDialog(
        int targetCount,
        string defaultOutputFolder,
        bool rawTargets = true,
        int frameWidth = 0,
        int frameHeight = 0)
    {
        InitializeComponent();
        _frameWidth = frameWidth;
        _frameHeight = frameHeight;
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
        UpdateQualityNote();
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
            UpdateQualityNote();
        }

        if (DisplayLutCheck is not null)
        {
            // TIFF16はraw値そのままの出力なので表示調整の選択自体がない
            DisplayLutCheck.Visibility = FormatCombo.SelectedIndex == 2
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
    }

    private VideoQuality SelectedQuality => QualityCombo.SelectedIndex switch
    {
        0 => VideoQuality.Standard,
        2 => VideoQuality.Highest,
        3 => VideoQuality.NearLossless,
        _ => VideoQuality.High,
    };

    private void OnVideoSettingChanged(object sender, RoutedEventArgs e)
    {
        UpdateQualityNote();
    }

    /// <summary>選んだ品質で何が起きるか(推定ビットレート等)を書き添える。</summary>
    private void UpdateQualityNote()
    {
        if (QualityNoteText is null || FpsCombo is null || QualityCombo is null)
        {
            return; // 初期化途中
        }

        VideoQuality quality = SelectedQuality;
        if (FormatCombo.SelectedIndex == 4)
        {
            double bpp = VideoQualitySettings.BitsPerPixel(quality);
            string estimate = _frameWidth > 0 && _frameHeight > 0
                ? $"(目安 約 {(long)_frameWidth * _frameHeight
                    * FpsInput.ParseInteger(FpsCombo.Text, DefaultFps) * bpp / 1e6:F0} Mbps)"
                : "";
            QualityNoteText.Text =
                $"H.264 品質 {VideoQualitySettings.EncoderQuality(quality)}/100{estimate}。" +
                "ノイズの多いraw素材は圧縮しにくいため、実際のビットレートは目安より増減します。" +
                "色差は4:2:0へ間引かれるので、色ノイズの評価にはTIFF/PNG連番を使ってください。";
        }
        else
        {
            QualityNoteText.Text =
                $"MJPEG: JPEG品質 {VideoQualitySettings.JpegQuality(quality)}。" +
                "フレーム間圧縮をしないので、H.264よりブロックノイズは出にくい代わりに容量が増えます。";
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
        Result = new BatchChoice(
            format,
            FpsInput.ParseInteger(FpsCombo.Text, DefaultFps),
            folder,
            DisplayLutCheck.IsChecked == true,
            SelectedQuality);
        DialogResult = true;
    }
}

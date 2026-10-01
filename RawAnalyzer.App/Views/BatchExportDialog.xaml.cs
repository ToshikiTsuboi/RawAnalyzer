using System.IO;
using System.Windows;
using Microsoft.Win32;

using RawAnalyzer.App.Controls;
using RawAnalyzer.App.Services;

namespace RawAnalyzer.App.Views;

/// <summary>バッチ出力の形式。</summary>
public enum BatchFormat
{
    /// <summary>PNG 8bit(現像/LUT焼き込み)。</summary>
    Png8,

    /// <summary>JPEG 8bit(現像/LUT焼き込み)。</summary>
    Jpeg8,

    /// <summary>TIFF 16bitグレー(無処理。画素値は内部表現の16bitフルスケールで、Nbit の raw は code&lt;&lt;(16−N))。</summary>
    Tiff16,

    /// <summary>TIFF 16bitグレー(無処理。画素値は raw の code のまま下詰め、BitsPerSample は16)。</summary>
    Tiff16Code,

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

    /// <summary>出力形式の選択肢(FormatCombo の並び)。</summary>
    private static readonly BatchFormat[] FormatChoices =
    {
        BatchFormat.Png8, BatchFormat.Jpeg8, BatchFormat.Tiff16, BatchFormat.Tiff16Code,
        BatchFormat.AviMjpeg, BatchFormat.Mp4H264,
    };

    private readonly int _frameWidth;
    private readonly int _frameHeight;
    private readonly string _sourceFolder;

    // 「参照…」の始めるフォルダを確かめている・選択ダイアログを出している間
    private bool _browsing;

    /// <summary>ダイアログを生成する。</summary>
    /// <param name="targetCount">対象ファイル数。</param>
    /// <param name="defaultOutputFolder">出力先の初期値。</param>
    /// <param name="rawTargets">対象がrawか(falseならTIFF等の連番画像)。</param>
    /// <param name="frameWidth">1フレームの幅(ビットレート目安の表示用。0なら表示しない)。</param>
    /// <param name="frameHeight">1フレームの高さ。</param>
    /// <param name="tiffPageCount">単一TIFFスタック内のページ数。通常は1。</param>
    /// <param name="sourceFolder">
    /// 出力先の相対パスの基準にする元画像のフォルダ。null なら出力先の初期値の親フォルダ。
    /// </param>
    public BatchExportDialog(
        int targetCount,
        string defaultOutputFolder,
        bool rawTargets = true,
        int frameWidth = 0,
        int frameHeight = 0,
        int tiffPageCount = 1,
        string? sourceFolder = null)
    {
        InitializeComponent();
        _frameWidth = frameWidth;
        _frameHeight = frameHeight;
        _sourceFolder = sourceFolder ?? Path.GetDirectoryName(defaultOutputFolder) ?? "";
        TargetInfoText.Text = tiffPageCount > 1
            ? $"対象: TIFFスタック 1 件・全 {tiffPageCount} ページ"
            : rawTargets
            ? $"対象: フォーマットが一致するrawファイル {targetCount} 件"
            : $"対象: 連番の画像ファイル {targetCount} 件";
        if (!rawTargets)
        {
            // カラー画像は既にRGBなので、現像段(WB/マトリクス/デモザイク)は適用されない
            ProcessingNoteText.Text =
                "※ カラー画像は表示LUT(黒/白点・ゲイン・ガンマ・コントラスト)のみ焼き込みます。" +
                "TIFF 16bitグレーはカラーの場合、輝度(BT.601)を出力します。" +
                "複数ページTIFFは全ページを連番の静止画、または1本の動画へ書き出します。";
            ProcessingNoteText.Visibility = Visibility.Visible;
        }

        OutputFolderBox.Text = defaultOutputFolder;
        FormatCombo.SelectedIndex = 0;
        UpdateQualityNote();
    }

    /// <summary>「実行」で確定された選択。</summary>
    public BatchChoice? Result { get; private set; }

    /// <summary>選んでいる出力形式。</summary>
    internal BatchFormat SelectedFormat =>
        (uint)FormatCombo.SelectedIndex < (uint)FormatChoices.Length
            ? FormatChoices[FormatCombo.SelectedIndex]
            : BatchFormat.Png8;

    private void OnFormatChanged(object sender, RoutedEventArgs e)
    {
        if (FpsPanel is not null)
        {
            FpsPanel.Visibility = SelectedFormat is BatchFormat.AviMjpeg or BatchFormat.Mp4H264
                ? Visibility.Visible
                : Visibility.Collapsed;
            UpdateQualityNote();
        }

        if (DisplayLutCheck is not null)
        {
            // TIFFは無処理(16bitフルスケールの内部値、または raw の code のまま)の出力なので表示調整の選択自体がない
            DisplayLutCheck.Visibility = BatchTiffOutput.IsTiff(SelectedFormat)
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
        if (FpsCombo is not null)
        {
            // 読めない・0 以下・範囲外のフレームレートは既定値・範囲へ落として書き出す。以前は黙って落としたので、
            // 打った値と違うフレームレートになったことが見えなかった。絞り込み欄と同じく赤枠と理由で示す
            FpsInput.ParseInteger(FpsCombo.Text, DefaultFps, out string? notice);
            InputFeedback.SetError(FpsCombo, notice);
        }

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
        if (SelectedFormat == BatchFormat.Mp4H264)
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

    /// <summary>
    /// 「参照…」のフォルダ選択ダイアログを始めるフォルダの候補(実在は確かめない)。
    /// </summary>
    /// <remarks>
    /// 出力先の入力が相対パスなら、「実行」と同じく元画像のフォルダを基準に解決する(空なら元画像のフォルダ)。
    /// 以前は入力をそのまま Directory.Exists に渡し、プロセスのカレントディレクトリ(exe の場所など)を基準に探していた。
    /// </remarks>
    /// <returns>候補の絶対パス。パスとして解釈できなければ null。</returns>
    internal string? BrowseStartFolder()
    {
        return OutputPaths.TryResolveOutputFolder(OutputFolderBox.Text.Trim(), _sourceFolder, out string folder)
            ? folder
            : null;
    }

    private async void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        if (_browsing)
        {
            return;
        }

        // 始めるフォルダの実在は UI スレッドの外で確かめる(出力先は元画像のそばの NAS 上のことが多く、切断していると
        // Directory.Exists がタイムアウトまで戻らない。DialogInitialFolder)。確かめる間に重ねて押されても選択ダイアログを
        // 重ねて出さず、閉じられたら出さない
        _browsing = true;
        try
        {
            string? folder = await DialogInitialFolder.ConfirmAsync(BrowseStartFolder(), DialogInitialFolder.Timeout);
            if (!IsVisible)
            {
                return;
            }

            var dialog = new OpenFolderDialog();
            if (folder is not null)
            {
                dialog.InitialDirectory = folder;
            }

            if (dialog.ShowDialog(this) == true)
            {
                OutputFolderBox.Text = dialog.FolderName;
            }
        }
        finally
        {
            _browsing = false;
        }
    }

    private void OnRunClick(object sender, RoutedEventArgs e)
    {
        string input = OutputFolderBox.Text.Trim();
        if (string.IsNullOrEmpty(input))
        {
            MessageBox.Show(this, "出力先フォルダを指定してください。", "バッチ書き出し",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 相対パスはカレントディレクトリではなく元画像のフォルダを基準に絶対パスにする
        // (完了表示にも絶対パスで出す)
        if (!OutputPaths.TryResolveOutputFolder(input, _sourceFolder, out string folder))
        {
            MessageBox.Show(this, $"出力先フォルダのパスが正しくありません: {input}", "バッチ書き出し",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result = new BatchChoice(
            SelectedFormat,
            FpsInput.ParseInteger(FpsCombo.Text, DefaultFps),
            folder,
            DisplayLutCheck.IsChecked == true,
            SelectedQuality);
        DialogResult = true;
    }
}

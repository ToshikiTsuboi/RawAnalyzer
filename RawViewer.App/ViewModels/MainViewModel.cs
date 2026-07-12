using System.Collections.ObjectModel;
using System.Windows.Media;
using RawViewer.App.Mvvm;

namespace RawViewer.App.ViewModels;

/// <summary>ファイルリストの1項目。</summary>
/// <param name="Name">表示名。</param>
/// <param name="FullPath">フルパス。</param>
public sealed record FileEntry(string Name, string FullPath);

/// <summary>
/// メインウィンドウのViewModel。表示状態(テキスト・スライダー値・ヒストグラム)を保持する。
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private string _folderPath = "";
    private FileEntry? _selectedFile;
    private bool _hasImage;
    private string _imageInfoText = "画像未読込";
    private string _cursorStatusText = "";
    private string _zoomStatusText = "";
    private string _zoomPercentText = "—";
    private string _cursorOverlayText = "";
    private string _levelOverlayText = "";
    private double _gain = 1.0;
    private double _gamma = 1.0;
    private double _contrast = 1.0;
    private double _blackLevel;
    private double _blackLevelMax = 65535;
    private bool _histogramIsLog = true;
    private bool _histogramIsSampled;
    private bool _hasRoi;
    private string _roiOverlayText = "";
    private double _wbGainR = 1.0;
    private double _wbGainB = 1.0;
    private bool _hdrTargetVisible;
    private ImageSource? _histogramSource;
    private string _histMeanSigmaText = "— / —";
    private string _histMinMaxText = "— / —";
    private string _fmtBitDepthText = "—";
    private string _fmtEndianText = "—";
    private string _fmtBayerText = "—";
    private string _fmtHdrText = "—";

    /// <summary>左パネルに表示中のフォルダパス。</summary>
    public string FolderPath
    {
        get => _folderPath;
        set => SetProperty(ref _folderPath, value);
    }

    /// <summary>左パネルのファイル一覧。</summary>
    public ObservableCollection<FileEntry> Files { get; } = new();

    /// <summary>選択中のファイル。</summary>
    public FileEntry? SelectedFile
    {
        get => _selectedFile;
        set => SetProperty(ref _selectedFile, value);
    }

    /// <summary>画像が読み込まれているか。</summary>
    public bool HasImage
    {
        get => _hasImage;
        set => SetProperty(ref _hasImage, value);
    }

    /// <summary>ステータスバー左端の画像情報。</summary>
    public string ImageInfoText
    {
        get => _imageInfoText;
        set => SetProperty(ref _imageInfoText, value);
    }

    /// <summary>ステータスバー中央のカーソル位置情報。</summary>
    public string CursorStatusText
    {
        get => _cursorStatusText;
        set => SetProperty(ref _cursorStatusText, value);
    }

    /// <summary>ステータスバー右端のズーム/レベル情報。</summary>
    public string ZoomStatusText
    {
        get => _zoomStatusText;
        set => SetProperty(ref _zoomStatusText, value);
    }

    /// <summary>ツールバーのズーム率表示。</summary>
    public string ZoomPercentText
    {
        get => _zoomPercentText;
        set => SetProperty(ref _zoomPercentText, value);
    }

    /// <summary>キャンバス左上のカーソル画素オーバーレイ。</summary>
    public string CursorOverlayText
    {
        get => _cursorOverlayText;
        set => SetProperty(ref _cursorOverlayText, value);
    }

    /// <summary>キャンバス右下の間引きレベルオーバーレイ。</summary>
    public string LevelOverlayText
    {
        get => _levelOverlayText;
        set => SetProperty(ref _levelOverlayText, value);
    }

    /// <summary>表示ゲイン(LUTパラメータ)。</summary>
    public double Gain
    {
        get => _gain;
        set => SetProperty(ref _gain, value);
    }

    /// <summary>表示ガンマ(LUTパラメータ)。</summary>
    public double Gamma
    {
        get => _gamma;
        set => SetProperty(ref _gamma, value);
    }

    /// <summary>表示コントラスト(LUTパラメータ)。</summary>
    public double Contrast
    {
        get => _contrast;
        set => SetProperty(ref _contrast, value);
    }

    /// <summary>黒レベル(raw code、LUTパラメータ)。</summary>
    public double BlackLevel
    {
        get => _blackLevel;
        set => SetProperty(ref _blackLevel, value);
    }

    /// <summary>黒レベルスライダーの最大値(ビット深度の最大raw code)。</summary>
    public double BlackLevelMax
    {
        get => _blackLevelMax;
        set => SetProperty(ref _blackLevelMax, value);
    }

    /// <summary>ヒストグラムをlogスケールで表示するか。</summary>
    public bool HistogramIsLog
    {
        get => _histogramIsLog;
        set => SetProperty(ref _histogramIsLog, value);
    }

    /// <summary>ヒストグラムがサンプリング計算されたか(sampled表記)。</summary>
    public bool HistogramIsSampled
    {
        get => _histogramIsSampled;
        set => SetProperty(ref _histogramIsSampled, value);
    }

    /// <summary>ROIが選択されているか。</summary>
    public bool HasRoi
    {
        get => _hasRoi;
        set => SetProperty(ref _hasRoi, value);
    }

    /// <summary>キャンバス右上のROI統計オーバーレイ。</summary>
    public string RoiOverlayText
    {
        get => _roiOverlayText;
        set => SetProperty(ref _roiOverlayText, value);
    }

    /// <summary>ホワイトバランスRゲイン。</summary>
    public double WbGainR
    {
        get => _wbGainR;
        set => SetProperty(ref _wbGainR, value);
    }

    /// <summary>ホワイトバランスBゲイン。</summary>
    public double WbGainB
    {
        get => _wbGainB;
        set => SetProperty(ref _wbGainB, value);
    }

    /// <summary>HDR分割表示中の調整対象コンボを表示するか。</summary>
    public bool HdrTargetVisible
    {
        get => _hdrTargetVisible;
        set => SetProperty(ref _hdrTargetVisible, value);
    }

    /// <summary>ヒストグラム画像。</summary>
    public ImageSource? HistogramSource
    {
        get => _histogramSource;
        set => SetProperty(ref _histogramSource, value);
    }

    /// <summary>ヒストグラム統計 mean/σ。</summary>
    public string HistMeanSigmaText
    {
        get => _histMeanSigmaText;
        set => SetProperty(ref _histMeanSigmaText, value);
    }

    /// <summary>ヒストグラム統計 min/max。</summary>
    public string HistMinMaxText
    {
        get => _histMinMaxText;
        set => SetProperty(ref _histMinMaxText, value);
    }

    /// <summary>フォーマット欄: ビット深度。</summary>
    public string FmtBitDepthText
    {
        get => _fmtBitDepthText;
        set => SetProperty(ref _fmtBitDepthText, value);
    }

    /// <summary>フォーマット欄: エンディアン。</summary>
    public string FmtEndianText
    {
        get => _fmtEndianText;
        set => SetProperty(ref _fmtEndianText, value);
    }

    /// <summary>フォーマット欄: Bayerパターン。</summary>
    public string FmtBayerText
    {
        get => _fmtBayerText;
        set => SetProperty(ref _fmtBayerText, value);
    }

    /// <summary>フォーマット欄: HDR方式。</summary>
    public string FmtHdrText
    {
        get => _fmtHdrText;
        set => SetProperty(ref _fmtHdrText, value);
    }
}

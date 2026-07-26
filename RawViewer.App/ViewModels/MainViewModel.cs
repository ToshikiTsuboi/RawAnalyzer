using System.Collections.ObjectModel;
using System.Windows.Media;
using RawViewer.App.Mvvm;

namespace RawViewer.App.ViewModels;

/// <summary>ファイルリストの1項目(ファイルまたはディレクトリ)。</summary>
/// <param name="Name">表示名。</param>
/// <param name="FullPath">フルパス。</param>
/// <param name="IsDirectory">ディレクトリかどうか(ダブルクリックで移動)。</param>
public sealed record FileEntry(string Name, string FullPath, bool IsDirectory = false);

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
    private bool _histogramIsCumulative;
    private bool _histogramByChannel;
    private bool _channelStatsVisible;
    private string _chRText = "";
    private string _chGrText = "";
    private string _chGbText = "";
    private string _chBText = "";
    private bool _histogramIsSampled;
    private bool _hasRoi;
    private string _roiOverlayText = "";
    private double _wbGainR = 1.0;
    private double _wbGainG = 1.0;
    private double _wbGainB = 1.0;
    private bool _zebraOn;
    private bool _hdrTargetVisible;
    private bool _hasSequence;
    private double _sequenceIndex;
    private double _sequenceMax;
    private string _sequenceLabel = "";
    private ImageSource? _histogramSource;
    private string _histMeanSigmaText = "— / —";
    private string _histMinMaxText = "— / —";
    private string _histMedianModeText = "— / —";
    private string _histPercentileText = "— / —";
    private string _histClipText = "— / —";
    private string _histDynamicRangeText = "—";
    private bool _isFullscreen;
    private bool _isColorImage;
    private string _fmtBitDepthText = "—";
    private string _fmtEndianText = "—";
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

    /// <summary>累積ヒストグラム(横軸=出力、縦軸=累積頻度)を表示するか。</summary>
    public bool HistogramIsCumulative
    {
        get => _histogramIsCumulative;
        set => SetProperty(ref _histogramIsCumulative, value);
    }

    /// <summary>Bayerチャネル別(R/Gr/Gb/B)表示にするか。</summary>
    public bool HistogramByChannel
    {
        get => _histogramByChannel;
        set => SetProperty(ref _histogramByChannel, value);
    }

    /// <summary>チャネル別統計テーブルを表示するか。</summary>
    public bool ChannelStatsVisible
    {
        get => _channelStatsVisible;
        set => SetProperty(ref _channelStatsVisible, value);
    }

    /// <summary>Rチャネル統計(mean/σ)。</summary>
    public string ChRText
    {
        get => _chRText;
        set => SetProperty(ref _chRText, value);
    }

    /// <summary>Grチャネル統計。</summary>
    public string ChGrText
    {
        get => _chGrText;
        set => SetProperty(ref _chGrText, value);
    }

    /// <summary>Gbチャネル統計。</summary>
    public string ChGbText
    {
        get => _chGbText;
        set => SetProperty(ref _chGbText, value);
    }

    /// <summary>Bチャネル統計。</summary>
    public string ChBText
    {
        get => _chBText;
        set => SetProperty(ref _chBText, value);
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

    /// <summary>ホワイトバランスGゲイン。</summary>
    public double WbGainG
    {
        get => _wbGainG;
        set => SetProperty(ref _wbGainG, value);
    }

    /// <summary>ホワイトバランスBゲイン。</summary>
    public double WbGainB
    {
        get => _wbGainB;
        set => SetProperty(ref _wbGainB, value);
    }

    /// <summary>ゼブラ(飽和/黒潰れ警告)を表示するか。</summary>
    public bool ZebraOn
    {
        get => _zebraOn;
        set => SetProperty(ref _zebraOn, value);
    }

    /// <summary>HDR分割表示中の調整対象コンボを表示するか。</summary>
    public bool HdrTargetVisible
    {
        get => _hdrTargetVisible;
        set => SetProperty(ref _hdrTargetVisible, value);
    }

    /// <summary>再生可能なシーケンス(連番ファイル/マルチフレーム)があるか。</summary>
    public bool HasSequence
    {
        get => _hasSequence;
        set => SetProperty(ref _hasSequence, value);
    }

    /// <summary>シーケンスの現在位置(0始まり)。</summary>
    public double SequenceIndex
    {
        get => _sequenceIndex;
        set => SetProperty(ref _sequenceIndex, value);
    }

    /// <summary>シーケンスの最終インデックス。</summary>
    public double SequenceMax
    {
        get => _sequenceMax;
        set => SetProperty(ref _sequenceMax, value);
    }

    /// <summary>「3 / 25」形式の位置表示。</summary>
    public string SequenceLabel
    {
        get => _sequenceLabel;
        set => SetProperty(ref _sequenceLabel, value);
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

    /// <summary>ヒストグラム指標 中央値/最頻値。</summary>
    public string HistMedianModeText
    {
        get => _histMedianModeText;
        set => SetProperty(ref _histMedianModeText, value);
    }

    /// <summary>ヒストグラム指標 P1/P99。</summary>
    public string HistPercentileText
    {
        get => _histPercentileText;
        set => SetProperty(ref _histPercentileText, value);
    }

    /// <summary>ヒストグラム指標 飽和率/黒つぶれ率。</summary>
    public string HistClipText
    {
        get => _histClipText;
        set => SetProperty(ref _histClipText, value);
    }

    /// <summary>ヒストグラム指標 簡易ダイナミックレンジ。</summary>
    public string HistDynamicRangeText
    {
        get => _histDynamicRangeText;
        set => SetProperty(ref _histDynamicRangeText, value);
    }

    /// <summary>フルスクリーン表示中か。</summary>
    public bool IsFullscreen
    {
        get => _isFullscreen;
        set => SetProperty(ref _isFullscreen, value);
    }

    /// <summary>デコード済みカラー画像(JPEG/PNG/カラーTIFF)を表示中か。</summary>
    public bool IsColorImage
    {
        get => _isColorImage;
        set => SetProperty(ref _isColorImage, value);
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

    /// <summary>フォーマット欄: HDR方式。</summary>
    public string FmtHdrText
    {
        get => _fmtHdrText;
        set => SetProperty(ref _fmtHdrText, value);
    }
}

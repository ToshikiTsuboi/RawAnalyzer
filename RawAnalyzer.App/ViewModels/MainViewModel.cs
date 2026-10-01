using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using RawAnalyzer.App.Mvvm;
using RawAnalyzer.App.Services;

namespace RawAnalyzer.App.ViewModels;

/// <summary>ファイルリストの1項目(ファイルまたはディレクトリ)。</summary>
/// <param name="Name">表示名。</param>
/// <param name="FullPath">フルパス。</param>
/// <param name="IsDirectory">ディレクトリかどうか(ダブルクリックで移動)。</param>
/// <param name="Length">
/// ファイルサイズ(バイト)。不明なら-1。列挙時に取得済みの値を保持しておき、
/// 仮想スタック判定のたびに GetFileAttributesEx を撃ち直さないようにする。
/// </param>
public sealed record FileEntry(
    string Name, string FullPath, bool IsDirectory = false, long Length = -1);

/// <summary>
/// メインウィンドウのViewModel。表示状態(テキスト・スライダー値・ヒストグラム)を保持する。
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private string _folderPath = "";
    private FileEntry? _selectedFile;
    private string _fileFilterText = "";
    private FileNameFilter _fileFilter = FileNameFilter.Empty;
    private IReadOnlyList<FileEntry> _filteredFiles = Array.Empty<FileEntry>();
    private IReadOnlyList<string> _fileExtensionPatterns = Array.Empty<string>();
    private string _fileFilterSummary = "0 件";
    private bool _suspendFileFilter;
    private bool _hasImage;
    private string _imageInfoText = "画像未読込";
    private bool _isLoading;
    private double _loadProgress;
    private bool _isCompareMode;
    private bool _isProcessed;
    private string _processingStateText = "";
    private string _processingStateTooltip = "";
    private string _formatNoticeText = "";
    private string _formatNoticeToolTip = "";
    private string _cursorStatusText = "";
    private string _zoomStatusText = "";
    private string _zoomPercentText = "—";
    private string _cursorOverlayText = "";
    private string _levelOverlayText = "";
    private double _gain = 1.0;
    private double _gamma = 1.0;
    private double _contrast = 1.0;
    private double _blackLevel;
    private double _whiteLevel = 65535;
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
    private bool _isRawFile;
    private bool _isHdrViewShown;
    private bool _isHdrComputing;
    private bool _leftPanelVisible = true;
    private bool _rightPanelVisible = true;
    private string _fmtBitDepthText = "—";
    private string _fmtEndianText = "—";
    private string _fmtHdrText = "—";

    /// <summary>ViewModel を生成し、ファイル一覧の変更に絞り込みを追従させる。</summary>
    public MainViewModel()
    {
        Files.CollectionChanged += (_, _) =>
        {
            if (!_suspendFileFilter)
            {
                ApplyFileFilter();
            }
        };
    }

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
        set
        {
            // 右クリックで選び直すたびに、右クリックメニューの「フォーマットを指定して開く…」を合わせる
            if (SetProperty(ref _selectedFile, value))
            {
                OnPropertyChanged(nameof(CanOpenSelectedFileWithFormat));
                OnPropertyChanged(nameof(OpenSelectedFileWithFormatToolTip));
            }
        }
    }

    /// <summary>
    /// <see cref="Files"/> のうち絞り込み条件に一致するもの。リストはこちらを表示する。
    /// ディレクトリ項目は常に残す。
    /// </summary>
    public IReadOnlyList<FileEntry> FilteredFiles
    {
        get => _filteredFiles;
        private set => SetProperty(ref _filteredFiles, value);
    }

    /// <summary>
    /// ファイル一覧の絞り込み条件。書式は <see cref="FileNameFilter"/> を参照
    /// (拡張子・ワイルドカードの OR、または <c>/.../</c> で正規表現)。
    /// </summary>
    public string FileFilterText
    {
        get => _fileFilterText;
        set
        {
            value ??= "";
            if (SetProperty(ref _fileFilterText, value))
            {
                _fileFilter = FileNameFilter.Parse(value);
                OnPropertyChanged(nameof(FileFilterError));
                OnPropertyChanged(nameof(FileFilterHasError));
                ApplyFileFilter();
            }
        }
    }

    /// <summary>絞り込み条件が不正な場合の説明(ツールチップ用)。正常なら null。</summary>
    public string? FileFilterError => _fileFilter.Error;

    /// <summary>絞り込み条件が不正か(入力欄の強調表示に使う)。</summary>
    public bool FileFilterHasError => _fileFilter.Error is not null;

    /// <summary>「12 / 240 件」形式の件数表示。絞り込みがないときは総数のみ。</summary>
    public string FileFilterSummary
    {
        get => _fileFilterSummary;
        private set => SetProperty(ref _fileFilterSummary, value);
    }

    /// <summary>
    /// 現在のフォルダに含まれる拡張子のワイルドカード(<c>*.raw</c> など)。
    /// 多い順に並び、入力欄のドロップダウン候補になる。
    /// </summary>
    public IReadOnlyList<string> FileExtensionPatterns
    {
        get => _fileExtensionPatterns;
        private set => SetProperty(ref _fileExtensionPatterns, value);
    }

    /// <summary>
    /// ファイル一覧を丸ごと差し替える。1 件ずつ Add すると件数ぶん絞り込みが走るため、
    /// フォルダの読み込みはこちらを使う。
    /// </summary>
    /// <param name="entries">新しい一覧(表示順)。</param>
    public void ReplaceFiles(IEnumerable<FileEntry> entries)
    {
        _suspendFileFilter = true;
        try
        {
            Files.Clear();
            foreach (FileEntry entry in entries)
            {
                Files.Add(entry);
            }
        }
        finally
        {
            _suspendFileFilter = false;
        }

        FileExtensionPatterns = BuildExtensionPatterns(Files);
        ApplyFileFilter();
    }

    /// <summary>絞り込みを解除する。</summary>
    public void ClearFileFilter()
    {
        FileFilterText = "";
    }

    private void ApplyFileFilter()
    {
        FileEntry[] filtered = _fileFilter.IsEmpty
            ? Files.ToArray()
            : Files.Where(f => f.IsDirectory || _fileFilter.IsMatch(f.Name)).ToArray();
        FilteredFiles = filtered;
        FileFilterSummary = _fileFilter.IsEmpty
            ? $"{Files.Count} 件"
            : $"{filtered.Length} / {Files.Count} 件";
    }

    private static IReadOnlyList<string> BuildExtensionPatterns(IEnumerable<FileEntry> files)
    {
        return files
            .Where(f => !f.IsDirectory)
            .Select(f => Path.GetExtension(f.Name).ToLowerInvariant())
            .Where(extension => extension.Length > 1)
            .GroupBy(extension => extension, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => "*" + group.Key)
            .ToArray();
    }

    /// <summary>画像が読み込まれているか。</summary>
    public bool HasImage
    {
        get => _hasImage;
        set
        {
            if (SetProperty(ref _hasImage, value))
            {
                OnPropertyChanged(nameof(CanEditBayer));
                OnPropertyChanged(nameof(BayerEditToolTip));
                OnPropertyChanged(nameof(CanChangeFormat));
                OnPropertyChanged(nameof(ChangeFormatToolTip));
                OnPropertyChanged(nameof(CanUseMainView));
            }
        }
    }

    /// <summary>ステータスバー左端の画像情報。</summary>
    public string ImageInfoText
    {
        get => _imageInfoText;
        set => SetProperty(ref _imageInfoText, value);
    }

    /// <summary>比較モード中か(メニューのチェック表示と、メイン画像を対象にする操作の可否に使う)。</summary>
    public bool IsCompareMode
    {
        get => _isCompareMode;
        set
        {
            if (SetProperty(ref _isCompareMode, value))
            {
                OnPropertyChanged(nameof(CanUseMainView));
                OnPropertyChanged(nameof(CanChangeFormat));
                OnPropertyChanged(nameof(ChangeFormatToolTip));
            }
        }
    }

    /// <summary>
    /// 通常表示(メインの画像・ビューポート)を操作できるか。画像を開いていて、比較モードでないとき。
    /// </summary>
    /// <remarks>
    /// 比較モードは通常表示の上に比較画面を重ねるだけなので、比較中にメイン画像を対象にする操作(ズーム・
    /// 表示モード・表示調整・ROI・コピー・保存・解析など)を受け付けると、見えている比較ペインではなく隠れた
    /// 通常表示に効いてしまう(画素値・表示のコピーは別の画像の値・描画を黙ってコピーしていた)。メニュー・
    /// ツールバーの該当項目はこの値で有効・無効を決め、コマンド表(キー・コマンドパレット)も同じ条件で断る。
    /// </remarks>
    public bool CanUseMainView => _hasImage && !_isCompareMode;

    /// <summary>ファイル読み込み中か(ステータスバーの進捗バー表示に使う)。</summary>
    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    /// <summary>読み込みの進捗率(0〜100)。</summary>
    public double LoadProgress
    {
        get => _loadProgress;
        set
        {
            if (SetProperty(ref _loadProgress, value))
            {
                OnPropertyChanged(nameof(LoadProgressText));
            }
        }
    }

    /// <summary>進捗率の%表示文字列。</summary>
    public string LoadProgressText => $"{_loadProgress:F0}%";

    /// <summary>
    /// 表示中の画像が加工済み(補正・HDR派生)かどうか。
    /// ステータスバーの常設バッジに使い、保存完了などの一時メッセージで消えないようにする。
    /// </summary>
    public bool IsProcessed
    {
        get => _isProcessed;
        set => SetProperty(ref _isProcessed, value);
    }

    /// <summary>加工済みバッジの表示文字列。</summary>
    public string ProcessingStateText
    {
        get => _processingStateText;
        set => SetProperty(ref _processingStateText, value);
    }

    /// <summary>加工済みバッジのツールチップ(適用内容の詳細)。</summary>
    public string ProcessingStateTooltip
    {
        get => _processingStateTooltip;
        set => SetProperty(ref _processingStateTooltip, value);
    }

    /// <summary>
    /// 同じサイズのファイルの記憶から推定して開いたことの通知(空なら出さない)。
    /// 画像情報とは別の欄に出し、読み込み後の画像情報や一時メッセージで消えないようにする。
    /// </summary>
    public string FormatNoticeText
    {
        get => _formatNoticeText;
        set
        {
            if (SetProperty(ref _formatNoticeText, value ?? ""))
            {
                OnPropertyChanged(nameof(HasFormatNotice));
            }
        }
    }

    /// <summary>推定の通知のツールチップ(推定したフォーマットと直し方)。</summary>
    public string FormatNoticeToolTip
    {
        get => _formatNoticeToolTip;
        set => SetProperty(ref _formatNoticeToolTip, value ?? "");
    }

    /// <summary>推定の通知を出すか。</summary>
    public bool HasFormatNotice => _formatNoticeText.Length > 0;

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
        set
        {
            if (SetProperty(ref _gain, value))
            {
                OnPropertyChanged(nameof(GainDb));
                OnPropertyChanged(nameof(GainNote));
            }
        }
    }

    /// <summary>
    /// 表示ゲインをdBで表したもの(20·log10(倍率))。UIはこちらを操作する。
    /// </summary>
    /// <remarks>
    /// 内部の <see cref="Gain"/> は線形倍率だが、線形スライダーでは
    /// 低倍率側の分解能が潰れるうえ、センサ評価では dB のほうが扱いやすい。
    /// </remarks>
    public double GainDb
    {
        get => DisplayLevels.ToGainDb(_gain);
        set => Gain = Math.Pow(10, Math.Clamp(value, MinGainDb, MaxGainDb) / 20.0);
    }

    /// <summary>ゲインの線形倍率表示(dB表記の補助)。</summary>
    public string GainNote => $"= ×{_gain:0.###}";

    /// <summary>ゲインスライダーの下限[dB](×0.1)。</summary>
    public double MinGainDb => DisplayLevels.MinGainDb;

    /// <summary>ゲインスライダーの上限[dB](×1000)。</summary>
    public double MaxGainDb => 60;

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

    /// <summary>
    /// 白レベル(raw code)。これ以上の値が最大にマップされる。
    /// 従来は自動コントラストのみが書き換える隠し状態だった。
    /// </summary>
    public double WhiteLevel
    {
        get => _whiteLevel;
        set => SetProperty(ref _whiteLevel, value);
    }

    /// <summary>黒レベル/白レベルスライダーの最大値(ビット深度の最大raw code)。</summary>
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
        set
        {
            if (SetProperty(ref _isColorImage, value))
            {
                OnPropertyChanged(nameof(CanEditBayer));
                OnPropertyChanged(nameof(BayerEditToolTip));
                OnPropertyChanged(nameof(ChangeFormatToolTip));
            }
        }
    }

    /// <summary>表示中の画像のファイルが raw(.raw/.bin)か。</summary>
    public bool IsRawFile
    {
        get => _isRawFile;
        set
        {
            if (SetProperty(ref _isRawFile, value))
            {
                OnPropertyChanged(nameof(CanChangeFormat));
                OnPropertyChanged(nameof(ChangeFormatToolTip));
            }
        }
    }

    /// <summary>「フォーマット変更…」(HDRメニューの項目・右パネルの「変更…」)を使えるか。</summary>
    /// <remarks>
    /// 読み込みダイアログでフォーマットを指定し直して開き直せるのは raw だけ。画像ファイル(TIFF 等)は
    /// フォーマットをファイル自身が持つので使えない(以前は押せて、押しても何も起きなかった)。
    /// 比較モード中は、比較画面に隠れた通常表示の raw を開き直すことになるので使えない(<see cref="CanUseMainView"/>)。
    /// </remarks>
    public bool CanChangeFormat => _hasImage && _isRawFile && !_isCompareMode;

    /// <summary>
    /// 「フォーマット変更…」を使えないときに示す理由(ツールチップ)。使えるとき・画像がないときは null。
    /// </summary>
    public string? ChangeFormatToolTip =>
        !_hasImage ? null
        : _isCompareMode ? CommandDisabledReasons.CompareMode
        : !_isRawFile ? FormatChangeAvailability.ExplainUnavailable(_isColorImage)
        : null;

    /// <summary>
    /// ファイル一覧の右クリックメニュー「フォーマットを指定して開く…」を使えるか(選択中の項目が raw ファイル)。
    /// </summary>
    /// <remarks>
    /// フォーマットを指定して開けるのは raw だけ。以前は画像ファイル(TIFF 等)でも押せて、ダイアログを出さずに
    /// 普通に開いた(「フォーマット変更…」と同じ規約で無効にし、理由をツールチップで示す)。フォルダ・未選択も
    /// 開くファイルがないので使えない。
    /// </remarks>
    public bool CanOpenSelectedFileWithFormat =>
        _selectedFile is { IsDirectory: false } file && FormatChangeAvailability.CanOpenWithFormat(file.FullPath);

    /// <summary>
    /// 「フォーマットを指定して開く…」のツールチップ。画像ファイルでは使えない理由、それ以外は項目の説明。
    /// </summary>
    public string OpenSelectedFileWithFormatToolTip =>
        _selectedFile is { IsDirectory: false } file && !FormatChangeAvailability.CanOpenWithFormat(file.FullPath)
            ? FormatChangeAvailability.OpenWithFormatUnavailableReason
            : FormatChangeAvailability.OpenWithFormatDescription;

    /// <summary>HDR分割・合成の派生ビューを表示しているか(右パネルの Bayer の選択を無効にする)。</summary>
    public bool IsHdrViewShown
    {
        get => _isHdrViewShown;
        set
        {
            if (SetProperty(ref _isHdrViewShown, value))
            {
                OnPropertyChanged(nameof(CanEditBayer));
                OnPropertyChanged(nameof(BayerEditToolTip));
            }
        }
    }

    /// <summary>
    /// HDR分割・合成の計算中か(開始から、派生ビューへ差し替えるか採用せずに終えるまで。右パネルの Bayer の
    /// 選択を無効にする)。
    /// </summary>
    public bool IsHdrComputing
    {
        get => _isHdrComputing;
        set
        {
            if (SetProperty(ref _isHdrComputing, value))
            {
                OnPropertyChanged(nameof(CanEditBayer));
                OnPropertyChanged(nameof(BayerEditToolTip));
            }
        }
    }

    /// <summary>右パネルの Bayer 指定を操作できるか。</summary>
    /// <remarks>
    /// デコード済みのカラー画像は RGB のまま表示し、Bayer を適用しない(TIFF のページ送り・
    /// ファイル連番の送り・表示モードの選択と同じ規約)。カラー画像で指定できると、表示はカラーのまま
    /// チャネル別統計などが輝度へ Bayer を当ててしまう。指定そのものはカラー画像を挟んでも保持され、
    /// 次のグレーの画像に付く。HDR分割・合成の派生ビューの表示中と計算中も、派生ビューが計算を始めたときの
    /// Bayer のまま右パネルと食い違うので操作させない(<see cref="BayerEditAvailability"/>)。
    /// </remarks>
    public bool CanEditBayer => BayerEditRefusal == BayerEditAvailability.Refusal.None;

    /// <summary>
    /// 右パネルの Bayer 指定のツールチップ。操作できないときはその理由、操作できるときは項目の説明。
    /// </summary>
    public string BayerEditToolTip => BayerEditAvailability.ToolTip(BayerEditRefusal);

    private BayerEditAvailability.Refusal BayerEditRefusal =>
        BayerEditAvailability.Check(_hasImage, _isColorImage, _isHdrViewShown, _isHdrComputing);

    /// <summary>左パネル(ファイル)を表示するか。</summary>
    public bool LeftPanelVisible
    {
        get => _leftPanelVisible;
        set => SetProperty(ref _leftPanelVisible, value);
    }

    /// <summary>右パネル(調整)を表示するか。</summary>
    public bool RightPanelVisible
    {
        get => _rightPanelVisible;
        set => SetProperty(ref _rightPanelVisible, value);
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

using System.IO;
using System.Windows;
using Microsoft.Win32;
using RawAnalyzer.App.Rendering;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.Views;
using RawAnalyzer.Core;

namespace RawAnalyzer.App;

public partial class MainWindow
{
    private readonly FormatPresetStore _presetStore = new();

    // サイズ別フォーマット記憶(format-history.json)。raw の読み込みに成功するたびに記録・保存する
    private readonly FormatMemory _formatMemory = new(new FormatHistoryStore());
    private bool _updatingFormatPanel;

    // 表示中のファイルを同じサイズの記憶から推定して開いたことの通知(比較モードを抜けたら表示し直す)
    private string _mainFormatNotice = "";
    private string _mainFormatNoticeToolTip = "";

    // OpenPath の世代。await から戻った時点で世代が進んでいたら結果を捨てる
    private int _openGeneration;

    // ファイルを開く要求(OpenPath)の数。フォーマット指定ダイアログを出す前に数える(世代とは別)
    private int _openRequests;

    // 実在・種類の確認を UI スレッドの外で待ってから開く要求(ツリーの選択・最近使ったファイル・ドロップ・一覧の更新)を
    // 受け付けた数。受け付けた時点で数える。_openRequests・_folderGeneration は開き始めるときに進むので、確認を待っている
    // 要求は数えられない。以前はこれがなく、先に受け付けた要求・起動時に開くもの(起動引数・前回のフォルダ)が、確認を
    // 待っている後からの要求より先に開き始めて、後からの要求を照合で捨てさせていた(後から来た方を優先する)
    private int _deferredOpenRequests;

    /// <summary>起動引数で渡されたパスを開く(フォルダなら一覧表示のみ)。</summary>
    /// <remarks>
    /// 一覧の読み込み(・ファイルを開く要求)は呼び出したこの UI ターンで始める(世代を進める)。以前はここで
    /// もう一度 UI スレッドの外でフォルダかを確かめ、その間に利用者が開いたフォルダを、確認の後で起動引数のフォルダで
    /// 置き換えていた(利用者のフォルダの一覧の読み込みも捨てていた)。
    /// </remarks>
    /// <param name="startup">起動引数のパスとフォルダかどうか(起動引数の解釈(App.StartupPath)で確かめてある)。</param>
    private void OpenStartupPath(StartupTarget startup)
    {
        if (startup.IsFolder)
        {
            LoadFolder(startup.Path, selectPath: null);
            return;
        }

        _ = LoadFolderAndOpenAsync(startup.Path);
    }

    /// <summary>
    /// ファイルのフォルダの一覧を読み込んでから、そのファイルを開く(「開く」・最近使ったファイル・ドロップ・起動引数)。
    /// </summary>
    /// <remarks>
    /// 連番判定はファイル一覧を見るので、一覧が揃ってから開く。一覧の列挙を待つ間に別のフォルダの読み込みや
    /// 別のファイルを開く操作が始まったら(確認を待っている要求を含む)、後から来た方を優先してこのファイルは開かない。
    /// 以前は遅れて終わった列挙の後にも開いたので、後から開いた画像を古いファイルで置き換え(一覧は後から開いたフォルダの
    /// まま)、新規サイズの raw なら別の画像を見ている途中に突然フォーマット指定ダイアログを出していた。
    /// </remarks>
    /// <param name="path">開くファイル。</param>
    /// <returns>開く処理を始める(または開かずに終える)までのタスク。</returns>
    private async Task LoadFolderAndOpenAsync(string path)
    {
        int openRequests = _openRequests;
        int deferredRequests = _deferredOpenRequests;
        bool latest = await LoadFolderAsync(Path.GetDirectoryName(path)!, selectPath: path);
        if (latest && openRequests == _openRequests && deferredRequests == _deferredOpenRequests)
        {
            OpenPath(path);
        }
    }

    private void RememberFileFormat(string path, RawFormat format)
    {
        string key = SessionStore.NormalizeKey(path);
        _sessionStore.Update(session => SessionStore.TouchFileFormat(session, key, format));
    }

    private RawFormat? TryGetRememberedFormat(string path, long fileSize)
    {
        // 別のインスタンスが記憶した・F2 で直したフォーマットも使う(古い記憶で開いて記憶し直し、直したものを巻き戻さない)
        if (!_sessionStore.Current.FileFormats.TryGetValue(SessionStore.NormalizeKey(path), out RawFormat? format))
        {
            return null;
        }

        // 手で編集した・壊れた session.json の未定義の列挙値などは、サイズ別の記憶と同じ基準で使わない
        return FormatHistory.CanOpen(format, fileSize) ? format : null;
    }

    /// <summary>
    /// 表示中のファイルを同じサイズの記憶から推定して開いたことをステータスバーに示す(空文字で消す)。
    /// </summary>
    /// <remarks>
    /// 画像情報(ImageInfoText)とは別の欄に出すので、読み込み後の画像情報や保存完了などの一時メッセージで
    /// 消えず、別のファイルを表示するまで見える。
    /// </remarks>
    /// <param name="text">通知の文。</param>
    /// <param name="toolTip">通知のツールチップ(推定したフォーマットと直し方)。</param>
    private void SetMainFormatNotice(string text, string toolTip = "")
    {
        _mainFormatNotice = text;
        _mainFormatNoticeToolTip = toolTip;
        _vm.FormatNoticeText = text;
        _vm.FormatNoticeToolTip = toolTip;
    }

    // ---- ファイル読込 ----

    private const string OpenImageFilter =
        "対応画像 (*.raw;*.bin;*.tif;*.tiff;*.dng;*.jpg;*.jpeg;*.png;*.bmp)"
        + "|*.raw;*.bin;*.tif;*.tiff;*.dng;*.jpg;*.jpeg;*.png;*.bmp"
        + "|Raw (*.raw;*.bin)|*.raw;*.bin"
        + "|画像 (*.tif;*.tiff;*.dng;*.jpg;*.jpeg;*.png;*.bmp)|*.tif;*.tiff;*.dng;*.jpg;*.jpeg;*.png;*.bmp"
        + "|すべてのファイル (*.*)|*.*";

    private async void OnOpenFileClick(object sender, RoutedEventArgs e)
    {
        // 比較モード中は比較画面の「＋ 画像を追加」と同じく、選んだ画像を比較ペインへ追加する(通常表示へは開かない)
        if (_compareMode)
        {
            await CompareArea.AddPaneFromPickerAsync();
            return;
        }

        var dialog = new OpenFileDialog { Filter = OpenImageFilter };
        if (dialog.ShowDialog(this) == true)
        {
            await LoadFolderAndOpenAsync(dialog.FileName);
        }
    }

    /// <summary>
    /// 比較モード中なら、ファイルを開く操作(ファイル一覧のダブルクリック・右クリックの「開く」・最近使ったファイル・
    /// 比較画面の外へのドロップ)を比較ペインへの追加に回す。
    /// </summary>
    /// <remarks>
    /// 比較モードは通常表示の上に比較画面を重ねるだけなので、以前は比較中に開いた画像が比較画面に隠れた通常表示へ
    /// 読み込まれ、見えている比較には何も起きなかった。比較画面へのドロップと同じ経路で順に追加し、4枚表示中・
    /// 読み込み中で追加できないものは理由を示す(<see cref="Compare.CompareView.AddPanesFromPathsAsync"/>)。
    /// </remarks>
    /// <param name="paths">開こうとしたファイル。</param>
    /// <returns>比較ペインへの追加に回したら true(通常表示へは開かない)。</returns>
    private bool AddToCompareInstead(IReadOnlyList<string> paths)
    {
        if (!_compareMode)
        {
            return false;
        }

        _ = CompareArea.AddPanesFromPathsAsync(paths);
        return true;
    }

    private void OnChangeFormatClick(object sender, RoutedEventArgs e)
    {
        if (_currentPath is not null && IsRawFile(_currentPath))
        {
            ChangeCurrentRawFormat(_currentPath);
            return;
        }

        // 画像ファイル(TIFF 等)はフォーマットをファイル自身が持ち、開き直せない。メニュー・右パネルの
        // ボタンは無効にしてツールチップで理由を示すが、キー(F2)・コマンドパレットからは実行されるので、
        // 黙って何もしないのではなく同じ理由を知らせる
        if (_currentImage is not null)
        {
            MessageBox.Show(this, FormatChangeAvailability.ExplainUnavailable(_colorImage is not null),
                "フォーマット変更", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    /// <summary>
    /// 表示中の raw をフォーマットを指定し直して開き直す(F2)。開き直せたら、元のフォーマットが同じサイズの
    /// 記憶にあれば新しいフォーマットで置き換える(誤って自動で開いたものを直すと、次からは直した方で開く)。
    /// </summary>
    /// <param name="path">表示中の raw ファイル。</param>
    private void ChangeCurrentRawFormat(string path)
    {
        // ビニング後の縮小寸法を元ファイルの寸法として提案しない。
        // (path は表示中のファイルなので、サイズは読み込んだときに取ったものを使う)
        RawFormat? initial = _correctionLabel is null ? _currentFormat
            : _openedRawFormat ?? TryGetRememberedFormat(path, _currentFileSize);
        OpenPathChoosingFormat(path, initial, correctFrom: initial);
    }

    /// <summary>フォーマット指定が必要な生バイナリ(.raw/.bin)かどうか。</summary>
    private static bool IsRawFile(string path)
    {
        return Compare.ComparePane.IsRawFile(path);
    }

    /// <summary>
    /// ファイルを開く。raw は同じパスの記憶 → 同じサイズの記憶(自動適用がちょうど1つ)の順に使い、
    /// なければダイアログで指定する(<see cref="RawOpenPlanner"/>)。
    /// </summary>
    /// <param name="path">開くファイル。</param>
    private void OpenPath(string path)
    {
        OpenPathCore(path, chooseFormat: false, initialFormat: null, correctFrom: null);
    }

    /// <summary>raw をフォーマットを指定し直して開く(記憶を使わず必ずダイアログを出す)。</summary>
    /// <param name="path">開く raw ファイル。</param>
    /// <param name="initialFormat">ダイアログの初期値(null なら候補一覧の先頭)。</param>
    /// <param name="correctFrom">開き直す前のフォーマット(同じサイズの記憶にあれば置き換える)。</param>
    private void OpenPathChoosingFormat(string path, RawFormat? initialFormat, RawFormat? correctFrom)
    {
        OpenPathCore(path, chooseFormat: true, initialFormat, correctFrom);
    }

    private async void OpenPathCore(
        string path, bool chooseFormat, RawFormat? initialFormat, RawFormat? correctFrom)
    {
        // フォルダの一覧を待ってから開く要求(ドロップなど)は、待つ間にこの要求が来たら開かない
        int openRequest = ++_openRequests;

        // 読み込み中に再生タイマーや保留中の連番送りが画像を差し替えないようにする
        using BusyScope busy = EnterLoadBusy();

        // 開始から確定(または破棄)までは、保存・演算など表示画像を使う操作を始めさせない。
        // 確定がそれらのダイアログ・進捗表示(入れ子ポンプ)の中で走ると、操作のために
        // 作った画面が別の画像を処理し、処理中の画像も破棄されてしまう
        using IDisposable pendingLoad = _imageGate.BeginLoad();

        RawFormat? format = null;
        RawFormatOrigin? origin = null;
        bool? autoOpenChoice = null;
        if (IsRawFile(path))
        {
            // 同じファイルを開き直すときは記憶したフォーマットでダイアログをスキップし、なければ同じサイズ・
            // 拡張子のファイルの記憶(自動適用がちょうど1つ)で開く。それ以外はダイアログ(候補一覧の先頭が初期値)。
            // フォーマットを指定し直すとき(F2 など)は記憶を使わず、渡された初期値でダイアログを出す。
            // サイズは UI スレッドの外で取り、ダイアログへも渡す(切断した NAS の raw では FileInfo が SMB の
            // タイムアウトまで戻らず、ダイアログを出す前にウィンドウが固まっていた)。取る間に別のファイルを開く要求が
            // 来た・ウィンドウを閉じたら、後から来た方を優先してこのファイルは開かない(ダイアログも出さない)
            long size = await Task.Run(() => SafeFileSize(path));
            if (openRequest != _openRequests || _closed)
            {
                return;
            }

            RawOpenPlan plan = RawOpenPlanner.Plan(
                path, size, chooseFormat ? null : TryGetRememberedFormat(path, size),
                _formatMemory.History, _currentFormat, initialFormat, chooseFormat);
            origin = plan.Origin;
            format = plan.Format;
            if (format is null)
            {
                var dialog = new RawImportDialog(
                    path, size, _presetStore, plan.DialogInitial, _formatMemory, _currentFormat)
                {
                    Owner = this,
                };
                if (dialog.ShowDialog() != true || dialog.Result is null)
                {
                    return;
                }

                format = dialog.Result;
                autoOpenChoice = dialog.AutoOpenNextTime;
            }
        }

        // 世代はフォーマットが確定して読み込みを始めるときに進める(先に始まっていた読み込みは下の
        // ReplaceLoadCts で取り消す)。ダイアログを出す前に進めると、ダイアログを取り消したときに、先に始まっていた
        // 読み込みが取り消されないまま古い世代になって読み終わっても黙って捨てられ、進捗表示も誰も消さなくなる
        // (取り消されたTIFFのページ読み込みも、進捗表示と送りのUIを戻さなくなる)
        int generation = ++_openGeneration;
        CancelAnalysis();
        var cts = new CancellationTokenSource();
        ReplaceLoadCts(cts);
        _vm.ImageInfoText = "読込中…";
        _vm.LoadProgress = 0;
        _vm.IsLoading = true;

        // Progress<T>は生成スレッド(UI)へマーシャリングして通知する
        var loadProgress = new Progress<double>(
            p => _vm.LoadProgress = Math.Clamp(p * 100, 0, 100));

        RawImage image;
        ColorImage? color = null;
        int pageCount = 1;
        string? valueNote = null;
        SampleScaling? valueScaling = null;

        // 画像情報・サイズ別の記憶に使う読み込み後のサイズも、読み込みと一緒に UI スレッドの外で取る
        long fileSize;
        try
        {
            if (IsRawFile(path))
            {
                (image, fileSize) = await Task.Run(
                    () => (RawLoader.Load(path, format!, cts.Token, loadProgress), SafeFileSize(path)), cts.Token);
            }
            else
            {
                DecodedImage decoded;
                (decoded, fileSize) = await Task.Run(
                    () => (ImageFileLoader.Load(path, cts.Token, loadProgress), SafeFileSize(path)), cts.Token);
                image = decoded.Luminance;
                color = decoded.Color;
                pageCount = decoded.PageCount;
                valueNote = decoded.ValueNote;
                valueScaling = decoded.Scaling;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            // 取り消された・後から開いた読み込みに置き換わった読み込みの失敗は知らせない(利用者はもうこのファイルを
            // 待っていない)。取り消しは読み込みの区切りでしか見ないので、ネットワーク上で固まった読み出しが後から
            // IOException などで終わることがある。知らせると見ていないファイルのエラーを出し、表示中の画像の情報や
            // 後続の読み込みの「読込中…」を「読込失敗」で上書きしていた
            if (cts.IsCancellationRequested || generation != _openGeneration)
            {
                return;
            }

            _vm.ImageInfoText = "読込失敗";

            // 読み込みを始めるときに取り消した、表示を続ける画像の縮小表示(Bayerを含む)の生成と
            // ヒストグラム・ROI統計・ラインプロファイル・射影の計算を作り直す。作り直さないと、開き直すまで
            // 縮小表示なしで等倍の行を読んで描き、ヒストグラム欄には前に表示していた画像の統計が残る
            // (縮小表示は表示中の画像の世代として、取り消されていないこの読み込みのトークンで作る)
            RefreshAfterSequenceMove();
            MessageBox.Show(this, $"読み込みに失敗しました: {ex.Message}", "RawAnalyzer",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        finally
        {
            // 読み込み中に別ファイルが開かれた場合、後続世代の進捗表示を
            // 先行世代のfinallyが消してしまわないよう、現行世代のみ落とす
            if (generation == _openGeneration)
            {
                _vm.IsLoading = false;
            }
        }

        // 読み込みより先に始まっていた操作(保存・演算・HDR分割など)があれば、終わるのを
        // 待ってから差し替える。操作のダイアログ・進捗表示の中で確定すると、操作の
        // 対象画像を背後で差し替え・破棄してしまう(開く要求は捨てずに後から表示する)
        try
        {
            while (_imageGate.IsOperationRunning
                && !cts.IsCancellationRequested && generation == _openGeneration)
            {
                _vm.ImageInfoText = $"処理の完了後に {Path.GetFileName(path)} を表示します…";
                await _imageGate.WhenOperationsIdleAsync().WaitAsync(cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // 待機中に別ファイルが開かれた・ウィンドウが閉じられた(下で結果を捨てる)
        }

        if (cts.IsCancellationRequested || generation != _openGeneration)
        {
            // 読み込み中に別ファイルを開かれていた場合は結果を捨てる
            image.Dispose();
            return;
        }

        // 旧画像の描画を止めてから破棄し、新画像へ差し替える
        await Viewport.ClearImageAsync();
        _derivedImage?.Dispose();
        _derivedImage = null;
        _vm.IsHdrViewShown = false;
        _hdrFloatImage = null;
        _hdrFrameParams = null;

        // 合成ビューの表示中に開いたら、合成ビューへ入る前の黒点・白点を後で戻さない(表示調整は下で既定へ戻す)
        _mergedViewLevels.Reset();
        _mainPyramid = null;
        _mainBayerPyramid?.Dispose();
        _mainBayerPyramid = null;
        _derivedBayerPyramid?.Dispose();
        _derivedBayerPyramid = null;
        _correctionLabel = null;
        UpdateProcessingBadge();
        _channelHistograms = null;
        _colorImage = color;
        _vm.IsColorImage = color is not null;
        _vm.HdrTargetVisible = false;
        _currentImage?.Dispose();
        _currentImage = image;
        ClearDefectSource();
        _currentFormat = image.Format;
        _currentPath = path;
        _currentFileSize = fileSize;

        // 「フォーマット変更…」は raw でしか使えない。ファイル連番の送りは同じ拡張子のファイルだけを
        // 送る(SequenceScanner)ので、開いたときに決めれば送りの後も変わらない
        _vm.IsRawFile = IsRawFile(path);
        _tiffStack = pageCount > 1
            ? new TiffStackSource(path, pageCount) { BayerOverride = image.Format.Bayer } : null;
        _sequenceBayerOverride = image.Format.Bayer;
        _tiffPageIndex = 0;
        _valueNote = ValueMappingNote.FromFile(valueNote, valueScaling);
        _valueScaling = valueScaling;
        _histogram = null;
        _vm.HasRoi = false;
        ClearCursorReadout();
        _vm.BlackLevelMax = (1 << image.Format.BitDepth) - 1;
        ResetDisplayParameters();

        Title = $"RawAnalyzer — {Path.GetFileName(path)}{TiffPageNote}";
        Viewport.SetDefectMarkers(null);
        _defectWindow?.DiscardResult();
        UpdateNoiseWindowSource();
        _recentFiles.Add(path);
        RebuildRecentMenu();
        bool guessedFromSize = origin == RawFormatOrigin.SizeMemory;
        if (IsRawFile(path))
        {
            // 同じパスの記憶は従来どおり記録する。ただし同じサイズの記憶から推定して開いたときは記録しない
            // (推定した形式をこのファイルの記憶として固定すると、同じサイズの記憶を後から F2 で直しても
            // このファイルだけ古い形式で開き続ける。一覧を次々に開いて上限の50件を使い切ることもない)
            if (!guessedFromSize)
            {
                RememberFileFormat(path, image.Format);
            }

            // 同じサイズ・拡張子の記憶へ記録する(F2 で開き直したときは元の形式を置き換える)
            _formatMemory.RememberLoaded(path, fileSize, image.Format, autoOpenChoice, correctFrom);
            _openedRawFormat = image.Format;
        }
        else
        {
            _openedRawFormat = null;
        }

        // 読み込み後の画像情報とは別の欄に出し、このファイルを表示している間は見えるようにする
        SetMainFormatNotice(
            guessedFromSize ? RawOpenPlanner.AutoOpenNotice : "",
            guessedFromSize ? RawOpenPlanner.AutoOpenToolTip(path, fileSize, image.Format) : "");

        UpdateFormatPanel(image.Format);
        _vm.ImageInfoText =
            $"{image.Width}×{image.Height} · {image.Format.BitDepth}bit"
            + (color is not null ? " · RGB" : "")
            + (fileSize >= 0 ? $" · {fileSize / (1024.0 * 1024.0):F1} MB" : "")
            + (image.FrameCount > 1 ? $" · {image.FrameCount}fr" : "") + TiffPageNote + ValueNoteSuffix;
        _vm.HasImage = true;

        DisplayModeCombo.SelectedIndex = 0;
        DisplayModeCombo.IsEnabled = color is null;
        Viewport.SetDisplayMode(ViewportDisplayMode.Raw);
        Viewport.SetImage(image, image.Format);
        Viewport.SetColorImage(color);
        Viewport.SetLut(BuildLut());
        DetectSequence();

        RefreshHistogram(roi: null);

        // 開いているラインプロファイル窓は、ヒストグラムと同じく開いた画像で計算し直す(同じ基準点・方向)。
        // 以前は前の画像の断面を出し続けていた(マーカーだけが消える)。射影の窓で選んだチャネルは、別のファイルでは使わない
        RefreshLineProfile();
        ForgetProjectionChannel();
        RefreshProjections();

        // 差し替えを終えたので、以降の操作は新しい画像を対象に始めてよい
        // (ピラミッド生成は差し替え後の画像を検証してから取り付ける。二重の Dispose は無視される)
        pendingLoad.Dispose();
        await BuildPyramidAsync(image, cts.Token);
    }

    private void UpdateFormatPanel(RawFormat format)
    {
        string packing = format.Packing == BitPacking.Lsb ? "下詰め" : "上詰め";
        _vm.FmtBitDepthText = $"{format.BitDepth}bit {packing}";
        _vm.FmtEndianText = format.Endianness == Endianness.Big ? "Big" : "Little";
        _updatingFormatPanel = true;
        FmtBayerCombo.SelectedIndex = format.Bayer switch
        {
            BayerPattern.Rggb => 0,
            BayerPattern.Bggr => 1,
            BayerPattern.Grbg => 2,
            BayerPattern.Gbrg => 3,
            _ => 4,
        };
        _updatingFormatPanel = false;
        _vm.FmtHdrText = HdrFormatText.DescribePanel(format);
    }
}

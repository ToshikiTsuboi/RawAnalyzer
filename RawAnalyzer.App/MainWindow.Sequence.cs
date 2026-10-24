using System.IO;
using System.Windows;
using System.Windows.Threading;
using RawAnalyzer.App.Controls;
using RawAnalyzer.App.Rendering;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;

namespace RawAnalyzer.App;

public partial class MainWindow
{
    // 連番判定・一括書き出しの候補を、表示中のファイルのフォルダの一覧から選ぶ(左パネルの一覧は別のフォルダのことがある)
    private readonly SequenceCandidateSource _sequenceCandidates = new();

    // シーケンス再生
    private enum SequenceMode
    {
        None,
        Frames,
        Files,
        TiffPages,
    }

    /// <summary>再生フレームレートの既定値(コンボの初期選択と同じ)。</summary>
    private const double DefaultPlaybackFps = 15;

    private SequenceMode _sequenceMode;
    private List<string> _sequenceFiles = new();
    private int _sequenceIndex;
    private DispatcherTimer? _playTimer;
    private bool _sequenceBusy;
    private bool _updatingSequenceUi;

    // ファイル連番の送りで送り先を読んでいる間の取り消し(TIFFのページ送りの _tiffPageLoadCts と同じく、
    // 表示中の画像の世代 _loadCts にリンクする)。開く・操作の開始(EnterBusyCore)・ウィンドウを閉じると取り消す
    private CancellationTokenSource? _sequenceLoadCts;

    // ファイル連番の送りで画像ファイル(TIFF等)へ引き継ぐ、右パネルのBayer指定。
    // TIFFのページ送り(TiffStackSource.BayerOverride)と同じく、開いた画像の配列から始めて
    // 右パネルの変更で更新し、カラー画像を挟んでも保持してグレーの画像にだけ付ける(ImageFileBayer)
    private BayerPattern _sequenceBayerOverride;

    // ---- シーケンス再生 ----

    private int SequenceCount => _sequenceMode switch
    {
        SequenceMode.Frames => _currentImage?.FrameCount ?? 0,
        SequenceMode.Files => _sequenceFiles.Count,
        SequenceMode.TiffPages => _tiffStack?.PageCount ?? 0,
        _ => 0,
    };

    private void DetectSequence()
    {
        StopPlayback();
        _sequenceMode = SequenceMode.None;
        _sequenceFiles = new List<string>();
        _sequenceIndex = 0;

        if (_currentImage is not null && _derivedImage is null && _correctionLabel is null)
        {
            if (_tiffStack is { PageNavigationEnabled: true })
            {
                _sequenceMode = SequenceMode.TiffPages;
                _sequenceIndex = _tiffPageIndex;
            }
            else if (_currentImage.FrameCount > 1)
            {
                _sequenceMode = SequenceMode.Frames;
                _sequenceIndex = Viewport.Frame;
            }
            else if (_currentPath is not null)
            {
                // 同一フォルダのファイル群をバーチャルスタックとみなす。
                // rawはファイルサイズが解像度そのものを表すためサイズ一致で判定できるが、
                // 画像ファイルは解像度がヘッダにあるうえ圧縮でサイズが変わるので、
                // ファイル名の連番で判定する
                IReadOnlyList<SequenceFile> candidates = CandidateFiles();
                IReadOnlyList<string> files = IsRawFile(_currentPath)
                    ? SequenceScanner.FindStack(
                        _currentPath, CurrentFileLength(candidates), candidates)
                    : SequenceScanner.FindNumberedStack(_currentPath, candidates);
                if (files.Count > 1)
                {
                    _sequenceMode = SequenceMode.Files;
                    _sequenceFiles = files.ToList();
                    _sequenceIndex = SequenceScanner.IndexOf(files, _currentPath);
                }
            }
        }

        UpdateSequenceUi();
    }

    /// <summary>
    /// 表示中のファイルの仮想スタック判定・一括書き出しの候補(サイズは列挙時のキャッシュ)。
    /// </summary>
    /// <remarks>
    /// 左パネルの一覧が表示中のファイルのフォルダのものならその一覧、別のフォルダのもの(フォルダツリーで
    /// 移った後など)なら、表示中のファイルのフォルダの一覧を前に使ったときの内容から選ぶ(<see cref="SequenceCandidateSource"/>)。
    /// </remarks>
    private IReadOnlyList<SequenceFile> CandidateFiles()
    {
        if (_currentPath is null)
        {
            return Array.Empty<SequenceFile>();
        }

        return _sequenceCandidates.Resolve(
            _currentPath, _currentFolder,
            _vm.Files
                .Where(f => !f.IsDirectory)
                .Select(f => new SequenceFile(f.FullPath, f.Length)));
    }

    /// <summary>現在開いているファイルのサイズ。候補(一覧の列挙時のキャッシュ)を優先する。</summary>
    /// <param name="candidates">表示中のファイルの候補(<see cref="CandidateFiles"/>)。</param>
    private long CurrentFileLength(IReadOnlyList<SequenceFile> candidates)
    {
        if (_currentPath is null)
        {
            return -1;
        }

        foreach (SequenceFile file in candidates)
        {
            if (file.Length > 0
                && string.Equals(file.Path, _currentPath, StringComparison.OrdinalIgnoreCase))
            {
                return file.Length;
            }
        }

        // 一覧にない・一覧でサイズを取れなかったときは、読み込んだときに取ったサイズ(UI スレッドでは取り直さない)
        return _currentFileSize;
    }

    /// <summary>ファイルサイズ(取得できなければ -1)。ファイルシステムに触れるので UI スレッドで呼ばない。</summary>
    private static long SafeFileSize(string path)
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

    private void UpdateSequenceUi()
    {
        _updatingSequenceUi = true;
        int count = SequenceCount;

        // HDR分割・合成の派生ビューの表示中は送りのUIを有効にしない。送りの件数は派生ビューの間も残るので、
        // 派生ビューより前に始まった送りの後始末(ShowSequenceIndexAsync の finally)から呼ばれても有効に戻さない
        _vm.HasSequence = SequenceNavigation.IsAvailable(count, derivedViewShown: _derivedImage is not null);
        _vm.SequenceMax = Math.Max(0, count - 1);
        _vm.SequenceIndex = _sequenceIndex;
        _vm.SequenceLabel = count > 1 ? $"{_sequenceIndex + 1} / {count}"
            + (_sequenceMode == SequenceMode.TiffPages ? " (TIFF)" : "") : "";
        _updatingSequenceUi = false;
    }

    private async Task ShowSequenceIndexAsync(int index, bool refreshAnalysis)
    {
        if (_sequenceMode == SequenceMode.TiffPages)
        {
            await ShowTiffPageAsync(index, refreshAnalysis);
            return;
        }

        int count = SequenceCount;

        // HDR分割・合成の派生ビューの表示中は送らない(送りのUIも無効)。ファイル連番では派生ビューを残したまま
        // 元画像だけがビューポートへ入り、フレーム送りでは派生ビューのBayerピラミッドがビューポートから外れる
        bool available = SequenceNavigation.IsAvailable(count, derivedViewShown: _derivedImage is not null);

        // 重い処理の実行中は画像を差し替えない(処理対象が背後で破棄されるため)。
        // スライダーが先に動いてしまっているので、表示中のフレームへ戻す
        if (!available || _sequenceBusy || _busyDepth > 0)
        {
            // 実行中で送れないときは黙って戻さず、理由をステータスバーに出す
            // (前の送りの完了待ちは連続操作で普通に起こるので知らせない)
            if (available)
            {
                NotifySequenceBusy();
            }

            UpdateSequenceUi();
            return;
        }

        index = ((index % count) + count) % count;
        if (index == _sequenceIndex)
        {
            UpdateSequenceUi();
            return;
        }

        _sequenceBusy = true;
        try
        {
            if (_sequenceMode == SequenceMode.Frames)
            {
                // 旧フレーム用の縮小ピラミッド・Bayerピラミッドの生成を打ち切る(ファイル連番・TIFFのページ送りと
                // 同じく、表示中の画像・フレームの世代を進める)。生成は _loadCts のトークンで走り、完了時に
                // トークンの取り消しを見て結果を捨てる。フレームを移しても取り消さないと、連続して送ったときに
                // 前フレーム用の生成が後から終わって現フレームのピラミッドを上書きし(描画側はフレームの
                // 不一致で使わないので、次に送るまで等倍データから描く)、送りのたびの生成も積み上がって
                // CPU とメモリを使い続ける。送れなかったときは取り消さない
                ReplaceLoadCts(new CancellationTokenSource());

                // 旧フレームを読んでいる解析(ヒストグラム・ROI統計・ラインプロファイル・射影)も打ち切る
                // (ファイル連番・TIFFのページ送りと同じ)。フレーム送りでは画像がそのままなので、取り消さないと
                // 送った後に届いた前フレームの結果を、画像の照合だけで現フレームの測定値として表示していた
                // (結果の採用時にもフレームを照合する)。送った後の解析は RefreshAfterSequenceMove が始める
                CancelAnalysis();

                // Bayerピラミッドも生成元フレーム専用なので、フレームを移したら捨てる。切り離しは送りを決めた
                // このUIターンで、フレームを移すのと一緒に行う(先に切り離して描画の停止を待つと、その間に
                // 始まった操作に譲って送りをやめたとき、表示中のフレームのピラミッドだけを失って作り直されず、
                // Bayer系の表示が等倍データからの描画になる)。切り離す前に始まっていた描画は読んでいる
                // 可能性があるので、止まってから破棄する(描画中に破棄すると読み出しがODEになる)。
                // フィールドはawait前に切っておく(await中に別経路が同じインスタンスを掴むとDisposeが二重になる)
                BayerPyramid? oldBayer = _mainBayerPyramid;
                _mainBayerPyramid = null;
                Task bayerDetached = oldBayer is null
                    ? Task.CompletedTask
                    : Viewport.DetachBayerPyramidAsync();

                // 欠陥検出の結果は検出したフレームの画素のもの。フレームを移すこのUIターンで、
                // 他の差し替え経路と同じく検出元を手放し、マーカーと欠陥ウィンドウの一覧も破棄する
                // (前フレームの一覧を現フレームのものと誤解させない。残すと一覧からの移動は
                // 現フレームの同じ座標を指し、補正はフレームの不一致で断られる)。
                // 送れなかったときは一覧・マーカーをそろえて残すので、消すのは移すときだけにする
                ClearDefectSource();
                Viewport.SetDefectMarkers(null);
                _defectWindow?.DiscardResult();
                Viewport.SetFrame(index);
                _sequenceIndex = index;

                // ノイズ測定ウィンドウも、ファイル・ページの送りと同じく前のフレームの結果を消し、対象名に
                // 送った先のフレームを示す(画像はそのままなので、ダイアログはフレームも含めて照合する)
                UpdateNoiseWindowSource();

                // カーソル位置の画素値も送った先のフレームから読み直す(再生中も毎フレーム)
                RefreshCursorReadout();

                if (oldBayer is not null)
                {
                    try
                    {
                        await bayerDetached;
                    }
                    finally
                    {
                        oldBayer.Dispose();
                    }
                }
            }
            else
            {
                string path = _sequenceFiles[index];
                bool isRaw = IsRawFile(path);
                RawFormat expectedFormat = _currentFormat!;

                // 送りは保存・演算などの操作や通常の読み込みに譲る。次のファイルを読む間に
                // それらが始まったら、差し替えの時点で終わっていても送らない(HDR分割の
                // 派生ビューや開き直した画像を上書きしない。TIFFのページ送りは読み込みの中止で同じことをする)
                int activity = _imageGate.ActivityStamp;

                // await中にモーダル(保存・測定・演算)が開いていたら差し替えない。
                // モーダルのディスパッチャポンプ内でここが再開すると、処理対象の
                // 画像を背後で破棄してしまう。HDR分割・合成の派生ビューの表示中も差し替えない
                // (派生ビューを残したまま元画像だけが入れ替わる。派生ビューはHDR表示の開始で
                // ActivityStamp が進むので通常はその判定で譲るが、確定の前提として確かめる)
                bool Yielded() => _busyDepth > 0 || activity != _imageGate.ActivityStamp
                    || !ReferenceEquals(expectedFormat, _currentFormat) || _derivedImage is not null;
                RawImage image;
                ColorImage? color = null;
                int pageCount = 1;

                // 値の対応関係の説明は送りが確定したときに状態へ入れる(送れなかったときに
                // 表示中の画像と食い違わせない)
                string? valueNote = null;
                SampleScaling? valueScaling = null;

                // 画像情報に出す送り先のサイズも、読み込みと一緒に UI スレッドの外で取る(再生中は毎フレーム)
                long frameFileSize;

                // 読み込みは取り消せるようにする。読む間に別ファイルを開く・操作を始める・ウィンドウを閉じると
                // 結果は下で捨てるが、取り消さないと大きなファイルの読み込み(ネットワーク上の raw の一時コピー、
                // TIFF のデコード)が最後まで走り、新しい読み込みや処理と I/O・メモリを奪い合う
                var loadCts = CancellationTokenSource.CreateLinkedTokenSource(
                    _loadCts?.Token ?? CancellationToken.None);
                _sequenceLoadCts = loadCts;
                try
                {
                    // raw は表示中のフォーマットで、TIFF等の連番はファイル自身のフォーマットで読む
                    DecodedImage decoded;
                    (decoded, frameFileSize) = await Task.Run(
                        () => (SequenceFileLoad.Load(path, isRaw, expectedFormat, loadCts.Token), SafeFileSize(path)),
                        loadCts.Token);
                    image = decoded.Luminance;
                    color = decoded.Color;
                    pageCount = decoded.PageCount;
                    valueNote = decoded.ValueNote;
                    valueScaling = decoded.Scaling;
                }
                catch (OperationCanceledException)
                {
                    return; // 開く・操作の開始・終了で取り消した
                }
                catch (Exception ex)
                {
                    // 消えた・ロックされた・壊れたファイル。黙って戻ると、再生は毎ティック同じファイルを読み直して
                    // 捨て続け(前のファイルで止まって見える)、「次」も理由なく効かない。再生を止めて理由を知らせる
                    // (送り先は飛ばさない。スライダー・End などで越えられる)。読む間に始まった操作などに譲るときは知らせない
                    if (!Yielded())
                    {
                        NotifySequenceMoveFailed(SequenceFileLoad.ExplainUnreadable(path, ex));
                    }

                    return;
                }
                finally
                {
                    if (ReferenceEquals(_sequenceLoadCts, loadCts))
                    {
                        _sequenceLoadCts = null;
                    }

                    loadCts.Dispose();
                }

                // 読み込みの間に始まった操作・読み込みなどに譲る(上の Yielded)
                if (Yielded())
                {
                    image.Dispose();
                    return;
                }

                // 通常のファイル連番では既存どおり同一サイズだけを送る。寸法の違う画像ファイルが混じっていたら、
                // 読めないときと同じく再生を止めて理由を知らせる
                if (_currentImage is not null
                    && SequenceFileLoad.CheckSize(path, image, _currentImage) is { } sizeRefusal)
                {
                    image.Dispose();
                    NotifySequenceMoveFailed(sizeRefusal);
                    return;
                }

                // raw は現在のフォーマット(右パネルの Bayer 指定を含む)で読んでいる。画像ファイル(TIFF等)は
                // ファイル自身のフォーマットに、右パネルの Bayer 指定を引き継ぐ(TIFFのページ送りと同じ規約。
                // グレーの画像ではファイルの CFAPattern より指定を優先し、カラー画像には付けない)。
                // ファイル自身の Bayer(通常の TIFF はなし)をそのまま使うと、指定が送りで消えて
                // Bayer 系の表示が Raw 表示へ戻る
                RawFormat format = isRaw ? expectedFormat
                    : ImageFileBayer.Apply(image.Format, color is not null, _sequenceBayerOverride);

                // ビット深度やカラー/グレーが変わると、黒レベル上限・画像情報・
                // フォーマットパネル・表示モードの前提が崩れる。追従させる
                bool layoutChanged = format.BitDepth != _currentFormat!.BitDepth
                    || (color is not null) != (_colorImage is not null)
                    || format.Bayer != _currentFormat.Bayer;

                // 表示と MainWindow の状態は同じUIターンで交換する(TIFFのページ送りと同じ)。
                // ReplaceImageAsync は最初の await より前にビューポートの参照を交換する。
                // 状態の交換を await の後にすると、その間に始まった保存・演算などが旧画像を
                // 対象にし、その途中で画像を差し替え・破棄してしまう。
                // 旧画像を読んでいる解析は先に止め、旧画像と旧Bayerピラミッドは
                // それを読んでいた描画が止まってから破棄する
                // (走行中だとParallel.For内でObjectDisposedExceptionになる)
                CancelAnalysis();

                // 旧画像の縮小ピラミッド・Bayerピラミッドの生成も打ち切る(TIFFのページ送りと同じ)。
                // 生成は _loadCts のトークンで走り、結果は差し替え後の画像と照合して捨てられるだけなので、
                // 止めないと旧画像を破棄するまで、また破棄後も元画像を読まない縮小段が CPU とメモリを使い続ける。
                // 新しい画像のピラミッドは差し替え後に新しいトークンで作る
                ReplaceLoadCts(new CancellationTokenSource());
                RawImage? oldImage = _currentImage;
                BayerPyramid? oldBayer = _mainBayerPyramid;
                (_blackPoint, _whitePoint) = DisplayLevels.ForNextImage(
                    _blackPoint, _whitePoint, _currentFormat!.BitDepth, format.BitDepth);
                _currentImage = image;
                _currentFormat = format;
                _currentPath = path;
                _currentFileSize = frameFileSize;

                // 送り先は表示中のフォーマットで読んだ(記憶から推定したのではない)ので、推定の通知は消す。
                // 送りでは同じサイズの記憶へ記録しない(開いたときに記録済みのフォーマットで読むだけ)
                _openedRawFormat = isRaw ? format : null;
                SetMainFormatNotice("");
                _valueNote = ValueMappingNote.FromFile(valueNote, valueScaling);
                _valueScaling = valueScaling;
                _tiffStack = pageCount > 1
                    ? new TiffStackSource(path, pageCount, pageNavigationEnabled: false)
                    { BayerOverride = format.Bayer } : null;
                _tiffPageIndex = 0;
                _mainPyramid = null;
                _mainBayerPyramid = null;
                _sequenceIndex = index;

                // 欠陥検出の結果は旧画像の座標・画素のもの。他の差し替え経路(開く・TIFFのページ送り)と
                // 同じく、検出元への参照(この後で破棄する旧画像)を手放し、マーカーと欠陥ウィンドウの一覧も
                // 破棄して別画像の欠陥を残して見せない。送れなかったときは画像も検出結果もそのまま残すので、
                // マーカーを消すのは差し替えるときだけにする
                ClearDefectSource();
                Viewport.SetDefectMarkers(null);
                _defectWindow?.DiscardResult();

                // カラー画像は輝度と一緒に差し替える。片方だけだと前フレームの色が残る
                _colorImage = color;
                _vm.IsColorImage = color is not null;
                Task<RawImage?> pending = Viewport.ReplaceImageAsync(image, format, color: color);

                // 表示モードはツールバーの選択を保つ。新しい画像で成立しないモード(カラー画像への
                // Bayer系表示、Bayerなしでのカラー・現像・分割)だけ、選択も含めて戻す
                // (SetColorImage は選択を見ずに Raw/カラー表示へ戻すので、表示と選択が食い違う)。
                // 選択の変更は OnDisplayModeChanged を通るので状態の交換後に行う
                ApplyDisplayModeToNewImage(color is not null, format);
                Viewport.SetLut(BuildLut());

                Title = $"RawAnalyzer — {Path.GetFileName(path)}{TiffPageNote}";
                if (layoutChanged)
                {
                    // 上限だけ変えると旧ビット深度のコード値が残り、次の操作で白点が飛ぶ
                    SyncLevelControlsToActiveBitDepth();
                    UpdateFormatPanel(format);
                    _histogram = null;
                    _channelHistograms = null;

                    // 現像LUT(白飛びの判定に素材のビット深度を使う)は、カラー現像のまま送った場合に
                    // 上の ApplyDisplayModeToNewImage が新しいビット深度で作り直している
                }

                // ノイズ測定ウィンドウの対象名・飽和コード・ROI の有無を新しい画像へ合わせる
                // (表示モードの適用後に行う。分割表示かどうかで ROI を測れるかが変わる)
                UpdateNoiseWindowSource();

                // カーソル位置の画素値も送った先の画像から読み直す(寸法は同じ。ビット深度・パターンは
                // 送った先のもの)
                RefreshCursorReadout();

                _vm.ImageInfoText =
                    $"{image.Width}×{image.Height} · {format.BitDepth}bit"
                    + (color is not null ? " · RGB" : "")
                    + (frameFileSize >= 0
                        ? $" · {frameFileSize / (1024.0 * 1024.0):F1} MB"
                        : "") + TiffPageNote + ValueNoteSuffix;
                _vm.SelectedFile = _vm.FilteredFiles.FirstOrDefault(f => string.Equals(
                    f.FullPath, path, StringComparison.OrdinalIgnoreCase));
                // Filesのリスト・位置・再生状態を維持する。TIFFのページ送りへは切り替えない。

                try
                {
                    await pending;
                }
                finally
                {
                    oldBayer?.Dispose();
                    oldImage?.Dispose();
                }
            }

        }
        finally
        {
            // 読み込み失敗・サイズ不一致などで送れなかった場合も、
            // スライダーとフレーム番号を実際の表示内容へ戻す
            UpdateSequenceUi();
            _sequenceBusy = false;
        }

        // 再生中の送りでも、読み込みの間に再生が止まっていたら送った先で作り直す(止めたときの作り直しは
        // 送る前の画像に対して始まり、上の入れ替えで取り消されている)
        if (SequenceNavigation.RefreshesAnalysisAfterMove(refreshAnalysis, _playTimer?.IsEnabled == true))
        {
            RefreshAfterSequenceMove();
        }
    }

    private void RefreshAfterSequenceMove()
    {
        if (_currentImage is null)
        {
            return;
        }

        // ピラミッドは生成元フレームでしか使えない。フレーム送りでも作り直さないと
        // フレーム1以降が常に等倍描画になり、縮小表示のパン追従が破綻する
        if (_derivedImage is null && !Viewport.HasPyramidForCurrentFrame)
        {
            _ = BuildPyramidAsync(
                _currentImage, _loadCts?.Token ?? CancellationToken.None, Viewport.Frame);
        }

        if (Viewport.DisplayMode is ViewportDisplayMode.BayerColor
            or ViewportDisplayMode.ColorDevelop or ViewportDisplayMode.ChannelSplit)
        {
            _ = EnsureBayerPyramidAsync();
        }

        RefreshHistogram(Viewport.Roi is { PixelCount: > 0 } roi ? roi : null);

        // ラインプロファイル窓も、送った先の画像・フレームで同じ基準点・方向と送った後のROIで計算し直す
        // (送りで取り消した計算は結果を出さないので、開いたままだと送る前の断面が残る)
        RefreshLineProfile();
        RefreshProjections();
    }

    private void OnPlayToggleChanged(object sender, RoutedEventArgs e)
    {
        if (PlayToggle.IsChecked == true)
        {
            if (SequenceCount <= 1)
            {
                PlayToggle.IsChecked = false;
                return;
            }

            // 実行中は再生しない(始めても送れず、実行中に入ると再生は止まる)。理由を知らせる
            if (NotifySequenceBusy())
            {
                PlayToggle.IsChecked = false;
                return;
            }

            PlayToggle.Content = "⏸ 停止";
            _playTimer ??= new DispatcherTimer();
            _playTimer.Tick -= OnPlayTick;
            _playTimer.Tick += OnPlayTick;
            _playTimer.Interval = TimeSpan.FromSeconds(1.0 / CurrentPlaybackFps());
            _playTimer.Start();
        }
        else
        {
            StopPlayback();
        }
    }

    /// <summary>
    /// 再生フレームレート。一覧の選択と手入力のどちらも同じ経路で読む。
    /// </summary>
    /// <returns>0.1〜240のフレームレート。</returns>
    private double CurrentPlaybackFps()
    {
        return FpsInput.Parse(FpsCombo.Text, DefaultPlaybackFps);
    }

    private void OnFpsChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ApplyPlaybackFps();
    }

    private void OnFpsTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // 手入力(編集可能コンボ)はSelectionChangedが出ないのでこちらで拾う
        ApplyPlaybackFps();

        // 読めない・0 以下・範囲外の入力は既定値・範囲へ落として再生する。以前は黙って落としたので、打った値と
        // 違う速さで再生していることが見えなかった。ファイル一覧の絞り込み欄と同じく赤枠とツールチップの理由で示す。
        // 一覧から選んだときも欄の文字が変わってここへ来る(SelectionChanged の時点では Text がまだ前の値)
        FpsInput.Parse(FpsCombo.Text, DefaultPlaybackFps, out string? notice);
        InputFeedback.SetError(FpsCombo, notice);
    }

    private void ApplyPlaybackFps()
    {
        // 再生中の変更も即座にタイマー間隔へ反映する(開始時にしか読まないと
        // 表示上の値と実際の再生速度が食い違う)
        if (_playTimer?.IsEnabled == true)
        {
            _playTimer.Interval = TimeSpan.FromSeconds(1.0 / CurrentPlaybackFps());
        }
    }

    /// <summary>
    /// ファイル連番の送り先を表示できなかった(読めない・寸法が違う)とき、再生を止めて理由をステータスバーに出す。
    /// </summary>
    /// <remarks>
    /// 再生は毎ティック次のファイルを要求するので、止めないと同じファイルの読み込みを繰り返して先へ進まない。
    /// 送り先は飛ばさず、表示中の画像と位置はそのまま残す(TIFFのページ送りが読めないページで再生を止めるのと同じ)。
    /// </remarks>
    /// <param name="reason">送れなかった理由(<see cref="SequenceFileLoad"/>)。</param>
    private void NotifySequenceMoveFailed(string reason)
    {
        bool wasPlaying = _playTimer?.IsEnabled == true;
        StopPlayback();
        _vm.ImageInfoText = wasPlaying ? $"{reason}(再生を止めました)" : reason;
    }

    private void StopPlayback()
    {
        bool wasPlaying = _playTimer?.IsEnabled == true;
        _playTimer?.Stop();
        if (PlayToggle is not null)
        {
            PlayToggle.IsChecked = false;
            PlayToggle.Content = "▶ 再生";
        }

        if (wasPlaying)
        {
            RefreshAfterSequenceMove();
        }
    }

    private void OnPlayTick(object? sender, EventArgs e)
    {
        if (!_sequenceBusy && _busyDepth == 0)
        {
            _ = ShowSequenceIndexAsync(_sequenceIndex + 1, refreshAnalysis: false);
        }
    }

    private void OnSeqSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSequenceUi || !_vm.HasSequence)
        {
            return;
        }

        bool playing = _playTimer?.IsEnabled == true;
        _ = ShowSequenceIndexAsync((int)Math.Round(e.NewValue), refreshAnalysis: !playing);
    }

    private void OnSeqFirstClick(object sender, RoutedEventArgs e)
    {
        _ = ShowSequenceIndexAsync(0, refreshAnalysis: true);
    }

    private void OnSeqPrevClick(object sender, RoutedEventArgs e)
    {
        _ = ShowSequenceIndexAsync(_sequenceIndex - 1, refreshAnalysis: true);
    }

    private void OnSeqNextClick(object sender, RoutedEventArgs e)
    {
        _ = ShowSequenceIndexAsync(_sequenceIndex + 1, refreshAnalysis: true);
    }

    private void OnSeqLastClick(object sender, RoutedEventArgs e)
    {
        _ = ShowSequenceIndexAsync(SequenceCount - 1, refreshAnalysis: true);
    }
}

using System.IO;
using System.Windows;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.Views;
using RawAnalyzer.Core;

namespace RawAnalyzer.App;

public partial class MainWindow
{
    // 欠陥検出リストはこの画像・フレーム・Bayerパターンでのみ有効。
    // 別画像(HDR派生ビュー・ファイル送り後)や別のパターンへ適用すると無関係な画素を壊すため、
    // 結果の採用時と補正時に一致を検証する(右パネルでパターンを変えたら破棄する)
    private DefectDetectionSource? _defectSource;

    /// <summary>
    /// 欠陥検出の検出元画像への参照を手放す。
    /// </summary>
    /// <remarks>
    /// 表示画像を差し替えたら検出結果は流用できない(補正適用時は
    /// <see cref="DefectDetectionSource.IsCurrent"/> で弾かれる)。参照を残すと破棄済み画像のヒープ側画素配列
    /// (最大約200MB)が回収されないため、差し替えのたびに明示的に切る。
    /// </remarks>
    private void ClearDefectSource()
    {
        _defectSource = null;
    }

    // ---- 欠陥画素検出 ----

    private DefectPixelWindow? _defectWindow;

    private void OnDefectDetectClick(object sender, RoutedEventArgs e)
    {
        if (ActiveImage is null)
        {
            return;
        }

        if (_defectWindow is null)
        {
            _defectWindow = new DefectPixelWindow { Owner = this };
            _defectWindow.RunRequested += OnDefectRunRequested;
            _defectWindow.DefectActivated += OnDefectActivated;
            _defectWindow.CorrectionRequested += OnDefectCorrectionRequested;
            _defectWindow.Closed += (_, _) =>
            {
                // 一覧はウィンドウと一緒に消えるので、検出元も手放す(開き直したウィンドウは未実行から始まる。
                // 残すと Bayer パターンの変更で、何も表示していないウィンドウに破棄の案内を出してしまう)
                _defectWindow = null;
                ClearDefectSource();
                Viewport.SetDefectMarkers(null);
            };
            _defectWindow.Show();
        }

        _defectWindow.Activate();
    }

    private void OnDefectRunRequested(double sigma, bool detectHot, bool detectDead)
    {
        // 比較モード中は、開いたままのウィンドウからの検出も断る(メニュー・キーと同じく、比較画面に隠れた
        // 通常表示の画像を検出してしまう)
        if (RejectWhileComparing("欠陥画素検出"))
        {
            _defectWindow?.ResetRunButton();
            return;
        }

        if (ActiveImage is null)
        {
            _defectWindow?.ResetRunButton();
            return;
        }

        // 読み込み中・縮小表示の作成中・他の処理の実行中は始めない(ビニング・フィルタ・補正・演算と同じ)。
        // HDR分割・合成の計算中に始めると、進捗表示の中で派生ビューが表示され、検出結果は表示中の画像の
        // ものではなくなって黙って捨てられる。理由を知らせ、実行ボタンを押せる状態へ戻す
        if (RejectWhileBusy("欠陥画素検出"))
        {
            _defectWindow?.ResetRunButton();
            return;
        }

        // 行交互HDRの Raw 表示は、長秒と短秒の行を1つの統計で判定して欠陥を見逃すので、分割ビューへ案内する
        if (_derivedImage is null && _currentFormat is not null
            && HdrExposureMix.DefectDetectionRefusal(_currentFormat, derivedViewShown: false) is { } hdrRefusal)
        {
            MessageBox.Show(this, hdrRefusal, "欠陥画素検出", MessageBoxButton.OK, MessageBoxImage.Information);
            _defectWindow?.ResetRunButton();
            return;
        }

        using BusyScope busy = EnterBusy();
        RawImage image = ActiveImage;
        int frame = Viewport.Frame;
        int maxCode = (1 << image.Format.BitDepth) - 1;

        // HDR分割ビューは露光の段を左右に並べた1枚なので、段ごとに統計と閾値を求める
        // (1つの統計にすると露光差で σ が膨らみ、閾値が値域の外へ出て欠陥を見逃す)
        int segmentWidth = _derivedImage is not null && _hdrFloatImage is null ? _hdrSegmentWidth : 0;

        // Bayerはチャネル感度差で混合σが膨らみ閾値が値域外へ出るため、チャネル別に判定する
        BayerPattern pattern = ActiveFormat?.Bayer ?? BayerPattern.None;

        // このリストは「この画像・このフレーム・このBayerパターン」でのみ有効(採用時と補正時に検証する)
        var source = new DefectDetectionSource(image, frame, pattern);

        // 巨大画像では数十秒かかるため、進捗表示とキャンセルを付ける
        DefectDetectionResult? result = null;
        ProgressWindow progress = ProgressWindow.Run(
            this,
            "欠陥画素を検出中…",
            (report, ct) => Task.Run(
                () => result = DefectPixelDetector.Detect(
                    image, frame, sigma, detectHot, detectDead,
                    pattern: pattern, progress: report, cancellationToken: ct, segmentWidth: segmentWidth),
                ct));

        if (progress.Error is not null)
        {
            MessageBox.Show(this, $"検出に失敗しました: {progress.Error.Message}", "欠陥画素検出",
                MessageBoxButton.OK, MessageBoxImage.Error);
            _defectWindow?.ResetRunButton();
            return;
        }

        // 検出中に表示中の画像・フレーム・Bayerパターンが替わっていたら、結果は表示中の画像を
        // いまの条件で検出したものではないので採用しない(旧条件の一覧で補正させない)
        if (progress.WasCanceled || result is null || _defectWindow is null
            || !source.IsCurrent(ActiveImage, Viewport.Frame, ActiveFormat?.Bayer ?? BayerPattern.None))
        {
            _defectWindow?.ResetRunButton();
            return;
        }

        // HDR表示中の検出結果は派生ビューの座標・画素のもので、HDR素材(Raw表示を含む)の補正は断る。
        // 一覧・移動・コピー・CSV は使えるようにし、断られるだけの補正ボタンは有効にせず理由を示す
        _defectWindow.ShowResult(result, maxCode,
            correctionUnavailableReason: _currentFormat is null ? null
                : DefectCorrectionAvailability.Refusal(
                    _compareMode, _currentFormat, _derivedImage is not null, _colorImage is not null));
        _defectSource = source;
        Viewport.SetDefectMarkers(result.Defects);
    }

    private void OnDefectActivated(DefectPixel defect)
    {
        // 比較モード中は、移動先の通常表示が比較画面に隠れていて何も見えないので、黙って動かさず理由を示す
        if (RejectWhileComparing("欠陥画素検出"))
        {
            return;
        }

        // 欠陥は元画像の座標。チャネル分割表示ではその画素が並ぶ象限上の位置へ移動する。
        // 画素値が重ねて見えるズーム(デバイス基準の 32 倍)まで拡大する
        Viewport.CenterOnSourcePixel(defect.X, defect.Y, Math.Max(Viewport.Zoom, Viewport.RawOverlayZoom));
    }

    // ---- ノイズ / ダイナミックレンジ測定 ----

    private NoiseMeasureDialog? _noiseWindow;

    private void OnNoiseMeasureClick(object sender, RoutedEventArgs e)
    {
        if (ActiveImage is null || ActiveFormat is null)
        {
            return;
        }

        if (_noiseWindow is null)
        {
            _noiseWindow = new NoiseMeasureDialog(
                NoiseTargetName(),
                _currentFolder,
                (1 << ActiveFormat.BitDepth) - 1,
                HasAnalyzableRoi,
                ExpectedReferenceSize(),
                new AnalysisSource(ActiveImage, Viewport.Frame),
                TargetRawFileSize())
            {
                Owner = this,
            };
            _noiseWindow.MeasureRequested += OnNoiseMeasureRequested;
            _noiseWindow.Closed += (_, _) => _noiseWindow = null;
            _noiseWindow.Show();
        }
        else
        {
            UpdateNoiseWindowSource();
        }

        _noiseWindow.Activate();
    }

    private string NoiseSourceName()
    {
        string name = Path.GetFileName(_currentPath ?? "(画像)") + TiffPageNote;
        return _correctionLabel is null ? name : $"{name} [{_correctionLabel}]";
    }

    /// <summary>
    /// ノイズ測定ダイアログに出す対象名。マルチフレームなら測るフレームも示す(TIFFのページは
    /// <see cref="NoiseSourceName"/> に含まれる。画像演算・ビニング・フィルタのダイアログと同じ書式)。
    /// </summary>
    private string NoiseTargetName() =>
        NoiseSourceName() + (_tiffStack is null && ActiveImage is { } image
            ? CalculationFrameNote(image, Viewport.Frame) : "");

    /// <summary>
    /// 表示中の raw ファイルのバイト数(raw 参照の大きさの照合で、同じ形のファイルを警告しないのに使う)。
    /// raw でなければ0。
    /// </summary>
    private long TargetRawFileSize()
    {
        // 読み込んだときに取ったサイズ(ダイアログを開く・対象を替えるたびに UI スレッドで取り直さない)
        return _openedRawFormat is not null && _currentPath is not null
            ? Math.Max(0, _currentFileSize)
            : 0;
    }

    /// <summary>
    /// raw参照ファイルに期待するバイト数(ヘッダ+読むフレーム。HDR分割ビューのフレーム連結は全段)。不明なら0。
    /// </summary>
    private long ExpectedReferenceSize()
    {
        // HDR派生ビューでも2枚目の raw は元の raw ファイルの形式で読む(分割ビューは読んでから同じく分割する。
        // 合成ビューは断る)。並置画像の形式で求めると、行交互・フレーム連結では元のファイルとたまたま一致した
        if (_derivedImage is not null && _currentFormat is not null)
        {
            try
            {
                return HdrSplitComposite.ReferenceReadFormat(_currentFormat).RequiredBytes();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                // 格納レイアウトを決められない(派生ビューは分割できたので通常は起きない)。サイズは照合しない
                return 0;
            }
        }

        // raw の2枚目はファイルを読んだ形式で読む(処理結果の形式ではない。処理結果では2枚目の raw を断る)
        return _currentFormat is null
            ? 0
            : ReferenceImage.ExpectedRawSize(ReferenceImage.RawReadFormat(_currentFormat, _openedRawFormat));
    }

    /// <summary>
    /// ノイズ測定ダイアログの対象表示・飽和コード既定値・ROI有無を現在の画像に合わせる。
    /// </summary>
    private void UpdateNoiseWindowSource()
    {
        if (_noiseWindow is null || ActiveFormat is null)
        {
            return;
        }

        // 表示中の画像(派生ビューならその画像)・フレームが替わったら、ダイアログは前の画像・フレームの
        // 測定結果を消す(フレーム送りでは画像がそのままなので、フレームも含めて照合させる)
        _noiseWindow.UpdateSource(
            NoiseTargetName(),
            _currentFolder,
            (1 << ActiveFormat.BitDepth) - 1,
            HasAnalyzableRoi,
            ExpectedReferenceSize(),
            ActiveImage is { } image ? new AnalysisSource(image, Viewport.Frame) : null,
            TargetRawFileSize());
    }

    private void OnNoiseMeasureRequested(NoiseMeasureRequest request)
    {
        // 比較モード中は、開いたままのダイアログからの測定も断る(メニュー・キーと同じく、比較画面に隠れた
        // 通常表示の画像を測ってしまう)
        if (RejectWhileComparing("ノイズ測定"))
        {
            _noiseWindow?.ResetRunButton();
            return;
        }

        RawImage? image = ActiveImage;
        RawFormat? format = ActiveFormat;
        if (image is null || format is null)
        {
            _noiseWindow?.ResetRunButton();
            return;
        }

        if (RejectWhileImageReplacing("ノイズ測定"))
        {
            _noiseWindow?.ResetRunButton();
            return;
        }

        using BusyScope busy = EnterBusy();
        int frame = Viewport.Frame;
        SampleScaling? targetScaling = _valueScaling;

        // HDR分割ビューでは、2枚目の raw も元のファイルの形式で読んで対象Aと同じく分割・並置して比べる
        // (並置画像の形式のまま読むと別の画素同士の差分になる)。合成ビューでは raw の2枚目を断る
        bool rawReference = request.ReferencePath is not null && IsRawFile(request.ReferencePath);
        bool mergedView = _derivedImage is not null && _hdrFloatImage is not null;
        RawFormat? splitFormat = _derivedImage is not null && !mergedView && rawReference ? _currentFormat : null;
        RawFormat rawReadFormat = ReferenceImage.RawReadFormat(_currentFormat ?? format, _openedRawFormat);
        if (rawReference
            && NoiseReference.RawReferenceRefusal(_correctionLabel is not null, mergedView) is { } rawRefusal)
        {
            MessageBox.Show(this, rawRefusal, "ノイズ測定", MessageBoxButton.OK, MessageBoxImage.Warning);
            _noiseWindow?.ResetRunButton();
            return;
        }

        // 2枚目は常に先頭フレーム・先頭ページを読む。対象Aと同一ファイルの先頭を表示中に
        // 指定すると完全に同一のデータ同士になり、σ_temporal=0という
        // 誤った測定値が無警告で出てしまう。TIFFの2ページ目以降を表示中なら別データ。
        // 分割ビューで raw を指定したときは、2枚目もファイルの先頭から同じく分割するので、分割の元にした
        // フレーム(フレーム連結なら常に同じデータ)で比べる
        if (request.ReferencePath is not null
            && NoiseReference.ReadsSameDataAsTarget(
                request.ReferencePath, _currentPath, _derivedImage is not null && splitFormat is null,
                splitFormat is null ? frame : HdrSplitComposite.EquivalentTargetFrame(
                    splitFormat, _hdrSourceFrame.Frame),
                _tiffPageIndex))
        {
            MessageBox.Show(this,
                "2枚目に対象Aと同じファイルが指定されています。同一データ同士の差分は" +
                "常に0になり、時間ノイズを測定できません。別撮りのフレームを指定するか、" +
                "マルチフレーム・複数ページのファイルなら表示フレーム・ページを変えてください" +
                "(2枚目は先頭フレーム・先頭ページを使います)。",
                "ノイズ測定", MessageBoxButton.OK, MessageBoxImage.Warning);
            _noiseWindow?.ResetRunButton();
            return;
        }

        RegionOfInterest? roi = request.UseRoi && Viewport.Roi is { PixelCount: > 0 } r ? r : null;

        // ROIは表示されている画素へ対応づけて測る(チャネル分割では1チャネルの格子)。
        // 対応づけられないROIを画像全体などで代用すると「ROI内」の値と誤読される
        RoiAnalysisTarget target = ResolveRoiTarget(image, roi);
        if (target is UnsupportedRoiTarget unsupported)
        {
            MessageBox.Show(this, unsupported.Reason, "ノイズ測定",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            _noiseWindow?.ResetRunButton();
            return;
        }

        // HDR素材で露光の違う行・段を1つの集合として測ると、露光差が σ_FPN・DR に乗る。行交互の Raw 表示は
        // 分割ビューへ、分割ビューでは段の中の ROI へ案内する
        if (HdrExposureMix.NoiseRefusal(
                _derivedImage is null && _currentFormat is not null && HdrExposureMix.InFrame(_currentFormat),
                _derivedImage is not null && !mergedView ? _hdrSegmentWidth : 0,
                target) is { } hdrRefusal)
        {
            MessageBox.Show(this, hdrRefusal, "ノイズ測定", MessageBoxButton.OK, MessageBoxImage.Warning);
            _noiseWindow?.ResetRunButton();
            return;
        }

        NoiseMeasurement measurement = default;

        ProgressWindow result = ProgressWindow.Run(
            this,
            "ノイズを測定中…",
            (progress, ct) => Task.Run(() =>
            {
                if (request.ReferencePath is null)
                {
                    measurement = RoiAnalysis.MeasureNoise(
                        image, null, frame, target, format.Bayer, request.SaturationCode, ct);
                    return;
                }

                // 32bit TIFF などを値域で写した2枚目は、対象と係数が同じときだけ使う(違えば理由を示して断る。
                // ビット深度はどちらも16なので MeasurePair の検査では見分けられない)
                using RawImage reference = splitFormat is not null
                    ? HdrSplitComposite.LoadReference(request.ReferencePath, splitFormat, targetScaling, ct)
                    : ReferenceImage.Load(
                        request.ReferencePath, rawReference, rawReadFormat, targetScaling, ct);
                measurement = RoiAnalysis.MeasureNoise(
                    image, reference, frame, target, format.Bayer,
                    request.SaturationCode, ct);
            }, ct));

        if (result.Error is not null)
        {
            MessageBox.Show(this, $"測定に失敗しました: {result.Error.Message}", "ノイズ測定",
                MessageBoxButton.OK, MessageBoxImage.Error);
            _noiseWindow?.ResetRunButton();
            return;
        }

        if (result.WasCanceled)
        {
            _noiseWindow?.ResetRunButton();
            return;
        }

        // 1チャネルの格子はそのチャネルのσそのもの(チャネル別に分けて合成していない)
        _noiseWindow?.ShowResult(
            measurement, format.BitDepth,
            request.ReferencePath is null ? null : Path.GetFileName(request.ReferencePath),
            perChannel: format.Bayer != BayerPattern.None && target is not ChannelRoiTarget);
    }

    private async void OnDefectCorrectionRequested(
        DefectDetectionResult detection, DefectCorrectionMethod method)
    {
        if (_currentImage is null || _currentFormat is null)
        {
            _defectWindow?.ResetRunButton();
            return;
        }

        // 比較モード中(開いたままのウィンドウからの補正)は、比較画面に隠れた通常表示の画像を差し替えるので断る。
        // HDR素材(派生ビューの表示中、HDR方式を指定した raw の Raw 表示)とカラー画像は補正しない。行交互では
        // 同色近傍に露光の違う行が混ざり、結果は HDR 方式を失う(ビニング・フィルタと同じく分割して開くよう案内する)。
        // カラー画像は輝度で補正するので、結果がグレーになりカラーを失う
        if (DefectCorrectionAvailability.Refusal(
                _compareMode, _currentFormat, _derivedImage is not null, _colorImage is not null) is { } refusal)
        {
            MessageBox.Show(this, refusal,
                "欠陥画素補正", MessageBoxButton.OK, MessageBoxImage.Information);
            _defectWindow?.ResetRunButton();
            return;
        }

        // HDR分割ビューで検出→Raw表示へ戻す→補正、やファイル送り後の適用は
        // 座標系/データが異なるのに配列範囲内に収まるため、例外にならず
        // 健全画素を黙って上書きしてしまう。右パネルでBayerパターンを変えた後の適用も、
        // 前のパターンで欠陥とした画素を新しいパターンの近傍で書き換える(変えた時点で一覧は
        // 破棄するが、ここでも確かめる)。検出時の画像・フレーム・パターンと一致しない
        // リストの適用は拒否する
        if (_defectSource is null
            || !_defectSource.IsCurrent(_currentImage, Viewport.Frame, _currentFormat.Bayer))
        {
            MessageBox.Show(this,
                "この検出結果は現在表示中の画像・フレーム・Bayerパターンで検出したものではないため" +
                "適用できません。再度「検出実行」を行ってください。",
                "欠陥画素補正", MessageBoxButton.OK, MessageBoxImage.Information);
            _defectWindow?.ResetRunButton();
            return;
        }

        // 読み込み中・縮小表示の作成中・他の処理の実行中は始めない(ビニング・フィルタと同じ)。
        // HDR分割・合成の計算中に始めると、進捗表示の中で派生ビューが表示され、その後で元画像だけを
        // 補正結果へ差し替えてしまう。黙って無視せず理由を知らせる
        if (RejectWhileBusy("欠陥画素補正"))
        {
            _defectWindow?.ResetRunButton();
            return;
        }

        using BusyScope busy = EnterBusy();
        RawImage source = _currentImage;

        // 近傍は検出と同じパターンで選ぶ(上で一致を確かめた)
        BayerPattern pattern = _currentFormat.Bayer;
        int frame = Viewport.Frame;
        int count = detection.Defects.Count;

        RawImage? corrected = null;
        ProgressWindow result = ProgressWindow.Run(
            this,
            $"欠陥画素を補正中 ({count} 画素)",
            (progress, ct) => Task.Run(() =>
            {
                corrected = DefectCorrector.Correct(
                    source, detection.Defects, pattern, method, frame, progress, ct);
            }, ct));

        if (result.Error is not null)
        {
            corrected?.Dispose();
            MessageBox.Show(this, $"欠陥補正に失敗しました: {result.Error.Message}", "欠陥画素補正",
                MessageBoxButton.OK, MessageBoxImage.Error);
            _defectWindow?.ResetRunButton();
            return;
        }

        if (result.WasCanceled || corrected is null)
        {
            corrected?.Dispose();
            _defectWindow?.ResetRunButton();
            return;
        }

        string methodLabel = method == DefectCorrectionMethod.Mean ? "平均" : "メディアン";

        // 補正前の一覧は差し替えで破棄する(残すと「この欠陥を補正」を押せても表示中の画像のもの
        // ではないと断られるだけ)。欠陥ウィンドウには補正したことと、補正後の画像は検出し直して
        // 確かめることを示す(一覧がないので下の ResetRunButton でも補正ボタンは無効のまま)
        bool applied = await ApplyProcessedImageAsync(
            source, corrected, $"欠陥補正 {count}px ({methodLabel})",
            defectNotice: DefectPixelWindow.CorrectionAppliedNotice(count, methodLabel));
        _defectWindow?.ResetRunButton();
        if (!applied)
        {
            // 差し替えなかった(理由は表示済み)。一覧は前提を崩した差し替えの側で破棄されている
            return;
        }

        _vm.ImageInfoText = $"欠陥画素 {count} 個を{methodLabel}補間で補正しました" +
            "(保存すると補正後のデータが出力されます)" + ValueNoteSuffix;
    }
}

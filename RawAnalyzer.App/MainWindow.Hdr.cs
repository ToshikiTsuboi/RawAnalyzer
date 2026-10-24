using System.Windows;
using RawAnalyzer.App.Rendering;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;

namespace RawAnalyzer.App;

public partial class MainWindow
{
    // HDR分割/合成の派生ビュー
    private RawImage? _derivedImage;
    private HdrImage? _hdrFloatImage;

    // オーバーレイに出す表示中のHDR派生ビューの説明(派生画像と組で持ち、その派生画像の表示中だけ出す)と、
    // 最後に描画した間引きの縮小率
    private (RawImage Image, string Note)? _derivedViewNote;
    private int _overlayRenderedFactor = 1;
    private BayerPyramid? _derivedBayerPyramid;
    private DisplayParameters[]? _hdrFrameParams;
    private int _hdrSegmentWidth;

    /// <summary>
    /// HDR派生ビューの計算結果を適用してよいか判定する。
    /// </summary>
    /// <remarks>
    /// 分割・合成は数秒かかり、その間も表示モードは操作できる。元画像の差し替えだけでなく
    /// モード変更も見ないと、ユーザーが選び直した表示を計算結果が後から上書きしてしまう。
    /// 計算を始めたときのフォーマット(Bayer など派生ビューに効く値)とも照合し、替わっていたら結果を捨てて
    /// 理由を知らせ、表示モードを Raw 表示へ戻す(計算中は Bayer を選べなくしているので、多重防御。
    /// 判定は HdrViewReplacement)。
    /// </remarks>
    /// <param name="expectedModeIndex">開始時の表示モード(分割=4、合成=5)。</param>
    /// <param name="source">計算元の画像。</param>
    /// <param name="sourceFrame">計算元のフレーム(<see cref="CaptureHdrSourceFrame"/> の戻り値)。</param>
    /// <param name="sourceFormat">計算に使ったフォーマット(計算を始めたときの元画像のフォーマット)。</param>
    /// <returns>適用してよければtrue。</returns>
    private bool CanApplyHdrView(int expectedModeIndex, RawImage source, int sourceFrame, RawFormat sourceFormat)
    {
        HdrViewReplacement.Refusal refusal = HdrViewReplacement.Check(
            modeReselected: DisplayModeCombo.SelectedIndex != expectedModeIndex,
            sourceReplaced: !IsHdrSourceCurrent(source, sourceFrame),
            sourceFormat, _currentFormat);
        if (refusal == HdrViewReplacement.Refusal.FormatChanged)
        {
            // 表示モードの選択は分割・合成のまま、表示は計算前のまま残っている。そろえて Raw 表示へ戻す
            string operation = expectedModeIndex == 4 ? "HDR分割" : "HDR合成";
            MessageBox.Show(this, HdrViewReplacement.Explain(refusal, operation),
                operation, MessageBoxButton.OK, MessageBoxImage.Information);
            DisplayModeCombo.SelectedIndex = 0;
        }

        return refusal == HdrViewReplacement.Refusal.None;
    }

    /// <summary>
    /// HDR派生ビューの計算元(元画像とフレーム)が、いまも表示中の元画像・フレームか判定する。
    /// </summary>
    /// <param name="source">計算元の画像。</param>
    /// <param name="sourceFrame">計算元のフレーム(<see cref="CaptureHdrSourceFrame"/> の戻り値)。</param>
    /// <returns>計算を始めたときと同じならtrue。</returns>
    private bool IsHdrSourceCurrent(RawImage source, int sourceFrame)
    {
        return ReferenceEquals(source, _currentImage)
            && _hdrSourceFrame.IsCurrent(sourceFrame, Viewport.Frame, derivedViewShown: _derivedImage is not null);
    }

    // HDR派生ビュー(分割・合成)の元にした元画像のフレーム。
    // 派生ビューの計算とRaw表示への復帰(表示し直すフレーム)で参照する
    private readonly HdrSourceFrame _hdrSourceFrame = new();

    // HDR合成ビューの表示中に、合成ビューへ入る前(元画像・分割ビュー)の黒点・白点を控える。
    // 合成画像は黒レベル減算済みで合成域のフルスケールを65535へ量子化しているので、合成ビューの表示黒点は0、
    // 表示白点は65535から始める
    private readonly MergedViewLevels _mergedViewLevels = new();

    // HDR分割・合成の計算中(開始から、派生ビューへ差し替えるか採用せずに終えるまで)の数。
    // 計算は始めたときのフォーマットで行うので、計算中は右パネルの Bayer の選択を無効にする
    private int _hdrComputations;

    /// <summary>
    /// HDR分割・合成の計算中に入る。戻り値を using で受けること。抜けると(採用・取り消し・失敗・元画像の
    /// 差し替えのどれで終えても)計算中でなくなる。
    /// </summary>
    /// <returns>計算中を抜けるスコープ。</returns>
    private HdrComputationScope BeginHdrComputation()
    {
        _hdrComputations++;
        _vm.IsHdrComputing = true;
        return new HdrComputationScope(this);
    }

    /// <summary>HDR分割・合成の計算中のスコープ。Disposeで抜ける。</summary>
    private readonly struct HdrComputationScope : IDisposable
    {
        private readonly MainWindow _owner;

        internal HdrComputationScope(MainWindow owner)
        {
            _owner = owner;
        }

        /// <summary>スコープを抜ける。</summary>
        public void Dispose()
        {
            _owner._hdrComputations--;
            _owner._vm.IsHdrComputing = _owner._hdrComputations > 0;
        }
    }

    /// <summary>
    /// HDR分割・合成の元にする元画像のフレーム番号を決めて控える。
    /// </summary>
    /// <remarks>
    /// 行交互HDRは1フレームが全露光を含む1回の撮影なので、表示中のフレームを分割する。
    /// 派生ビューの表示中は Viewport.Frame が派生画像(常に0)を指すため、
    /// 分割⇔合成の切替では派生ビューの元にしたフレームを引き継ぐ。
    /// </remarks>
    /// <returns>元画像のフレーム番号。</returns>
    private int CaptureHdrSourceFrame()
    {
        return _hdrSourceFrame.Capture(Viewport.Frame, derivedViewShown: _derivedImage is not null);
    }

    /// <summary>
    /// HDR分割・合成の計算を取り消した・結果を捨てたときに、表示モードの選択が計算を始めたモードのままなら
    /// Raw 表示へ戻す(選び直されていればそのまま残す)。
    /// </summary>
    /// <param name="computingModeIndex">計算を始めた表示モード(分割=4、合成=5)。</param>
    private void ResetHdrModeSelection(int computingModeIndex)
    {
        if (DisplayModeCombo.SelectedIndex == computingModeIndex)
        {
            DisplayModeCombo.SelectedIndex = 0;
        }
    }

    private async Task EnterHdrSplitAsync()
    {
        // 読み込み中・縮小表示の作成中・他の処理(HDR分割・合成の計算を含む)の実行中は始めない。
        // ビニング・フィルタ・欠陥検出/補正・画像演算と同じく理由を示し、表示モードの選択も戻す
        if (RejectWhileBusy("HDR分割表示"))
        {
            DisplayModeCombo.SelectedIndex = 0;
            return;
        }

        // 再生タイマーの連番送りと競合すると、Split中の画像が背後で破棄される
        using BusyScope busy = EnterBusy();

        // 計算は待つ間も操作を受け付け、完了すると表示画像を派生ビューへ差し替える。その間は保存・バッチ書き出し・
        // ノイズ測定を始めさせない(始めると、そのダイアログ・進捗表示の中で対象の画像が入れ替わる・破棄される)
        using IDisposable replacing = _imageGate.BeginReplacement();

        // 計算は下で決めるフォーマットで行うので、計算中は右パネルの Bayer を選べなくする。採用されずに終えたら
        // 戻す(派生ビューへ差し替えたら、派生ビューの表示中として無効のまま)
        using HdrComputationScope computing = BeginHdrComputation();
        RawImage image = _currentImage!;
        int sourceFrame = CaptureHdrSourceFrame();

        // フォーマットパネルで変更したBayerパターンやHDR方式を反映する
        // (image.Format は読み込み時のまま固定なので _currentFormat を渡す)。適用時にもこのフォーマットのままか照合する
        RawFormat splitFormat = _currentFormat!;

        // 計算は表示中の画像の世代(_loadCts)のトークンで走らせる。別のファイルを開く・ウィンドウを閉じると世代が
        // 進んで取り消され、開く要求は計算の終わりを待たずに表示へ進める(全画素のコピーを最後まで走らせない)
        CancellationToken ct = _loadCts?.Token ?? CancellationToken.None;
        IReadOnlyList<RawImage> frames;
        try
        {
            frames = await Task.Run(() => HdrSplitter.Split(image, splitFormat, sourceFrame, ct), ct);
        }
        catch (Exception ex) when (TaskRaceGuard.IsAbandoned(ex))
        {
            // 別ファイルへの切替で取り消された・元画像が破棄された。結果は不要
            // (InvalidOperationExceptionの派生なので先に受ける)。計算中に表示モードを選び直して取り消されたときは
            // (合成ビューからRaw表示へ戻すなど)、選び直した表示モードを残す
            ResetHdrModeSelection(4);
            return;
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, "HDR分割", MessageBoxButton.OK, MessageBoxImage.Warning);
            DisplayModeCombo.SelectedIndex = 0;
            return;
        }

        if (!CanApplyHdrView(4, image, sourceFrame, splitFormat))
        {
            foreach (RawImage frame in frames)
            {
                frame.Dispose();
            }

            return;
        }

        // 長秒/短秒を左右並置した合成画像を作る(ノイズ測定の2枚目の並置と同じ HdrSplitComposite.Compose)
        int stages = frames.Count;
        int subWidth = frames[0].Width;
        RawImage composite;
        try
        {
            composite = await Task.Run(() => HdrSplitComposite.Compose(frames, splitFormat, ct), ct);
        }
        catch (Exception ex) when (TaskRaceGuard.IsAbandoned(ex))
        {
            // 別ファイルを開いた・ウィンドウを閉じた・表示モードを選び直したので取り消された。結果は不要
            ResetHdrModeSelection(4);
            return;
        }
        finally
        {
            foreach (RawImage frame in frames)
            {
                frame.Dispose();
            }
        }

        if (!CanApplyHdrView(4, image, sourceFrame, splitFormat))
        {
            composite.Dispose();
            return;
        }

        if (!await ApplyDerivedViewAsync(composite, image, sourceFrame, merged: false))
        {
            return;
        }

        _hdrSegmentWidth = subWidth;
        _hdrFrameParams = new DisplayParameters[stages];
        DisplayParameters current = CurrentDisplayParameters();
        for (int i = 0; i < stages; i++)
        {
            _hdrFrameParams[i] = current;
        }

        HdrTargetCombo.Items.Clear();
        HdrTargetCombo.Items.Add("全体");
        HdrTargetCombo.Items.Add("長秒");
        if (stages == 3)
        {
            HdrTargetCombo.Items.Add("中秒");
        }

        HdrTargetCombo.Items.Add("短秒");
        HdrTargetCombo.SelectedIndex = 0;
        _vm.HdrTargetVisible = true;
        ApplySplitLuts();
        SetDerivedViewNote(LevelOverlay.DescribeSplit(stages));
    }

    private async Task EnterHdrMergeAsync()
    {
        // すでに合成ビュー表示中ならRaw表示へ戻すだけ(再計算しない)
        if (_hdrFloatImage is not null && _derivedImage is not null)
        {
            Viewport.SetDisplayMode(ViewportDisplayMode.Raw);
            return;
        }

        // 読み込み中・縮小表示の作成中・他の処理(HDR分割・合成の計算を含む)の実行中は始めない。
        // ビニング・フィルタ・欠陥検出/補正・画像演算と同じく理由を示し、表示モードの選択も戻す
        if (RejectWhileBusy("HDR合成表示"))
        {
            DisplayModeCombo.SelectedIndex = 0;
            return;
        }

        // 再生タイマーの連番送りと競合すると、Split/Merge中の画像が背後で破棄される
        using BusyScope busy = EnterBusy();

        // 完了すると表示画像を派生ビューへ差し替える。その間は保存などを始めさせない(HDR分割と同じ)
        using IDisposable replacing = _imageGate.BeginReplacement();

        // 計算中は右パネルの Bayer を選べなくする(HDR分割と同じ。適用時にもこのフォーマットのままか照合する)
        using HdrComputationScope computing = BeginHdrComputation();
        RawImage image = _currentImage!;
        RawFormat format = _currentFormat!;
        int sourceFrame = CaptureHdrSourceFrame();

        // 元画像(分割ビューからの切替では分割フレーム。どちらも未減算)の黒点を減算して合成する。
        // 計算中に黒レベルを動かしても、合成に使う値はここで決める(UIの状態を計算のスレッドから読まない)
        ushort mergeBlackPoint = _blackPoint;

        // 計算は表示中の画像の世代(_loadCts)のトークンで走らせる(HDR分割と同じ。別のファイルを開く・
        // ウィンドウを閉じると取り消され、分割・線形化・合成・量子化を最後まで走らせない)
        CancellationToken ct = _loadCts?.Token ?? CancellationToken.None;
        HdrImage merged;
        RawImage quantized;
        try
        {
            (merged, quantized) = await Task.Run(() =>
            {
                IReadOnlyList<RawImage> frames = HdrSplitter.Split(image, format, sourceFrame, ct);
                try
                {
                    HdrImage result = HdrMerger.Merge(frames, new HdrMergeParameters(
                        format.ExposureRatio, mergeBlackPoint), ct);
                    return (result, result.ToRawImage16(ct));
                }
                finally
                {
                    foreach (RawImage frame in frames)
                    {
                        frame.Dispose();
                    }
                }
            }, ct);
        }
        catch (Exception ex) when (TaskRaceGuard.IsAbandoned(ex))
        {
            // 別ファイルへの切替で取り消された・元画像が破棄された。結果は不要
            // (InvalidOperationExceptionの派生なので先に受ける)。計算中に表示モードを選び直して取り消されたときは
            // (分割ビューからRaw表示・Bayer表示へ戻すなど)、選び直した表示モードを残す
            ResetHdrModeSelection(5);
            return;
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, "HDR合成", MessageBoxButton.OK, MessageBoxImage.Warning);
            DisplayModeCombo.SelectedIndex = 0;
            return;
        }

        if (!CanApplyHdrView(5, image, sourceFrame, format))
        {
            quantized.Dispose();
            return;
        }

        if (!await ApplyDerivedViewAsync(quantized, image, sourceFrame, merged: true))
        {
            return;
        }

        _hdrFloatImage = merged;

        // 派生ビュー適用時点ではまだ合成結果を持っていないのでバッジを出し直す
        UpdateProcessingBadge();
        _vm.HdrTargetVisible = false;

        // 16bit量子化で情報が落ちる構成では、解析値がその精度で読まれることを明示する。
        // 16bitの合成画像は黒点未満を0に切り詰めている(float raw は負値のまま)ので、暗部の統計が偏ることも示す
        SetDerivedViewNote(LevelOverlay.DescribeMerge(merged));
    }

    /// <summary>
    /// 表示中のHDR派生ビューの説明をオーバーレイに出す。描画のたびの間引きレベルの表示で消さず、派生ビューを
    /// 抜けるまで(表示中の派生画像がこの画像のあいだ)残す。
    /// </summary>
    /// <param name="note">派生ビューの説明。</param>
    private void SetDerivedViewNote(string note)
    {
        _derivedViewNote = _derivedImage is { } derived ? (derived, note) : null;
        UpdateLevelOverlay();
    }

    /// <summary>
    /// オーバーレイを、表示中の派生ビューの説明・フルスクリーンの案内と、最後に描画した間引きレベルで出し直す。
    /// </summary>
    private void UpdateLevelOverlay()
    {
        string? derivedNote = _derivedViewNote is { } note && ReferenceEquals(note.Image, _derivedImage)
            ? note.Note
            : null;
        _vm.LevelOverlayText = LevelOverlay.Describe(
            _overlayRenderedFactor, derivedNote, _vm.IsFullscreen ? LevelOverlay.FullscreenHint : null);
    }

    /// <summary>
    /// HDR分割・合成の計算結果(派生画像)を派生ビューとして表示する。
    /// </summary>
    /// <remarks>
    /// 呼び出し側は <see cref="CanApplyHdrView"/> で適用してよいか判定してから呼ぶ。旧派生画像を読んでいる
    /// 描画の停止を待つ間に元画像・元フレームが替わっていたら、結果を破棄して派生ビューへ差し替えない。
    /// </remarks>
    /// <param name="derived">派生画像。適用しなかったときは破棄する。</param>
    /// <param name="source">計算元の画像。</param>
    /// <param name="sourceFrame">計算元のフレーム(<see cref="CaptureHdrSourceFrame"/> の戻り値)。</param>
    /// <param name="merged">
    /// HDR合成の画像(黒レベル減算済み)か。falseはHDR分割の画像(各フレームは黒レベル未減算)。
    /// </param>
    /// <returns>派生ビューへ差し替えた場合はtrue。</returns>
    private async Task<bool> ApplyDerivedViewAsync(
        RawImage derived, RawImage source, int sourceFrame, bool merged)
    {
        StopPlayback();
        _vm.HasSequence = false;

        // 旧派生画像を読んでいる描画・解析・縮小ピラミッドの生成を止めてから破棄する
        CancelAnalysis();
        _derivedPyramidBuild.Cancel();
        if (_derivedImage is not null)
        {
            // 分割⇔合成の切替では旧派生画像用のBayerピラミッドの生成も取り消す(Raw表示から入るときは
            // 派生画像用の生成は走っていない。元画像の縮小ピラミッドの生成はRaw表示へ戻ったときに使うので残す)
            CancelDerivedBayerPyramidBuild();
        }

        await Viewport.ClearImageAsync();

        // 適用の直前に、計算元の元画像・フレームのままか、表示モードを選び直していないかを確かめ直す。描画の停止を
        // 待つ間に元画像が替わった・選び直したら、結果は捨てる。以前は選び直しを見ず、待つ間に Raw表示を選んでも
        // 選択は Raw表示のまま派生ビューを表示した。選び直した表示を誰も表示し直さないとき(停止のために外した画像が
        // ビューポートに戻らないとき)は、ここで選ばれている表示で表示し直す(HdrViewReplacement.CheckAdoption)
        HdrViewReplacement.Adoption adoption = HdrViewReplacement.CheckAdoption(
            sourceReplaced: !IsHdrSourceCurrent(source, sourceFrame),
            DisplayModeCombo.SelectedIndex, computedIndex: merged ? 5 : 4,
            viewportEmpty: Viewport.Image is null,
            derivedViewShown: _derivedImage is not null,
            mergedViewShown: _hdrFloatImage is not null);
        if (adoption != HdrViewReplacement.Adoption.Adopt)
        {
            derived.Dispose();
            ShowAfterDiscardedHdrView(adoption);
            return false;
        }

        // 旧派生画像のBayerピラミッドを残すと、EnsureBayerPyramidAsyncが
        // 非nullを見て早期returnし、新しい派生画像のピラミッドが二度と作られない
        _derivedBayerPyramid?.Dispose();
        _derivedBayerPyramid = null;
        _derivedImage?.Dispose();
        _derivedImage = derived;

        // 前の画像(元画像・前の派生ビュー)のヒストグラムを捨てる。下の RefreshHistogram が終わるまでに自動コントラストを
        // 押すと、別のビット深度・別の画像の分布で黒/白を決めてしまう(画像を開く・処理結果へ差し替える経路と同じ)
        _histogram = null;
        _channelHistograms = null;

        // 派生ビューは計算を始めたときの Bayer で作った画像。表示中は右パネルの Bayer を選べなくする
        // (変えても派生ビューは古いパターンのままで、右パネルの表示と食い違う)。Raw表示へ戻ると選べる
        _vm.IsHdrViewShown = true;

        // 欠陥検出の結果は検出した画像(元画像、または前の派生ビュー)の座標・画素のもの。
        // 他の差し替え経路と同じく、派生ビューへ差し替えるこのUIターンで検出元を手放し、
        // マーカーと欠陥ウィンドウの一覧も破棄する(元画像座標のマーカーを派生ビューに重ねない)
        ClearDefectSource();
        Viewport.SetDefectMarkers(null);
        _defectWindow?.DiscardResult();
        _hdrFloatImage = null;
        _hdrFrameParams = null;
        _vm.HasRoi = false;

        // 派生ビューは元画像と座標の意味が違う(分割は段の並置)。カーソル位置の表示を消す
        ClearCursorReadout();

        // HDR合成の画像は黒レベル減算済みで、合成域のフルスケールを65535へ量子化している。元画像の黒点のまま
        // 表示すると黒を二重に引き、元画像で下げた白点のままだと合成で取り戻した高輝度を白飛びとして切るので、
        // 合成ビューへ入る前の黒点・白点を控えて表示黒点を0、表示白点を65535にする。合成 → 分割では控えた
        // 黒点・白点へ戻す(分割フレームは未減算で元画像と同じ値域)。合成ビューで動かした黒/白レベルは
        // 合成ビューの表示だけのもので、抜けるときに捨てる(ゲイン・ガンマ・コントラストは共有のまま)
        (_blackPoint, _whitePoint) = merged
            ? _mergedViewLevels.Enter(_blackPoint, _whitePoint)
            : _mergedViewLevels.Leave(_blackPoint, _whitePoint);

        // HDR合成は16bitになる。黒/白レベルの上限とコード値を、表示する画像のビット深度で表し直す
        SyncLevelControlsToActiveBitDepth();
        Viewport.SetDisplayMode(ViewportDisplayMode.Raw);
        Viewport.SetImage(derived, derived.Format);
        Viewport.SetLut(BuildLut());
        UpdateNoiseWindowSource();
        UpdateProcessingBadge();
        RefreshHistogram(roi: null);

        // 開いているラインプロファイル窓も、ヒストグラムと同じく派生ビューの画像で計算し直す
        // (同じ基準点・方向。派生ビューの寸法で範囲外なら範囲外であることを示す)
        RefreshLineProfile();
        RefreshProjections();
        _ = BuildDerivedPyramidAsync(derived);
        return true;
    }

    // HDR派生画像の縮小ピラミッドの生成。派生画像の差し替え・Raw表示への復帰で旧派生画像用の生成を取り消す
    private readonly DerivedPyramidBuild _derivedPyramidBuild = new();

    private async Task BuildDerivedPyramidAsync(RawImage derived)
    {
        // 取り消された・派生画像の破棄と競合した生成は null で終わる(投げっぱなしのタスクから例外を漏らさない)
        TilePyramid? pyramid = await _derivedPyramidBuild.CreateAsync(derived);
        if (pyramid is not null && ReferenceEquals(derived, _derivedImage))
        {
            Viewport.SetPyramid(pyramid);
        }
    }

    /// <summary>
    /// 派生ビューを抜ける・差し替えるときに、旧派生画像用のBayerピラミッドの生成を取り消す。
    /// </summary>
    /// <remarks>
    /// 派生ビューのBayer系表示(HDR合成のBayerカラー・カラー現像・チャネル分割)のBayerピラミッドは、
    /// EnsureBayerPyramidAsync が表示中の画像・フレームの世代(_loadCts)のトークンで作る。派生ビューの出入りでは
    /// 世代が進まないので、進めないとRaw表示へ戻っても分割⇔合成を切り替えても旧派生画像を読み続ける
    /// (結果は表示中の画像との照合で捨てられるだけ)。世代を進めると同じトークンで走る元画像の縮小ピラミッドの
    /// 生成も取り消されるので、派生画像用の生成が走り得る派生ビューの表示中だけ呼ぶ。
    /// 通常の読み込みが確定待ちのときは、開く要求(同じ世代のトークン)を取り消さないよう進めない
    /// (確定で派生画像ごと差し替わり、生成の結果は捨てられる)。
    /// </remarks>
    private void CancelDerivedBayerPyramidBuild()
    {
        if (!_imageGate.IsLoadPending)
        {
            ReplaceLoadCts(new CancellationTokenSource());
        }
    }

    private async Task RestoreMainImageAsync()
    {
        // 派生画像を読んでいる描画・解析・縮小ピラミッド(Bayerを含む)の生成を止めてから破棄する。
        // 描画の停止を待つ間も操作を受け付けるので、差し替えるまでは保存などを始めさせない
        CancelAnalysis();
        _derivedPyramidBuild.Cancel();
        CancelDerivedBayerPyramidBuild();
        using (_imageGate.BeginReplacement())
        {
            await Viewport.ClearImageAsync();
        }

        _derivedImage?.Dispose();
        _derivedImage = null;
        _derivedViewNote = null;
        _vm.IsHdrViewShown = false;

        // 派生ビューで検出した結果は、ここで破棄する派生画像のもの。HDR表示に入るときに元画像の
        // 結果も破棄しているので、Raw表示へ戻したら検出し直す(他の差し替え経路と同じ規約)。
        // マーカーと欠陥ウィンドウの一覧も破棄し、派生ビュー座標のマーカーを元画像に重ねない
        ClearDefectSource();
        Viewport.SetDefectMarkers(null);
        _defectWindow?.DiscardResult();
        _hdrFloatImage = null;
        _hdrFrameParams = null;
        _vm.HdrTargetVisible = false;
        _vm.HasRoi = false;

        // 派生ビューの座標で持っていたカーソル位置の表示を消す(元画像とは座標の意味が違う)
        ClearCursorReadout();

        // 合成ビューから戻るなら、合成ビューへ入る前の元画像の黒点・白点へ戻す(合成画像は黒レベル減算済みで
        // 値域も元画像と別物。合成ビューで動かした黒/白レベルは合成ビューの表示だけのもの)。分割ビューからはそのまま
        (_blackPoint, _whitePoint) = _mergedViewLevels.Leave(_blackPoint, _whitePoint);

        // HDR合成(16bit)から元画像のビット深度へ戻す。黒/白レベルのコード値を元画像のビット深度で表し直す
        SyncLevelControlsToActiveBitDepth();
        _derivedBayerPyramid?.Dispose();
        _derivedBayerPyramid = null;
        ShowMainImage();
    }

    /// <summary>
    /// 元画像を、HDR表示の元にしたフレームでビューポートへ表示し直す(表示モードは Raw 表示になる)。
    /// </summary>
    /// <remarks>
    /// Raw表示への復帰と、ビューポートから元画像を外して描画の停止を待つ間に表示モードを選び直され、HDR分割・合成の
    /// 結果を捨てたとき(<see cref="ShowAfterDiscardedHdrView"/>)に使う。解析・送りのUI・縮小ピラミッドも作り直す。
    /// </remarks>
    private void ShowMainImage()
    {
        // HDR表示の元にしたフレームを表示し直す(既定のフレーム0へ戻すと、分割・合成した
        // 撮影とは別のフレームになる)。シーケンスUIは下の DetectSequence が表示フレームに合わせる
        Viewport.SetImage(
            _currentImage!, _currentFormat!,
            _hdrSourceFrame.ResolveRestoreFrame(_currentImage!.FrameCount));

        // 生成元フレームを偽らずに渡す(別フレーム産はレンダラ側で使われない)
        Viewport.SetPyramid(_mainPyramid, _mainPyramidFrame);
        Viewport.SetBayerPyramid(_mainBayerPyramid, _mainBayerPyramidFrame);
        Viewport.SetLut(BuildLut());
        UpdateNoiseWindowSource();
        UpdateProcessingBadge();
        DetectSequence();

        // 派生ビューのヒストグラムを捨ててから元画像で作り直す(できるまでの自動コントラストは何もしない)
        _histogram = null;
        _channelHistograms = null;
        RefreshHistogram(roi: null);

        // 派生ビューで出していたラインプロファイルも、元画像(HDR表示の元にしたフレーム)で計算し直す
        RefreshLineProfile();
        RefreshProjections();

        // 派生ビューの出入り・分割⇔合成の切替で世代を進めると(CancelDerivedBayerPyramidBuild)、同じトークンで
        // 走っていた元画像の縮小ピラミッドの生成(フレーム送りの後に始めたものなど)も取り消される。表示し直した
        // フレームのピラミッドがなければ作り直す(作らないと次に送るまで等倍データから縮小描画する。性能ルール2)。
        // 読み込みの確定待ちなら、まもなく差し替わる元画像のためには作らない
        if (!Viewport.HasPyramidForCurrentFrame && !_imageGate.IsLoadPending)
        {
            _ = BuildPyramidAsync(_currentImage!, _loadCts?.Token ?? CancellationToken.None, Viewport.Frame);
        }
    }

    /// <summary>
    /// HDR分割・合成の結果を、描画の停止を待つ間に表示モードを選び直されて捨てたときに、停止のためにビューポートから
    /// 外した画像を、いま選ばれている表示モードで表示し直す(レビュー 2026-10-03 R4)。
    /// </summary>
    /// <remarks>
    /// 選び直した側は、派生ビューがなければ(合成ビューの表示を選び直したなら合成ビューのまま)ビューポートの表示モード
    /// だけを替え、外された画像を表示し直さない。表示し直すと表示モードは Raw 表示に戻るので、選択の表示へそろえる。
    /// 誰が表示し直すかの判定は <see cref="HdrViewReplacement.CheckAdoption"/>。
    /// </remarks>
    /// <param name="adoption">結果を捨てたときの判定。表示し直さない判定なら何もしない。</param>
    private void ShowAfterDiscardedHdrView(HdrViewReplacement.Adoption adoption)
    {
        if (adoption == HdrViewReplacement.Adoption.DiscardAndShowSource && _currentImage is not null)
        {
            ShowMainImage();
        }
        else if (adoption == HdrViewReplacement.Adoption.DiscardAndShowMergedView && _derivedImage is { } derived)
        {
            // 合成ビューの縮小ピラミッドは差し替えのために生成を取り消し、ビューポートからも外れた。作り直す
            Viewport.SetImage(derived, derived.Format);
            Viewport.SetBayerPyramid(_derivedBayerPyramid);
            Viewport.SetLut(BuildLut());
            RefreshHistogram(roi: null);
            RefreshLineProfile();
            RefreshProjections();
            _ = BuildDerivedPyramidAsync(derived);
        }
        else
        {
            return;
        }

        // 表示し直すと表示モードは Raw 表示に戻るので、いまの選択の表示へそろえる(Bayer なしで選び直しを断られた
        // ときなどは Raw 表示)
        ViewportDisplayMode mode = DisplayModeSelection.ForSelectedMode(
            DisplayModeCombo.SelectedIndex, _derivedImage is null && _colorImage is not null,
            ActiveFormat?.Bayer ?? BayerPattern.None, BayerPhaseMismatchShown).ViewportMode;
        if (mode == ViewportDisplayMode.ColorDevelop)
        {
            EnsureDevelopLuts();
        }

        Viewport.SetDisplayMode(mode);
        if (mode is ViewportDisplayMode.BayerColor or ViewportDisplayMode.ColorDevelop
            or ViewportDisplayMode.ChannelSplit)
        {
            _ = EnsureBayerPyramidAsync();
        }
    }
}

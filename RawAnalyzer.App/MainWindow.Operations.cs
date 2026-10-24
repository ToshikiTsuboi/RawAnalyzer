using System.Windows;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.ViewModels;

namespace RawAnalyzer.App;

public partial class MainWindow
{
    // 重い処理(保存/演算/バッチ/測定/検出)の実行中を数える。
    // ShowDialog は Dispatcher の入れ子ポンプなのでモーダル表示中もタイマーや
    // 入力イベントが動き続ける。処理対象の画像が背後で差し替え・破棄されるのを防ぐ。
    private int _busyDepth;

    // 通常の読み込み(OpenPath)と、表示画像を使う操作(EnterBusy)の排他。
    // 読み込みの確定待ちの間は操作を始めず、読み込みは操作の終了を待ってから確定する
    private readonly ImageOperationGate _imageGate = new();

    /// <summary>
    /// 重い処理の実行中スコープに入る。再生を止め、連番送りを抑止する。
    /// 表示画像を使う操作として数え、その間は通常の読み込みを確定させない。
    /// 戻り値を using で受けること。
    /// </summary>
    private BusyScope EnterBusy() => EnterBusyCore(_imageGate.EnterOperation());

    /// <summary>
    /// 通常の読み込み(OpenPath)用の実行中スコープに入る。再生を止め、連番送りを抑止するが、
    /// 表示画像を使う操作としては数えない(読み込み自身が操作の終了を待って確定するため)。
    /// 戻り値を using で受けること。
    /// </summary>
    private BusyScope EnterLoadBusy() => EnterBusyCore(operation: null);

    private BusyScope EnterBusyCore(IDisposable? operation)
    {
        CancelTiffPageLoad();

        // ファイル連番の送りの読み込みも取り消す(TIFFのページ送りと同じ。結果は実行中の判定で捨てるので、
        // 読み込みを最後まで走らせない)
        _sequenceLoadCts?.Cancel();
        StopPlayback();
        _busyDepth++;
        return new BusyScope(this, operation);
    }

    /// <summary>
    /// 表示画像の差し替えを待っている間(通常の読み込みの確定待ち、HDR分割・合成の計算など完了時に
    /// 表示画像を差し替えるモーダルでない処理の途中)なら、表示画像を使う操作を始めずに理由を知らせる。
    /// </summary>
    /// <remarks>
    /// 保存・バッチ書き出し・ノイズ測定は、ダイアログ・進捗表示(Dispatcher の入れ子ポンプ)の間も開始時の
    /// 画像を対象にし続ける。差し替えを待っている間に始めると、その中で差し替えが走り、操作のために作った
    /// 画面と別の画像を処理したり(HDR合成の計算中に保存を始めると派生ビューを保存していた)、処理中の画像が
    /// 破棄されたりする。縮小表示の作成中や、差し替えを終えた処理の後始末の間は始められる(BusyNotice.ForImageUse)。
    /// </remarks>
    /// <param name="operation">操作名(「保存」など)。</param>
    /// <returns>拒否した場合はtrue。</returns>
    private bool RejectWhileImageReplacing(string operation)
    {
        if (BusyNotice.ForImageUse(_imageGate.IsLoadPending, _imageGate.IsReplacementPending)
            is not { } reason)
        {
            return false;
        }

        MessageBox.Show(this, BusyNotice.ForOperation(reason, operation),
            "RawAnalyzer", MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
    }

    /// <summary>
    /// 実行中(読み込み・操作・縮小表示の作成、表示画像の差し替え待ち)なら、画像を処理する操作を始めずに理由を知らせる。
    /// </summary>
    /// <remarks>
    /// <see cref="RejectWhileImageReplacing"/> は表示画像の差し替えを待っている間だけを断る(保存などは縮小表示の
    /// 作成中や、差し替えを終えた他の操作の内側でも始められる)。実行中の処理すべてを断る操作(結果で表示中の画像を
    /// 差し替えるビニング・フィルタ・画像演算・欠陥補正、実行中の処理の完了で結果が表示中の画像の
    /// ものでなくなる欠陥検出、結果で派生ビューを表示するHDR分割・合成)は、何が実行中かを
    /// 同じ形のダイアログで知らせる(黙って無視しない)。busy スコープの外の差し替え待ち(Raw表示へ戻るときの
    /// 描画の停止待ち)も断る(BusyNotice.ForBusy)。その間に始めると、直後に破棄される派生画像を処理してしまう。
    /// </remarks>
    /// <param name="operation">操作名(「ビニング」など)。</param>
    /// <returns>拒否した場合はtrue。</returns>
    private bool RejectWhileBusy(string operation)
    {
        if (CurrentBusyReason() is not { } reason)
        {
            return false;
        }

        MessageBox.Show(this, BusyNotice.ForOperation(reason, operation),
            "RawAnalyzer", MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
    }

    /// <summary>
    /// 比較モード中なら、通常表示の画像を対象にする操作を始めずに理由を知らせる。
    /// </summary>
    /// <remarks>
    /// メニュー・キーからは比較モード中に開けない(<see cref="MainViewModel.CanUseMainView"/>)が、比較モードへ入る前に
    /// 開いたままの欠陥画素・ノイズ測定のウィンドウからは実行できた。比較画面に隠れた通常表示の画像を検出・補正・
    /// 測定してしまうので、コマンドパレットと同じ理由で断る。
    /// </remarks>
    /// <param name="operation">操作名(ダイアログの見出し)。</param>
    /// <returns>拒否した場合はtrue。</returns>
    private bool RejectWhileComparing(string operation)
    {
        if (!_compareMode)
        {
            return false;
        }

        MessageBox.Show(this, CommandDisabledReasons.CompareMode,
            operation, MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
    }

    /// <summary>
    /// 実行中(読み込み・操作・縮小表示の作成、表示画像の差し替え待ち)でフレームを送れないとき、理由をステータスバーに出す。
    /// </summary>
    /// <remarks>
    /// 送り(ボタン・キー・スライダー・再生)は続けて起こり得るので、ダイアログではなく
    /// ステータスバーで知らせる。送りの途中で始まった処理に譲って送りをやめる場合は、
    /// 利用者がその処理を始めたことで分かるので知らせない。
    /// </remarks>
    /// <returns>実行中で送れない場合はtrue。</returns>
    private bool NotifySequenceBusy()
    {
        if (CurrentBusyReason() is not { } reason)
        {
            return false;
        }

        _vm.ImageInfoText = BusyNotice.ForSequence(reason);
        return true;
    }

    /// <summary>
    /// 実行中(<see cref="_busyDepth"/> が正、または表示画像の差し替え待ち)である理由。実行中でなければ null。
    /// </summary>
    private BusyReason? CurrentBusyReason() =>
        BusyNotice.ForBusy(_busyDepth > 0, _imageGate.IsLoadPending, _imageGate.IsOperationRunning,
            _imageGate.IsReplacementPending);

    /// <summary>重い処理の実行中スコープ。Disposeで抜ける。</summary>
    private readonly struct BusyScope : IDisposable
    {
        private readonly MainWindow _owner;
        private readonly IDisposable? _operation;

        internal BusyScope(MainWindow owner, IDisposable? operation)
        {
            _owner = owner;
            _operation = operation;
        }

        /// <summary>スコープを抜ける。</summary>
        public void Dispose()
        {
            _owner._busyDepth--;
            _operation?.Dispose();
        }
    }
}

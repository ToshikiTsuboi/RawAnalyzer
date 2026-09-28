using RawAnalyzer.App.Services;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 読み込み・操作の実行中に受け付けなかった操作について、利用者へ示す理由の検証。
/// </summary>
public class BusyNoticeTests
{
    [Fact]
    public void Classify_TellsWhatIsRunning()
    {
        Assert.Equal(BusyReason.Loading, BusyNotice.Classify(loadPending: true, operationRunning: false));

        // 読み込みが操作の終了を待っている間も「読み込み中」
        Assert.Equal(BusyReason.Loading, BusyNotice.Classify(loadPending: true, operationRunning: true));
        Assert.Equal(BusyReason.Operation, BusyNotice.Classify(loadPending: false, operationRunning: true));

        // 実行中なのに読み込み・操作のどちらでもないのは、確定後の縮小表示の作成中
        Assert.Equal(BusyReason.Finishing, BusyNotice.Classify(loadPending: false, operationRunning: false));
    }

    [Fact]
    public void ForBusy_WaitingForImageReplacementOutsideBusyScope_IsOperation()
    {
        // Raw表示へ戻る処理は busy スコープの外で、派生画像を読む描画の停止を待ってから派生画像を破棄する。
        // 以前は busy スコープの内側だけを断っていたので、その間に欠陥検出などを始めると、直後に破棄される
        // 派生画像を読んで失敗し得た。理由は「他の処理の実行中」とする
        Assert.Equal(BusyReason.Operation, BusyNotice.ForBusy(
            inBusyScope: false, loadPending: false, operationRunning: false, replacementPending: true));

        // busy スコープの内側は従来どおりの分類。どちらでもなければ断らない
        Assert.Equal(BusyReason.Loading, BusyNotice.ForBusy(
            inBusyScope: true, loadPending: true, operationRunning: true, replacementPending: false));
        Assert.Equal(BusyReason.Operation, BusyNotice.ForBusy(
            inBusyScope: true, loadPending: false, operationRunning: true, replacementPending: true));
        Assert.Equal(BusyReason.Finishing, BusyNotice.ForBusy(
            inBusyScope: true, loadPending: false, operationRunning: false, replacementPending: false));
        Assert.Null(BusyNotice.ForBusy(
            inBusyScope: false, loadPending: false, operationRunning: false, replacementPending: false));
    }

    [Fact]
    public void ForOperation_WhileLoading_MatchesExistingOpeningMessage()
    {
        // 読み込みの確定待ちで保存などを断るときの既存の文言と同じにする
        Assert.Equal(
            "画像の読み込み中は保存を開始できません。読み込みの完了後にもう一度実行してください。",
            BusyNotice.ForOperation(BusyReason.Loading, "保存"));
    }

    [Fact]
    public void ForOperation_NamesOperationAndDistinguishesReasons()
    {
        // 以前はビニング・フィルタを実行中の処理があるときに黙って無視していた
        string operation = BusyNotice.ForOperation(BusyReason.Operation, "ビニング");
        string finishing = BusyNotice.ForOperation(BusyReason.Finishing, "ビニング");

        Assert.Contains("ビニング", operation);
        Assert.Contains("他の処理の実行中", operation);
        Assert.Contains("ビニング", finishing);
        Assert.Contains("縮小表示", finishing);
        Assert.NotEqual(operation, finishing);
    }

    [Fact]
    public void ForImageUse_RejectsWhileLoadingOrReplacingImage_AllowsWhileFinishing()
    {
        // 保存・バッチ書き出し・ノイズ測定は、ダイアログ・進捗表示の間も開始時の画像を対象にし続ける。
        // その間に表示画像を差し替える処理の途中なら断る(レビュー指摘: HDR合成の計算中に保存を始めると、
        // 保存ダイアログの中で派生ビューへ差し替わり、ダイアログを作った画像と別の画像を保存していた)
        Assert.Equal(BusyReason.Loading, BusyNotice.ForImageUse(loadPending: true, replacementPending: false));
        Assert.Equal(BusyReason.Loading, BusyNotice.ForImageUse(loadPending: true, replacementPending: true));
        Assert.Equal(BusyReason.Operation, BusyNotice.ForImageUse(loadPending: false, replacementPending: true));

        // 読み込み確定後の縮小表示(ピラミッド)の作成中などは、これまでどおり始められる
        Assert.Null(BusyNotice.ForImageUse(loadPending: false, replacementPending: false));
    }

    [Fact]
    public void ForSequence_DistinguishesReasons()
    {
        // 送りは続けて起こるのでステータスバーに出す。理由ごとに文言を変える
        string[] texts =
        [
            BusyNotice.ForSequence(BusyReason.Loading),
            BusyNotice.ForSequence(BusyReason.Operation),
            BusyNotice.ForSequence(BusyReason.Finishing),
        ];

        Assert.All(texts, text => Assert.Contains("フレームを送れません", text));
        Assert.Contains("画像の読み込み中", texts[0]);
        Assert.Contains("他の処理の実行中", texts[1]);
        Assert.Contains("縮小表示", texts[2]);
        Assert.Equal(3, texts.Distinct().Count());
    }
}

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

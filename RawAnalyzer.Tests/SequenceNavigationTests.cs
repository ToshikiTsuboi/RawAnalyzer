using RawAnalyzer.App.Services;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// シーケンスの送り(シーケンスバー・送りのショートカット・再生)を使えるかの判定
/// (MainWindow の UpdateSequenceUi / ShowSequenceIndexAsync)。
/// </summary>
public class SequenceNavigationTests
{
    [Fact]
    public void SequenceInRawView_IsAvailable()
    {
        Assert.True(SequenceNavigation.IsAvailable(count: 25, derivedViewShown: false));
        Assert.True(SequenceNavigation.IsAvailable(count: 2, derivedViewShown: false));
    }

    [Theory]
    [InlineData(1)]
    public void NoSequence_IsNotAvailable(int count)
    {
        Assert.False(SequenceNavigation.IsAvailable(count, derivedViewShown: false));
        Assert.False(SequenceNavigation.IsAvailable(count, derivedViewShown: true));
    }

    [Fact]
    public void DerivedViewShown_IsNotAvailableEvenIfSequenceRemains()
    {
        // ファイル連番の送りで次のファイルを読んでいる間に HDR 分割・合成へ入り、派生ビューの表示の後で
        // 読み込みが終わる。送りは派生ビューに譲ってやめるが、その後始末の UpdateSequenceUi は
        // 送りの件数(ファイル連番・フレームの数は派生ビューの間も残る)だけを見て送りの UI を有効に戻して
        // いた。派生ビューのまま送ると、ファイル連番では派生ビューを残したまま元画像だけがビューポートへ入り、
        // フレーム送りでは派生ビューの Bayer ピラミッドがビューポートから外れる
        Assert.False(SequenceNavigation.IsAvailable(count: 25, derivedViewShown: true));
        Assert.False(SequenceNavigation.IsAvailable(count: 2, derivedViewShown: true));
    }

    [Fact]
    public void PlaybackStoppedWhileLoading_RefreshesAnalysisForTheShownImage()
    {
        // 再生のティックが始めた送り(作り直しは求めない)で次のファイル・ページを読んでいる間に再生を止める。
        // 止めたときの作り直しは送る前の画像に対して始まり、読み終えた送りが状態を入れ替えるときに取り消される。
        // 以前は要求時の指定だけを見て作り直さず、表示中の画像のヒストグラム・ROI統計・ラインプロファイル・
        // 縮小ピラミッドが作られなかった(前の画像の統計が残り、縮小表示も等倍データから描き続けた)
        Assert.True(SequenceNavigation.RefreshesAnalysisAfterMove(requested: false, playing: false));
    }

    [Fact]
    public void StillPlaying_DefersAnalysisUntilStopped()
    {
        // 再生中は送りのたびに計算しない(止めたときに StopPlayback が作り直す)
        Assert.False(SequenceNavigation.RefreshesAnalysisAfterMove(requested: false, playing: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequestedRefresh_IsKept(bool playing)
    {
        // ボタン・キー・停止中のスライダーでの送りは、従来どおり送った後に作り直す
        Assert.True(SequenceNavigation.RefreshesAnalysisAfterMove(requested: true, playing));
    }
}

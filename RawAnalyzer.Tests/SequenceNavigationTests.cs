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
}

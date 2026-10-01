using RawAnalyzer.App.Services;
using RawAnalyzer.App.Views;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 保存の付随テキスト(MainWindow の WriteProcessingSidecar が書く)の判断。
/// </summary>
public class ProcessingSidecarTests
{
    [Theory]
    [InlineData(SaveFormat.Tiff16)]
    [InlineData(SaveFormat.Png16)]
    [InlineData(SaveFormat.Png8)]
    [InlineData(SaveFormat.Jpeg8)]
    public void SourceFrameLine_MultiFrameRawSavedAsOneFrame_RecordsWhichFrame(SaveFormat format)
    {
        // 回帰テスト: マルチフレーム raw の表示中のフレームだけを TIFF/PNG/JPEG に保存しても、付随テキストに
        // どのフレームかが書かれず、同じ raw から別のフレームを保存したファイル同士を後から区別できなかった
        Assert.Equal("元画像のフレーム: 37/100", ProcessingSidecar.SourceFrameLine(100, 36, tiffStack: false, format));
    }

    [Fact]
    public void SourceFrameLine_NotWrittenWhenAllFramesOrSingleFrameOrTiffPage()
    {
        // raw 形式は全フレームを書き出す。1フレームの画像は区別が要らない。TIFF スタックは「元TIFFのページ」を書く
        Assert.Null(ProcessingSidecar.SourceFrameLine(100, 36, tiffStack: false, SaveFormat.Raw));
        Assert.Null(ProcessingSidecar.SourceFrameLine(1, 0, tiffStack: false, SaveFormat.Tiff16));
        Assert.Null(ProcessingSidecar.SourceFrameLine(5, 2, tiffStack: true, SaveFormat.Png8));
    }
}

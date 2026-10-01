using RawAnalyzer.App.Services;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// ノイズ測定の2枚目(B)が、表示中の対象A(フレーム・TIFFページ)と同じデータかの判定。
/// Bは常にファイルの先頭フレーム・先頭ページを読む。
/// </summary>
public class NoiseReferenceTests
{
    private const string Stack = @"C:\data\dark_stack.tif";

    [Fact]
    public void SameTiff_SecondPageShown_IsDifferentData()
    {
        // TIFFの各ページはFrameCount=1の画像なので表示フレームは常に0。
        // ページ番号を見ないと、2ページ目表示中に同じTIFFを指定しただけで拒否していた
        Assert.False(NoiseReference.ReadsSameDataAsTarget(
            Stack, Stack, isDerivedView: false, frame: 0, tiffPageIndex: 1));
    }

    [Fact]
    public void SameRawFile_SecondFrameShown_IsDifferentData()
    {
        // マルチフレームrawの2フレーム目とBの先頭フレームは別データ(先頭フレーム同士が同一データになることは
        // SameFileWrittenDifferently_IsSameData が見る)
        const string raw = @"C:\data\burst.raw";

        Assert.False(NoiseReference.ReadsSameDataAsTarget(
            raw, raw, isDerivedView: false, frame: 1, tiffPageIndex: 0));
    }

    [Fact]
    public void DerivedView_IsNeverSameAsFile()
    {
        // HDR分割/合成の派生ビューはファイルの内容と別物
        Assert.False(NoiseReference.ReadsSameDataAsTarget(
            Stack, Stack, isDerivedView: true, frame: 0, tiffPageIndex: 0));
    }

    [Fact]
    public void DifferentFile_IsDifferentData()
    {
        Assert.False(NoiseReference.ReadsSameDataAsTarget(
            @"C:\data\dark_stack2.tif", Stack, isDerivedView: false, frame: 0, tiffPageIndex: 0));
        Assert.False(NoiseReference.ReadsSameDataAsTarget(
            Stack, currentPath: null, isDerivedView: false, frame: 0, tiffPageIndex: 0));
    }

    [Fact]
    public void RawReference_InMergedView_IsRefusedWithReason()
    {
        // 合成ビューの対象Aと、合成していない2枚目の raw は同じ画素・同じ値の対応で比べられない
        string? reason = NoiseReference.RawReferenceRefusal(processed: false, mergedView: true);

        Assert.NotNull(reason);
        Assert.Contains("分割ビュー", reason);
        Assert.Null(NoiseReference.RawReferenceRefusal(processed: false, mergedView: false));
    }

    [Fact]
    public void RawReference_ForProcessedTarget_IsRefusedWithReason()
    {
        // ビニング・フィルタの結果(16bit・ヘッダ0)を対象Aにすると、2枚目の raw は以前その処理結果の形式で
        // 読まれ、12bit 下詰めの raw では正規化されない値(約1/16)との差分を、サイズ警告も出さずに測っていた。
        // ファイルの形式で読んでも、処理した A と処理していない B の差分は時間ノイズにならない
        string? reason = NoiseReference.RawReferenceRefusal(processed: true, mergedView: false);

        Assert.NotNull(reason);
        Assert.Contains("開き直", reason);
    }

    [Fact]
    public void SameFileWrittenDifferently_IsSameData()
    {
        // 大文字小文字や . / .. を含む書き方の違いでは見逃さない
        Assert.True(NoiseReference.ReadsSameDataAsTarget(
            @"c:\DATA\sub\..\dark_stack.TIF", Stack, isDerivedView: false, frame: 0, tiffPageIndex: 0));
    }
}

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
    public void ResolvePath_KeepsUnrelatedTextWithSameName()
    {
        // 回帰テスト: 付随テキストは画像と同名の .txt へ確認なしで上書きしていた。元 raw の横に撮影条件を書いた
        // foo.txt があると、初期名のまま同じフォルダへ foo.png を保存しただけで撮影メモが消えた。
        // 付随テキストでないファイルは残し、画像の名前に .txt を足した名前へ書く
        using var folder = new TempFolder();
        string image = Path.Combine(folder.Path, "foo.png");
        File.WriteAllText(Path.Combine(folder.Path, "foo.txt"), "露光 1/60s, ゲイン 12dB\n");

        Assert.Equal(image + ".txt", ProcessingSidecar.ResolvePath(image));
    }

    [Fact]
    public void ResolvePath_KeepsSidecarOfAnotherImageWithSameBaseName()
    {
        // 同じフレームを foo.tif と foo.png に保存すると、後の付随テキストが foo.tif の来歴を置き換えていた
        using var folder = new TempFolder();
        string image = Path.Combine(folder.Path, "foo.png");
        File.WriteAllText(Path.Combine(folder.Path, "foo.txt"), SidecarOf("foo.tif"));

        Assert.Equal(image + ".txt", ProcessingSidecar.ResolvePath(image));

        // foo.png.txt もほかの画像のもの・利用者のファイルなら、番号を付ける
        File.WriteAllText(image + ".txt", "メモ\n");
        Assert.Equal(image + " (2).txt", ProcessingSidecar.ResolvePath(image));
    }

    [Fact]
    public void ResolvePath_SameNameTxt_WhenFreeOrSidecarOfSameImage()
    {
        // 同名の .txt がない、または同じ画像を前に保存したときの付随テキストなら、従来どおり同名の .txt
        // (画像自体の上書きは保存ダイアログで確認済み)
        using var folder = new TempFolder();
        string image = Path.Combine(folder.Path, "foo.png");
        string sameName = Path.Combine(folder.Path, "foo.txt");

        Assert.Equal(sameName, ProcessingSidecar.ResolvePath(image));

        File.WriteAllText(sameName, SidecarOf("FOO.png"), System.Text.Encoding.UTF8);
        Assert.Equal(sameName, ProcessingSidecar.ResolvePath(image));
    }

    private static string SidecarOf(string imageFileName) =>
        ProcessingSidecar.Title + "\n====================\n保存日時: 2026-10-01 12:00:00\n"
        + ProcessingSidecar.OutputFileLabel + imageFileName + "\n元ファイル: foo.raw\n";

    private sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "RawAnalyzerTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // 一時フォルダの後始末の失敗はテストの結果に関わらない
            }
        }
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

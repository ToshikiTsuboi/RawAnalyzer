using RawAnalyzer.App.Compare;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 比較ペインのファイル名に、マルチフレーム raw の表示フレームを添えることの検証。
/// </summary>
/// <remarks>
/// 比較ペインは先頭フレームを表示する(比較モードへ入ったときにメイン表示の画像を読み直すペインAも同じ)。
/// 以前は raw のフレームに何も添えなかったので、メイン表示でフレーム7を見ていて比較モードへ入ると、
/// 同じファイル名のままフレーム1が出て、別のフレーム同士を比べていることに気付けなかった。
/// </remarks>
public class ComparePaneFrameNoteTests
{
    [Fact]
    public async Task MultiFrameRaw_FileNameShowsDisplayedFrame()
    {
        var format = new RawFormat { Width = 8, Height = 4, BitDepth = 12, FrameCount = 3 };
        string path = TestData.WriteTempFile(
            TestData.EncodeRawFile(new ushort[format.TotalPixels], format));
        try
        {
            using ComparePane pane = await ComparePane.LoadAsync(path, format);

            Assert.Equal($"{Path.GetFileName(path)} [フレーム 1/3]", pane.FileName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SingleFrameRaw_FileNameHasNoFrameNote()
    {
        var format = new RawFormat { Width = 8, Height = 4, BitDepth = 12 };
        string path = TestData.WriteTempFile(
            TestData.EncodeRawFile(new ushort[format.TotalPixels], format));
        try
        {
            using ComparePane pane = await ComparePane.LoadAsync(path, format);

            Assert.Equal(Path.GetFileName(path), pane.FileName);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

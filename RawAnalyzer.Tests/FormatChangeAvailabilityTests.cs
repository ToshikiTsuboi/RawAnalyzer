using System.ComponentModel;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.ViewModels;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 「フォーマット変更…」(HDRメニューの項目・右パネルの「変更…」)を使えるか
/// (MainViewModel.CanChangeFormat)と、使えないときに示す理由の検証。
/// 読み込みダイアログで開き直せるのは raw(.raw/.bin)だけで、以前は画像ファイルで押しても何も起きなかった。
/// </summary>
public class FormatChangeAvailabilityTests
{
    [Fact]
    public void NoImage_CannotChangeFormat()
    {
        var vm = new MainViewModel();

        Assert.False(vm.CanChangeFormat);
        Assert.Null(vm.ChangeFormatToolTip);
    }

    [Theory]
    [InlineData(false)] // グレーの画像ファイル(TIFF 等)。カラー画像は ChangingImage_NotifiesAvailabilityAndTooltip
    public void ImageFile_CannotChangeFormat_AndTooltipExplainsWhy(bool isColor)
    {
        // 以前は画像があればメニュー・ボタンを押せ、押しても何も起きなかった
        var vm = new MainViewModel { HasImage = true, IsRawFile = false, IsColorImage = isColor };

        Assert.False(vm.CanChangeFormat);
        Assert.Equal(FormatChangeAvailability.ExplainUnavailable(isColor), vm.ChangeFormatToolTip);
        Assert.Contains("raw(.raw/.bin)", vm.ChangeFormatToolTip);
    }

    [Fact]
    public void Explanation_ForGrayImageFile_PointsToWhatCanBeSpecified()
    {
        // グレーの画像ファイルでも Bayer は右パネルで指定でき、HDR方式は raw で保存し直せば指定できる
        string text = FormatChangeAvailability.ExplainUnavailable(isColor: false);

        Assert.StartsWith("フォーマット変更は raw(.raw/.bin)を開いているときに使えます。", text);
        Assert.Contains("右パネルの「Bayer」", text);
        Assert.Contains("rawで保存", text);
    }

    [Fact]
    public void Explanation_ForColorImage_DoesNotPointToBayer()
    {
        // カラー画像には Bayer を当てない(右パネルの Bayer も操作不可)ので、そこへ案内しない
        string text = FormatChangeAvailability.ExplainUnavailable(isColor: true);

        Assert.StartsWith("フォーマット変更は raw(.raw/.bin)を開いているときに使えます。", text);
        Assert.Contains("カラーのまま表示", text);
        Assert.DoesNotContain("右パネル", text);
    }

    [Fact]
    public void ChangingImage_NotifiesAvailabilityAndTooltip()
    {
        // 別のファイルを開き直す・カラー/グレーが替わるたびに、押せるかと理由が追従する
        var vm = new MainViewModel { HasImage = true, IsRawFile = true };
        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.IsRawFile = false;
        Assert.Contains(nameof(MainViewModel.CanChangeFormat), changed);
        Assert.Contains(nameof(MainViewModel.ChangeFormatToolTip), changed);
        Assert.False(vm.CanChangeFormat);

        changed.Clear();
        vm.IsColorImage = true;
        Assert.Contains(nameof(MainViewModel.ChangeFormatToolTip), changed);
        Assert.False(vm.CanChangeFormat);
        Assert.Contains("カラーのまま表示", vm.ChangeFormatToolTip);

        changed.Clear();
        vm.IsColorImage = false;
        vm.IsRawFile = true;
        Assert.Contains(nameof(MainViewModel.CanChangeFormat), changed);
        Assert.True(vm.CanChangeFormat);
        Assert.Null(vm.ChangeFormatToolTip);
    }

    // ---- ファイル一覧の右クリックメニュー「フォーマットを指定して開く…」 ----

    [Theory]
    [InlineData("b.BIN")] // 拡張子の大文字小文字は問わない
    public void OpenWithFormat_RawFile_IsAvailable(string name)
    {
        var vm = new MainViewModel { SelectedFile = Entry(name) };

        Assert.True(vm.CanOpenSelectedFileWithFormat);
        Assert.Equal(FormatChangeAvailability.OpenWithFormatDescription, vm.OpenSelectedFileWithFormatToolTip);
    }

    [Theory]
    [InlineData("a.tif")]
    public void OpenWithFormat_ImageFile_IsUnavailable_AndTooltipExplainsWhy(string name)
    {
        // 以前は押せて、ダイアログを出さずに普通に開いた(フォーマットを指定して開くつもりの利用者が驚く)
        var vm = new MainViewModel { SelectedFile = Entry(name) };

        Assert.False(vm.CanOpenSelectedFileWithFormat);
        Assert.Equal(FormatChangeAvailability.OpenWithFormatUnavailableReason, vm.OpenSelectedFileWithFormatToolTip);
    }

    [Fact]
    public void OpenWithFormat_FolderOrNoSelection_IsUnavailable()
    {
        // フォルダ・未選択では開くファイルがない(以前は押せて何も起きなかった)。画像ファイルの理由は示さない
        var vm = new MainViewModel();
        Assert.False(vm.CanOpenSelectedFileWithFormat);

        vm.SelectedFile = new FileEntry("sub", @"C:\data\sub", IsDirectory: true);
        Assert.False(vm.CanOpenSelectedFileWithFormat);
        Assert.Equal(FormatChangeAvailability.OpenWithFormatDescription, vm.OpenSelectedFileWithFormatToolTip);
    }

    [Fact]
    public void OpenWithFormat_Explanation_SaysImageFilesOpenAsIs()
    {
        // 画像ファイルはフォーマットを指定せずにそのまま開くこと、開いた後に指定できるものを示す
        string text = FormatChangeAvailability.OpenWithFormatUnavailableReason;

        Assert.StartsWith("フォーマットを指定して開けるのは raw(.raw/.bin)だけです。", text);
        Assert.Contains("「開く」", text);
        Assert.Contains("右パネルの「Bayer」", text);
        Assert.Contains("rawで保存", text);
    }

    [Fact]
    public void ChangingSelectedFile_NotifiesOpenWithFormatAvailability()
    {
        // 右クリックで選び直すたびに、メニュー項目を押せるかと理由が追従する
        var vm = new MainViewModel { SelectedFile = Entry("a.raw") };
        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SelectedFile = Entry("a.tif");

        Assert.Contains(nameof(MainViewModel.CanOpenSelectedFileWithFormat), changed);
        Assert.Contains(nameof(MainViewModel.OpenSelectedFileWithFormatToolTip), changed);
        Assert.False(vm.CanOpenSelectedFileWithFormat);
    }

    private static FileEntry Entry(string name) => new(name, @"C:\data\" + name);
}

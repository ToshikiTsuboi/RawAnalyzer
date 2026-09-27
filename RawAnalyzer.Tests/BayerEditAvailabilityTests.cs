using System.ComponentModel;
using RawAnalyzer.App.ViewModels;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 右パネルの Bayer 指定を操作できるか(MainViewModel.CanEditBayer)の検証。
/// デコード済みのカラー画像には Bayer を適用しない規約(ImageFileBayer / DisplayModeSelection)と
/// 表示・統計を食い違わせないため、カラー画像の表示中は指定を操作させない。
/// </summary>
public class BayerEditAvailabilityTests
{
    [Fact]
    public void NoImage_CannotEditBayer()
    {
        Assert.False(new MainViewModel().CanEditBayer);
    }

    [Fact]
    public void GrayImage_CanEditBayer()
    {
        // raw・グレーの画像ファイル(CFA TIFF を含む)は右パネルで配列を指定・変更できる
        var vm = new MainViewModel { HasImage = true, IsColorImage = false };

        Assert.True(vm.CanEditBayer);
    }

    [Fact]
    public void ColorImage_CannotEditBayer()
    {
        // カラー画像は RGB のまま表示する。以前は指定でき、変えると表示はカラーのまま
        // チャネル別統計(と欠陥検出・ノイズ測定)が輝度に Bayer を当てていた
        var vm = new MainViewModel { HasImage = true, IsColorImage = true };

        Assert.False(vm.CanEditBayer);
    }

    [Fact]
    public void SwitchingColorAndGray_NotifiesAvailability()
    {
        // 送り(TIFF のページ・ファイル連番)でカラー/グレーが替わったら、操作できるかも追従する
        var vm = new MainViewModel { HasImage = true };
        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.IsColorImage = true;
        Assert.Contains(nameof(MainViewModel.CanEditBayer), changed);
        Assert.False(vm.CanEditBayer);

        changed.Clear();
        vm.IsColorImage = false;
        Assert.Contains(nameof(MainViewModel.CanEditBayer), changed);
        Assert.True(vm.CanEditBayer);

        changed.Clear();
        vm.HasImage = false;
        Assert.Contains(nameof(MainViewModel.CanEditBayer), changed);
        Assert.False(vm.CanEditBayer);
    }
}

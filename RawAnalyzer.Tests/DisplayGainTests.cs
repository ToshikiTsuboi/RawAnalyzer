using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>表示ゲインの線形倍率と dB の換算・範囲・変更通知(MainViewModel)。</summary>
public class DisplayGainTests
{
    [Theory]
    [InlineData(10.0, 20.0)]     // 20·log10 (10·log10 なら 10)
    [InlineData(0.5, -6.0206)]
    public void GainDb_MatchesLinearGain(double linear, double db)
    {
        var vm = new App.ViewModels.MainViewModel { Gain = linear };
        Assert.Equal(db, vm.GainDb, 3);

        // dB を設定すると線形倍率へ戻る
        var other = new App.ViewModels.MainViewModel();
        other.GainDb = db;
        Assert.Equal(linear, other.Gain, 6);
    }

    [Fact]
    public void GainDb_IsClampedToSliderRange()
    {
        var vm = new App.ViewModels.MainViewModel();

        vm.GainDb = 999;
        Assert.Equal(vm.MaxGainDb, vm.GainDb, 6);

        vm.GainDb = -999;
        Assert.Equal(vm.MinGainDb, vm.GainDb, 6);
    }

    [Fact]
    public void GainDb_NotifiesWhenLinearGainChanges()
    {
        var vm = new App.ViewModels.MainViewModel();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        vm.Gain = 4.0;

        Assert.Contains(nameof(App.ViewModels.MainViewModel.Gain), changed);
        Assert.Contains(nameof(App.ViewModels.MainViewModel.GainDb), changed);
        Assert.Contains(nameof(App.ViewModels.MainViewModel.GainNote), changed);
        Assert.Equal("= ×4", vm.GainNote);
    }
}

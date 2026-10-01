using System.ComponentModel;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.ViewModels;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 右パネルの Bayer 指定を操作できるか(MainViewModel.CanEditBayer)と、ツールチップ
/// (MainViewModel.BayerEditToolTip。無効のときも出すので理由を示す)の検証。判定は BayerEditAvailability。
/// デコード済みのカラー画像には Bayer を適用しない規約(ImageFileBayer / DisplayModeSelection)と
/// 表示・統計を食い違わせないため、カラー画像の表示中は指定を操作させない。HDR分割・合成の派生ビューは
/// 計算を始めたときの Bayer で作るので、派生ビューの表示中と計算中も操作させない。
/// </summary>
public class BayerEditAvailabilityTests
{
    [Fact]
    public void ColorImage_CannotEditBayer_AndTooltipExplainsWhy()
    {
        // カラー画像は RGB のまま表示する。以前は指定でき、変えると表示はカラーのまま
        // チャネル別統計(と欠陥検出・ノイズ測定)が輝度に Bayer を当てていた。
        // 無効のときもツールチップを出すので、変更できるという説明ではなく無効の理由を示す
        var vm = new MainViewModel { HasImage = true, IsColorImage = true };

        Assert.False(vm.CanEditBayer);
        Assert.Equal("カラー画像(RGB)には Bayer を適用せず、カラーのまま表示します。", vm.BayerEditToolTip);
    }

    [Fact]
    public void HdrViewOrComputation_CannotEditBayerWithReason_AndLeavingReenables()
    {
        // 以前は HDR分割・合成の計算中(モーダルではない)と表示中にも変えられ、計算は開始時のパターンで
        // 行って結果をそのまま表示し、派生ビューは古いパターンのまま右パネルの表示と食い違った。
        // 計算・派生ビューへの出入りのたびに、操作できるかと理由が追従する(通知がないと無効のまま・有効のまま残る)
        var vm = new MainViewModel { HasImage = true };
        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        // Raw表示 → 合成を計算中 → 採用されずに終わった(取り消し・失敗・元画像の差し替え)
        vm.IsHdrComputing = true;
        AssertNotified(vm, changed, canEdit: false);
        Assert.StartsWith("HDR分割・合成の計算中は Bayer を変更できません", vm.BayerEditToolTip);
        Assert.Contains("計算は始めたときの Bayer で行います", vm.BayerEditToolTip);
        vm.IsHdrComputing = false;
        AssertNotified(vm, changed, canEdit: true);
        Assert.Equal(BayerEditAvailability.Description, vm.BayerEditToolTip);

        // 分割ビューの表示中に合成を計算 → 採用されずに終わっても分割ビューのままなので無効のまま
        vm.IsHdrViewShown = true;
        AssertNotified(vm, changed, canEdit: false);
        vm.IsHdrComputing = true;
        Assert.StartsWith("HDR分割・合成の計算中", vm.BayerEditToolTip);
        vm.IsHdrComputing = false;
        Assert.False(vm.CanEditBayer);
        Assert.StartsWith("HDR表示(分割・合成)の間", vm.BayerEditToolTip);
        Assert.EndsWith("Raw表示に戻してから変更してください。", vm.BayerEditToolTip);

        // Raw表示へ戻った
        changed.Clear();
        vm.IsHdrViewShown = false;
        AssertNotified(vm, changed, canEdit: true);
    }

    [Fact]
    public void SwitchingColorAndGray_NotifiesAvailability()
    {
        // 送り(TIFF のページ・ファイル連番)でカラー/グレーが替わったら、操作できるかと理由も追従する
        var vm = new MainViewModel { HasImage = true };
        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.IsColorImage = true;
        AssertNotified(vm, changed, canEdit: false);

        vm.IsColorImage = false;
        AssertNotified(vm, changed, canEdit: true);

        vm.HasImage = false;
        AssertNotified(vm, changed, canEdit: false);
    }

    /// <summary>操作できるかとツールチップの変更が通知され、操作できるかが期待どおりか確かめて、通知の記録を消す。</summary>
    private static void AssertNotified(MainViewModel vm, List<string?> changed, bool canEdit)
    {
        Assert.Contains(nameof(MainViewModel.CanEditBayer), changed);
        Assert.Contains(nameof(MainViewModel.BayerEditToolTip), changed);
        Assert.Equal(canEdit, vm.CanEditBayer);
        changed.Clear();
    }
}

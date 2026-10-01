using RawAnalyzer.App.Services;
using RawAnalyzer.App.ViewModels;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 比較モード中に、比較画面に隠れた通常表示(メインの画像・ビューポート)を対象にする操作を断る規約の検証。
/// </summary>
/// <remarks>
/// 以前は比較モード中も Ctrl+C(画素値のコピー)・Ctrl+Shift+C(表示のコピー)・ズーム・送り・再生などが
/// 隠れた通常表示に効き、見えている比較ペインとは別の画像の値・描画を黙ってコピーしていた。
/// </remarks>
public class CompareModeCommandTests
{
    [Fact]
    public void MainView_IsUnavailableWhileComparing()
    {
        // メニュー・ツールバーのメイン画像を対象にする項目はこの値で有効・無効を決める(コマンド表と同じ条件)
        var vm = new MainViewModel();
        Assert.False(vm.CanUseMainView);

        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");
        vm.HasImage = true;
        Assert.True(vm.CanUseMainView);
        Assert.Contains(nameof(MainViewModel.CanUseMainView), changed);

        changed.Clear();
        vm.IsCompareMode = true;
        Assert.False(vm.CanUseMainView);
        Assert.Contains(nameof(MainViewModel.CanUseMainView), changed);

        changed.Clear();
        vm.IsCompareMode = false;
        Assert.True(vm.CanUseMainView);
        Assert.Contains(nameof(MainViewModel.CanUseMainView), changed);
    }

    [Fact]
    public void FormatChange_IsRefusedWhileComparing_WithTheReason()
    {
        // 「フォーマット変更…」も隠れた通常表示の raw を開き直すので、比較モード中は無効にして理由を示す
        var vm = new MainViewModel { HasImage = true, IsRawFile = true };
        Assert.True(vm.CanChangeFormat);
        Assert.Null(vm.ChangeFormatToolTip);

        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");
        vm.IsCompareMode = true;

        Assert.False(vm.CanChangeFormat);
        Assert.Equal(CommandDisabledReasons.CompareMode, vm.ChangeFormatToolTip);
        Assert.Contains(nameof(MainViewModel.CanChangeFormat), changed);
        Assert.Contains(nameof(MainViewModel.ChangeFormatToolTip), changed);
    }

    [Fact]
    public void AdjustPanel_IsUnavailableWhileComparing_WithTheReason()
    {
        // 右の調整パネル(表示調整のスライダー・ボタン、ヒストグラムの切替・コピー、ホワイトバランス、カラーマトリクス、
        // フォーマット)は通常表示を対象にする。以前は比較モード中も操作でき、比較画面に隠れた通常表示に効いた。
        // 比較モードでなければ、画像がなくても従来どおり操作できる(比較モードの間だけ無効にして理由を示す)
        var vm = new MainViewModel();
        Assert.True(vm.CanUseAdjustPanel);
        Assert.Null(vm.AdjustPanelDisabledReason);

        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");
        vm.IsCompareMode = true;
        Assert.False(vm.CanUseAdjustPanel);
        Assert.Equal(CommandDisabledReasons.AdjustPanelInCompareMode, vm.AdjustPanelDisabledReason);
        Assert.StartsWith("比較モード中は調整パネルを使えません", vm.AdjustPanelDisabledReason);
        Assert.Contains(nameof(MainViewModel.CanUseAdjustPanel), changed);
        Assert.Contains(nameof(MainViewModel.AdjustPanelDisabledReason), changed);

        changed.Clear();
        vm.IsCompareMode = false;
        Assert.True(vm.CanUseAdjustPanel);
        Assert.Null(vm.AdjustPanelDisabledReason);
        Assert.Contains(nameof(MainViewModel.CanUseAdjustPanel), changed);
        Assert.Contains(nameof(MainViewModel.AdjustPanelDisabledReason), changed);
    }

    [Fact]
    public void OpenWithFormat_IsRefusedWhileComparing_WithTheReason()
    {
        // ファイル一覧の「フォーマットを指定して開く…」は通常表示へ開く(比較ペインはフォーマットを指定し直せない)。
        // 以前は比較モード中も押せて、比較画面に隠れた通常表示へ読み込んだ。比較モード中は無効にして理由を示す
        // (ダブルクリック・「開く」は比較ペインへの追加に回す)
        var vm = new MainViewModel { SelectedFile = new FileEntry("a.raw", @"C:\data\a.raw") };
        Assert.True(vm.CanOpenSelectedFileWithFormat);

        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");
        vm.IsCompareMode = true;

        Assert.False(vm.CanOpenSelectedFileWithFormat);
        Assert.Equal(CommandDisabledReasons.CompareMode, vm.OpenSelectedFileWithFormatToolTip);
        Assert.Contains(nameof(MainViewModel.CanOpenSelectedFileWithFormat), changed);
        Assert.Contains(nameof(MainViewModel.OpenSelectedFileWithFormatToolTip), changed);

        changed.Clear();
        vm.IsCompareMode = false;
        Assert.True(vm.CanOpenSelectedFileWithFormat);
        Assert.Equal(FormatChangeAvailability.OpenWithFormatDescription, vm.OpenSelectedFileWithFormatToolTip);
        Assert.Contains(nameof(MainViewModel.CanOpenSelectedFileWithFormat), changed);
    }

    [Fact]
    public void BayerEdit_IsRefusedWhileComparing_WithTheReason()
    {
        // 右パネルの Bayer の選択は隠れた通常表示の画像のパターンを変え、同じファイル・同じサイズの記憶にも残る。
        // 以前は比較モード中も選べ、ツールチップは「変更できます」のままだった。比較モードは画像の有無より先に示す
        var vm = new MainViewModel { HasImage = true };
        Assert.True(vm.CanEditBayer);

        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");
        vm.IsCompareMode = true;

        Assert.False(vm.CanEditBayer);
        Assert.Equal(CommandDisabledReasons.CompareMode, vm.BayerEditToolTip);
        Assert.Contains(nameof(MainViewModel.CanEditBayer), changed);
        Assert.Contains(nameof(MainViewModel.BayerEditToolTip), changed);

        vm.HasImage = false;
        Assert.Equal(CommandDisabledReasons.CompareMode, vm.BayerEditToolTip);

        changed.Clear();
        vm.HasImage = true;
        vm.IsCompareMode = false;
        Assert.True(vm.CanEditBayer);
        Assert.Equal(BayerEditAvailability.Description, vm.BayerEditToolTip);
        Assert.Contains(nameof(MainViewModel.CanEditBayer), changed);
    }

    [Theory]
    [InlineData(true, CommandDisabledReasons.CompareMode)] // 比較モードは画像の有無より先に示す
    [InlineData(false, CommandDisabledReasons.NoImage)]
    public void MainViewReason_PutsCompareModeFirst(bool compareMode, string expected)
    {
        // 比較だけ使っている(通常表示に画像がない)ときも「画像を開いていない」とは言わない
        Assert.Equal(expected, CommandDisabledReasons.ForMainView(compareMode));
    }

    [Theory]
    [InlineData(true, true, CommandDisabledReasons.CompareMode)]
    [InlineData(true, false, CommandDisabledReasons.NoSequence)]
    [InlineData(false, false, CommandDisabledReasons.NoImage)]
    public void SequenceReason_PutsCompareModeFirst(bool hasImage, bool compareMode, string expected)
    {
        // 比較モードに入るときに再生は止める。送り・再生のキーで隠れた通常表示のフレームを送らない
        Assert.Equal(expected, CommandDisabledReasons.ForSequence(hasImage, compareMode));
    }

    [Theory]
    [InlineData(true, true, CommandDisabledReasons.CompareMode)]
    [InlineData(true, false, CommandDisabledReasons.NoRoi)]
    [InlineData(false, false, CommandDisabledReasons.NoImage)]
    public void RoiReason_PutsCompareModeFirst(bool hasImage, bool compareMode, string expected)
    {
        Assert.Equal(expected, CommandDisabledReasons.ForRoi(hasImage, compareMode));
    }
}

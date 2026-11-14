using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.Views;
using Xunit;

namespace RawAnalyzer.Tests;

[Collection("WPF UI")]
public class ShortcutHelpWindowTests
{
    [Theory]
    [InlineData("Ctrl+Shift+P", "Ctrl|Shift|P")]
    [InlineData("Ctrl++", "Ctrl|+")]
    [InlineData("+", "+")]
    [InlineData("Ctrl+Alt + ↑↓←→", "Ctrl|Alt|↑|↓|←|→")]
    [InlineData("ダブルクリック", "ダブルクリック")]
    public void GesturesSplitIntoIndividualKeycaps(string gesture, string expected) =>
        Assert.Equal(expected.Split('|'), ShortcutHelpWindow.GestureKeys(gesture));

    [Fact]
    public void FilterDropsEmptyCategoriesAndSearchesNamesAndKeys()
    {
        ShortcutHelpWindow.ShortcutGroup[] groups =
        [
            new("ファイル", [new("Ctrl+O", "画像を開く"), new("Ctrl+S", "保存")]),
            new("表示", [new("Ctrl++", "拡大")]),
        ];
        var matched = Assert.Single(ShortcutHelpWindow.FilterGroups(groups, "Ｃｔｒｌ　保存"));
        Assert.Equal("ファイル", matched.Category);
        Assert.Equal("Ctrl+S", Assert.Single(matched.Entries).Gesture);
        Assert.Equal("表示", Assert.Single(ShortcutHelpWindow.FilterGroups(groups, "表示")).Category);
        Assert.Empty(ShortcutHelpWindow.FilterGroups(groups, "F12"));
    }

    [Fact]
    public Task LayoutReturnsToOneColumnAndSearchHidesCategories() => WpfTestHost.Run(async () =>
    {
        var window = new ShortcutHelpWindow(
        [
            Command("open", "ファイル", "画像を開く", Key.O),
            Command("save", "ファイル", "画像を保存", Key.S),
            Command("zoom", "表示", "拡大", Key.OemPlus),
            Command("palette", "操作", "コマンドパレット", Key.P, ModifierKeys.Control | ModifierKeys.Shift),
        ]) { ShowActivated = false };
        try
        {
            window.Show();
            await Layout();
            var list = (ItemsControl)window.FindName("GroupList");
            Point Position(int index)
            {
                var item = (FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(index);
                return item.TransformToAncestor(list).Transform(new Point(0, 0));
            }
            Assert.Equal(2, window.ColumnCount);
            Assert.True(Position(1).X > Position(0).X);
            WpfTestHost.CaptureDialogIfRequested(window, "d8-shortcuts");
            window.Width = 500;
            await Layout();
            Assert.Equal(1, window.ColumnCount);
            Assert.Equal(Position(0).X, Position(1).X);
            Assert.True(Position(1).Y > Position(0).Y);
            ((TextBox)window.FindName("FilterBox")).Text = "Ctrl Shift P コマンドパレット";
            await Layout();
            Assert.Equal("操作", Assert.Single(list.Items.Cast<ShortcutHelpWindow.ShortcutGroup>()).Category);
            WpfTestHost.CaptureDialogIfRequested(window, "d8-shortcuts-filtered");
        }
        finally
        {
            window.Close();
        }

        async Task Layout()
        {
            window.UpdateLayout();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
        }
    });

    private static AppCommand Command(string id, string category, string title, Key key,
        ModifierKeys modifiers = ModifierKeys.Control) =>
        new() { Id = id, Category = category, Title = title, Key = key, Modifiers = modifiers, Execute = () => { } };
}

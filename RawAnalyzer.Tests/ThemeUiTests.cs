using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using RawAnalyzer.App.Compare;
using RawAnalyzer.App.Themes;
using RawAnalyzer.App.Views;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

[Collection("WPF UI")]
public class ThemeUiTests
{
    [Fact]
    public Task IconGlyphs_AreAvailableInWindowsFontsAndRender() => WpfTestHost.Run(() =>
    {
        var iconStyle = (Style)Application.Current.FindResource("IconText");
        var icons = typeof(IconGlyphs).GetFields(BindingFlags.Public | BindingFlags.Static);
        var rows = new UniformGrid { Columns = 4 };
        foreach (FieldInfo icon in icons)
        {
            string glyph = (string)icon.GetRawConstantValue()!;
            var symbol = new TextBlock { Style = iconStyle, Text = glyph, Margin = new Thickness(0, 0, 8, 0) };
            var fallback = new Typeface("Segoe MDL2 Assets");
            Assert.True(fallback.TryGetGlyphTypeface(out GlyphTypeface? font));
            Assert.True(font!.CharacterToGlyphMap.TryGetValue(glyph[0], out ushort glyphIndex) && glyphIndex != 0,
                $"Segoe MDL2 Assets: {icon.Name}");
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 6, 8, 6) };
            row.Children.Add(symbol);
            row.Children.Add(new TextBlock { Text = icon.Name });
            rows.Children.Add(row);
        }

        var sample = new Border
        {
            Background = (Brush)Application.Current.FindResource("PanelBrush"),
            Child = rows,
        };
        TextElement.SetForeground(sample, (Brush)Application.Current.FindResource("TextBrush"));
        sample.Measure(new Size(640, 300));
        sample.Arrange(new Rect(sample.DesiredSize));
        sample.UpdateLayout();
        WpfTestHost.CaptureIfRequested(sample, "windows-icon-glyphs");
    });

    [Fact]
    public Task DefaultDialogButtons_UsePrimaryStyleAndAccessibleLink() => WpfTestHost.Run(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "RawAnalyzerTests", Guid.NewGuid().ToString("N"));
        Window[] dialogs =
        [
            new RawImportDialog("sample.raw", 640 * 480 * 2, new FormatPresetStore(directory)),
            new NoiseMeasureDialog("sample.raw", null, 4095, false, 0, new object()),
            new DefectPixelWindow(),
            new SaveDialog(100, bitDepth: 12),
            new BatchExportDialog(new[] { @"C:\capture\scene_001.raw", @"C:\capture\scene_002.raw", @"C:\capture\scene_003.raw" }, directory),
            new ImageProcessingDialog("sample.raw", 640, 480, BayerPattern.Rggb, false, true),
            new ImageCalculatorDialog("A", directory, expectedSize: 640 * 480 * 2),
        ];
        try
        {
            foreach (Window dialog in dialogs)
            {
                Button primary = Assert.Single(LogicalButtons(dialog), button => button.IsDefault);
                Assert.Same(dialog.FindResource("PrimaryButton"), primary.Style);
                if (primary.IsEnabled)
                {
                    Assert.Same(dialog.FindResource("AccentBrush"), primary.Background);
                    Assert.Same(dialog.FindResource("CanvasBrush"), primary.Foreground);
                }
            }
            var defect = (DefectPixelWindow)dialogs[2];
            Assert.Same(defect.FindResource("SuccessButton"), ((Button)defect.FindName("CorrectButton")).Style);

            var pane = new ComparePaneView();
            var link = (ToggleButton)pane.FindName("LinkToggle");
            Assert.Equal(IconGlyphs.Link, link.Content);
            Assert.Equal("調整リンク", AutomationProperties.GetName(link));
            Assert.NotNull(link.ToolTip);
        }
        finally
        {
            foreach (Window dialog in dialogs) dialog.Close();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    });

    private static IEnumerable<Button> LogicalButtons(DependencyObject parent)
    {
        foreach (DependencyObject child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is Button button) yield return button;
            foreach (Button nested in LogicalButtons(child)) yield return nested;
        }
    }
}

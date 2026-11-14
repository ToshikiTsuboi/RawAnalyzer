using System.Windows;
using RawAnalyzer.App.Controls;
using RawAnalyzer.App.Services;

namespace RawAnalyzer.App;

public partial class MainWindow
{
    private void OnViewportInteractionModeChanged(object? sender, EventArgs e)
    {
        PanToggle.IsChecked = Viewport.InteractionMode == ViewportInteractionMode.Pan;
        InteractionHint.Tag = ViewportModeHint.For(Viewport.InteractionMode);
    }

    private void OnPanClick(object sender, RoutedEventArgs e)
    {
        ExitViewportToolMode();
        PanToggle.IsChecked = true;
    }

    private bool ExitViewportToolMode()
    {
        switch (Viewport.InteractionMode)
        {
            case ViewportInteractionMode.RoiSelect:
                RoiToggle.IsChecked = false;
                break;
            case ViewportInteractionMode.LineProfile:
                ProfileToggle.IsChecked = false;
                break;
            case ViewportInteractionMode.WhiteBalancePick:
                WbPickToggle.IsChecked = false;
                break;
            default:
                return false;
        }

        return true;
    }

    private bool CancelViewportInteraction() =>
        _vm.CanUseMainView && (Viewport.ClearKeyboardCursor() || ExitViewportToolMode());
}

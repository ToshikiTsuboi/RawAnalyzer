using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace RawAnalyzer.App.Controls;

/// <summary>標準のあふれ判定を使い、指定した項目以降をバーの右端に寄せる。</summary>
public sealed class SpacedToolBarPanel : ToolBarPanel
{
    public static readonly DependencyProperty StartsTrailingGroupProperty = DependencyProperty.RegisterAttached(
        "StartsTrailingGroup", typeof(bool), typeof(SpacedToolBarPanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static bool GetStartsTrailingGroup(UIElement element) =>
        (bool)element.GetValue(StartsTrailingGroupProperty);

    public static void SetStartsTrailingGroup(UIElement element, bool value) =>
        element.SetValue(StartsTrailingGroupProperty, value);

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        if (Orientation != Orientation.Horizontal)
        {
            return base.ArrangeOverride(arrangeSize);
        }

        double freeWidth = Math.Max(0, arrangeSize.Width - InternalChildren.Cast<UIElement>()
            .Sum(child => child.DesiredSize.Width));
        double x = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (GetStartsTrailingGroup(child))
            {
                x += freeWidth;
                freeWidth = 0;
            }

            Size size = child.DesiredSize;
            child.Arrange(new Rect(x, 0, size.Width, Math.Max(arrangeSize.Height, size.Height)));
            x += size.Width;
        }

        return arrangeSize;
    }
}

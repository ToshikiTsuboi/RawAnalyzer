using System.Windows;
using System.Windows.Controls;
using RawAnalyzer.App.Services;

namespace RawAnalyzer.App.Controls;

public sealed class FolderTreeView : TreeView
{
    protected override DependencyObject GetContainerForItemOverride() => new FolderTreeViewItem();

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        FolderTreeViewItem.Attach(element, item);
    }

    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        FolderTreeViewItem.Detach(element, item);
        base.ClearContainerForItemOverride(element, item);
    }
}

internal sealed class FolderTreeViewItem : TreeViewItem
{
    protected override DependencyObject GetContainerForItemOverride() => new FolderTreeViewItem();

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        Attach(element, item);
    }

    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        Detach(element, item);
        base.ClearContainerForItemOverride(element, item);
    }

    internal static void Attach(DependencyObject element, object item)
    {
        if (element is FolderTreeViewItem container && item is FolderTreeNode node)
        {
            node.Container = container;
        }
    }

    internal static void Detach(DependencyObject element, object item)
    {
        if (item is FolderTreeNode node && ReferenceEquals(node.Container, element))
        {
            node.Container = null;
        }
    }
}

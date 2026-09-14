using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace ModStudio.App;

/// <summary>
/// Content area of the document TabControl. The stock ContentPresenter detaches the previous tab's content and attaches the
/// next one on every switch, which re-styles and re-measures every visual in an editor pane (two DataGrids, thousands of cells).
/// Editor panes stay attached here and only toggle visibility, so switching tabs costs a layout pass instead of a rebuild.
/// Other content (loading placeholders, welcome, preview panes) still swaps in and out the way a ContentPresenter would.
/// </summary>
public sealed class DocumentTabHost : Control
{
    private TabControl? owner;
    private readonly HashSet<TabItem> tracked = [];
    private readonly HashSet<Control> parented = [];

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        owner = TemplatedParent as TabControl;
        if (owner == null) return;
        owner.SelectionChanged += OwnerSelectionChanged;
        ((INotifyCollectionChanged)owner.Items).CollectionChanged += ItemsChanged;
        Sync();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (owner != null) { owner.SelectionChanged -= OwnerSelectionChanged; ((INotifyCollectionChanged)owner.Items).CollectionChanged -= ItemsChanged; }
        foreach (var tab in tracked) tab.PropertyChanged -= TabPropertyChanged;
        tracked.Clear(); owner = null;
        for (int i = VisualChildren.Count - 1; i >= 0; i--) if (VisualChildren[i] is Control child) Remove(child);
    }
    private void OwnerSelectionChanged(object? sender, SelectionChangedEventArgs e) => Sync();
    private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Sync();
    private void TabPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e) { if (e.Property == ContentControl.ContentProperty) Sync(); }

    private void Sync()
    {
        if (owner == null) return;
        var items = owner.Items.OfType<TabItem>().ToList();
        foreach (var tab in items) if (tracked.Add(tab)) tab.PropertyChanged += TabPropertyChanged;
        foreach (var tab in tracked.Where(t => !items.Contains(t)).ToList()) { tab.PropertyChanged -= TabPropertyChanged; tracked.Remove(tab); }
        var selected = owner.SelectedItem as TabItem;
        Control? visible = null; var desired = new List<Control>();
        foreach (var tab in items)
        {
            if (tab.Content is not Control content) continue;
            bool isSelected = tab == selected;
            if (isSelected) visible = content;
            if (isSelected || content is EditorPane) desired.Add(content);
        }
        for (int i = VisualChildren.Count - 1; i >= 0; i--) if (VisualChildren[i] is Control child && !desired.Contains(child)) Remove(child);
        foreach (var content in desired)
        {
            if (!VisualChildren.Contains(content))
            {
                if (content.Parent == null) { ((ISetLogicalParent)content).SetParent(owner); parented.Add(content); }
                VisualChildren.Add(content);
            }
            content.IsVisible = content == visible;
        }
        // A hidden pane must not keep keyboard focus, or typing would land in an editor the user cannot see.
        // Avalonia 12 dropped ClearFocus; focusing nothing is how the focus manager clears it.
        if (TopLevel.GetTopLevel(this) is { FocusManager: { } focus } && focus.GetFocusedElement() is Visual focused && focused.GetSelfAndVisualAncestors().Any(v => v != visible && v is Control { IsVisible: false } && v.GetVisualParent() == this))
            focus.Focus(null);
        InvalidateMeasure();
    }
    private void Remove(Control child)
    {
        VisualChildren.Remove(child);
        if (parented.Remove(child)) ((ISetLogicalParent)child).SetParent(null);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size();
        foreach (var child in VisualChildren.OfType<Control>())
        {
            if (!child.IsVisible) continue;
            child.Measure(availableSize);
            size = new Size(Math.Max(size.Width, child.DesiredSize.Width), Math.Max(size.Height, child.DesiredSize.Height));
        }
        return size;
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in VisualChildren.OfType<Control>()) if (child.IsVisible) child.Arrange(new Rect(finalSize));
        return finalSize;
    }
}

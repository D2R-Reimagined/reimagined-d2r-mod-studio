using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace ModStudio.App;

/// <summary>
/// Dragging a document tab sideways reorders the tabs; the order is what the session remembers and restores. While dragging,
/// a ghost of the tab follows the pointer and a bar marks the slot it will land in; the tabs themselves only move on release.
/// </summary>
public partial class MainWindow
{
    private static readonly IBrush TabGhostBackground = new SolidColorBrush(Color.Parse("#2E2F31")), TabAccent = new SolidColorBrush(Color.Parse("#D8BC86"));
    private TabItem? draggedTab;
    private Point tabDragOrigin;
    private bool tabDragging;
    private int tabDropSlot = -1;
    private Border? tabGhost, tabInsertMark;

    private void InitializeTabDragging()
    {
        // Handlers sit on the tab control rather than on each tab, so they survive the tab strip re-laying out under the pointer.
        Documents.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(Documents).Properties.IsLeftButtonPressed) return;
            draggedTab = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<TabItem>().FirstOrDefault(t => tabs.Contains(t));
            tabDragOrigin = e.GetPosition(Documents); tabDragging = false; tabDropSlot = -1;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        Documents.AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (draggedTab == null) return;
            if (!e.GetCurrentPoint(Documents).Properties.IsLeftButtonPressed) { EndTabDrag(drop: false); return; }
            var position = e.GetPosition(Documents);
            if (!tabDragging) { if (Math.Abs(position.X - tabDragOrigin.X) < 8) return; tabDragging = true; draggedTab.Opacity = 0.45; ShowTabGhost(draggedTab); }
            e.Handled = true;
            UpdateTabDrag(position);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        Documents.AddHandler(PointerReleasedEvent, (_, _) => EndTabDrag(drop: true), RoutingStrategies.Tunnel, handledEventsToo: true);
        Documents.PointerCaptureLost += (_, _) => EndTabDrag(drop: false);
    }

    private void ShowTabGhost(TabItem tab)
    {
        var overlay = OverlayLayer.GetOverlayLayer(Documents); if (overlay == null) return;
        var name = System.IO.Path.GetFileName(TabFile(tab)); if (string.IsNullOrEmpty(name)) name = tab.Header?.ToString() ?? "";
        tabGhost = new Border
        {
            Child = new TextBlock { Text = name, FontSize = 13 }, Background = TabGhostBackground, BorderBrush = TabAccent, BorderThickness = new(1), CornerRadius = new(4),
            Padding = new(10, 5), Opacity = 0.92, IsHitTestVisible = false, BoxShadow = new BoxShadows(new BoxShadow { Blur = 10, Color = Color.FromArgb(120, 0, 0, 0) })
        };
        tabInsertMark = new Border { Width = 3, Background = TabAccent, CornerRadius = new(1.5), IsHitTestVisible = false, IsVisible = false };
        overlay.Children.Add(tabInsertMark); overlay.Children.Add(tabGhost);
    }
    private void UpdateTabDrag(Point position)
    {
        if (draggedTab == null) return;
        tabDropSlot = TabDropSlot(position.X);
        var overlay = tabGhost?.Parent as OverlayLayer; if (overlay == null) return;
        var pointer = Documents.TranslatePoint(position, overlay) ?? position;
        Canvas.SetLeft(tabGhost!, pointer.X + 12); Canvas.SetTop(tabGhost!, pointer.Y - 14);
        // The bar sits at the left edge of the tab that will follow the dropped one, or after the last tab.
        var others = tabs.Where(t => t != draggedTab && t.Bounds.Width > 0).ToList(); if (others.Count == 0 || tabInsertMark == null) return;
        var anchor = tabDropSlot < others.Count ? others[tabDropSlot] : others[^1];
        if (anchor.TranslatePoint(new Point(tabDropSlot < others.Count ? 0 : anchor.Bounds.Width, 0), overlay) is not { } at) return;
        Canvas.SetLeft(tabInsertMark, at.X - 1.5); Canvas.SetTop(tabInsertMark, at.Y + 4); tabInsertMark.Height = Math.Max(16, anchor.Bounds.Height - 8); tabInsertMark.IsVisible = true;
    }
    private void EndTabDrag(bool drop)
    {
        var tab = draggedTab; int slot = tabDropSlot; bool dragging = tabDragging;
        if (tabGhost?.Parent is OverlayLayer overlay) { overlay.Children.Remove(tabGhost); if (tabInsertMark != null) overlay.Children.Remove(tabInsertMark); }
        tabGhost = null; tabInsertMark = null; draggedTab = null; tabDragging = false; tabDropSlot = -1;
        if (tab == null) return; tab.Opacity = 1;
        if (drop && dragging && slot >= 0) DropTab(tab, slot);
    }
    /// <summary>The slot a tab dropped at <paramref name="x"/> takes: how many other tabs have their midpoint left of the pointer.</summary>
    internal int TabDropSlot(double x)
    {
        int slot = 0;
        foreach (var other in tabs)
        {
            if (other == draggedTab || other.Bounds.Width <= 0 || other.TranslatePoint(new Point(0, 0), Documents) is not { } origin) continue;
            if (x > origin.X + other.Bounds.Width / 2) slot++;
        }
        return slot;
    }
    private void DropTab(TabItem tab, int slot)
    {
        int from = tabs.IndexOf(tab); if (from < 0 || slot == from) return;
        var selected = Documents.SelectedItem; tabs.Move(from, Math.Clamp(slot, 0, tabs.Count - 1)); Documents.SelectedItem = selected;
    }
}

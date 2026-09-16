using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace ModStudio.App;

/// <summary>Column guide flyout and the item hover-card toggle, both living on the table toolbar.</summary>
public sealed partial class EditorPane
{
    /// <summary>Whether hovering a unique/set item row pops up its rendered tooltip. Shared by every pane; the window persists it.</summary>
    public static bool ItemHoverCards { get; set; } = true;
    public static event Action? ItemHoverCardsChanged;
    public static void RaiseItemHoverCardsChanged() => ItemHoverCardsChanged?.Invoke();
    /// <summary>Pointer position within the row that raised the last <see cref="ItemHovered"/>, so a hover card can avoid the pointer.</summary>
    public Point HoverPosition { get; private set; }
    private ToggleButton? itemCardToggle;

    /// <summary>Opens the searchable data-guide flyout for a column, anchored at its header when it is on screen (else at the toolbar button).</summary>
    public void ShowColumnGuide(string column, Control? anchor)
    {
        var table = Document.Table; if (table == null) return;
        if (anchor == null)
        {
            int index = table.ColumnIndex(column);
            var header = TableGrid.GetVisualDescendants().OfType<DataGridColumnHeader>().FirstOrDefault(h => h.IsEffectivelyVisible && h.Bounds.Width > 0 &&
                TableGrid.Columns.FirstOrDefault(c => Equals(c.Header, h.Content)) is { } c && columnMap.TryGetValue(c, out var i) && i == index);
            anchor = header ?? (Control)this;
        }
        var value = SelectedRow >= 0 && SelectedRow < table.Records.Count && table.ColumnIndex(column) >= 0 ? table.Cell(SelectedRow, column) : null;
        ColumnGuideFlyout.Show(anchor, table, column, value, error);
    }

    private void InitializeItemCardToggle(Panel toolbar)
    {
        if (Document.Table?.Name is not ("uniqueitems" or "setitems")) return;
        itemCardToggle = new ToggleButton { Content = "Hover cards", IsChecked = ItemHoverCards, VerticalAlignment = VerticalAlignment.Center, Margin = new(6, 0, 0, 0), Padding = new(8, 4) };
        ToolTip.SetTip(itemCardToggle, "Show the rendered item when the pointer rests on a row. The Item Preview tab always shows the selected item.");
        itemCardToggle.IsCheckedChanged += (_, _) => { if (ItemHoverCards != (itemCardToggle.IsChecked == true)) { ItemHoverCards = itemCardToggle.IsChecked == true; RaiseItemHoverCardsChanged(); } };
        // Follow toggles made in other panes; subscribe only while in the tree so closed panes are not kept alive by the static event.
        void Follow() { if (itemCardToggle.IsChecked != ItemHoverCards) itemCardToggle.IsChecked = ItemHoverCards; }
        AttachedToVisualTree += (_, _) => { Follow(); ItemHoverCardsChanged += Follow; };
        DetachedFromVisualTree += (_, _) => ItemHoverCardsChanged -= Follow;
        toolbar.Children.Add(itemCardToggle);
    }
}

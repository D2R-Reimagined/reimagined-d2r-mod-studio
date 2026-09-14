using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using ModStudio.Core;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace ModStudio.App;

/// <summary>Dragging a row header moves the selected rows; dragging a column header (the grid's own reordering) moves the column in the schema.</summary>
public partial class EditorPane
{
    private int rowDragSource = -1, rowDropTarget = -1;
    private Point rowDragOrigin;
    private bool rowDragging;
    private bool columnReordering;
    private static readonly IBrush DropBrush = new SolidColorBrush(Color.Parse("#E8C878"));
    // Drop markers sit over the grid: a horizontal bar with a dot at the row-header edge, or a vertical bar down the full height for a column.
    private readonly Border rowDropIndicator = new() { Height = 3, Background = DropBrush, CornerRadius = new CornerRadius(1.5), IsVisible = false, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Border rowDropDot = new() { Width = 9, Height = 9, Background = DropBrush, CornerRadius = new CornerRadius(4.5), IsVisible = false, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border columnDropIndicator = new() { Width = 3, Background = DropBrush, CornerRadius = new CornerRadius(1.5), IsVisible = false, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Left };

    private void WireReordering()
    {
        foreach (var marker in new Control[] { rowDropIndicator, rowDropDot, columnDropIndicator }) { Grid.SetRow(marker, 1); tableHost.Children.Add(marker); }
        TableGrid.AddHandler(PointerPressedEvent, RowDragPressed, RoutingStrategies.Tunnel);
        TableGrid.AddHandler(PointerMovedEvent, RowDragMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        TableGrid.AddHandler(PointerReleasedEvent, RowDragReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        TableGrid.PointerCaptureLost += (_, _) => { EndRowDrag(); EndColumnDrag(); };
        TableGrid.CanUserReorderColumns = true;
        TableGrid.ColumnReordering += (_, _) => columnReordering = true;
        TableGrid.ColumnReordered += (_, e) => { EndColumnDrag(); try { ColumnReordered(e.Column); } catch (Exception ex) { error(ex); RefreshColumns(); } };
    }

    private void RowDragPressed(object? sender, PointerPressedEventArgs e)
    {
        rowDragSource = -1;
        if (Document.Table == null || Document.PendingSource || e.Source is not Visual visual || !e.GetCurrentPoint(TableGrid).Properties.IsLeftButtonPressed) return;
        if (!visual.GetSelfAndVisualAncestors().OfType<DataGridRowHeader>().Any()) return;
        if (visual.GetSelfAndVisualAncestors().OfType<DataGridRow>().FirstOrDefault()?.DataContext is not RowView { IsPlaceholder: false } row) return;
        rowDragSource = row.Row; rowDragOrigin = e.GetPosition(TableGrid); rowDragging = false;
    }

    private void RowDragMoved(object? sender, PointerEventArgs e)
    {
        var position = e.GetPosition(TableGrid);
        if (columnReordering) { ShowColumnDropAt(position); return; }
        if (rowDragSource < 0) return;
        if (!e.GetCurrentPoint(TableGrid).Properties.IsLeftButtonPressed) { EndRowDrag(); return; }
        if (!rowDragging)
        {
            if (Math.Abs(position.Y - rowDragOrigin.Y) < 6 && Math.Abs(position.X - rowDragOrigin.X) < 6) return;
            if (sortColumn != null) { EndRowDrag(); error(new InvalidOperationException("Clear sorting before reordering rows; the view order would not match the table.")); return; }
            rowDragging = true; e.Pointer.Capture(TableGrid);
        }
        if (RowDropTargetAt(position) is var (target, y))
        {
            rowDropTarget = target; rowDropIndicator.Margin = new Thickness(0, y - 1.5, 0, 0); rowDropDot.Margin = new Thickness(0, y - 4.5, 0, 0);
            rowDropIndicator.IsVisible = rowDropDot.IsVisible = true;
        }
        e.Handled = true;
    }

    private void RowDragReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (columnReordering) EndColumnDrag(); // the grid raises ColumnReordered afterwards when the drop moved the column
        if (rowDragSource < 0) return;
        int source = rowDragSource, target = rowDropTarget; bool dragging = rowDragging; EndRowDrag();
        if (!dragging || target < 0) return;
        e.Handled = true;
        try
        {
            var rows = SelectedRowsForCommands(); if (!rows.Contains(source)) rows = [source];
            MoveRows(rows, target);
        }
        catch (Exception ex) { error(ex); }
    }

    private void EndRowDrag() { rowDragSource = -1; rowDropTarget = -1; rowDragging = false; rowDropIndicator.IsVisible = rowDropDot.IsVisible = false; }
    private void EndColumnDrag() { columnReordering = false; columnDropIndicator.IsVisible = false; }

    /// <summary>
    /// Marks the boundary the grid's header drag will drop before: the near edge of the header under the pointer (left half
    /// inserts before it, right half after), matching the grid's own midpoint rule.
    /// </summary>
    private void ShowColumnDropAt(Point position)
    {
        var headers = TableGrid.GetVisualDescendants().OfType<DataGridColumnHeader>().Where(h => h.IsVisible && h.Bounds.Width > 0)
            .Select(h => (Left: h.TranslatePoint(new Point(0, 0), TableGrid)?.X ?? double.NaN, h.Bounds.Width)).Where(h => !double.IsNaN(h.Left)).OrderBy(h => h.Left).ToArray();
        if (headers.Length == 0) return;
        double x;
        if (position.X >= headers[^1].Left + headers[^1].Width) x = headers[^1].Left + headers[^1].Width;
        else
        {
            var under = headers.FirstOrDefault(h => position.X >= h.Left && position.X < h.Left + h.Width);
            if (under == default) return;
            x = position.X < under.Left + under.Width / 2 ? under.Left : under.Left + under.Width;
        }
        columnDropIndicator.Margin = new Thickness(x - 1.5, 0, 0, 0); columnDropIndicator.IsVisible = true;
    }

    /// <summary>The slot a drop at this point inserts before, and the y position of the indicator line: above or below the row under the pointer, or after the last row.</summary>
    private (int Target, double Y)? RowDropTargetAt(Point position)
    {
        if (TableGrid.InputHitTest(position) is not Visual under) return null;
        if (under.GetSelfAndVisualAncestors().OfType<DataGridRow>().FirstOrDefault() is not { DataContext: RowView view } rowControl) return null;
        var top = rowControl.TranslatePoint(new Point(0, 0), TableGrid) ?? default; double height = rowControl.Bounds.Height;
        if (view.IsPlaceholder) return (Document.Table!.Records.Count, top.Y);
        bool below = position.Y > top.Y + height / 2;
        return (view.Row + (below ? 1 : 0), top.Y + (below ? height : 0));
    }

    /// <summary>Moves rows so they sit before <paramref name="target"/>; the selection and frozen rows follow their records.</summary>
    public void MoveRows(int[] rows, int target)
    {
        Storage.Require(Document.Table != null && !Document.PendingSource, "Apply valid source before moving rows.");
        int revision = Document.Revision; Document.MoveRows(rows, target);
        if (Document.Revision == revision) return;
        var order = ModStudio.Core.Document.MoveOrder(Document.Table!.Records.Count, rows, target);
        var to = new int[order.Length]; for (int i = 0; i < order.Length; i++) to[order[i]] = i;
        var cells = selectedCells.Where(c => c.Row >= 0 && c.Row < to.Length).Select(c => (Row: to[c.Row], c.Col)).ToArray(); selectedCells.Clear(); selectedCells.UnionWith(cells);
        if (cellAnchor is { } anchor && anchor.Row >= 0 && anchor.Row < to.Length) cellAnchor = (to[anchor.Row], anchor.Col);
        for (int i = 0; i < frozenRows.Count; i++) frozenRows[i] = to[frozenRows[i]];
        int first = rows.Where(r => r >= 0 && r < to.Length).Select(r => to[r]).DefaultIfEmpty(-1).Min();
        var column = SelectedColumn; Refresh(); if (first >= 0) Jump(first, column);
    }

    private void ColumnReordered(DataGridColumn column)
    {
        if (!columnMap.TryGetValue(column, out var from)) return;
        var display = TableGrid.Columns.OrderBy(c => c.DisplayIndex).Select(c => columnMap[c]).ToArray();
        int to = ColumnMoveTarget(display, from, frozenColumns);
        if (to < 0) { RefreshColumns(); Storage.Require(false, "Unfreeze the column before moving it, and drop it next to an unfrozen column."); }
        MoveColumn(from, to);
    }

    /// <summary>
    /// Physical destination for a column shown at a new place in the display order: just before the column that now follows it,
    /// or just after the one before it. Frozen columns are shown out of physical order, so they cannot anchor a move; -1 refuses.
    /// </summary>
    internal static int ColumnMoveTarget(int[] display, int from, IReadOnlyList<int> frozen)
    {
        int k = Array.IndexOf(display, from); if (k < 0 || frozen.Contains(from)) return -1;
        if (k + 1 < display.Length && !frozen.Contains(display[k + 1])) { int next = display[k + 1]; return from < next ? next - 1 : next; }
        if (k > 0 && !frozen.Contains(display[k - 1])) { int previous = display[k - 1]; return from < previous ? previous : previous + 1; }
        return display.Length == 1 ? from : -1;
    }

    /// <summary>Moves a column in the schema; widths, frozen columns and the cell selection follow their columns.</summary>
    public void MoveColumn(int from, int to)
    {
        Storage.Require(Document.Table != null && !Document.PendingSource, "Apply valid source before moving columns.");
        int revision = Document.Revision; Document.MoveColumn(from, to);
        if (Document.Revision == revision) { RefreshColumns(); return; }
        int Map(int index) => index == from ? to : from < to ? (index > from && index <= to ? index - 1 : index) : (index >= to && index < from ? index + 1 : index);
        var movedWidths = widths.ToDictionary(p => Map(p.Key), p => p.Value); widths.Clear(); foreach (var p in movedWidths) widths[p.Key] = p.Value;
        var movedFits = fittedWidths.ToDictionary(p => Map(p.Key), p => p.Value); fittedWidths.Clear(); foreach (var p in movedFits) fittedWidths[p.Key] = p.Value;
        for (int i = 0; i < frozenColumns.Count; i++) frozenColumns[i] = Map(frozenColumns[i]);
        var cells = selectedCells.Select(c => (c.Row, Col: Map(c.Col))).ToArray(); selectedCells.Clear(); selectedCells.UnionWith(cells);
        if (cellAnchor is { } anchor) cellAnchor = (anchor.Row, Map(anchor.Col));
        Refresh();
    }
}

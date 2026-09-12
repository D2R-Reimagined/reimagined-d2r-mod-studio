using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ModStudio.Core;

namespace ModStudio.App;

/// <summary>
/// Spreadsheet-style cell selection on top of the row-based DataGrid: click, Ctrl+click, Shift+click and drag pick cells;
/// the row header picks whole rows. Editing, Delete, copy and paste act on every selected cell.
/// Selection is stored as table coordinates (record index, column index) so it survives column windows and re-sorting.
/// </summary>
public sealed partial class EditorPane
{
    private readonly HashSet<(int Row, int Col)> selectedCells = [];
    private (int Row, int Col)? cellAnchor;
    private bool draggingCells, rowHeaderPress, settingCurrent, paintQueued;
    private (int Row, int Col)? editingCell;
    private string? editedValue;
    private static readonly IBrush SelectedCellBrush = new SolidColorBrush(Color.Parse("#4A4123"));
    public IReadOnlyCollection<(int Row, int Col)> SelectedCells => selectedCells;
    public bool RowSelectionMode => rowHeaderPress;
    /// <summary>Table column index behind a displayed grid column (-1 when unknown).</summary>
    public int ColumnIndexOf(DataGridColumn column) => columnMap.TryGetValue(column, out var index) ? index : -1;

    private void WireCellSelection(DataGrid grid)
    {
        grid.ClipboardCopyMode = DataGridClipboardCopyMode.None; // the grid's own Ctrl+C copies whole rows; ours copies cells
        grid.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (Document.Table == null || e.Source is not Visual visual) return;
            var point = e.GetCurrentPoint(grid);
            rowHeaderPress = visual.GetSelfAndVisualAncestors().OfType<DataGridRowHeader>().Any();
            if (rowHeaderPress || !point.Properties.IsLeftButtonPressed && !point.Properties.IsRightButtonPressed) return;
            if (LocateCell(grid, visual) is not { } hit) return;
            activeGrid = grid;
            if (point.Properties.IsRightButtonPressed) { if (!selectedCells.Contains(hit.Cell)) { selectedCells.Clear(); selectedCells.Add(hit.Cell); cellAnchor = hit.Cell; PaintCells(); } return; }
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                if (!selectedCells.Remove(hit.Cell)) selectedCells.Add(hit.Cell);
                cellAnchor = hit.Cell; e.Handled = true; SetCurrent(grid, hit.Item, hit.Column); PaintCells();
            }
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && cellAnchor != null)
            {
                SelectRectangle(grid, cellAnchor.Value, hit.Cell); e.Handled = true; SetCurrent(grid, hit.Item, hit.Column); PaintCells();
            }
            else
            {
                // Plain click: let the grid move focus/current cell; we own the selection.
                selectedCells.Clear(); selectedCells.Add(hit.Cell); cellAnchor = hit.Cell; draggingCells = e.ClickCount == 1; PaintCells();
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        grid.AddHandler(PointerReleasedEvent, (_, _) => draggingCells = false, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        grid.PointerCaptureLost += (_, _) => draggingCells = false;
        grid.PointerMoved += (_, e) =>
        {
            if (!draggingCells || cellAnchor == null || !e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed) return;
            if (grid.InputHitTest(e.GetPosition(grid)) is not Visual under || LocateCell(grid, under) is not { } hit) return;
            if (selectedCells.Count == 1 && selectedCells.Contains(hit.Cell)) return;
            SelectRectangle(grid, cellAnchor.Value, hit.Cell); PaintCells();
        };
        grid.SelectionChanged += (_, _) => { if (refreshing || settingCurrent) return; if (rowHeaderPress) { SelectRows(grid); PaintCells(); } };
        grid.CurrentCellChanged += (_, _) =>
        {
            if (refreshing || settingCurrent || rowHeaderPress || draggingCells) return;
            // Keyboard navigation and plain clicks collapse the selection to the current cell.
            if (grid.SelectedItem is RowView row && grid.CurrentColumn != null && columnMap.TryGetValue(grid.CurrentColumn, out var col) && !(selectedCells.Count > 1 && selectedCells.Contains((row.Row, col))))
            { selectedCells.Clear(); selectedCells.Add((row.Row, col)); cellAnchor = (row.Row, col); PaintCells(); }
        };
        grid.BeginningEdit += (_, e) => { draggingCells = false; if (e.Row.DataContext is RowView row && columnMap.TryGetValue(e.Column, out var col)) editingCell = (row.Row, col); };
        grid.CellEditEnding += (_, e) => { editedValue = e.EditAction == DataGridEditAction.Commit ? (e.EditingElement as TextBox)?.Text : null; };
        grid.CellEditEnded += (_, e) =>
        {
            var cell = editingCell; var value = editedValue; editingCell = null; editedValue = null;
            if (e.EditAction != DataGridEditAction.Commit || cell == null || value == null) return;
            if (cell.Value.Row < 0 && e.Row.DataContext is RowView created && !created.IsPlaceholder) { Dispatcher.UIThread.Post(() => { Refresh(); Jump(created.Row, Document.Table!.Columns[cell.Value.Col]); }, DispatcherPriority.Background); return; }
            // Editing one of several selected cells writes the value into all of them.
            if (selectedCells.Count > 1 && selectedCells.Contains(cell.Value)) try { ApplyToSelection(value, cell.Value); } catch (Exception ex) { error(ex); }
        };
        grid.LoadingRow += (_, _) => QueuePaint();
        grid.KeyDown += (_, e) =>
        {
            rowHeaderPress = false;
            if (e.KeyModifiers != KeyModifiers.None || e.Key != Key.Delete || selectedCells.Count == 0 || Document.Table == null) return;
            try { ApplyToSelection("", null); e.Handled = true; } catch (Exception ex) { error(ex); }
        };
    }
    private (RowView Item, DataGridColumn Column, (int Row, int Col) Cell)? LocateCell(DataGrid grid, Visual visual)
    {
        var cell = visual.GetSelfAndVisualAncestors().OfType<DataGridCell>().FirstOrDefault(); if (cell == null) return null;
        var rowControl = cell.GetVisualAncestors().OfType<DataGridRow>().FirstOrDefault(); if (rowControl?.DataContext is not RowView item) return null;
        foreach (var column in grid.Columns)
            if (column.GetCellContent(rowControl) is Visual content && (content == cell || content.GetVisualAncestors().Contains(cell)) && columnMap.TryGetValue(column, out var index))
                return (item, column, (item.Row, index));
        return null;
    }
    private void SetCurrent(DataGrid grid, RowView item, DataGridColumn column)
    {
        settingCurrent = true;
        try { activeGrid = grid; grid.SelectedItem = item; grid.CurrentColumn = column; selectedRow = item.Row; selectedColumn = Document.Table!.Columns[columnMap[column]]; grid.Focus(); }
        finally { settingCurrent = false; }
        selection(this);
    }
    private void SelectRows(DataGrid grid)
    {
        selectedCells.Clear();
        foreach (var row in grid.SelectedItems.OfType<RowView>().Where(r => !r.IsPlaceholder))
            for (int col = 0; col < Document.Table!.Columns.Length; col++) selectedCells.Add((row.Row, col));
        if (grid.SelectedItem is RowView first && !first.IsPlaceholder) cellAnchor = (first.Row, 0);
    }
    /// <summary>Selects the rectangle between two cells in displayed order (rows as sorted/filtered, columns as shown).</summary>
    private void SelectRectangle(DataGrid grid, (int Row, int Col) a, (int Row, int Col) b)
    {
        var rows = (grid.ItemsSource as IEnumerable<RowView> ?? []).Where(r => !r.IsPlaceholder).Select(r => r.Row).ToList();
        var cols = grid.Columns.Select(c => columnMap.TryGetValue(c, out var i) ? i : -1).Where(i => i >= 0).ToList();
        int r1 = rows.IndexOf(a.Row), r2 = rows.IndexOf(b.Row), c1 = cols.IndexOf(a.Col), c2 = cols.IndexOf(b.Col);
        selectedCells.Clear();
        if (r1 < 0 || r2 < 0 || c1 < 0 || c2 < 0) { selectedCells.Add(b); return; }
        for (int r = Math.Min(r1, r2); r <= Math.Max(r1, r2); r++)
            for (int c = Math.Min(c1, c2); c <= Math.Max(c1, c2); c++) selectedCells.Add((rows[r], cols[c]));
    }
    /// <summary>Programmatic selection used by tests and the inspector: same semantics as clicking with the given modifiers.</summary>
    public void SelectCell(int row, int col, bool control = false, bool shift = false)
    {
        var cell = (row, col);
        if (control) { if (!selectedCells.Remove(cell)) selectedCells.Add(cell); cellAnchor = cell; }
        else if (shift && cellAnchor != null) SelectRectangle(activeGrid, cellAnchor.Value, cell);
        else { selectedCells.Clear(); selectedCells.Add(cell); cellAnchor = cell; }
        selectedRow = row; selectedColumn = Document.Table!.Columns[col];
        PaintCells(); selection(this);
    }
    private void QueuePaint()
    {
        if (paintQueued) return; paintQueued = true;
        Dispatcher.UIThread.Post(() => { paintQueued = false; PaintCells(); }, DispatcherPriority.Background);
    }
    private void PaintCells()
    {
        foreach (var grid in new[] { TableGrid, FrozenGrid })
        {
            if (!grid.IsVisible) continue;
            foreach (var rowControl in grid.GetVisualDescendants().OfType<DataGridRow>())
            {
                if (rowControl.DataContext is not RowView item) continue;
                foreach (var column in grid.Columns)
                {
                    if (column.GetCellContent(rowControl) is not Visual content || !columnMap.TryGetValue(column, out var col)) continue;
                    var cell = content as DataGridCell ?? content.GetVisualAncestors().OfType<DataGridCell>().FirstOrDefault(); if (cell == null) continue;
                    if (!item.IsPlaceholder && selectedCells.Contains((item.Row, col))) cell.Background = SelectedCellBrush; else cell.ClearValue(DataGridCell.BackgroundProperty);
                }
            }
        }
    }
    /// <summary>Writes one value into every selected editable cell (except the cell that was just committed by the grid itself).</summary>
    public void ApplyToSelection(string value, (int Row, int Col)? except)
    {
        var table = Document.Table!;
        var edits = selectedCells.Where(c => c != except && c.Row >= 0 && c.Row < table.Records.Count && !Document.LockedRows.Contains(c.Row) && !Document.LockedColumns.Contains(table.Columns[c.Col]))
            .Where(c => !(table.IsCatalog && c.Col < 2)).Select(c => (c.Row, table.Columns[c.Col], value)).ToArray();
        if (edits.Length == 0) return;
        Document.SetCells(edits);
        foreach (var row in edits.Select(e => e.Row).Distinct()) RefreshRowValues(row);
        selection(this);
    }
    private void ShiftSelection(int from, int delta)
    {
        var moved = selectedCells.Where(c => c.Row >= from).ToArray(); selectedCells.ExceptWith(moved);
        selectedCells.UnionWith(moved.Select(c => (Row: c.Row + delta, c.Col)).Where(c => c.Row >= 0));
        if (cellAnchor is { } a && a.Row >= from) cellAnchor = (a.Row + delta, a.Col);
        frozenRows.RemoveAll(r => r >= from && r + delta < from); for (int i = 0; i < frozenRows.Count; i++) if (frozenRows[i] >= from) frozenRows[i] += delta;
    }
    /// <summary>Inserts blank rows and selects the first one. Rows are identified, not renumbered, so later profile overrides stay attached.</summary>
    public void InsertRows(int index, int count = 1)
    {
        Storage.Require(Document.Table != null && !Document.PendingSource, "Apply valid source before adding rows.");
        Document.InsertRows(index, count); ShiftSelection(index, count);
        Refresh(); Jump(index, Document.Table!.Columns[0]);
        selectedCells.Clear(); for (int i = 0; i < count; i++) selectedCells.Add((index + i, 0)); cellAnchor = (index, 0); QueuePaint();
    }
    public void DeleteSelectedRows()
    {
        var rows = SelectedRowsForCommands(); Storage.Require(rows.Length > 0, "Select rows to delete.");
        Document.DeleteRows(rows); selectedCells.Clear(); frozenRows.Clear(); Refresh();
    }
    /// <summary>Rows the row-level commands act on: row-header selection first, otherwise every row with a selected cell.</summary>
    private int[] SelectedRowsForCommands()
    {
        var fromGrid = activeGrid.SelectedItems.OfType<RowView>().Where(r => !r.IsPlaceholder).Select(r => r.Row).ToArray();
        var fromCells = selectedCells.Select(c => c.Row).Where(r => r >= 0).Distinct().Order().ToArray();
        return fromCells.Length > fromGrid.Length ? fromCells : fromGrid;
    }
    private int MaterializePlaceholder()
    {
        Storage.Require(Document.Table != null && !Document.PendingSource, "Apply valid source before adding rows.");
        int index = Document.Table!.Records.Count; Document.InsertRows(index); return index;
    }
    /// <summary>Tab-separated text for the selection: a single cell, the bounding rectangle of selected cells, or whole rows.</summary>
    private string SelectionText(bool wholeRows)
    {
        var table = Document.Table!;
        if (wholeRows || selectedCells.Count == 0)
            return string.Join('\n', SelectedRowsForCommands().Select(r => string.Join('\t', VisibleColumns().Select(c => table.Cell(r, table.Columns[c])))));
        var rows = (activeGrid.ItemsSource as IEnumerable<RowView> ?? []).Where(r => !r.IsPlaceholder).Select(r => r.Row).ToList();
        var cols = selectedCells.Select(c => c.Col).Distinct().Order().ToList();
        var rowOrder = selectedCells.Select(c => c.Row).Distinct().OrderBy(r => rows.IndexOf(r)).ToList();
        return string.Join('\n', rowOrder.Select(r => string.Join('\t', cols.Select(c => selectedCells.Contains((r, c)) ? table.Cell(r, table.Columns[c]) : ""))));
    }
}

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
    /// <summary>The selected cells are a whole row picked from its header, not a block the user built cell by cell.</summary>
    private bool rowBlockSelection;
    private (int Row, int Col)? editingCell;
    private (int Row, int Col)[] editTargets = [];
    private string? editedValue, typedText;
    /// <summary>The cell's value when the edit began, restored when the edit is canceled.</summary>
    private string? editingOriginal;
    private TextBox? liveEditor;
    private string? liveValue;
    private TopLevel? editInputRoot;
    private void CommitEditBeforePointerSelection(object? sender, PointerPressedEventArgs e)
    {
        if (liveEditor == null || e.Source is not Visual visual || visual.GetSelfAndVisualAncestors().Contains(liveEditor)) return;
        if (editTargets.Length > 1 && liveValue is { } value && GroupEditChanged(value, editingOriginal))
        {
            // Persist the owned edit buffer before any focus/selection handler can cancel the grid editor.
            // RowView defers the grid's writeback for these cells, so it cannot overwrite this value.
            try { ApplyToCells(editTargets, value, null); }
            catch (Exception ex) { error(ex); e.Handled = true; return; }
        }
        if (!activeGrid.CommitEdit(DataGridEditingUnit.Cell, true)) e.Handled = true;
    }
    /// <summary>
    /// Whether an edit that covers several selected cells has anything to write. Opening a cell's editor and clicking
    /// away again is how a value gets read, not a request to copy it over the rest of the selection, so a group edit
    /// only writes once the text differs from what the cell held when the edit began.
    /// </summary>
    private static bool GroupEditChanged(string? value, string? original) => value != null && original != null && value != original;
    private bool IsEditableCell((int Row, int Col) cell) => Document.Table is { } table && cell.Row >= 0 && cell.Row < table.Records.Count &&
        cell.Col >= 0 && cell.Col < table.Columns.Length && !Document.LockedRows.Contains(cell.Row) &&
        !Document.LockedColumns.Contains(table.Columns[cell.Col]) && !(table.IsCatalog && cell.Col < 2 && table.IsOriginalRow(cell.Row));
    private bool DeferCellEdit(int row, int col) => editingCell != null && editTargets.Length > 1 && editTargets.Contains((row, col));
    private string? PreviewCellValue(int row, int col) => DeferCellEdit(row, col) && IsEditableCell((row, col)) ? liveValue : null;
    private void LiveEditorChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (liveEditor == null || e.Property != TextBox.TextProperty || sender != liveEditor || editTargets.Length < 2 || liveValue == liveEditor.Text) return;
        liveValue = liveEditor.Text ?? "";
        RefreshLiveCells(editTargets);
    }
    private static readonly IBrush SelectedCellBrush = new SolidColorBrush(Color.Parse("#4A4123"));
    public IReadOnlyCollection<(int Row, int Col)> SelectedCells => selectedCells;
    public bool RowSelectionMode => rowHeaderPress;
    /// <summary>Table column index behind a displayed grid column (-1 when unknown).</summary>
    public int ColumnIndexOf(DataGridColumn column) => columnMap.TryGetValue(column, out var index) ? index : -1;

    private void WireCellSelection(DataGrid grid)
    {
        if (grid == TableGrid)
        {
            AttachedToVisualTree += (_, _) =>
            {
                editInputRoot = TopLevel.GetTopLevel(this);
                editInputRoot?.AddHandler(PointerPressedEvent, CommitEditBeforePointerSelection, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            };
            DetachedFromVisualTree += (_, _) =>
            {
                editInputRoot?.RemoveHandler(PointerPressedEvent, CommitEditBeforePointerSelection); editInputRoot = null;
            };
        }
        grid.ClipboardCopyMode = DataGridClipboardCopyMode.None; // the grid's own Ctrl+C copies whole rows; ours copies cells
        // While a cell is being edited the arrow keys stay in its text. Left/Right went to the grid whenever the editor had lost
        // focus (it can, right after an edit started by typing) and Up/Down always did, so a key that moved the caret one moment
        // moved the selection the next. Enter and Tab commit; Escape cancels.
        grid.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Tab && (e.KeyModifiers & ~KeyModifiers.Shift) == 0 &&
                grid.SelectedItem is RowView tabRow && grid.CurrentColumn is { } tabColumn && columnMap.TryGetValue(tabColumn, out int tabIndex))
            {
                bool createdRow = editingCell is { Row: < 0 };
                e.Handled = true;
                if (!grid.CommitEdit(DataGridEditingUnit.Cell, true)) return;
                bool reverse = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                // A newly typed row is refreshed after commit; navigate after that refresh.
                if (createdRow) Dispatcher.UIThread.Post(() => MoveTabSelection(grid, tabRow.Row, tabIndex, reverse), DispatcherPriority.Background);
                else MoveTabSelection(grid, tabRow.Row, tabIndex, reverse);
                return;
            }
            if (editingCell == null || liveEditor is not { } editor || (e.KeyModifiers & ~KeyModifiers.Shift) != 0) return;
            int length = editor.Text?.Length ?? 0;
            switch (e.Key)
            {
                case Key.Up or Key.PageUp: if (!editor.IsFocused) editor.Focus(); if (!editor.AcceptsReturn) { editor.SelectionStart = editor.SelectionEnd = editor.CaretIndex = 0; e.Handled = true; } return;
                case Key.Down or Key.PageDown: if (!editor.IsFocused) editor.Focus(); if (!editor.AcceptsReturn) { editor.SelectionStart = editor.SelectionEnd = editor.CaretIndex = length; e.Handled = true; } return;
                case Key.Left or Key.Right or Key.Home or Key.End:
                    if (editor.IsFocused) return;
                    editor.Focus();
                    int caret = e.Key switch { Key.Left => Math.Max(0, editor.CaretIndex - 1), Key.Right => Math.Min(length, editor.CaretIndex + 1), Key.Home => 0, _ => length };
                    if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) editor.SelectionEnd = caret; else editor.SelectionStart = editor.SelectionEnd = editor.CaretIndex = caret;
                    e.Handled = true; return;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        grid.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (IsReferenceButton(e.Source)) return;
            if (Document.Table == null || e.Source is not Visual visual) return;
            // Commit before pointer selection/focus can tear down the editing control or replace the selection.
            if (liveEditor != null && !visual.GetSelfAndVisualAncestors().Contains(liveEditor))
            {
                if (!activeGrid.CommitEdit(DataGridEditingUnit.Cell, true)) { e.Handled = true; return; }
            }
            var point = e.GetCurrentPoint(grid);
            rowHeaderPress = visual.GetSelfAndVisualAncestors().OfType<DataGridRowHeader>().Any();
            if (rowHeaderPress || !point.Properties.IsLeftButtonPressed && !point.Properties.IsRightButtonPressed) return;
            if (LocateCell(grid, visual) is not { } hit)
            {
                // Empty viewport space (including the filler to the right of a row) is a way
                // out of a bulk selection. Headers and scrollbars keep their normal behavior.
                if (point.Properties.IsLeftButtonPressed && !visual.GetSelfAndVisualAncestors()
                    .Any(v => v is DataGridColumnHeader or DataGridColumnHeadersPresenter or ScrollBar or Button or TextBox))
                {
                    ClearCellSelection();
                    grid.Focus();
                    e.Handled = true;
                }
                return;
            }
            activeGrid = grid;
            if (point.Properties.IsRightButtonPressed) { if (!selectedCells.Contains(hit.Cell)) { selectedCells.Clear(); selectedCells.Add(hit.Cell); cellAnchor = hit.Cell; rowBlockSelection = false; PaintCells(); } return; }
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                if (!selectedCells.Remove(hit.Cell)) selectedCells.Add(hit.Cell);
                cellAnchor = hit.Cell; rowBlockSelection = false; e.Handled = true; SetCurrent(grid, hit.Item, hit.Column); PaintCells();
            }
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && cellAnchor != null)
            {
                SelectRectangle(grid, cellAnchor.Value, hit.Cell); rowBlockSelection = false; e.Handled = true; SetCurrent(grid, hit.Item, hit.Column); PaintCells();
            }
            else
            {
                // Plain click: let the grid move focus/current cell; we own the selection. Clicking (or double-clicking
                // to edit) a cell that is already part of a multi-cell selection keeps that selection, so the edit
                // applies to all of it; clicking anywhere else collapses to the clicked cell.
                // A row picked from its header is the exception: it is a way to mark a row, so a click inside it
                // means that one cell, not "write this into all 40 columns".
                if (!rowBlockSelection && selectedCells.Count > 1 && selectedCells.Contains(hit.Cell)) { cellAnchor = hit.Cell; draggingCells = false; return; }
                selectedCells.Clear(); selectedCells.Add(hit.Cell); cellAnchor = hit.Cell; rowBlockSelection = false; draggingCells = e.ClickCount == 1; PaintCells();
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        grid.AddHandler(PointerReleasedEvent, (_, _) =>
        {
            draggingCells = false;
            // Clicking an already-current row need not raise SelectionChanged, but its header still picks the whole row.
            if (rowHeaderPress && activeGrid == grid && Document.Table != null) { SelectRows(grid); PaintCells(); }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
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
            if (grid.SelectedItem is RowView row && grid.CurrentColumn != null && columnMap.TryGetValue(grid.CurrentColumn, out var col) && !(!rowBlockSelection && selectedCells.Count > 1 && selectedCells.Contains((row.Row, col))))
            { selectedCells.Clear(); selectedCells.Add((row.Row, col)); cellAnchor = (row.Row, col); rowBlockSelection = false; PaintCells(); }
        };
        grid.BeginningEdit += (_, e) =>
        {
            if (e.Cancel) return;
            ClearReferenceHighlight();
            draggingCells = false; Document.BeginEditGroup(); // one undo step per committed cell value, not per keystroke
            if (e.Row.DataContext is RowView row && columnMap.TryGetValue(e.Column, out var col))
            {
                // Selecting a row from its header marks the row; it is not a request to edit every column of it.
                // The first edit inside such a row collapses the selection to the cell being typed.
                if (rowBlockSelection)
                {
                    rowBlockSelection = rowHeaderPress = false;
                    selectedCells.Clear(); selectedCells.Add((row.Row, col)); cellAnchor = (row.Row, col); QueuePaint();
                }
                editingCell = (row.Row, col); editTargets = selectedCells.Contains((row.Row, col)) ? selectedCells.ToArray() : [(row.Row, col)];
                editingOriginal = row.IsPlaceholder || row.Row >= Document.Table!.Records.Count ? null : Document.Table.Cell(row.Row, Document.Table.Columns[col]);
            }
        };
        grid.CellEditEnding += (_, e) =>
        {
            editedValue = e.EditAction == DataGridEditAction.Commit ? liveValue ?? (e.EditingElement as TextBox)?.Text : null;
            // The grid tears down/rebinds the editor after this event. That must not erase the pending value.
            if (liveEditor != null) liveEditor.PropertyChanged -= LiveEditorChanged;
        };
        grid.CellEditEnded += (_, e) =>
        {
            try { OnCellEditEnded(e); } finally { Document.EndEditGroup(); }
        };
        void OnCellEditEnded(DataGridCellEditEndedEventArgs e)
        {
            var cell = editingCell; var value = editedValue; var targets = editTargets; var original = editingOriginal;
            if (liveEditor != null) liveEditor.PropertyChanged -= LiveEditorChanged;
            liveEditor = null; liveValue = null; editingCell = null; editedValue = null; editTargets = []; editingOriginal = null;
            RefreshLiveCells(targets); UpdateNote();
            if (e.EditAction != DataGridEditAction.Commit || cell == null || value == null)
            {
                // A single cell's editor writes through to the document as it is typed; Escape means "as it was", so put the value back.
                if (e.EditAction == DataGridEditAction.Cancel && cell is { } canceled && original != null && targets.Length == 1 && IsEditableCell(canceled) && Document.Table!.Cell(canceled.Row, Document.Table.Columns[canceled.Col]) != original)
                    try { Document.SetCells([(canceled.Row, Document.Table.Columns[canceled.Col], original)]); } catch (Exception ex) { error(ex); }
                foreach (var row in targets.Select(c => c.Row).Distinct()) RefreshRowValues(row); return;
            }
            if (cell.Value.Row < 0 && e.Row.DataContext is RowView created && !created.IsPlaceholder) { Dispatcher.UIThread.Post(() => { Refresh(); Jump(created.Row, Document.Table!.Columns[cell.Value.Col]); }, DispatcherPriority.Background); return; }
            // Editing one of several selected cells writes the value into all of them, but only once the value really
            // changed: an editor that was opened and left alone must not overwrite the rest of the selection. The
            // selection captured when editing began is used, because committing by clicking elsewhere changes it first.
            if (targets.Length > 1 && GroupEditChanged(value, original)) try { ApplyToCells(targets, value, null); } catch (Exception ex) { foreach (var row in targets.Select(c => c.Row).Distinct()) RefreshRowValues(row); error(ex); }
        }
        grid.LoadingRow += (_, _) => QueuePaint();
        // Typing into a selected cell starts editing with that text, like a spreadsheet; no second click needed.
        // The live editor mirrors changes into the selection; CellEditEnded commits one document edit.
        grid.TextInput += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Text) || e.Text.Any(char.IsControl) || Document.Table == null || Document.PendingSource) return;
            if (editingCell != null)
            {
                // An edit is open but the editor does not own focus (it can happen right after a programmatic BeginEdit): route the text into it.
                if (liveEditor is { IsFocused: false } editor)
                {
                    int at = Math.Clamp(editor.CaretIndex, 0, editor.Text?.Length ?? 0); var text = editor.Text ?? "";
                    if (editor.SelectionStart != editor.SelectionEnd) { int s = Math.Min(editor.SelectionStart, editor.SelectionEnd); text = text.Remove(s, Math.Abs(editor.SelectionEnd - editor.SelectionStart)); at = s; }
                    editor.Text = text.Insert(at, e.Text); editor.SelectionStart = editor.SelectionEnd = editor.CaretIndex = at + e.Text.Length; editor.Focus(); e.Handled = true;
                }
                return;
            }
            if (grid.SelectedItem is not RowView row || grid.CurrentColumn == null || !columnMap.TryGetValue(grid.CurrentColumn, out var col)) return;
            if (!row.IsPlaceholder && (Document.LockedRows.Contains(row.Row) || Document.LockedColumns.Contains(Document.Table.Columns[col]))) return;
            typedText = e.Text; e.Handled = true;
            if (!grid.BeginEdit()) typedText = null;
        };
        grid.PreparingCellForEdit += (_, e) =>
        {
            var typed = typedText; typedText = null;
            if (e.EditingElement is not TextBox editor) return;
            liveEditor = editor;
            // The row is shorter than the app-wide TextBox minimum, so the editor's inner ScrollViewer thought its text overflowed and
            // showed a squashed vertical scrollbar: a lone "^" at the right end of the cell. The cell never scrolls vertically.
            editor.MinHeight = 0; ScrollViewer.SetVerticalScrollBarVisibility(editor, ScrollBarVisibility.Hidden);
            EditorTextInfo.Attach(editor, () => UpdateNote());
            if (editTargets.Length > 1)
            {
                // Own the edit buffer. A two-way grid binding can push the old value back while focus moves,
                // before CellEditEnded gets a chance to commit the selected group.
                var initial = editor.Text ?? "";
                editor.ClearValue(TextBox.TextProperty); editor.Text = initial;
            }
            editor.PropertyChanged += LiveEditorChanged;
            if (typed == null) return;
            // The grid selects the existing text after this event; replace it once that has happened.
            Dispatcher.UIThread.Post(() => { if (liveEditor != editor) return; editor.Text = typed; editor.SelectionStart = editor.SelectionEnd = editor.CaretIndex = typed.Length; editor.Focus(); }, DispatcherPriority.Input);
        };
        grid.KeyDown += (_, e) =>
        {
            // Copy/paste keeps the row-header selection. Navigation or editing returns to cell selection.
            if (!(e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)) || e.Key is not (Key.C or Key.V)) rowHeaderPress = false;
            if (e.Key == Key.Escape && editingCell == null && selectedCells.Count > 1 && grid.SelectedItem is RowView current && grid.CurrentColumn != null && columnMap.TryGetValue(grid.CurrentColumn, out var currentCol))
            { selectedCells.Clear(); selectedCells.Add((current.Row, currentCol)); cellAnchor = (current.Row, currentCol); rowBlockSelection = false; PaintCells(); e.Handled = true; return; }
            if (editingCell != null || e.KeyModifiers != KeyModifiers.None || e.Key != Key.Delete || selectedCells.Count == 0 || Document.Table == null) return;
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
        try { activeGrid = grid; grid.SelectedItem = item; grid.CurrentColumn = column; selectedRow = item.Row; if (columnMap.TryGetValue(column, out var ci)) selectedColumn = Document.Table!.Columns[ci]; grid.Focus(); }
        finally { settingCurrent = false; }
        selection(this);
    }
    private void MoveTabSelection(DataGrid grid, int row, int column, bool reverse)
    {
        var rows = (grid.ItemsSource as IEnumerable<RowView> ?? []).ToList();
        var columns = VisibleColumns().ToList();
        int r = rows.FindIndex(item => item.Row == row), c = columns.IndexOf(column);
        if (r < 0 || c < 0) return;
        int next = r * columns.Count + c + (reverse ? -1 : 1);
        next = Math.Clamp(next, 0, rows.Count * columns.Count - 1);
        var target = rows[next / columns.Count]; int targetColumn = columns[next % columns.Count];
        activeGrid = grid;
        SelectCell(target.Row, targetColumn);
        if (GridColumnOf(grid, targetColumn) is { } displayedColumn) grid.ScrollIntoView(target, displayedColumn);
    }
    private void ClearCellSelection()
    {
        settingCurrent = true;
        try
        {
            draggingCells = rowHeaderPress = rowBlockSelection = false;
            selectedCells.Clear(); cellAnchor = null;
            foreach (var grid in new[] { TableGrid, FrozenGrid })
            {
                grid.SelectedItems.Clear();
            }
            selectedRow = -1; selectedColumn = "";
        }
        finally { settingCurrent = false; }
        PaintCells(); selection(this);
    }
    private void SelectRows(DataGrid grid)
    {
        selectedCells.Clear();
        foreach (var row in grid.SelectedItems.OfType<RowView>().Where(r => !r.IsPlaceholder))
            for (int col = 0; col < Document.Table!.Columns.Length; col++) selectedCells.Add((row.Row, col));
        if (grid.SelectedItem is RowView first && !first.IsPlaceholder) cellAnchor = (first.Row, 0);
        rowBlockSelection = selectedCells.Count > 0;
    }
    /// <summary>Selects the rectangle between two cells in displayed order (rows as sorted/filtered, columns as shown).</summary>
    private void SelectRectangle(DataGrid grid, (int Row, int Col) a, (int Row, int Col) b)
    {
        var rows = (grid.ItemsSource as IEnumerable<RowView> ?? []).Where(r => !r.IsPlaceholder).Select(r => r.Row).ToList();
        // Columns in display order across the whole table, not just the ones currently in the grid's window.
        var cols = VisibleColumns().ToList();
        int r1 = rows.IndexOf(a.Row), r2 = rows.IndexOf(b.Row), c1 = cols.IndexOf(a.Col), c2 = cols.IndexOf(b.Col);
        selectedCells.Clear();
        if (r1 < 0 || r2 < 0 || c1 < 0 || c2 < 0) { selectedCells.Add(b); return; }
        for (int r = Math.Min(r1, r2); r <= Math.Max(r1, r2); r++)
            for (int c = Math.Min(c1, c2); c <= Math.Max(c1, c2); c++) selectedCells.Add((rows[r], cols[c]));
    }
    /// <summary>Programmatic selection used by tests and the inspector: same semantics as clicking with the given modifiers.</summary>
    public void SelectCell(int row, int col, bool control = false, bool shift = false)
    {
        rowHeaderPress = rowBlockSelection = false; EnsureColumnInWindow(col);
        var cell = (row, col);
        if (control) { if (!selectedCells.Remove(cell)) selectedCells.Add(cell); cellAnchor = cell; }
        else if (shift && cellAnchor != null) SelectRectangle(activeGrid, cellAnchor.Value, cell);
        else { selectedCells.Clear(); selectedCells.Add(cell); cellAnchor = cell; }
        selectedRow = row; selectedColumn = Document.Table!.Columns[col];
        // Move the grid's current cell too, as a click would, so typing edits the selected cell.
        if ((activeGrid.ItemsSource as IEnumerable<RowView>)?.FirstOrDefault(r => r.Row == row) is { } item && activeGrid.Columns.FirstOrDefault(c => columnMap.TryGetValue(c, out var i) && i == col) is { } column)
            SetCurrent(activeGrid, item, column);
        PaintCells(); selection(this);
    }
    private void QueuePaint()
    {
        if (paintQueued) return; paintQueued = true;
        Dispatcher.UIThread.Post(() => { paintQueued = false; PaintCells(); }, DispatcherPriority.Background);
    }
    /// <summary>Cells given a background by the last paint. Clearing just these keeps a paint proportional to the selection, not to rows × columns.</summary>
    private readonly List<DataGridCell> paintedCells = [];
    private DataGridCell? CellOf(DataGridColumn column, DataGridRow rowControl) =>
        column.GetCellContent(rowControl) is Visual content ? content as DataGridCell ?? content.GetVisualAncestors().OfType<DataGridCell>().FirstOrDefault() : null;
    private void PaintCells()
    {
        if (HighlightedReferenceRow >= 0 && SelectedRow != HighlightedReferenceRow) ClearReferenceHighlight();
        foreach (var cell in paintedCells) cell.ClearValue(DataGridCell.BackgroundProperty);
        paintedCells.Clear();
        foreach (var grid in new[] { TableGrid, FrozenGrid })
        {
            if (!grid.IsVisible) continue;
            var rows = new Dictionary<int, DataGridRow>();
            // Recycled rows stay in the tree hidden, still bound to whatever they last showed; only visible rows are painted.
            foreach (var rowControl in RealizedRows(grid))
            {
                if (!rowControl.IsVisible || rowControl.DataContext is not RowView item) continue;
                bool referenceRow = !item.IsPlaceholder && item.Row == HighlightedReferenceRow;
                if (referenceRow) rowControl.Background = ReferenceRowBrush;
                else if (!item.IsPlaceholder && highlightedRows.Contains(item.Row)) rowControl.Background = HighlightBrush;
                else rowControl.ClearValue(DataGridRow.BackgroundProperty);
                if (!item.IsPlaceholder) rows[item.Row] = rowControl;
            }
            if (rows.Count == 0) continue;
            var columns = new Dictionary<int, DataGridColumn>();
            foreach (var column in grid.Columns) if (columnMap.TryGetValue(column, out var col)) columns[col] = column;
            // Highlights sit under everything else: a reference jump or a cell selection still reads on a highlighted row.
            if (HasHighlights)
                foreach (var (row, rowControl) in rows)
                {
                    bool wholeRow = highlightedRows.Contains(row);
                    foreach (var (col, column) in columns)
                    {
                        bool wholeColumn = highlightedColumns.Contains(col);
                        if (!wholeRow && !wholeColumn) continue;
                        if (CellOf(column, rowControl) is not { } cell) continue;
                        cell.Background = wholeRow && wholeColumn ? HighlightCrossBrush : HighlightBrush; paintedCells.Add(cell);
                    }
                }
            if (HighlightedReferenceRow >= 0 && rows.TryGetValue(HighlightedReferenceRow, out var highlighted))
                foreach (var column in columns.Values)
                    if (CellOf(column, highlighted) is { } cell) { cell.Background = ReferenceRowBrush; paintedCells.Add(cell); }
            foreach (var (row, col) in selectedCells)
                if (rows.TryGetValue(row, out var rowControl) && columns.TryGetValue(col, out var column) && CellOf(column, rowControl) is { } cell)
                { cell.Background = SelectedCellBrush; paintedCells.Add(cell); }
        }
    }
    /// <summary>Writes one value into every selected editable cell (except the cell that was just committed by the grid itself).</summary>
    public void ApplyToSelection(string value, (int Row, int Col)? except) => ApplyToCells(selectedCells, value, except);
    private void ApplyToCells(IEnumerable<(int Row, int Col)> cells, string value, (int Row, int Col)? except)
    {
        var table = Document.Table!;
        var edits = cells.Where(c => c != except && c.Row >= 0 && c.Row < table.Records.Count && !Document.LockedRows.Contains(c.Row) && !Document.LockedColumns.Contains(table.Columns[c.Col]))
            .Where(c => !(table.IsCatalog && c.Col < 2 && table.IsOriginalRow(c.Row))).Select(c => (c.Row, table.Columns[c.Col], value)).ToArray();
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
        ShiftHighlightedRows(from, delta);
    }
    /// <summary>
    /// Inserts blank rows and selects them in the column that was already selected. Rows are identified, not renumbered, so
    /// later profile overrides stay attached. The view rebuilds once and stays on its column page: a jump to the first
    /// column would rebuild it a second time and pull the user away from where they were working.
    /// </summary>
    public void InsertRows(int index, int count = 1)
    {
        Storage.Require(Document.Table != null && !Document.PendingSource, "Apply valid source before adding rows.");
        Document.InsertRows(index, count); SelectInsertedRows(index, count);
    }
    public void CloneSelectedRows(bool append)
    {
        var rows = SelectedRowsForCommands();
        int index = Document.CloneRows(rows, append);
        SelectInsertedRows(index, rows.Length);
    }
    private void SelectInsertedRows(int index, int count)
    {
        var column = SelectedColumn; int col = Math.Max(0, Array.IndexOf(Document.Table!.Columns, column));
        ShiftSelection(index, count);
        // Blank rows never match a filter term, so the filter is cleared to keep the new rows on screen.
        if (!string.IsNullOrEmpty(filter.Text)) filter.Text = "";
        selectedRow = index; selectedColumn = column;
        selectedCells.Clear(); for (int i = 0; i < count; i++) selectedCells.Add((index + i, col)); cellAnchor = (index, col); rowBlockSelection = false;
        Refresh(scrollToSelection: true);
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
            return string.Join('\n', SelectedRowsForCommands().Select(r => string.Join('\t', table.Columns.Select(c => table.Cell(r, c)))));
        var rows = (activeGrid.ItemsSource as IEnumerable<RowView> ?? []).Where(r => !r.IsPlaceholder).Select(r => r.Row).ToList();
        var cols = selectedCells.Select(c => c.Col).Distinct().Order().ToList();
        var rowOrder = selectedCells.Select(c => c.Row).Distinct().OrderBy(r => rows.IndexOf(r)).ToList();
        return string.Join('\n', rowOrder.Select(r => string.Join('\t', cols.Select(c => selectedCells.Contains((r, c)) ? table.Cell(r, table.Columns[c]) : ""))));
    }
}

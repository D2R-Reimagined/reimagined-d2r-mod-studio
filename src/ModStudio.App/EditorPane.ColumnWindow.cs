using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ModStudio.App;

/// <summary>
/// Column virtualization. The DataGrid keeps a cell for every column of every realized row and revisits every column on
/// every layout pass, so a 300-column table paid for all 300 whenever a row scrolled in or the view scrolled sideways.
/// The grids therefore only hold the columns around the horizontal viewport (one viewport of buffer on each side); a
/// spacer column on either side stands in for the width of everything outside the window, so the scrollbar, the scroll
/// offset and column positions are exactly those of the full table. Scrolling moves the window before the spacers can
/// come into view. Frozen columns are always present, ahead of the left spacer.
/// </summary>
public sealed partial class EditorPane
{
    /// <summary>A blank, non-interactive column whose width is the total width of the columns it stands in for.</summary>
    private sealed class SpacerColumn : DataGridColumn
    {
        public SpacerColumn() { IsReadOnly = true; CanUserResize = false; CanUserReorder = false; CanUserSort = false; MinWidth = 0; Width = new DataGridLength(0); Header = ""; }
        protected override Control GenerateElement(DataGridCell cell, object dataItem) => new Panel { IsHitTestVisible = false };
        protected override Control GenerateEditingElement(DataGridCell cell, object dataItem, out BindingExpressionBase? binding) { binding = null; return new Panel(); }
        protected override object? PrepareCellForEdit(Control editingElement, Avalonia.Interactivity.RoutedEventArgs editingEventArgs) => null;
    }
    private static readonly IBrush FrozenHeaderTint = new SolidColorBrush(Color.Parse("#33D8BC86"));
    /// <summary>Non-frozen columns in display order (table column indexes); the window is a range of positions in this array.</summary>
    private int[] scrollOrder = [];
    private int windowStart, windowEnd;
    private readonly Dictionary<DataGrid, (SpacerColumn Left, SpacerColumn Right)> spacers = [];
    private readonly Dictionary<DataGrid, List<LiveCellColumn>> windowControls = [];
    private bool windowUpdateQueued;
    /// <summary>Whether idle work on the columns is still pending. Column widths are fitted synchronously now, so this is always false; kept for callers.</summary>
    public bool ColumnsLoading => false;

    private double ColumnWidthOf(int index) => widths.TryGetValue(index, out var saved) && saved.IsAbsolute ? saved.Value : FitColumn(index);
    /// <summary>Width of the scrolling region on screen; generous when the grid has not been laid out yet.</summary>
    private double ViewportWidth => TableGrid.Bounds.Width > 0 ? TableGrid.Bounds.Width : 1400;

    /// <summary>Rebuilds every column control: frozen columns, the left spacer, the window around the current scroll offset, the right spacer.</summary>
    public void RefreshColumns()
    {
        if (Document.Table == null) return;
        HideCellTip();
        bool previous = refreshing; refreshing = true; var selectedColumn = SelectedColumn;
        try
        {
            columnMap.Clear(); windowControls.Clear(); spacers.Clear(); slotColumns.Clear(); TableGrid.CanUserReorderColumns = !Document.Table.IsCatalog;
            var frozen = frozenColumns.Where(i => i < Document.Table.Columns.Length).ToArray();
            scrollOrder = Enumerable.Range(0, Document.Table.Columns.Length).Where(i => !frozen.Contains(i)).ToArray();
            foreach (var grid in new[] { TableGrid, FrozenGrid })
            {
                grid.FrozenColumnCount = 0; grid.Columns.Clear();
                foreach (var i in frozen) grid.Columns.Add(CreateColumn(grid, i));
                var pair = (Left: new SpacerColumn(), Right: new SpacerColumn());
                spacers[grid] = pair; windowControls[grid] = [];
                grid.Columns.Add(pair.Left); grid.Columns.Add(pair.Right);
                grid.FrozenColumnCount = frozen.Length;
            }
            // Only the plain window is built while the table opens; the part a table end hands to the other side follows in idle time.
            var (start, end) = DesiredWindow(mainBar?.Value ?? 0, ViewportWidth, fill: false);
            windowStart = windowEnd = start;
            ApplyWindow(start, end);
            QueueWindowFill();
            foreach (var grid in new[] { TableGrid, FrozenGrid })
                if (grid.SelectedItem != null) grid.CurrentColumn = grid.Columns.FirstOrDefault(c => columnMap.TryGetValue(c, out var i) && Document.Table.Columns[i] == selectedColumn) ?? grid.Columns.FirstOrDefault(c => columnMap.ContainsKey(c));
            columnSignature = ColumnSignature();
        }
        finally { refreshing = previous; }
        columnsLabel.Text = $"{Document.Table.Columns.Length} columns";
        UpdateNote();
    }
    private LiveCellColumn CreateColumn(DataGrid grid, int i)
    {
        var column = new LiveCellColumn(this, i) { MinWidth = 40, CanUserResize = true };
        column.HeaderTemplate = new FuncDataTemplate<object>((_, _) => {
            int i = column.Index;
            var label = new TextBlock { Text = Header(i, sortMark: false), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            // Documented columns show the data-guide card; others keep their complete name.
            ColumnGuideTooltip.Attach(label, Document.Table, Document.Table!.Columns[i], Document.Table.Columns[i]);
            // Frozen columns carry the same tint as their cells, so the pinned block is recognizable at the header too.
            Control content = label;
            if (Document.Table.Columns[i] == sortColumn)
            {
                // The sort chevron sits at the far edge so a narrow column trims the name, never the indicator.
                var chevron = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(descending ? "M0,0 L3.5,4 L7,0" : "M0,4 L3.5,0 L7,4"), Stroke = new SolidColorBrush(Color.Parse("#D8BC86")), StrokeThickness = 1.5, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, Width = 7, Height = 4, Stretch = Stretch.None, Margin = new Thickness(4, 0, 6, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                DockPanel.SetDock(chevron, Dock.Right);
                content = new DockPanel { Children = { chevron, label } };
            }
            return frozenColumns.Contains(i) ? new Border { Background = FrozenHeaderTint, Child = content, Padding = new Thickness(6, 3), CornerRadius = new CornerRadius(3) } : content;
        });
        column.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name != "Width" || synchronizingWidths || !columnMap.ContainsKey(column)) return; HideCellTip(); synchronizingWidths = true;
            try { int i = column.Index; widths[i] = column.Width; foreach (var pair in columnMap.Where(p => p.Value == i && p.Key != column)) pair.Key.Width = column.Width; }
            finally { synchronizingWidths = false; }
        };
        Retarget(grid, column, i);
        return column;
    }
    /// <summary>Points a column control (and every cell it owns) at a table column: header, width, lock state and the cells' text follow.</summary>
    private void Retarget(DataGrid grid, LiveCellColumn column, int i)
    {
        var table = Document.Table!;
        bool moved = column.Index != i || !columnMap.ContainsKey(column);
        column.Retarget(i); columnMap[column] = i;
        synchronizingWidths = true;
        try
        {
            var header = Header(i); if (!Equals(column.Header, header)) column.Header = header;
            var width = ColumnWidthOf(i); if (Math.Abs(column.Width.Value - width) > 0.5 || !column.Width.IsAbsolute) column.Width = new(width);
            bool readOnly = Document.LockedColumns.Contains(table.Columns[i]); if (column.IsReadOnly != readOnly) column.IsReadOnly = readOnly;
        }
        finally { synchronizingWidths = false; }
        if (moved && grid.Columns.Contains(column)) foreach (var rowControl in RealizedRows(grid)) if (column.GetCellContent(rowControl) is LiveCellDisplay display) display.Invalidate();
    }
    /// <summary>
    /// The window positions that cover the viewport plus a viewport of buffer on each side. Near an end of the table the
    /// buffer that would fall outside it goes to the other side (unless <paramref name="fill"/> is false), so the window
    /// keeps the same number of column controls wherever the view is: scrolling away from an end then recycles controls
    /// instead of adding columns, and adding a column mid-scroll (a cell in every row, each hidden again by the grid's next
    /// layout) made the first horizontal scroll of a freshly opened table stutter.
    /// </summary>
    private (int Start, int End) DesiredWindow(double offset, double viewport, bool fill = true)
    {
        double left = offset - viewport, right = offset + 2 * viewport, x = 0;
        if (fill)
        {
            double total = 0; foreach (var i in scrollOrder) total += ColumnWidthOf(i);
            if (left < 0) right -= left;
            if (right > total) left -= right - total;
        }
        int start = -1, end = scrollOrder.Length;
        for (int k = 0; k < scrollOrder.Length; k++)
        {
            double w = ColumnWidthOf(scrollOrder[k]);
            if (start < 0 && x + w > left) start = k;
            if (x >= right) { end = k; break; }
            x += w;
        }
        if (start < 0) start = end;
        return (start, end);
    }
    /// <summary>Moves the window to [start, end): columns leaving it are removed, columns entering it are inserted at their place, and the spacers take up the rest.</summary>
    private void ApplyWindow(int start, int end)
    {
        if (Document.Table == null || spacers.Count == 0) return;
        if (start == windowStart && end == windowEnd) { UpdateSpacers(); return; }
        // The grid subtracts a removed column's width from its scroll offset when the column was left of the view, while the
        // spacer that replaces the column keeps the total width the same: the offset is therefore put back afterwards.
        double offset = mainBar?.Value ?? 0;
        bool previous = refreshing; refreshing = true;
        try
        {
            foreach (var grid in new[] { TableGrid, FrozenGrid })
            {
                // Column controls are recycled: a control (with every cell the grid created for it) that leaves the window on one side
                // is retargeted to a column entering on the other side and moved there by display index. Creating cells is the
                // expensive part of a slide; moving a column is not.
                var controls = windowControls[grid]; var next = new LiveCellColumn?[end - start]; var spare = new Stack<LiveCellColumn>();
                int live = windowEnd - windowStart; // controls beyond that are parked (hidden) spares from an earlier, wider window
                for (int k = 0; k < controls.Count; k++) { int position = windowStart + k; if (k < live && position >= start && position < end) next[position - start] = controls[k]; else spare.Push(controls[k]); }
                int frozenCount = grid.FrozenColumnCount;
                for (int position = start; position < end; position++)
                {
                    var column = next[position - start];
                    if (column == null)
                    {
                        if (spare.Count > 0) { column = spare.Pop(); Retarget(grid, column, scrollOrder[position]); if (!column.IsVisible) column.IsVisible = true; }
                        else { column = CreateColumn(grid, scrollOrder[position]); grid.Columns.Add(column); }
                        next[position - start] = column;
                    }
                    int desired = frozenCount + 1 + (position - start);
                    if (column.DisplayIndex != desired) column.DisplayIndex = desired;
                }
                // Surplus controls (the window is narrower near the table's end) are hidden, not removed: the grid keeps mouse-over
                // state per cell index and removing a column under the pointer can leave it pointing past the row's cells.
                foreach (var extra in spare) { columnMap.Remove(extra); if (extra.IsVisible) extra.IsVisible = false; }
                var right = spacers[grid].Right; if (right.DisplayIndex != grid.Columns.Count - 1) right.DisplayIndex = grid.Columns.Count - 1;
                windowControls[grid] = next.Select(c => c!).Concat(spare).ToList();
            }
            windowStart = start; windowEnd = end;
            UpdateSpacers();
            SetHorizontalOffset(offset);
            foreach (var row in RealizedRows(TableGrid)) QueueWarmup(row);
            // A recycled control may have been the current cell; keep the current cell on the selected table column when it is in the window.
            foreach (var grid in new[] { TableGrid, FrozenGrid })
            {
                if (grid.SelectedItem == null) continue;
                int selectedIndex = Document.Table.ColumnIndex(SelectedColumn);
                if (grid.CurrentColumn != null && columnMap.TryGetValue(grid.CurrentColumn, out var currentIndex) && currentIndex == selectedIndex) continue;
                if (GridColumnOf(grid, selectedIndex) is { } restored) grid.CurrentColumn = restored;
            }
        }
        finally { refreshing = previous; }
        QueuePaint();
    }
    /// <summary>Scrolls the grid horizontally through its scrollbar; the grid only follows the bar's Scroll notification, which PageRight raises (with a zero page, so the value is exactly the one set).</summary>
    internal void SetHorizontalOffset(double offset)
    {
        if (mainBar == null) return;
        offset = Math.Clamp(offset, 0, Math.Max(0, mainBar.Maximum));
        double page = mainBar.LargeChange;
        try { mainBar.LargeChange = 0; mainBar.Value = offset; mainBar.PageRight(); }
        finally { mainBar.LargeChange = page; }
    }
    private void UpdateSpacers()
    {
        double left = 0, right = 0;
        for (int k = 0; k < windowStart && k < scrollOrder.Length; k++) left += ColumnWidthOf(scrollOrder[k]);
        for (int k = windowEnd; k < scrollOrder.Length; k++) right += ColumnWidthOf(scrollOrder[k]);
        foreach (var pair in spacers.Values)
        {
            if (Math.Abs(pair.Left.Width.Value - left) > 0.5) pair.Left.Width = new DataGridLength(left);
            if (Math.Abs(pair.Right.Width.Value - right) > 0.5) pair.Right.Width = new DataGridLength(right);
            pair.Left.IsVisible = left > 0; pair.Right.IsVisible = right > 0;
        }
    }
    private readonly Queue<DataGridRow> warmups = new();
    private bool warmupQueued;
    /// <summary>
    /// Applies the cell templates of a row's window columns in idle time. The grid builds a cell's template the first time
    /// the cell is measured, so without this the first scroll into a buffer column pays for a template per row right in the
    /// scroll handler; done ahead of time, a slide only has to lay out cells that already exist.
    /// </summary>
    private void QueueWarmup(DataGridRow row)
    {
        warmups.Enqueue(row);
        if (warmupQueued) return; warmupQueued = true;
        Dispatcher.UIThread.Post(Warmup, DispatcherPriority.Background);
    }
    private void Warmup()
    {
        warmupQueued = false;
        var budget = System.Diagnostics.Stopwatch.StartNew();
        while (warmups.Count > 0 && budget.ElapsedMilliseconds < 4)
        {
            var row = warmups.Dequeue();
            // A row the grid dropped since it was queued (a refresh replacing the rows) has no cells for columns added after that.
            if (!row.IsVisible || !windowControls.TryGetValue(TableGrid, out var controls) || !rowPanels.TryGetValue(TableGrid, out var panel) || row.GetVisualParent() != panel) continue;
            foreach (var column in controls)
                if (column.IsVisible && column.GetCellContent(row) is { } content && (content.Parent as DataGridCell ?? content.GetVisualAncestors().OfType<DataGridCell>().FirstOrDefault()) is { } cell)
                    cell.ApplyTemplate();
        }
        if (warmups.Count > 0) { warmupQueued = true; Dispatcher.UIThread.Post(Warmup, DispatcherPriority.Background); }
    }
    /// <summary>Slides the window after the view scrolled or resized; runs after the grid's own layout so columns are never changed inside it.</summary>
    private void QueueWindowUpdate()
    {
        if (windowUpdateQueued || Document.Table == null) return; windowUpdateQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            windowUpdateQueued = false;
            if (Document.Table == null || spacers.Count == 0 || mainBar == null || editingCell != null) return;
            double offset = mainBar.Value, viewport = ViewportWidth;
            // Slide only when the viewport gets within half a buffer of the window's edge, so ordinary scrolling rarely touches the columns.
            double x = 0; int k = 0;
            for (; k < windowStart; k++) x += ColumnWidthOf(scrollOrder[k]);
            double windowLeft = x; for (; k < windowEnd; k++) x += ColumnWidthOf(scrollOrder[k]);
            double windowRight = x;
            bool leftOk = windowStart == 0 || windowLeft <= offset - viewport / 2, rightOk = windowEnd == scrollOrder.Length || windowRight >= offset + viewport * 1.5;
            if (leftOk && rightOk) return;
            var (start, end) = DesiredWindow(offset, viewport);
            ApplyWindow(start, end);
        }, DispatcherPriority.Normal);
    }
    private bool windowFillQueued;
    /// <summary>
    /// Grows the window to its full size (see <see cref="DesiredWindow"/>) in idle time, a few columns per pass: each added
    /// column creates a cell in every realized row, which is too much to do in one go right after the table opened.
    /// </summary>
    private void QueueWindowFill()
    {
        if (windowFillQueued) return; windowFillQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            windowFillQueued = false;
            if (Document.Table == null || spacers.Count == 0 || mainBar == null || editingCell != null) return;
            var (start, end) = DesiredWindow(mainBar.Value, ViewportWidth);
            // The view moved away meanwhile: the scroll's own window update takes it from there.
            if (start >= windowEnd || end <= windowStart) return;
            const int step = 3;
            int nextStart = Math.Max(start, windowStart - step), nextEnd = Math.Min(end, windowEnd + step);
            if (nextStart == windowStart && nextEnd == windowEnd) return;
            ApplyWindow(nextStart, nextEnd);
            QueueWindowFill();
        }, DispatcherPriority.Background);
    }
    /// <summary>Width of the scrolling region itself: the grid minus row headers, frozen columns and the vertical scrollbar.</summary>
    private double ScrollViewportWidth
    {
        get
        {
            double frozen = 0; foreach (var i in frozenColumns) if (i < (Document.Table?.Columns.Length ?? 0)) frozen += ColumnWidthOf(i);
            return Math.Max(200, ViewportWidth - TableGrid.RowHeaderWidth - frozen - 20);
        }
    }
    /// <summary>
    /// Scrolls horizontally so a column is on screen and makes sure its control exists, so a jump, paste or reveal can address it.
    /// Scrolling and moving the window happen together: a window moved without scrolling would slide back to the view on the next update.
    /// </summary>
    public void EnsureColumnInWindow(int index)
    {
        if (Document.Table == null || spacers.Count == 0 || mainBar == null) return;
        int position = Array.IndexOf(scrollOrder, index);
        if (position < 0) return;
        double x = 0; for (int k = 0; k < position; k++) x += ColumnWidthOf(scrollOrder[k]);
        double width = ColumnWidthOf(index), viewport = ScrollViewportWidth, offset = mainBar.Value;
        if (x < offset) offset = x; else if (x + width > offset + viewport) offset = x + width - viewport;
        offset = Math.Max(0, offset);
        var (start, end) = DesiredWindow(offset, ViewportWidth);
        ApplyWindow(start, end);
        if (Math.Abs(offset - mainBar.Value) > 0.5) SetHorizontalOffset(offset);
    }
    /// <summary>
    /// Keyboard navigation (End, or a long run of Right presses) can land on a spacer, which stands for columns that are not
    /// in the grid. The current cell is moved to the outermost real column on that side, bringing it into the window first.
    /// </summary>
    private bool LeaveSpacer(DataGrid grid)
    {
        if (grid.CurrentColumn is not SpacerColumn spacer || !spacers.TryGetValue(grid, out var pair) || scrollOrder.Length == 0) return false;
        int target = spacer == pair.Left ? scrollOrder[0] : scrollOrder[^1];
        EnsureColumnInWindow(target);
        if (GridColumnOf(grid, target) is { } column) { refreshing = true; try { grid.CurrentColumn = column; } finally { refreshing = false; } CaptureSelection(grid); }
        return true;
    }
    /// <summary>The grid column showing a table column, or null while it is outside the window.</summary>
    public DataGridColumn? GridColumnOf(DataGrid grid, int index) => grid.Columns.FirstOrDefault(c => columnMap.TryGetValue(c, out var i) && i == index);
    /// <summary>Kept for callers that used to wait for idle column loading; every column that can be addressed is either in the window or reachable through <see cref="EnsureColumnInWindow"/>.</summary>
    public void EnsureColumnsLoaded() { }
    /// <summary>Applies header text and read-only state to the existing columns; the column controls themselves survive, so the view keeps its scroll position.</summary>
    private void UpdateColumnHeaders()
    {
        var table = Document.Table!;
        foreach (var (column, i) in columnMap)
        {
            var header = Header(i);
            if (!Equals(column.Header, header)) column.Header = header;
            bool readOnly = Document.LockedColumns.Contains(table.Columns[i]);
            if (column.IsReadOnly != readOnly) column.IsReadOnly = readOnly;
        }
    }
    /// <summary>Re-fits or re-applies widths in place after the width preferences changed.</summary>
    private void ApplyColumnWidths()
    {
        synchronizingWidths = true;
        try { foreach (var (column, i) in columnMap) column.Width = new(ColumnWidthOf(i)); }
        finally { synchronizingWidths = false; }
        UpdateSpacers();
    }
}

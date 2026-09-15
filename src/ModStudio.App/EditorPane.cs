using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using ModStudio.Core;

namespace ModStudio.App;

public sealed class RowView(Document document, int row, Action<Exception> error, Func<int>? materialize = null, Func<int, int, string?>? preview = null, Func<int, int, bool>? deferEdit = null) : INotifyPropertyChanged
{
    /// <summary>Table row index; -1 while this is the blank "type here to add a row" line at the bottom.</summary>
    public int Row { get; private set; } = row;
    public bool IsPlaceholder => Row < 0;
    public event PropertyChangedEventHandler? PropertyChanged;
    public void RefreshValues()
    {
        PropertyChanged?.Invoke(this, new("Item"));
        PropertyChanged?.Invoke(this, new("Item[]"));
    }
    public string this[int column]
    {
        get => preview?.Invoke(Row, column) ?? document.Table!.Cell(Row, document.Table.Columns[column]);
        set
        {
            if (deferEdit?.Invoke(Row, column) == true) return;
            try
            {
                // The first value typed into the placeholder line creates the row it stands for.
                if (IsPlaceholder) { if (value.Length == 0 || materialize == null) return; Row = materialize(); }
                document.SetCells([(Row, document.Table!.Columns[column], value)]);
            }
            catch (Exception e) { error(e); }
            RefreshValues();
        }
    }
}

public sealed partial class EditorPane : Grid
{
    public event Action<EditorPane, int, Control?>? ItemHovered;
    private Control? hoveredItem;
    private int hoveredRow = -1;
    public Document Document { get; }
    public DataGrid TableGrid { get; } = CreateGrid();
    public DataGrid FrozenGrid { get; } = CreateGrid();
    public TextEditor Source { get; } = new() { ShowLineNumbers = true, FontFamily = new("Consolas, Menlo, monospace"), FontSize = 13, IsVisible = false };
    public IReadOnlyList<int> FrozenRows => frozenRows;
    public IReadOnlyList<int> FrozenColumns => frozenColumns;
    public string? SortColumn => sortColumn;
    public bool SortDescending => descending;
    private readonly Grid tableHost = new() { RowDefinitions = new("Auto,*") };
    private readonly TextBlock columnsLabel = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 4, 0) };
    private readonly TextBox filter = new() { PlaceholderText = "Filter rows (Enter)", Width = 180, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock note = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<DataGridColumn, int> columnMap = [];
    private readonly Dictionary<int, DataGridLength> widths = [];
    private readonly Dictionary<int, double> fittedWidths = [];
    private readonly List<int> frozenRows = [];
    private readonly List<int> frozenColumns = [0];
    private readonly Action<Exception> error;
    private readonly Action<EditorPane> selection;
    private DataGrid activeGrid;
    private bool syncing, refreshing, synchronizingBars, synchronizingWidths;
    private int offset;
    private string columnSignature = "";
    private int viewVersion;
    private int selectedRow;
    private string selectedColumn = "";
    private bool acceptingInput;
    private bool descending;
    private string? sortColumn;
    private ScrollBar? mainBar, frozenBar;
    public Markdown.Avalonia.MarkdownScrollViewer? MarkdownPreview { get; private set; }
    private Border? markdownHost;
    public void ShowMarkdownPreview()
    {
        if (MarkdownPreview == null) return;
        MarkdownPreview.Markdown = MarkdownPreviewText.Prepare(Document.Text.TrimStart('\uFEFF'));
        Source.IsVisible = false; MarkdownPreview.IsVisible = true; markdownHost!.IsVisible = true;
    }
    public int SelectedRow => selectedRow;
    public string SelectedColumn => Document.Table?.Columns.Contains(selectedColumn) == true ? selectedColumn : Document.Table?.Columns.FirstOrDefault() ?? "";
    private static DataGrid CreateGrid() => new()
    {
        AutoGenerateColumns = false, CanUserReorderColumns = false, CanUserSortColumns = true, CanUserResizeColumns = true,
        RowHeight = 30, RowHeaderWidth = 60, HeadersVisibility = DataGridHeadersVisibility.All, SelectionMode = DataGridSelectionMode.Extended, IsReadOnly = false,
        HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Visible, VerticalScrollBarVisibility = ScrollBarVisibility.Visible
    };

    public EditorPane(Document document, Action<Exception> onError, Action<EditorPane> onSelection, Action<EditorPane>? save = null)
    {
        Document = document; error = onError; selection = onSelection; activeGrid = TableGrid;
        Source.SyntaxHighlighting = SourceCodeEditing.Highlighting(document.FilePath);
        Source.Options.ConvertTabsToSpaces = true; Source.Options.IndentationSize = 4;
        RowDefinitions = new("Auto,*,Auto");
        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new(6) };
        void Button(string label, Action action) { var b = EditorToolbarIcons.Create(label); b.Click += (_, _) => { try { action(); } catch (Exception e) { error(e); } }; toolbar.Children.Add(b); }
        Button("Table", () => { document.ApplySource(); Storage.Require(!document.PendingSource, "Fix source syntax before returning to Table."); Source.IsVisible = false; tableHost.IsVisible = document.Table != null; Refresh(); });
        Button("Source", () => { syncing = true; Source.Text = document.Text.TrimStart('\uFEFF'); syncing = false; Source.IsVisible = true; tableHost.IsVisible = false; UpdateNote(); });
        Button("Apply source", () => { document.ApplySource(); Refresh(); });
        Button("Undo", Undo); Button("Redo", Redo);
        Button("◀ columns", () => { offset = Math.Max(0, offset - 23); RefreshColumns(); });
        Button("columns ▶", () => { if (offset + 24 < (document.Table?.Columns.Length ?? 0)) offset += 23; RefreshColumns(); });
        Button("Fit columns", () => { widths.Clear(); fittedWidths.Clear(); RefreshColumns(); });
        var view = EditorToolbarIcons.Create("Freeze / Lock"); var menu = new ContextMenu();
        MenuItem Item(string title, Action action) { var item = new MenuItem { Header = title }; item.Click += (_, _) => { try { action(); } catch (Exception e) { error(e); } }; return item; }
        menu.ItemsSource = new Control[] {
            Item("Freeze / unfreeze selected rows", ToggleFrozenRows),
            Item("Freeze / unfreeze current column", () => ToggleFrozenColumn(SelectedColumn)),
            Item("Unfreeze all", () => { frozenRows.Clear(); frozenColumns.Clear(); Refresh(); }),
            Item("Clear sorting", () => { sortColumn = null; Refresh(); }), new Separator(),
            Item("Lock / unlock selected rows against edits", ToggleRowLocks),
            Item("Lock / unlock current column against edits", ToggleColumnLock),
            Item("Unlock all edits", () => { document.LockedRows.Clear(); document.LockedColumns.Clear(); Refresh(); }) };
        view.Click += (_, _) => menu.Open(view); toolbar.Children.Add(view);
        toolbar.Children.Add(filter); toolbar.Children.Add(columnsLabel);
        filter.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { try { await FilterAsync(); } catch (Exception ex) { error(ex); } e.Handled = true; } };
        Children.Add(toolbar);
        FrozenGrid.IsVisible = false; FrozenGrid.HeadersVisibility = DataGridHeadersVisibility.All;
        FrozenGrid.BorderBrush = new SolidColorBrush(Color.Parse("#D8BC86")); FrozenGrid.BorderThickness = new(0, 0, 0, 1);
        FrozenGrid.HorizontalScrollBarVisibility = ScrollBarVisibility.Visible;
        tableHost.Children.Add(FrozenGrid); SetRow(TableGrid, 1); tableHost.Children.Add(TableGrid);
        SetRow(tableHost, 1); Children.Add(tableHost); SetRow(Source, 1); Children.Add(Source);
        SetRow(note, 2); note.Margin = new(10, 5); Children.Add(note);
        Source.TextChanged += (_, _) => { if (!syncing) { try { document.SetRaw((document.Text.StartsWith('\uFEFF') ? "\uFEFF" : "") + Source.Text); } catch (Exception ex) { error(ex); Refresh(); } } };
        foreach (var grid in new[] { TableGrid, FrozenGrid }) WireGrid(grid);
        WireReordering();
        document.Changed += UpdateNote;
        document.Changed += ClearReferenceHighlight;
        // Fitted widths are recomputed when the table is re-parsed or rows come and go; single cell edits keep them, so an undo does not re-measure 24 columns.
        document.Changed += () => { if (document.LastChangedRows == null) fittedWidths.Clear(); };
        if (document.Table == null) { Source.IsVisible = true; tableHost.IsVisible = false; }
        if (document.Table == null)
        {
            toolbar.Children.Clear();
            Button("Source", () => { Source.IsVisible = true; Refresh(); });
            Button("Undo", Undo); Button("Redo", Redo);
            if (System.IO.Path.GetExtension(document.FilePath).Equals(".json", StringComparison.OrdinalIgnoreCase))
                Button("Format JSON", () =>
                {
                    var bom = document.Text.StartsWith('\uFEFF') ? "\uFEFF" : "";
                    using var json = System.Text.Json.JsonDocument.Parse(document.Text.TrimStart('\uFEFF'), Document.SourceJsonOptions);
                    var formatted = System.Text.Json.JsonSerializer.Serialize(json.RootElement, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    if (document.Text.Contains("\r\n")) formatted = formatted.Replace("\r\n", "\n").Replace("\n", "\r\n");
                    document.SetRaw(bom + formatted); document.ApplySource(); Refresh();
                });
        }
        if (System.IO.Path.GetExtension(document.FilePath).Equals(".md", StringComparison.OrdinalIgnoreCase) || System.IO.Path.GetExtension(document.FilePath).Equals(".markdown", StringComparison.OrdinalIgnoreCase))
        {
            toolbar.Children.Clear();
            MarkdownPreview = new Markdown.Avalonia.MarkdownScrollViewer
            {
                IsVisible = false, MarkdownStyleName = "GithubLike", Margin = new(24),
                AssetPathRoot = System.IO.Path.GetDirectoryName(document.FilePath),
                SaveScrollValueWhenContentUpdated = true, SelectionEnabled = true
            };
            var engine = (Markdown.Avalonia.Markdown)MarkdownPreview.Engine;
            engine.HyperlinkCommand = new MarkdownLinkCommand(onError);
            MarkdownPreview.SetValue(TextBlock.ForegroundProperty, Brushes.Black);
            markdownHost = new Border { Background = Brushes.White, Child = MarkdownPreview, IsVisible = false };
            SetRow(markdownHost, 1); Children.Add(markdownHost);
            Button("Source", () => { markdownHost.IsVisible = false; MarkdownPreview.IsVisible = false; Source.IsVisible = true; Refresh(); });
            Button("Preview", ShowMarkdownPreview);
            Button("Undo", Undo); Button("Redo", Redo);
            Button("Refresh preview", ShowMarkdownPreview);
            toolbar.Children.Add(new TextBlock { Text = "GitHub-style Markdown preview", Margin = new(10, 7) });
        }
        var saveButton = EditorToolbarIcons.Create("Save");
        saveButton.IsVisible = document.IsDirty;
        saveButton.Click += (_, _) => { try { if (save != null) save(this); else { document.ApplySource(); document.Save(); Refresh(); } } catch (Exception ex) { error(ex); } };
        document.Changed += () => saveButton.IsVisible = document.IsDirty;
        toolbar.Children.Insert(0, saveButton);
        InitializeSourceFeatures(toolbar);
        Refresh();
    }
    private void WireGrid(DataGrid grid)
    {
        ScrollViewer.SetAllowAutoHide(grid, false);
        grid.PointerMoved += (_, e) => {
            if (Document.Table?.Name is not ("uniqueitems" or "setitems")) return;
            var rowControl = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<DataGridRow>().FirstOrDefault();
            int row = (rowControl?.DataContext as RowView)?.Row ?? -1;
            if (row == hoveredRow && rowControl == hoveredItem) return;
            hoveredRow = row; hoveredItem = rowControl; ItemHovered?.Invoke(this, row, rowControl);
        };
        grid.PointerExited += (_, _) => { hoveredRow = -1; hoveredItem = null; ItemHovered?.Invoke(this, -1, null); };
        DetachedFromVisualTree += (_, _) => { hoveredRow = -1; hoveredItem = null; ItemHovered?.Invoke(this, -1, null); };
        // The document host hides rather than detaches panes on a tab switch.
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty && !IsVisible) { hoveredRow = -1; hoveredItem = null; ItemHovered?.Invoke(this, -1, null); } };
        grid.LoadingRow += (_, e) => { if (e.Row.DataContext is RowView row) e.Row.Header = row.IsPlaceholder ? "＋" : (Document.LockedRows.Contains(row.Row) ? "L " : "") + row.Row; };
        grid.TemplateApplied += (_, e) =>
        {
            var bar = e.NameScope.Find<ScrollBar>("PART_HorizontalScrollbar"); if (bar == null) return;
            bar.AllowAutoHide = false; bar.Height = 22; bar.MinHeight = 22;
            if (grid == FrozenGrid) { bar.Height = 0; bar.MinHeight = 0; bar.Opacity = 0; bar.IsHitTestVisible = false; }
            if (bar.Parent is Grid holder)
            {
                SetColumn(holder, 0); SetColumnSpan(holder, 3); SetColumn(bar, 0); SetColumnSpan(bar, 2);
                if (e.NameScope.Find<Control>("PART_FrozenColumnScrollBarSpacer") is { } spacer) spacer.IsVisible = false;
            }
            if (e.NameScope.Find<Control>("PART_RowsPresenter") is { } rows) SetRowSpan(rows, 1);
            if (grid == TableGrid) mainBar = bar; else frozenBar = bar;
            bar.PropertyChanged += (_, args) => { if (args.Property == RangeBase.ValueProperty) SyncBars(bar); };
        };
        grid.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (IsReferenceButton(e.Source)) return;
            BeginInput(grid);
            if (!e.GetCurrentPoint(grid).Properties.IsRightButtonPressed || e.Source is not Visual visual) return;
            var header = visual.GetSelfAndVisualAncestors().OfType<DataGridColumnHeader>().FirstOrDefault();
            if (header != null)
            {
                var headerColumn = grid.Columns.FirstOrDefault(c => Equals(c.Header, header.Content));
                if (headerColumn != null && columnMap.TryGetValue(headerColumn, out var index))
                {
                    e.Handled = true; acceptingInput = false;
                    var columnMenu = CreateColumnMenu(index);
                    header.ContextMenu = columnMenu; columnMenu.Closed += (_, _) => header.ContextMenu = null; columnMenu.Open(header);
                }
                return;
            }
            var row = visual.GetSelfAndVisualAncestors().OfType<DataGridRow>().FirstOrDefault();
            if (row?.DataContext is not RowView item) return;
            // Prevent the grid's default right-click handling from collapsing a multiple-row selection.
            e.Handled = true;
            if (!grid.SelectedItems.Contains(item)) grid.SelectedItem = item;
            var cell = visual.GetSelfAndVisualAncestors().OfType<DataGridCell>().FirstOrDefault();
            var column = grid.Columns.FirstOrDefault(c => c.GetCellContent(row)?.GetVisualAncestors().Contains(cell!) == true);
            if (column != null) grid.CurrentColumn = column;
            CaptureSelection(grid);
            selectedRow = item.Row;
            acceptingInput = false;
            selection(this);
            var menu = CreateRowMenu(grid);
            menu.Closed += (_, _) => { if (grid.ContextMenu == menu) grid.ContextMenu = null; };
            grid.ContextMenu = menu; menu.Open(grid);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        grid.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Alt && Document.Table is { } table && HasCellReference(Array.IndexOf(table.Columns, SelectedColumn)))
            {
                e.Handled = true; RequestCellReference(SelectedRow, Array.IndexOf(table.Columns, SelectedColumn), grid); return;
            }
            BeginInput(grid);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        grid.GotFocus += (_, e) => { if (e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional) BeginInput(grid); };
        grid.SelectionChanged += (_, _) => CaptureSelection(grid);
        grid.CurrentCellChanged += (_, _) => CaptureSelection(grid);
        grid.Sorting += async (_, e) => { e.Handled = true; if (columnMap.TryGetValue(e.Column, out var i)) { try { await SortAsync(Document.Table!.Columns[i]); } catch (Exception ex) { error(ex); } } };
        grid.KeyDown += async (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Meta)) return;
            activeGrid = grid;
            try
            {
                if (e.Key == Key.V) { await PasteAsync(); e.Handled = true; }
                if (e.Key == Key.C) { await CopyAsync(); e.Handled = true; }
                if (e.Key == Key.Z) { Undo(); e.Handled = true; }
                if (e.Key == Key.Y) { Redo(); e.Handled = true; }
            }
            catch (Exception ex) { error(ex); }
        };
        grid.BeginningEdit += (_, e) => { if (e.Row.DataContext is RowView row && columnMap.TryGetValue(e.Column, out var col)) e.Cancel = Document.LockedRows.Contains(row.Row) || Document.LockedColumns.Contains(Document.Table!.Columns[col]); };
        WireCellSelection(grid);
    }
    private ContextMenu CreateRowMenu(DataGrid grid)
    {
        var rows = SelectedRowsForCommands();
        string noun = rows.Length == 1 ? "row" : $"{rows.Length} rows";
        int cellCount = selectedCells.Count(c => c.Row >= 0);
        var table = Document.Table!; bool addable = !Document.PendingSource;
        bool deletable = addable && rows.Length > 0 && rows.All(r => !table.IsOriginalRow(r) && !Document.LockedRows.Contains(r));
        bool frozen = rows.All(frozenRows.Contains);
        bool locked = rows.All(Document.LockedRows.Contains);
        MenuItem Item(string label, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = label, IsEnabled = enabled };
            item.Click += (_, _) => { try { activeGrid = grid; action(); } catch (Exception ex) { error(ex); } };
            return item;
        }
        var copyCell = new MenuItem { Header = cellCount == 1 ? $"Copy cell: {SelectedColumn}" : $"Copy {cellCount} cells", IsEnabled = cellCount > 0 };
        copyCell.Click += async (_, _) => { try { activeGrid = grid; await CopyAsync(true); } catch (Exception ex) { error(ex); } };
        var copy = new MenuItem { Header = $"Copy {noun} (all columns)" };
        copy.Click += async (_, _) => { try { activeGrid = grid; await CopyAsync(false); } catch (Exception ex) { error(ex); } };
        var paste = new MenuItem { Header = rowHeaderPress ? $"Paste into selected {noun}" : cellCount > 1 ? $"Paste into {cellCount} selected cells" : "Paste starting at selected cell", IsEnabled = !Document.PendingSource && !Document.LockedRows.Contains(SelectedRow) && (rowHeaderPress || !Document.LockedColumns.Contains(SelectedColumn)) };
        paste.Click += async (_, _) => { try { activeGrid = grid; await PasteAsync(); } catch (Exception ex) { error(ex); } };
        var clear = new MenuItem { Header = cellCount > 1 ? $"Clear {cellCount} cells" : "Clear cell", IsEnabled = cellCount > 0 && !Document.PendingSource };
        clear.Click += (_, _) => { try { activeGrid = grid; ApplyToSelection("", null); } catch (Exception ex) { error(ex); } };
        int rowsToAdd = Math.Max(1, rows.Length); string added = rowsToAdd == 1 ? "row" : $"{rowsToAdd} rows";
        int above = rows.Length > 0 ? rows.Min() : table.Records.Count, below = rows.Length > 0 ? rows.Max() + 1 : table.Records.Count;
        return new ContextMenu { ItemsSource = new Control[] {
            Item($"Add {added} above", () => InsertRows(above, rowsToAdd), addable && rows.Length > 0),
            Item($"Add {added} below", () => InsertRows(below, rowsToAdd), addable),
            Item(rows.Length > 0 && rows.All(r => !table.IsOriginalRow(r)) ? $"Delete {noun}" : $"Delete {noun} (original rows are kept)", DeleteSelectedRows, deletable),
            new Separator(),
            Item($"{(frozen ? "Unfreeze" : "Freeze")} {noun}", ToggleFrozenRows, frozen || frozenRows.Union(rows).Count() <= 5),
            Item($"{(locked ? "Unlock" : "Lock")} {noun} against edits", ToggleRowLocks),
            new Separator(), copyCell, copy, paste, clear,
            new Separator(),
            Item($"{(frozenColumns.Contains(Array.IndexOf(Document.Table!.Columns, SelectedColumn)) ? "Unfreeze" : "Freeze")} column: {SelectedColumn}", () => ToggleFrozenColumn(SelectedColumn)),
            Item($"{(Document.LockedColumns.Contains(SelectedColumn) ? "Unlock" : "Lock")} column: {SelectedColumn} against edits", ToggleColumnLock)
        } };
    }
    private void BeginInput(DataGrid grid)
    {
        activeGrid = grid; acceptingInput = true; viewVersion++;
        Dispatcher.UIThread.Post(() => { CaptureSelection(grid); acceptingInput = false; }, DispatcherPriority.Background);
    }
    private void CaptureSelection(DataGrid grid)
    {
        if (refreshing || !acceptingInput || grid != activeGrid || grid.SelectedItem is not RowView row) return;
        selectedRow = row.Row;
        if (grid.CurrentColumn != null && columnMap.TryGetValue(grid.CurrentColumn, out var index)) selectedColumn = Document.Table!.Columns[index];
        selection(this);
    }
    private void SyncBars(ScrollBar source)
    {
        if (synchronizingBars || mainBar == null || frozenBar == null || !FrozenGrid.IsVisible) return;
        synchronizingBars = true;
        try { (source == mainBar ? frozenBar : mainBar).Value = source.Value; } finally { synchronizingBars = false; }
    }
    public int[] VisibleColumns() => Document.Table == null ? [] : frozenColumns.Where(i => i < Document.Table.Columns.Length).Concat(Enumerable.Range(offset, Math.Min(24, Math.Max(0, Document.Table.Columns.Length - offset)))).Distinct().ToArray();
    private string Header(int column, bool sortMark = true)
    {
        var name = Document.Table!.Columns[column];
        return (frozenColumns.Contains(column) ? "▣ " : "") + name + (Document.LockedColumns.Contains(name) ? " [locked]" : "") + (sortMark && name == sortColumn ? descending ? " ▼" : " ▲" : "");
    }
    private double FitColumn(int index)
    {
        const double maximum = 220;
        if (fittedWidths.TryGetValue(index, out var cached)) return cached;
        var table = Document.Table!;
        var text = new TextBlock { FontFamily = TableGrid.FontFamily, FontSize = TableGrid.FontSize };
        double Measure(string value)
        {
            // Only the first line is shown in a table cell. Bound layout work for long translations.
            var end = value.IndexOfAny(['\r', '\n']);
            if (end >= 0) value = value[..end];
            text.Text = value.Length > 256 ? value[..256] : value;
            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return text.DesiredSize.Width;
        }
        // Leave room for the frozen marker, sorting indicator and header padding.
        var width = Math.Clamp(Measure(Header(index)) + 48, 40, maximum);
        // Sample values cheaply, then measure only the longest candidates. Creating a font
        // layout for every distinct ID across 24 visible columns delays the first paint.
        var sampleRows = Enumerable.Range(0, Math.Min(100, table.Records.Count))
            .Concat(Enumerable.Range(0, Math.Min(100, table.Records.Count)).Select(i => i * (table.Records.Count - 1) / 99)).Distinct();
        var candidates = sampleRows.Select(row => table.Cell(row, table.Columns[index]))
            .Select(value => { var end = value.IndexOfAny(['\r', '\n']); return value[..Math.Min(end < 0 ? value.Length : end, 256)]; })
            .Distinct(StringComparer.Ordinal).OrderByDescending(value => value.Length).Take(12);
        foreach (var value in candidates)
        {
            if (width >= maximum) break;
            width = Math.Min(maximum, Math.Max(width, Measure(value) + 24));
        }
        return fittedWidths[index] = Math.Ceiling(width);
    }
    public void RefreshColumns()
    {
        if (Document.Table == null) return;
        bool previous = refreshing; refreshing = true; var selectedColumn = SelectedColumn;
        try
        {
            columnMap.Clear(); TableGrid.CanUserReorderColumns = !Document.Table.IsCatalog;
            foreach (var grid in new[] { TableGrid, FrozenGrid })
            {
                grid.FrozenColumnCount = 0; grid.Columns.Clear();
                foreach (var i in VisibleColumns())
                {
                    var column = new LiveCellColumn(this, i) { Header = Header(i), Binding = new Binding($"[{i}]") { Mode = BindingMode.TwoWay }, MinWidth = 40, CanUserResize = true, Width = widths.TryGetValue(i, out var savedWidth) ? savedWidth : new(FitColumn(i)), IsReadOnly = Document.LockedColumns.Contains(Document.Table.Columns[i]) };
                    column.HeaderTemplate = new FuncDataTemplate<object>((_, _) => {
                        var label = new TextBlock { Text = Header(i, sortMark: false), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                        // Documented columns show the data-guide card; others keep their complete name.
                        ColumnGuideTooltip.Attach(label, Document.Table, Document.Table.Columns[i], Document.Table.Columns[i]);
                        if (Document.Table.Columns[i] != sortColumn) return label;
                        // The sort chevron sits at the far edge so a narrow column trims the name, never the indicator.
                        var chevron = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(descending ? "M0,0 L3.5,4 L7,0" : "M0,4 L3.5,0 L7,4"), Stroke = new SolidColorBrush(Color.Parse("#D8BC86")), StrokeThickness = 1.5, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, Width = 7, Height = 4, Stretch = Stretch.None, Margin = new Thickness(4, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
                        DockPanel.SetDock(chevron, Dock.Right);
                        return new DockPanel { Children = { chevron, label } };
                    });
                    columnMap[column] = i; grid.Columns.Add(column);
                    column.PropertyChanged += (_, e) =>
                    {
                        if (e.Property.Name != "Width" || synchronizingWidths) return; synchronizingWidths = true;
                        try { widths[i] = column.Width; foreach (var pair in columnMap.Where(p => p.Value == i && p.Key != column)) pair.Key.Width = column.Width; }
                        finally { synchronizingWidths = false; }
                    };
                }
                grid.FrozenColumnCount = Math.Min(frozenColumns.Count, grid.Columns.Count);
                if (grid.SelectedItem != null) grid.CurrentColumn = grid.Columns.FirstOrDefault(c => Document.Table.Columns[columnMap[c]] == selectedColumn) ?? grid.Columns.FirstOrDefault();
            }
            columnsLabel.Text = $"{offset + 1}–{Math.Min(offset + 24, Document.Table.Columns.Length)} / {Document.Table.Columns.Length} columns";
            columnSignature = ColumnSignature();
        }
        finally { refreshing = previous; }
        UpdateNote();
    }
    /// <summary>
    /// Rebuilds the view from the document. Column controls are rebuilt only when something about them changed (the table,
    /// the visible window, headers, locks): recreating two dozen DataGrid columns regenerates every realized cell, which is
    /// the slowest part of a refresh and pointless after a change that only touched rows.
    /// </summary>
    public void Refresh(bool scrollToSelection = false)
    {
        viewVersion++;
        int selected = SelectedRow; string column = SelectedColumn; refreshing = true;
        try
        {
            if (Source.IsVisible) { syncing = true; Source.Text = Document.Text.TrimStart('\uFEFF'); syncing = false; }
            if (MarkdownPreview?.IsVisible == true) MarkdownPreview.Markdown = MarkdownPreviewText.Prepare(Document.Text.TrimStart('\uFEFF'));
            TableGrid.IsEnabled = FrozenGrid.IsEnabled = !Document.PendingSource;
            if (Document.Table != null && !Document.PendingSource)
            {
                frozenRows.RemoveAll(i => i >= Document.Table.Records.Count); frozenColumns.RemoveAll(i => i >= Document.Table.Columns.Length);
                Document.LockedRows.RemoveWhere(i => i < 0 || i >= Document.Table.Records.Count); Document.LockedColumns.RemoveWhere(c => !Document.Table.Columns.Contains(c));
                if (ColumnSignature() != columnSignature) RefreshColumns(); ApplyView(selected, column, scrollToSelection);
                if (selectedCells.Count == 0 && selectedRow >= 0 && Array.IndexOf(Document.Table.Columns, SelectedColumn) is var ci and >= 0) { selectedCells.Add((selectedRow, ci)); cellAnchor = (selectedRow, ci); }
                QueuePaint();
            }
        }
        finally { refreshing = false; }
        UpdateNote(); selection(this);
    }
    /// <summary>Everything RefreshColumns bakes into the column controls; equal signatures mean the existing columns can stay.</summary>
    private string ColumnSignature() => Document.Table == null ? "" :
        $"{RuntimeHelpers.GetHashCode(Document.Table)}|{Document.Table.IsCatalog}|{string.Join(',', VisibleColumns().Select(i => $"{i}:{Header(i)}:{Document.LockedColumns.Contains(Document.Table.Columns[i])}"))}";
    private void ApplyView(int selected, string column, bool scroll = false)
    {
        var table = Document.Table!; var term = filter.Text ?? "";
        IEnumerable<int> rows = Enumerable.Range(0, table.Records.Count).Where(i => !frozenRows.Contains(i) && (term.Length == 0 || table.Columns.Any(c => table.Cell(i, c).Contains(term, StringComparison.OrdinalIgnoreCase))));
        if (sortColumn != null && table.Columns.Contains(sortColumn)) rows = descending ? rows.OrderByDescending(i => table.Cell(i, sortColumn), StringComparer.OrdinalIgnoreCase) : rows.OrderBy(i => table.Cell(i, sortColumn), StringComparer.OrdinalIgnoreCase);
        // A blank line at the bottom creates a new row as soon as something is typed into it.
        var views = rows.Select(i => new RowView(Document, i, error, preview: PreviewCellValue, deferEdit: DeferCellEdit));
        TableGrid.ItemsSource = views.Append(new RowView(Document, -1, error, MaterializePlaceholder)).ToArray();
        FrozenGrid.ItemsSource = frozenRows.Select(i => new RowView(Document, i, error, preview: PreviewCellValue, deferEdit: DeferCellEdit)).ToArray();
        FrozenGrid.IsVisible = frozenRows.Count > 0; FrozenGrid.Height = frozenRows.Count * 30 + 34;
        TableGrid.HeadersVisibility = FrozenGrid.IsVisible ? DataGridHeadersVisibility.Row : DataGridHeadersVisibility.All;
        activeGrid = frozenRows.Contains(selected) ? FrozenGrid : TableGrid;
        activeGrid.SelectedItem = ((IEnumerable<RowView>)activeGrid.ItemsSource!).FirstOrDefault(r => r.Row == selected);
        activeGrid.SelectedItem ??= ((IEnumerable<RowView>)activeGrid.ItemsSource!).FirstOrDefault();
        selectedRow = (activeGrid.SelectedItem as RowView)?.Row ?? -1; selectedColumn = column;
        if (activeGrid.SelectedItem != null) activeGrid.CurrentColumn = activeGrid.Columns.FirstOrDefault(c => table.Columns[columnMap[c]] == column) ?? activeGrid.Columns.FirstOrDefault();
        RestoreAfterLayout(activeGrid, activeGrid.SelectedItem as RowView, activeGrid.CurrentColumn, scroll);
        if (mainBar != null) Dispatcher.UIThread.Post(() => SyncBars(mainBar), DispatcherPriority.Background);
    }
    private void RestoreAfterLayout(DataGrid grid, RowView? item, DataGridColumn? column, bool scroll)
    {
        var version = viewVersion;
        if (item == null || column == null) return;
        Dispatcher.UIThread.Post(() =>
        {
            // A reference jump can be followed immediately by freezing a row or switching tabs.
            // Do not restore a discarded row or a grid that has since left the visible workspace.
            if (version != viewVersion || !grid.Columns.Contains(column) || !grid.IsEffectivelyVisible || TopLevel.GetTopLevel(grid) == null ||
                (grid.ItemsSource as IEnumerable<RowView>)?.Contains(item) != true) return;
            refreshing = true;
            try { activeGrid = grid; grid.SelectedItem = item; if (scroll) grid.ScrollIntoView(item, column); if (grid.SelectedItem == item && grid.CurrentColumn != null) grid.CurrentColumn = column; }
            finally { refreshing = false; }
            QueuePaint();
            selection(this);
        }, DispatcherPriority.Background);
    }
    private void UpdateNote()
    {
        if (MarkdownPreview != null) { note.Text = Document.IsDirty ? "Unsaved Markdown · Preview includes current edits" : "Markdown · Source and rendered preview"; return; }
        if (Document.Table == null) { note.Text = (Source.SyntaxHighlighting?.Name ?? "Plain text") + (Document.IsDirty ? " · Unsaved edits" : "") + " · Save writes to disk"; return; }
        Source.IsReadOnly = Document.HasEditLocks;
        note.Text = Document.PendingSource ? "Raw source pending validation. Table editing is paused." :
            $"{Document.Table?.Records.Count ?? 0:N0} records · {frozenRows.Count} frozen rows · {Document.LockedRows.Count} locked rows / {Document.LockedColumns.Count} columns" +
            (sortColumn == null ? " · source order" : $" · {sortColumn} {(descending ? "▼ descending" : "▲ ascending")}") +
            (Source.IsVisible && Document.HasEditLocks ? " · Unlock edits to change Source" : "");
    }
    public Task FilterAsync() { Refresh(); return Task.CompletedTask; }
    public void RefreshRowValues(int row) => RefreshRowValues([row]);
    public void RefreshRowValues(IEnumerable<int> rows)
    {
        var wanted = rows as IReadOnlySet<int> ?? rows.ToHashSet();
        foreach (var grid in new[] { TableGrid, FrozenGrid })
            foreach (var item in (grid.ItemsSource as IEnumerable<RowView> ?? []).Where(r => wanted.Contains(r.Row))) item.RefreshValues();
    }
    public void Undo() => Replay(Document.Undo);
    public void Redo() => Replay(Document.Redo);
    /// <summary>
    /// Runs a history step and refreshes as little as it needs: a step that only put cell values back updates those rows in
    /// place, so undoing an edit in a large table feels immediate. Anything that changed the row set, re-parsed the source, or
    /// touched a column the view is sorted or filtered by rebuilds the view as before.
    /// </summary>
    private void Replay(Action step)
    {
        var table = Document.Table; int rows = table?.Records.Count ?? -1;
        step();
        var changedRows = Document.LastChangedRows; var changedColumns = Document.LastChangedColumns;
        bool inPlace = table != null && ReferenceEquals(table, Document.Table) && !Document.PendingSource && table.Records.Count == rows && !Source.IsVisible && MarkdownPreview?.IsVisible != true
            && changedRows != null && changedColumns != null && string.IsNullOrEmpty(filter.Text) && (sortColumn == null || !changedColumns.Contains(sortColumn));
        if (!inPlace) { Refresh(); return; }
        RefreshRowValues(changedRows!); QueuePaint(); UpdateNote(); selection(this);
    }
    /// <summary>
    /// Brings a cell on screen and makes it the selected cell without moving keyboard focus, so the Row Editor can show where
    /// the field it is editing lives in the table. Columns outside the current window scroll the window; filtered-out rows stay put.
    /// </summary>
    public void Reveal(int row, string column)
    {
        var table = Document.Table; if (table == null || Document.PendingSource || !tableHost.IsVisible || row < 0 || row >= table.Records.Count) return;
        int index = table.ColumnIndex(column); if (index < 0) return;
        if (!VisibleColumns().Contains(index)) { offset = index > 0 ? ((index - 1) / 23) * 23 : 0; RefreshColumns(); }
        var grid = frozenRows.Contains(row) ? FrozenGrid : TableGrid;
        var item = (grid.ItemsSource as IEnumerable<RowView>)?.FirstOrDefault(r => r.Row == row); if (item == null) return;
        var target = grid.Columns.FirstOrDefault(c => columnMap.TryGetValue(c, out var i) && i == index); if (target == null) return;
        selectedCells.Clear(); selectedCells.Add((row, index)); cellAnchor = (row, index); selectedRow = row; selectedColumn = column; activeGrid = grid;
        refreshing = true;
        // Selecting a row in a tab that just became visible may precede the grid's current-row initialization.
        // Scroll first, then let RestoreAfterLayout finish the current column when the reference is revealed.
        try { grid.SelectedItem = item; grid.ScrollIntoView(item, target); if (grid.CurrentColumn != null) grid.CurrentColumn = target; }
        finally { refreshing = false; }
        QueuePaint(); selection(this);
    }
    public Task SortAsync(string column) { descending = sortColumn == column && !descending; sortColumn = column; Refresh(); return Task.CompletedTask; }
    public void ToggleFrozenRows()
    {
        var rows = SelectedRowsForCommands(); Storage.Require(rows.Length > 0, "Select rows to freeze.");
        if (rows.All(frozenRows.Contains)) frozenRows.RemoveAll(rows.Contains);
        else { Storage.Require(frozenRows.Union(rows).Count() <= 5, "Freeze up to five comparison rows at a time."); foreach (var row in rows) if (!frozenRows.Contains(row)) frozenRows.Add(row); }
        Refresh();
    }
    public void ToggleFrozenColumn(string column)
    {
        if (Document.Table == null) return; var index = Array.IndexOf(Document.Table.Columns, column); if (index < 0) return;
        if (!frozenColumns.Remove(index)) frozenColumns.Add(index); Refresh();
    }
    public void ToggleRowLocks()
    {
        var rows = SelectedRowsForCommands();
        if (rows.All(Document.LockedRows.Contains)) Document.LockedRows.ExceptWith(rows); else Document.LockedRows.UnionWith(rows); Refresh();
    }
    public void ToggleColumnLock() { if (!Document.LockedColumns.Remove(SelectedColumn)) Document.LockedColumns.Add(SelectedColumn); Refresh(); }
    public void Jump(int row, string column = "")
    {
        if (Document.Table == null || Document.PendingSource) return;
        rowHeaderPress = false;
        ClearReferenceHighlight();
        Source.IsVisible = false; tableHost.IsVisible = true; filter.Text = "";
        var i = Array.IndexOf(Document.Table.Columns, column); offset = i > 0 ? ((i - 1) / 23) * 23 : 0; Refresh(); viewVersion++;
        activeGrid = frozenRows.Contains(row) ? FrozenGrid : TableGrid;
        var item = ((IEnumerable<RowView>)activeGrid.ItemsSource!).FirstOrDefault(r => r.Row == row);
        if (item != null)
        {
            selectedRow = row; selectedColumn = i >= 0 ? column : Document.Table.Columns.FirstOrDefault() ?? "";
            selectedCells.Clear(); selectedCells.Add((row, Math.Max(i, 0))); cellAnchor = (row, Math.Max(i, 0)); QueuePaint();
            activeGrid.SelectedItem = item;
            var col = activeGrid.Columns.FirstOrDefault(c => Document.Table.Columns[columnMap[c]] == column) ?? activeGrid.Columns.FirstOrDefault();
            if (col != null) { activeGrid.CurrentColumn = col; RestoreAfterLayout(activeGrid, item, col, true); }
        }
        selection(this);
    }
    /// <summary>Copies the selected cells (one cell, or the rectangle around a multi-cell selection); rows picked by their header copy every table column.</summary>
    public Task CopyAsync() => CopyAsync(cellOnly: !rowHeaderPress);
    public async Task CopyAsync(bool cellOnly)
    {
        if (Document.Table == null) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(SelectionText(!cellOnly));
    }
    /// <summary>Pastes at the current cell, or from the first column when row headers are selected. A block pastes across rows/columns, adding rows at the bottom when it runs past the last one.</summary>
    public async Task PasteAsync()
    {
        if (Document.Table == null || Document.PendingSource) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard; if (clipboard == null) return;
        var text = await clipboard.TryGetTextAsync(); if (text == null) return;
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList(); if (lines.Count > 1 && lines[^1] == "") lines.RemoveAt(lines.Count - 1);
        var block = lines.Select(l => l.Split('\t')).ToArray();
        if (block.Length == 1 && block[0].Length == 1 && selectedCells.Count > 1) { ApplyToSelection(block[0][0], null); return; }
        if (rowHeaderPress && SelectedRowsForCommands().Length > 0)
        {
            PasteRows(block);
            return;
        }
        // A copied whole row spans the table, not the current 24-column page. Adding a row selects a cell,
        // so recognize a table-width block there too and paste from column zero across every page.
        if (block.All(r => r.Length == Document.Table.Columns.Length) && SelectedRow >= 0)
        {
            PasteRows(block, fromCell: true);
            return;
        }
        var displayed = (activeGrid.ItemsSource as IEnumerable<RowView> ?? []).Where(r => !r.IsPlaceholder).Select(r => r.Row).ToList();
        int start = SelectedRow < 0 ? displayed.Count : displayed.IndexOf(SelectedRow); Storage.Require(start >= 0, "Select the cell to paste at.");
        var columns = VisibleColumns(); int firstColumn = Math.Max(0, activeGrid.CurrentColumn?.DisplayIndex ?? 0);
        int width = block.Max(r => r.Length);
        Storage.Require(firstColumn + width <= columns.Length, $"Pasting {width} cell(s) at {Document.Table.Columns[columns[firstColumn]]} runs past the displayed columns. Copy a single cell, or paste further left.");
        int missing = start + block.Length - displayed.Count;
        if (missing > 0)
        {
            int first = Document.Table.Records.Count; Document.InsertRows(first, missing); ShiftSelection(first, missing);
            displayed.AddRange(Enumerable.Range(first, missing));
        }
        var edits = new List<(int, string, string)>();
        for (int r = 0; r < block.Length; r++) for (int c = 0; c < block[r].Length; c++) edits.Add((displayed[start + r], Document.Table.Columns[columns[firstColumn + c]], block[r][c]));
        Document.SetCells(edits);
        selectedCells.Clear(); foreach (var (row, column, _) in edits) selectedCells.Add((row, Array.IndexOf(Document.Table.Columns, column)));
        cellAnchor = (displayed[start], columns[firstColumn]);
        Refresh(); QueuePaint();
    }

    private void PasteRows(string[][] block, bool fromCell = false)
    {
        var table = Document.Table!;
        Storage.Require(block.All(r => r.Length <= table.Columns.Length), $"Clipboard rows have more than {table.Columns.Length} columns.");
        var displayed = (activeGrid.ItemsSource as IEnumerable<RowView> ?? []).Where(r => !r.IsPlaceholder).Select(r => r.Row).ToArray();
        var selected = (fromCell ? [SelectedRow] : SelectedRowsForCommands()).Where(displayed.Contains).OrderBy(r => Array.IndexOf(displayed, r)).ToArray();
        Storage.Require(selected.Length > 0, "Select a row to paste into.");
        int[] targets;
        if (block.Length == 1) targets = selected;
        else if (block.Length == selected.Length) targets = selected;
        else
        {
            int start = Array.IndexOf(displayed, selected[0]);
            Storage.Require(start + block.Length <= displayed.Length, "Pasted rows run past the displayed rows. Add rows first, or paste higher in the table.");
            targets = displayed.Skip(start).Take(block.Length).ToArray();
        }
        var edits = new List<(int Row, string Column, string Value)>();
        for (int r = 0; r < targets.Length; r++)
            for (int c = 0; c < block[block.Length == 1 ? 0 : r].Length; c++)
            {
                var column = table.Columns[c];
                // Record identity belongs to the destination row. Imported string IDs and game identity columns are protected.
                if (table.IsIdentityColumn(column)) continue;
                edits.Add((targets[r], column, block[block.Length == 1 ? 0 : r][c]));
            }
        Document.SetCells(edits);
        selectedCells.Clear();
        foreach (var row in targets) for (int col = 0; col < table.Columns.Length; col++) selectedCells.Add((row, col));
        cellAnchor = (targets[0], 0); selectedRow = targets[0]; selectedColumn = table.Columns[0];
        Refresh(); QueuePaint();
    }
}

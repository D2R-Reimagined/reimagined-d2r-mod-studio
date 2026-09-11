using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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

public sealed class RowView(Document document, int row, Action<Exception> error) : INotifyPropertyChanged
{
    public int Row { get; } = row;
    public event PropertyChangedEventHandler? PropertyChanged;
    public void RefreshValues() => PropertyChanged?.Invoke(this, new("Item[]"));
    public string this[int column]
    {
        get => document.Table!.Cell(Row, document.Table.Columns[column]);
        set { try { document.SetCells([(Row, document.Table!.Columns[column], value)]); } catch (Exception e) { error(e); } PropertyChanged?.Invoke(this, new("Item[]")); }
    }
}

public sealed partial class EditorPane : Grid
{
    public Document Document { get; }
    public DataGrid TableGrid { get; } = CreateGrid();
    public DataGrid FrozenGrid { get; } = CreateGrid();
    public TextEditor Source { get; } = new() { ShowLineNumbers = true, FontFamily = new("Consolas, Menlo, monospace"), FontSize = 13, IsVisible = false };
    public IReadOnlyList<int> FrozenRows => frozenRows;
    public IReadOnlyList<int> FrozenColumns => frozenColumns;
    public string? SortColumn => sortColumn;
    public bool SortDescending => descending;
    private readonly Grid tableHost = new() { RowDefinitions = new("Auto,*") };
    private readonly TextBlock columnsLabel = new();
    private readonly TextBox filter = new() { PlaceholderText = "Filter rows (Enter)", Width = 180 };
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

    public EditorPane(Document document, Action<Exception> onError, Action<EditorPane> onSelection, Func<Task>? editSchema = null, Action<EditorPane>? save = null)
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
        Button("Undo", () => { document.Undo(); Refresh(); }); Button("Redo", () => { document.Redo(); Refresh(); });
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
        if (editSchema != null) Button("Schema…", () => { _ = editSchema(); });
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
        document.Changed += UpdateNote;
        document.Changed += () => fittedWidths.Clear();
        if (document.Table == null) { Source.IsVisible = true; tableHost.IsVisible = false; }
        if (document.Table == null)
        {
            toolbar.Children.Clear();
            Button("Source", () => { Source.IsVisible = true; Refresh(); });
            Button("Undo", () => { document.Undo(); Refresh(); }); Button("Redo", () => { document.Redo(); Refresh(); });
            if (System.IO.Path.GetExtension(document.FilePath).Equals(".json", StringComparison.OrdinalIgnoreCase))
                Button("Format JSON", () =>
                {
                    var bom = document.Text.StartsWith('\uFEFF') ? "\uFEFF" : "";
                    using var json = System.Text.Json.JsonDocument.Parse(document.Text.TrimStart('\uFEFF'));
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
            Button("Undo", () => { document.Undo(); Refresh(); });
            Button("Redo", () => { document.Redo(); Refresh(); });
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
        grid.LoadingRow += (_, e) => { if (e.Row.DataContext is RowView row) e.Row.Header = (Document.LockedRows.Contains(row.Row) ? "L " : "") + row.Row; };
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
        grid.AddHandler(KeyDownEvent, (_, _) => BeginInput(grid), Avalonia.Interactivity.RoutingStrategies.Tunnel);
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
                if (e.Key == Key.Z) { Document.Undo(); Refresh(); e.Handled = true; }
                if (e.Key == Key.Y) { Document.Redo(); Refresh(); e.Handled = true; }
            }
            catch (Exception ex) { error(ex); }
        };
        grid.BeginningEdit += (_, e) => { if (e.Row.DataContext is RowView row && columnMap.TryGetValue(e.Column, out var col)) e.Cancel = Document.LockedRows.Contains(row.Row) || Document.LockedColumns.Contains(Document.Table!.Columns[col]); };
    }
    private ContextMenu CreateRowMenu(DataGrid grid)
    {
        var rows = grid.SelectedItems.OfType<RowView>().Select(r => r.Row).ToArray();
        string noun = rows.Length == 1 ? "row" : $"{rows.Length} rows";
        bool frozen = rows.All(frozenRows.Contains);
        bool locked = rows.All(Document.LockedRows.Contains);
        MenuItem Item(string label, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = label, IsEnabled = enabled };
            item.Click += (_, _) => { try { activeGrid = grid; action(); } catch (Exception ex) { error(ex); } };
            return item;
        }
        var copy = new MenuItem { Header = $"Copy {noun} (displayed columns)" };
        copy.Click += async (_, _) => { try { activeGrid = grid; await CopyAsync(); } catch (Exception ex) { error(ex); } };
        var paste = new MenuItem { Header = "Paste starting at selected cell", IsEnabled = !Document.PendingSource && !Document.LockedRows.Contains(SelectedRow) && !Document.LockedColumns.Contains(SelectedColumn) };
        paste.Click += async (_, _) => { try { activeGrid = grid; await PasteAsync(); } catch (Exception ex) { error(ex); } };
        return new ContextMenu { ItemsSource = new Control[] {
            Item($"{(frozen ? "Unfreeze" : "Freeze")} {noun}", ToggleFrozenRows, frozen || frozenRows.Union(rows).Count() <= 5),
            Item($"{(locked ? "Unlock" : "Lock")} {noun} against edits", ToggleRowLocks),
            new Separator(), copy, paste,
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
    private string Header(int column)
    {
        var name = Document.Table!.Columns[column];
        return (frozenColumns.Contains(column) ? "▣ " : "") + name + (Document.LockedColumns.Contains(name) ? " [locked]" : "") + (name == sortColumn ? descending ? " ▼" : " ▲" : "");
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
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // Bound font-layout work on the UI thread. Sample the beginning and the full table,
        // rather than measuring thousands of distinct identifiers before the first frame.
        var sampleRows = Enumerable.Range(0, Math.Min(100, table.Records.Count))
            .Concat(Enumerable.Range(0, Math.Min(100, table.Records.Count)).Select(i => i * (table.Records.Count - 1) / 99)).Distinct();
        foreach (int row in sampleRows)
        {
            if (width >= maximum) break;
            var value = table.Cell(row, table.Columns[index]);
            if (seen.Add(value)) width = Math.Min(maximum, Math.Max(width, Measure(value) + 24));
        }
        return fittedWidths[index] = Math.Ceiling(width);
    }
    public void RefreshColumns()
    {
        if (Document.Table == null) return;
        bool previous = refreshing; refreshing = true; var selectedColumn = SelectedColumn;
        try
        {
            columnMap.Clear();
            foreach (var grid in new[] { TableGrid, FrozenGrid })
            {
                grid.FrozenColumnCount = 0; grid.Columns.Clear();
                foreach (var i in VisibleColumns())
                {
                    var column = new DataGridTextColumn { Header = Header(i), Binding = new Binding($"[{i}]") { Mode = BindingMode.TwoWay }, MinWidth = 40, CanUserResize = true, Width = widths.TryGetValue(i, out var savedWidth) ? savedWidth : new(FitColumn(i)), IsReadOnly = Document.Table.IsCatalog && i < 2 || Document.LockedColumns.Contains(Document.Table.Columns[i]) };
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
        }
        finally { refreshing = previous; }
        UpdateNote();
    }
    public void Refresh()
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
                RefreshColumns(); ApplyView(selected, column);
            }
        }
        finally { refreshing = false; }
        UpdateNote(); selection(this);
    }
    private void ApplyView(int selected, string column)
    {
        var table = Document.Table!; var term = filter.Text ?? "";
        IEnumerable<int> rows = Enumerable.Range(0, table.Records.Count).Where(i => !frozenRows.Contains(i) && (term.Length == 0 || table.Columns.Any(c => table.Cell(i, c).Contains(term, StringComparison.OrdinalIgnoreCase))));
        if (sortColumn != null && table.Columns.Contains(sortColumn)) rows = descending ? rows.OrderByDescending(i => table.Cell(i, sortColumn), StringComparer.OrdinalIgnoreCase) : rows.OrderBy(i => table.Cell(i, sortColumn), StringComparer.OrdinalIgnoreCase);
        TableGrid.ItemsSource = rows.Select(i => new RowView(Document, i, error)).ToArray();
        FrozenGrid.ItemsSource = frozenRows.Select(i => new RowView(Document, i, error)).ToArray();
        FrozenGrid.IsVisible = frozenRows.Count > 0; FrozenGrid.Height = frozenRows.Count * 30 + 34;
        TableGrid.HeadersVisibility = FrozenGrid.IsVisible ? DataGridHeadersVisibility.Row : DataGridHeadersVisibility.All;
        activeGrid = frozenRows.Contains(selected) ? FrozenGrid : TableGrid;
        activeGrid.SelectedItem = ((IEnumerable<RowView>)activeGrid.ItemsSource!).FirstOrDefault(r => r.Row == selected);
        activeGrid.SelectedItem ??= ((IEnumerable<RowView>)activeGrid.ItemsSource!).FirstOrDefault();
        selectedRow = (activeGrid.SelectedItem as RowView)?.Row ?? -1; selectedColumn = column;
        if (activeGrid.SelectedItem != null) activeGrid.CurrentColumn = activeGrid.Columns.FirstOrDefault(c => table.Columns[columnMap[c]] == column) ?? activeGrid.Columns.FirstOrDefault();
        RestoreAfterLayout(activeGrid, activeGrid.SelectedItem as RowView, activeGrid.CurrentColumn, false);
        if (mainBar != null) Dispatcher.UIThread.Post(() => SyncBars(mainBar), DispatcherPriority.Background);
    }
    private void RestoreAfterLayout(DataGrid grid, RowView? item, DataGridColumn? column, bool scroll)
    {
        var version = viewVersion;
        if (item == null || column == null) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (version != viewVersion || !grid.Columns.Contains(column)) return;
            refreshing = true;
            try { activeGrid = grid; grid.SelectedItem = item; grid.CurrentColumn = column; if (scroll) grid.ScrollIntoView(item, column); }
            finally { refreshing = false; }
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
    public void RefreshRowValues(int row)
    {
        foreach (var grid in new[] { TableGrid, FrozenGrid })
            foreach (var item in (grid.ItemsSource as IEnumerable<RowView> ?? []).Where(r => r.Row == row)) item.RefreshValues();
    }
    public Task SortAsync(string column) { descending = sortColumn == column && !descending; sortColumn = column; Refresh(); return Task.CompletedTask; }
    public void ToggleFrozenRows()
    {
        var rows = activeGrid.SelectedItems.OfType<RowView>().Select(r => r.Row).ToArray(); Storage.Require(rows.Length > 0, "Select rows to freeze.");
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
        var rows = activeGrid.SelectedItems.OfType<RowView>().Select(r => r.Row).ToArray();
        if (rows.All(Document.LockedRows.Contains)) Document.LockedRows.ExceptWith(rows); else Document.LockedRows.UnionWith(rows); Refresh();
    }
    public void ToggleColumnLock() { if (!Document.LockedColumns.Remove(SelectedColumn)) Document.LockedColumns.Add(SelectedColumn); Refresh(); }
    public void Jump(int row, string column = "")
    {
        if (Document.Table == null || Document.PendingSource) return;
        Source.IsVisible = false; tableHost.IsVisible = true; filter.Text = "";
        var i = Array.IndexOf(Document.Table.Columns, column); offset = i > 0 ? ((i - 1) / 23) * 23 : 0; Refresh(); viewVersion++;
        activeGrid = frozenRows.Contains(row) ? FrozenGrid : TableGrid;
        var item = ((IEnumerable<RowView>)activeGrid.ItemsSource!).FirstOrDefault(r => r.Row == row);
        if (item != null)
        {
            selectedRow = row; selectedColumn = i >= 0 ? column : Document.Table.Columns.FirstOrDefault() ?? "";
            activeGrid.SelectedItem = item;
            var col = activeGrid.Columns.FirstOrDefault(c => Document.Table.Columns[columnMap[c]] == column) ?? activeGrid.Columns.FirstOrDefault();
            if (col != null) { activeGrid.CurrentColumn = col; RestoreAfterLayout(activeGrid, item, col, true); }
        }
        selection(this);
    }
    public async Task CopyAsync()
    {
        if (Document.Table == null) return;
        var text = string.Join('\n', activeGrid.SelectedItems.OfType<RowView>().Select(r => string.Join('\t', VisibleColumns().Select(c => r[c]))));
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }
    public async Task PasteAsync()
    {
        if (Document.Table == null || Document.PendingSource) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard; if (clipboard == null) return;
        var text = await clipboard.TryGetTextAsync(); if (text == null) return;
        var displayed = ((IEnumerable<RowView>)activeGrid.ItemsSource!).ToArray(); int start = Array.FindIndex(displayed, r => r.Row == SelectedRow);
        var columns = VisibleColumns(); int firstColumn = Math.Max(0, activeGrid.CurrentColumn?.DisplayIndex ?? 0);
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList(); if (lines.Count > 1 && lines[^1] == "") lines.RemoveAt(lines.Count - 1);
        var edits = new List<(int, string, string)>();
        for (int r = 0; r < lines.Count; r++)
        {
            Storage.Require(start >= 0 && start + r < displayed.Length, "Paste exceeds the displayed rows."); var cells = lines[r].Split('\t');
            Storage.Require(firstColumn + cells.Length <= columns.Length, "Paste exceeds this column window.");
            for (int c = 0; c < cells.Length; c++) edits.Add((displayed[start + r].Row, Document.Table.Columns[columns[firstColumn + c]], cells[c]));
        }
        Document.SetCells(edits); Refresh();
    }
}

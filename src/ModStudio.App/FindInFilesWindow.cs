using System.ComponentModel;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using ModStudio.Core;
using static ModStudio.Core.Storage;

namespace ModStudio.App;

/// <summary>What Find in files needs from the main window: the project, the editors that are open, and a way to open a file without switching to it.</summary>
internal sealed record FindInFilesHost(Func<ModProject?> Project, Func<string, EditorPane?> OpenPane, Func<IEnumerable<EditorPane>> Panes, Func<string, bool, Task<EditorPane?>> Open, Action<Exception> Error, Action<string> Status);

/// <summary>A row of the table preview. Values come from the live editor when the file is open, otherwise from the table the search parsed; edits go to the editor.</summary>
internal sealed class PreviewRow(FindInFilesWindow owner, string file, int row) : INotifyPropertyChanged
{
    public int Row => row;
    public event PropertyChangedEventHandler? PropertyChanged;
    public void RefreshValues() { PropertyChanged?.Invoke(this, new("Item")); PropertyChanged?.Invoke(this, new("Item[]")); }
    public string this[int column]
    {
        get => owner.CellValue(file, row, column);
        set => owner.EditCell(file, row, column, value, this);
    }
}

/// <summary>
/// Workspace search in the spirit of the JetBrains "Find in Files" popup: results update as you type, the selected hit is
/// previewed below, and the preview is live. Table hits preview as an editable grid around the matching cell; text hits as
/// an editor at the matching line. Edits land in the document's unsaved buffer (a tab is opened in the background when the
/// file is not open yet), so undo, recovery and Save all treat them like any other change.
/// </summary>
internal sealed class FindInFilesWindow : Window
{
    private static readonly IBrush MatchBrush = new SolidColorBrush(Color.Parse("#5A4A1A"));
    private static readonly IBrush CurrentBrush = new SolidColorBrush(Color.Parse("#7A5F1E"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#B9AD97"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#D8BC86"));
    private readonly FindInFilesHost host;
    private readonly TextBox query = new() { PlaceholderText = "Search table cells, strings and files…", FontSize = 14, MinHeight = 34 };
    private readonly ToggleButton matchCase = Toggle("Cc", "Match case"), wholeWord = Toggle("W", "Whole words only"), useRegex = Toggle(".*", "Regular expression");
    private readonly ComboBox scope = new() { MinWidth = 190, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox mask = new() { PlaceholderText = "File mask: *.json; *.txt", Width = 170, MinHeight = 28, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock status = new() { Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(10, 0, 0, 0) };
    private readonly TextBlock empty = new() { Foreground = Muted, Margin = new(14, 12), TextWrapping = TextWrapping.Wrap, Text = "Type to search. Table and string files are searched cell by cell; everything else line by line." };
    private readonly ListBox results = new() { Background = Brushes.Transparent, BorderThickness = new(0), Padding = new(0, 2) };
    private readonly Grid body = new() { RowDefinitions = new("3*,Auto,2*") };
    private readonly TextBlock previewTitle = new() { Foreground = Accent, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock previewNote = new() { Foreground = Muted, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(10, 0, 0, 0) };
    private readonly Panel previewHost = new();
    private readonly TextBlock previewEmpty = new() { Foreground = Muted, Margin = new(14, 12), Text = "Select a result to preview it here. Cells and text can be edited in place." };
    private readonly Button openButton = new() { Content = "Open in editor", IsEnabled = false };
    private readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private CancellationTokenSource? searchWork;
    private int generation;
    private SearchResult? result;
    private SearchHit? previewed;
    private string? previewFile, textFile;
    private int previewColumn = -1;
    private bool syncingText, paintQueued, openingForText;
    private readonly Dictionary<string, TableData> snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<DataGridColumn, int> columnMap = [];
    private readonly HashSet<(int Row, int Col)> matchedCells = [];
    public DataGrid PreviewGrid { get; } = new()
    {
        AutoGenerateColumns = false, CanUserReorderColumns = false, CanUserSortColumns = false, CanUserResizeColumns = true, IsReadOnly = false,
        RowHeight = 28, RowHeaderWidth = 52, HeadersVisibility = DataGridHeadersVisibility.All, SelectionMode = DataGridSelectionMode.Single,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, IsVisible = false
    };
    public TextEditor PreviewText { get; } = new() { ShowLineNumbers = true, FontFamily = new("Consolas, Menlo, monospace"), FontSize = 13, IsVisible = false };
    public TextBox Query => query;
    public ListBox Results => results;
    public SearchResult? Result => result;
    public SearchHit? Previewed => previewed;
    public string StatusText => status.Text ?? "";
    /// <summary>Completes when the last search requested has been shown (or was superseded).</summary>
    public Task PendingSearch { get; private set; } = Task.CompletedTask;

    public FindInFilesWindow(FindInFilesHost host)
    {
        this.host = host;
        var preferences = LoadPreferences();
        Title = "Find in files"; Width = preferences?.FindInFilesWidth is > 400 and var w ? w : 1040; Height = preferences?.FindInFilesHeight is > 300 and var h ? h : 680;
        MinWidth = 640; MinHeight = 400; CanResize = true; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var root = new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), Margin = new(12, 10, 12, 10) };
        // Query line: the search text with its three mode toggles.
        var queryLine = new DockPanel { LastChildFill = true, Margin = new(0, 0, 0, 8) };
        var toggles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new(8, 0, 0, 0) };
        toggles.Children.Add(matchCase); toggles.Children.Add(wholeWord); toggles.Children.Add(useRegex);
        DockPanel.SetDock(toggles, Dock.Right); queryLine.Children.Add(toggles); queryLine.Children.Add(query);
        root.Children.Add(queryLine);
        // Scope line: where to look, what file names to include, and how the search is going.
        var scopeLine = new DockPanel { LastChildFill = true, Margin = new(0, 0, 0, 8) };
        var scopeLabel = new TextBlock { Text = "In", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new(2, 0, 8, 0) };
        scope.ItemsSource = WorkspaceSearch.Scopes.Select(s => s.Label).ToArray();
        scope.SelectedIndex = Math.Max(0, Array.FindIndex(WorkspaceSearch.Scopes, s => s.Folder == (preferences?.FindInFilesScope ?? "source")));
        DockPanel.SetDock(scopeLabel, Dock.Left); DockPanel.SetDock(scope, Dock.Left); DockPanel.SetDock(mask, Dock.Left);
        mask.Margin = new(8, 0, 0, 0);
        scopeLine.Children.Add(scopeLabel); scopeLine.Children.Add(scope); scopeLine.Children.Add(mask); scopeLine.Children.Add(status);
        Grid.SetRow(scopeLine, 1); root.Children.Add(scopeLine);
        // Body: results above, preview below, with a draggable splitter between them.
        var resultsFrame = new Border { Background = new SolidColorBrush(Color.Parse("#1D1E1F")), BorderBrush = new SolidColorBrush(Color.Parse("#494038")), BorderThickness = new(1), CornerRadius = new(4) };
        var resultsHost = new Panel(); resultsHost.Children.Add(results); resultsHost.Children.Add(empty); resultsFrame.Child = resultsHost;
        body.Children.Add(resultsFrame);
        var splitter = new GridSplitter { Height = 6, ResizeDirection = GridResizeDirection.Rows, Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetRow(splitter, 1); body.Children.Add(splitter);
        var previewFrame = new Border { Background = new SolidColorBrush(Color.Parse("#202122")), BorderBrush = new SolidColorBrush(Color.Parse("#494038")), BorderThickness = new(1), CornerRadius = new(4) };
        var previewLayout = new Grid { RowDefinitions = new("Auto,*") };
        var previewHeader = new DockPanel { Margin = new(10, 6, 6, 6), LastChildFill = true };
        openButton.MinHeight = 26; openButton.Padding = new(10, 3); openButton.Margin = new(8, 0, 0, 0); openButton.FontSize = 12;
        DockPanel.SetDock(openButton, Dock.Right); previewHeader.Children.Add(openButton);
        var titles = new StackPanel { Orientation = Orientation.Horizontal }; titles.Children.Add(previewTitle); titles.Children.Add(previewNote); previewHeader.Children.Add(titles);
        previewLayout.Children.Add(previewHeader);
        previewHost.Children.Add(previewEmpty); previewHost.Children.Add(PreviewGrid); previewHost.Children.Add(PreviewText);
        Grid.SetRow(previewHost, 1); previewLayout.Children.Add(previewHost); previewFrame.Child = previewLayout;
        Grid.SetRow(previewFrame, 2); body.Children.Add(previewFrame);
        Grid.SetRow(body, 2); root.Children.Add(body);
        // Footer: a hint about what the keys do.
        var footer = new TextBlock { Foreground = Muted, FontSize = 11, Margin = new(2, 8, 0, 0), Text = "Enter or double-click opens the match in the editor (Ctrl+Enter keeps this window open) · Esc closes · Edits made in the preview go to the file's unsaved buffer; save with Save all (Ctrl+S)" };
        Grid.SetRow(footer, 3); root.Children.Add(footer);
        Content = root;
        WireInput();
        WireResults();
        WirePreview();
    }

    private static ToggleButton Toggle(string text, string tip)
    {
        var button = new ToggleButton { Content = text, MinWidth = 34, MinHeight = 30, Padding = new(6, 2), FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(button, tip); return button;
    }
    private static StudioPreferences? LoadPreferences()
    {
        if (Program.Arguments.Contains("--smoke")) return null;
        try { return StudioPreferences.Load(StudioPreferences.DefaultFile); } catch (Exception) { return null; }
    }
    private string ScopeFolder => WorkspaceSearch.Scopes[Math.Max(0, scope.SelectedIndex)].Folder;

    private void WireInput()
    {
        query.TextChanged += (_, _) => Restart();
        mask.TextChanged += (_, _) => Restart();
        scope.SelectionChanged += (_, _) => Restart();
        foreach (var toggle in new[] { matchCase, wholeWord, useRegex }) toggle.IsCheckedChanged += (_, _) => Restart();
        debounce.Tick += (_, _) => { debounce.Stop(); PendingSearch = SearchAsync(); };
        query.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Down && results.ItemCount > 0) { results.SelectedIndex = Math.Max(0, results.SelectedIndex); results.Focus(); e.Handled = true; }
            else if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { e.Handled = true; await OpenSelectedAsync(close: !e.KeyModifiers.HasFlag(KeyModifiers.Control)); }
        };
        mask.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await OpenSelectedAsync(close: true); } };
        results.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; await OpenSelectedAsync(close: !e.KeyModifiers.HasFlag(KeyModifiers.Control)); }
            else if (e.Key == Key.Up && results.SelectedIndex <= 0) { query.Focus(); e.Handled = true; }
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        openButton.Click += async (_, _) => await OpenSelectedAsync(close: false);
        Opened += (_, _) => { query.Focus(); query.SelectAll(); };
        Closing += (_, _) =>
        {
            debounce.Stop(); searchWork?.Cancel();
            if (Program.Arguments.Contains("--smoke")) return;
            try
            {
                var preferences = StudioPreferences.Load(StudioPreferences.DefaultFile);
                preferences.FindInFilesWidth = Width; preferences.FindInFilesHeight = Height; preferences.FindInFilesScope = ScopeFolder;
                preferences.Save(StudioPreferences.DefaultFile);
            }
            catch (Exception) { }
        };
    }

    /// <summary>Puts text in the query (a cell value or editor selection when the window was summoned) and focuses it.</summary>
    public void Seed(string? text)
    {
        if (!string.IsNullOrWhiteSpace(text) && !text.Contains('\n') && text.Length <= 200) query.Text = text.Trim();
        Activate(); query.Focus(); query.SelectAll();
    }

    private void Restart() { debounce.Stop(); debounce.Start(); }

    private async Task SearchAsync()
    {
        int current = ++generation; searchWork?.Cancel(); var work = searchWork = new(); var token = work.Token;
        var project = host.Project();
        var text = query.Text ?? "";
        if (project == null || text.Length == 0)
        {
            result = null; results.ItemsSource = null; empty.IsVisible = true; status.Text = project == null ? "Open a project to search it." : "";
            empty.Text = "Type to search. Table and string files are searched cell by cell; everything else line by line.";
            Preview(null); return;
        }
        var request = new SearchQuery(text, matchCase.IsChecked == true, wholeWord.IsChecked == true, useRegex.IsChecked == true, mask.Text ?? "", ScopeFolder);
        // Editors with unsaved changes are searched as they are now; the copies keep the background search off the live document.
        var tables = new Dictionary<string, TableData>(StringComparer.Ordinal); var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pane in host.Panes())
        {
            var document = pane.Document; if (!document.IsDirty) continue;
            if (document.Table != null && !document.PendingSource) tables[document.FilePath] = new TableData((JsonObject)document.Table.Schema.DeepClone(), (JsonArray)document.Table.Records.DeepClone());
            else texts[document.FilePath] = document.Text.TrimStart('﻿');
        }
        status.Text = "Searching…";
        try
        {
            var found = await Task.Run(() => WorkspaceSearch.Run(project, request, tables, texts, token, count => Dispatcher.UIThread.Post(() => { if (current == generation) status.Text = $"Searching… {count:N0} files"; })), token);
            if (current != generation) return;
            ShowResults(found);
        }
        catch (OperationCanceledException) { }
        catch (ArgumentException e) { if (current == generation) { result = null; results.ItemsSource = null; empty.IsVisible = true; empty.Text = e.Message; status.Text = ""; Preview(null); } }
        catch (Exception e) { if (current == generation) { status.Text = e.Message; host.Error(e); } }
        finally { if (current == generation) { searchWork = null; } work.Dispose(); }
    }

    private void ShowResults(SearchResult found)
    {
        result = found;
        foreach (var pair in found.Tables) snapshots[pair.Key] = pair.Value;
        var previous = previewed;
        results.ItemsSource = found.Hits;
        empty.IsVisible = found.Hits.Count == 0; empty.Text = "No matches." + (found.Issues.Count > 0 ? " " + found.Issues[0] : "");
        int cells = found.Hits.Count(h => h.IsCell);
        status.Text = found.Hits.Count == 0 ? $"No matches in {found.Files:N0} files"
            : (found.Truncated ? $"First {found.Hits.Count:N0} matches" : $"{found.Hits.Count:N0} {(found.Hits.Count == 1 ? "match" : "matches")}") + $" in {found.MatchedFiles:N0} of {found.Files:N0} files"
              + (cells > 0 && cells < found.Hits.Count ? $" · {cells:N0} cells, {found.Hits.Count - cells:N0} lines" : cells == found.Hits.Count ? " · table cells" : " · text lines")
              + (found.Truncated ? " · refine the search to see the rest" : "") + (found.Issues.Count > 0 ? $" · {found.Issues.Count} files skipped" : "");
        if (found.Issues.Count > 0) ToolTip.SetTip(status, string.Join("\n", found.Issues.Take(20))); else ToolTip.SetTip(status, null);
        // Keep the same hit selected when a refined search still contains it, so the preview does not jump.
        var keep = previous == null ? null : found.Hits.FirstOrDefault(h => h.File == previous.File && h.Row == previous.Row && h.Column == previous.Column && h.Line == previous.Line);
        results.SelectedItem = keep ?? found.Hits.FirstOrDefault();
        if (results.SelectedItem is SearchHit selected) results.ScrollIntoView(selected);
    }

    private void WireResults()
    {
        results.ItemTemplate = new FuncDataTemplate<SearchHit>((hit, _) => RenderHit(hit), supportsRecycling: false);
        results.SelectionChanged += (_, _) => Preview(results.SelectedItem as SearchHit);
        results.DoubleTapped += async (_, e) => { if (results.SelectedItem is SearchHit) { e.Handled = true; await OpenSelectedAsync(close: true); } };
    }

    private Control RenderHit(SearchHit? hit)
    {
        // A recycled list container is re-templated with no item before it gets its next one.
        if (hit == null) return new Border();
        var project = host.Project(); var relative = project == null ? Path.GetFileName(hit.File) : Relative(project.Root, hit.File);
        var line = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(6, 1) };
        var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        if (hit.IsCell)
        {
            if (hit.Label.Length > 0 && hit.Label != hit.Text) text.Inlines!.Add(new Run(Shorten(hit.Label, 40) + "  ·  ") { Foreground = Muted });
            text.Inlines!.Add(new Run(hit.Column + " = ") { Foreground = Accent });
        }
        var (before, match, after) = Snippet(hit);
        text.Inlines!.Add(new Run(before));
        text.Inlines.Add(new Run(match) { Background = MatchBrush, FontWeight = FontWeight.SemiBold });
        text.Inlines.Add(new Run(after));
        var where = new TextBlock { Foreground = Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new(14, 0, 4, 0), Text = hit.IsCell ? $"{relative}  ·  row {hit.Row}" : $"{relative}:{hit.Line}" };
        Grid.SetColumn(where, 1); line.Children.Add(text); line.Children.Add(where);
        ToolTip.SetTip(line, hit.IsCell ? $"{relative}\nrow {hit.Row} · {hit.Column}\n{Shorten(hit.Text, 400)}" : $"{relative}\nline {hit.Line}\n{Shorten(hit.Text, 400)}");
        return line;
    }
    private static string Shorten(string value, int max) => value.Length <= max ? value : value[..max] + "…";
    /// <summary>The text around the first match, with everything on one line and only a little context before it.</summary>
    private static (string Before, string Match, string After) Snippet(SearchHit hit)
    {
        var text = hit.Text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
        int start = Math.Clamp(hit.Start, 0, text.Length), length = Math.Clamp(hit.Length, 0, text.Length - start);
        int from = Math.Max(0, start - 60);
        var before = (from > 0 ? "…" : "") + text[from..start]; var after = text[(start + length)..];
        return (before, text.Substring(start, length), after.Length > 240 ? after[..240] + "…" : after);
    }

    // ---- Preview -------------------------------------------------------------------------------------------------------

    private void WirePreview()
    {
        ScrollViewer.SetAllowAutoHide(PreviewGrid, false);
        PreviewGrid.LoadingRow += (_, e) => { if (e.Row.DataContext is PreviewRow row) e.Row.Header = row.Row; QueuePaint(); };
        PreviewGrid.CurrentCellChanged += (_, _) => QueuePaint();
        PreviewGrid.KeyDown += async (_, e) =>
        {
            // Ctrl+Enter from the grid opens the cell under the cursor in the editor; plain Enter stays with the grid's editing.
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control) && PreviewGrid.SelectedItem is PreviewRow row && previewed is { } hit && PreviewGrid.CurrentColumn is { } column && columnMap.TryGetValue(column, out var index) && LiveTable(hit.File) is { } table && index < table.Columns.Length)
            { e.Handled = true; await OpenAsync(new SearchHit(hit.File, row.Row, table.Columns[index], -1, "", 0, 0), close: false); }
        };
        PreviewText.TextChanged += (_, _) => { if (!syncingText && textFile != null) ApplyText(textFile); };
    }

    private void Preview(SearchHit? hit)
    {
        previewed = hit; openButton.IsEnabled = hit != null;
        if (hit == null)
        {
            previewTitle.Text = ""; previewNote.Text = ""; previewEmpty.IsVisible = true; PreviewGrid.IsVisible = false; PreviewText.IsVisible = false;
            previewFile = null; previewColumn = -1; matchedCells.Clear(); return;
        }
        var project = host.Project(); var relative = project == null ? Path.GetFileName(hit.File) : Relative(project.Root, hit.File);
        previewEmpty.IsVisible = false;
        if (hit.IsCell && LiveTable(hit.File) is { } table) ShowTable(hit, table, relative);
        else ShowText(hit, relative);
    }

    /// <summary>The table to read cells from: the open editor's document when there is one, else what the search parsed.</summary>
    private TableData? LiveTable(string file)
    {
        if (host.OpenPane(file)?.Document is { Table: { } live, PendingSource: false }) return live;
        return snapshots.GetValueOrDefault(file);
    }
    internal string CellValue(string file, int row, int column)
    {
        var table = LiveTable(file);
        return table == null || row < 0 || row >= table.Records.Count || column < 0 || column >= table.Columns.Length ? "" : table.Cell(row, table.Columns[column]);
    }

    private void ShowTable(SearchHit hit, TableData table, string relative)
    {
        PreviewText.IsVisible = false; PreviewGrid.IsVisible = true;
        int column = table.ColumnIndex(hit.Column);
        var pane = host.OpenPane(hit.File);
        previewTitle.Text = $"{relative}  ·  row {hit.Row}  ·  {hit.Column}";
        previewNote.Text = pane?.Document.IsDirty == true ? "unsaved edits in the open editor" : pane != null ? "open in the editor" : "";
        matchedCells.Clear();
        if (result != null) foreach (var other in result.Hits) if (other.IsCell && other.File == hit.File && table.ColumnIndex(other.Column) is var c and >= 0) matchedCells.Add((other.Row, c));
        bool sameFile = previewFile == hit.File && ReferenceEquals(PreviewGrid.Tag, table);
        bool columnShown = sameFile && columnMap.ContainsValue(column);
        if (!sameFile || !columnShown)
        {
            // A window of columns around the hit, with the row's identifying columns kept in front. Whole tables run to hundreds of columns.
            int identity = table.IsCatalog ? 2 : 1;
            var shown = Enumerable.Range(0, Math.Min(identity, table.Columns.Length)).Concat(Enumerable.Range(Math.Max(0, column - 4), Math.Min(table.Columns.Length, Math.Max(0, column - 4) + 14) - Math.Max(0, column - 4))).Distinct().ToList();
            columnMap.Clear(); PreviewGrid.Columns.Clear();
            foreach (var i in shown)
            {
                var name = table.Columns[i];
                var grid = new DataGridTextColumn { Header = name, Binding = new Binding($"[{i}]") { Mode = BindingMode.TwoWay }, MinWidth = 48, MaxWidth = 300, IsReadOnly = table.IsIdentityColumn(name) && !table.IsCatalog };
                columnMap[grid] = i; PreviewGrid.Columns.Add(grid);
            }
            PreviewGrid.FrozenColumnCount = Math.Min(identity, shown.Count);
            if (!sameFile) PreviewGrid.ItemsSource = Enumerable.Range(0, table.Records.Count).Select(i => new PreviewRow(this, hit.File, i)).ToArray();
            PreviewGrid.Tag = table; previewFile = hit.File;
        }
        previewColumn = column;
        var rows = (PreviewRow[])PreviewGrid.ItemsSource!;
        var item = hit.Row < rows.Length ? rows[hit.Row] : null; var target = columnMap.FirstOrDefault(p => p.Value == column).Key;
        if (item != null) { PreviewGrid.SelectedItem = item; if (target != null) PreviewGrid.CurrentColumn = target; CenterRow(hit, rows, item, target); }
        QueuePaint();
        var shownColumns = columnMap.Values.Order().ToArray();
        if (table.Columns.Length > shownColumns.Length) previewNote.Text = (previewNote.Text.Length > 0 ? previewNote.Text + " · " : "") + $"columns {shownColumns.Skip(table.IsCatalog ? 2 : 1).DefaultIfEmpty(0).Min() + 1}–{shownColumns.Max() + 1} of {table.Columns.Length}";
    }

    /// <summary>
    /// ScrollIntoView moves the least it can, which leaves a row below the viewport on its last line. Scrolling half a viewport
    /// past the hit first, then back to it, settles the hit in the middle where the eye lands. Runs after layout so the grid has a size.
    /// </summary>
    private void CenterRow(SearchHit hit, PreviewRow[] rows, PreviewRow item, DataGridColumn? target)
    {
        PreviewGrid.ScrollIntoView(item, target);
        Dispatcher.UIThread.Post(() =>
        {
            if (previewed != hit || !ReferenceEquals(PreviewGrid.ItemsSource, rows)) return;
            int half = Math.Max(0, (int)(PreviewGrid.Bounds.Height / Math.Max(1, PreviewGrid.RowHeight)) / 2 - 1);
            PreviewGrid.ScrollIntoView(rows[Math.Min(rows.Length - 1, item.Row + half)], null);
            PreviewGrid.ScrollIntoView(item, target);
            QueuePaint();
        }, DispatcherPriority.Background);
    }
    private void QueuePaint()
    {
        if (paintQueued) return; paintQueued = true;
        Dispatcher.UIThread.Post(() => { paintQueued = false; PaintCells(); }, DispatcherPriority.Background);
    }
    /// <summary>Marks every matching cell and the one being previewed. The app hides the grid's row selection, so cells are painted directly.</summary>
    private void PaintCells()
    {
        if (!PreviewGrid.IsVisible) return;
        foreach (var rowControl in PreviewGrid.GetVisualDescendants().OfType<DataGridRow>())
        {
            if (rowControl.DataContext is not PreviewRow item) continue;
            foreach (var column in PreviewGrid.Columns)
            {
                if (column.GetCellContent(rowControl) is not Visual content || !columnMap.TryGetValue(column, out var col)) continue;
                var cell = content as DataGridCell ?? content.GetVisualAncestors().OfType<DataGridCell>().FirstOrDefault(); if (cell == null) continue;
                if (previewed is { } hit && hit.Row == item.Row && col == previewColumn) cell.Background = CurrentBrush;
                else if (matchedCells.Contains((item.Row, col))) cell.Background = MatchBrush;
                else cell.ClearValue(DataGridCell.BackgroundProperty);
            }
        }
    }

    /// <summary>Writes a cell edit to the document, opening the file in a background tab first when it is not open.</summary>
    internal async void EditCell(string file, int row, int column, string value, PreviewRow view)
    {
        try
        {
            var table = LiveTable(file); if (table == null || column < 0 || column >= table.Columns.Length) return;
            var name = table.Columns[column];
            if (table.Cell(row, name) == value) return;
            var pane = host.OpenPane(file) ?? await host.Open(file, false);
            Require(pane != null, "The file could not be opened for editing.");
            var document = pane!.Document;
            Require(document.Table != null && !document.PendingSource, "Apply or fix the file's pending source in the editor before editing its cells here.");
            Require(row < document.Table!.Records.Count && document.Table.ColumnIndex(name) >= 0, "That row or column no longer exists. Search again.");
            document.SetCells([(row, name, value)]); pane.RefreshRowValues(row);
            var project = host.Project();
            host.Status($"Edited {(project == null ? Path.GetFileName(file) : Relative(project.Root, file))} · row {row} · {name} (unsaved)");
            if (previewed?.File == file) previewNote.Text = "unsaved edits in the open editor";
        }
        catch (Exception e) { host.Error(e); }
        finally { view.RefreshValues(); }
    }

    private async void ShowText(SearchHit hit, string relative)
    {
        PreviewGrid.IsVisible = false; PreviewText.IsVisible = true;
        previewTitle.Text = hit.IsCell ? $"{relative}  ·  row {hit.Row}  ·  {hit.Column}" : $"{relative}:{hit.Line}";
        var pane = host.OpenPane(hit.File);
        previewNote.Text = pane?.Document.IsDirty == true ? "unsaved edits in the open editor" : pane != null ? "open in the editor" : "";
        try
        {
            if (textFile != hit.File)
            {
                var text = pane != null ? pane.Document.Text.TrimStart('﻿') : await Task.Run(() => ReadText(hit.File));
                if (previewed != hit) return;
                syncingText = true;
                try { PreviewText.SyntaxHighlighting = SourceCodeEditing.Highlighting(hit.File); PreviewText.Text = text; }
                finally { syncingText = false; }
                textFile = hit.File; previewFile = null;
            }
            if (hit.IsCell) return;
            int lines = PreviewText.Document.LineCount; if (lines == 0) return;
            var line = PreviewText.Document.GetLineByNumber(Math.Clamp(hit.Line, 1, lines));
            int offset = line.Offset + Math.Clamp(hit.Offset, 0, line.Length); int length = Math.Clamp(hit.Length, 0, line.EndOffset - offset);
            PreviewText.Select(offset, length); PreviewText.TextArea.Caret.Offset = offset;
            PreviewText.ScrollToLine(line.LineNumber);
            Dispatcher.UIThread.Post(() => { if (previewed == hit) PreviewText.ScrollToLine(line.LineNumber); }, DispatcherPriority.Background);
        }
        catch (Exception e) { host.Error(e); }
    }
    private static string ReadText(string file)
    {
        NoLinks(file); var bytes = File.ReadAllBytes(file);
        var (encoding, preamble) = TextFileEncoding.Detect(bytes);
        return encoding.GetString(bytes, preamble, bytes.Length - preamble);
    }

    /// <summary>Pushes the preview editor's text into the document. Keystrokes typed while the file is still opening are applied once it is.</summary>
    private async void ApplyText(string file)
    {
        // While the file is still opening, later keystrokes are covered: the text applied below is read after the open completes.
        if (openingForText) return;
        try
        {
            var pane = host.OpenPane(file);
            if (pane == null)
            {
                openingForText = true;
                try { pane = await host.Open(file, false); } finally { openingForText = false; }
                Require(pane != null, "The file could not be opened for editing.");
                if (textFile != file) return;
            }
            var document = pane!.Document; var text = PreviewText.Text;
            if (document.Text.TrimStart('﻿') == text) return;
            document.SetRaw((document.Text.StartsWith('﻿') ? "﻿" : "") + text); pane.Refresh();
            var project = host.Project();
            host.Status($"Edited {(project == null ? Path.GetFileName(file) : Relative(project.Root, file))} (unsaved)");
            previewNote.Text = "unsaved edits in the open editor";
        }
        catch (Exception e) { host.Error(e); }
    }

    // ---- Navigation ----------------------------------------------------------------------------------------------------

    public Task OpenSelectedAsync(bool close) => results.SelectedItem is SearchHit hit ? OpenAsync(hit, close) : Task.CompletedTask;

    /// <summary>Opens the hit's file in the main editor and lands on the cell or the matching text.</summary>
    public async Task OpenAsync(SearchHit hit, bool close)
    {
        try
        {
            var pane = await host.Open(hit.File, true);
            if (pane != null)
            {
                if (hit.IsCell && pane.Document.Table != null && !pane.Document.PendingSource) pane.Jump(hit.Row, hit.Column);
                else pane.ShowSourceLine(Math.Max(1, hit.Line), hit.Offset, hit.Length);
            }
            if (close) Close();
        }
        catch (Exception e) { host.Error(e); }
    }
}

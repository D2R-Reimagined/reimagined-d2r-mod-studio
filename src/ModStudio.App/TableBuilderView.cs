using System.Collections;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ModStudio.Core;
using static ModStudio.Core.Storage;

namespace ModStudio.App;

/// <summary>
/// What a Visual Builder reads from the window: the open project, the profile, the workspace revision and where base-game
/// files live. <see cref="FindTable"/> is a related table already open in a tab; <see cref="OpenTable"/> opens one in a
/// background tab so a builder can edit it too. <see cref="MonsterDetails"/> draws the monster preview's full card.
/// </summary>
internal sealed record VisualBuilderHost(Func<ModProject?> Project, Func<string> Profile, Func<int> Workspace, Func<IReadOnlyList<string>> GameData,
    Func<EditorPane, string?> DirtyDependency, Func<Task> ChooseGameData, Func<string, Document?>? FindTable = null, Func<string, Task<Document?>>? OpenTable = null,
    Func<MonsterPreviewResult, Control>? MonsterDetails = null, Func<string, string, string, Task>? OpenInBuilder = null, Func<EditorPane, Task>? OpenLevelEditor = null,
    Func<string, Document?>? FindFile = null);

/// <summary>What the window and the editor pane need from any Visual Builder, whatever its table.</summary>
internal interface IVisualBuilder
{
    /// <summary>The builder became visible: catch up on what changed while it was hidden.</summary>
    void Shown();
    /// <summary>Something outside its document changed: resolve again.</summary>
    void Invalidate();
    /// <summary>Opens the first row whose column holds the value; false when there is none.</summary>
    bool SelectWhere(string column, string value);
}

/// <summary>
/// The shared frame of a Visual Builder: a searchable list of the table's rows beside an editor for the one picked. Every
/// field writes straight into the pane's document, so edits share its undo history and dirty state with the table and
/// source views; the row is followed by record as rows move, and a preview link read from the row focuses its field.
/// Subclasses supply the list's entries, the editor's cards and the preview.
/// </summary>
internal abstract class TableBuilderView<TEntry, TCatalog> : Grid, IVisualBuilder where TEntry : class where TCatalog : class
{
    protected static readonly IBrush WhiteBrush = new SolidColorBrush(Color.Parse("#E6E6E6")), Muted = new SolidColorBrush(Color.Parse("#A8A29A")),
        Heading = new SolidColorBrush(Color.Parse("#D8BC86")), CardBackground = new SolidColorBrush(Color.Parse("#1D1E20")),
        CardBorder = new SolidColorBrush(Color.Parse("#34363A")), Highlight = new SolidColorBrush(Color.Parse("#D8BC86"));
    protected static readonly FontFamily TooltipFont = new("Palatino Linotype, Book Antiqua, Georgia, serif");

    protected readonly EditorPane pane;
    protected readonly VisualBuilderHost host;
    protected Document Document => pane.Document;
    protected TableData? Table => Document.PendingSource ? null : Document.Table;

    private readonly TextBox search = new();
    private readonly ListBox results = new() { SelectionMode = SelectionMode.Single, Background = Brushes.Transparent };
    private readonly TextBlock resultCount = new() { FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentControl detail = new();
    private readonly ScrollViewer detailScroll;
    protected readonly ComboBox locale = new() { MinWidth = 90 };

    protected TCatalog? catalog;
    private IReadOnlyList<object> catalogRows = [];
    private (string? Project, string Profile, string Locale, int Workspace) catalogContext;
    private readonly PreviewWorkQueue catalogWork = new();
    private CancellationTokenSource? catalogCancellation;
    private readonly DispatcherTimer catalogTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer previewTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };

    /// <summary>The record being edited. Rows are followed by record, since indexes shift when rows are added or removed above.</summary>
    private JsonNode? selected;
    protected string selectedSourceId = "";
    private TableData? builtFor;
    protected bool loading;
    private bool selecting, stale = true;
    protected readonly Dictionary<string, Control> editors = new(StringComparer.Ordinal);
    protected TextBlock status = new();
    protected SelectableTextBlock title = new(), subtitle = new();

    internal Task PendingPreview { get; set; } = Task.CompletedTask;
    internal Task PendingCatalog { get; private set; } = Task.CompletedTask;
    internal IReadOnlyList<TEntry> Results => results.ItemsSource as IReadOnlyList<TEntry> ?? [];
    internal TextBox Search => search;
    internal int SelectedRow => selected == null || Table is not { } table ? -1 : table.Records.IndexOf(selected);
    internal Control? Editor(string column) => editors.GetValueOrDefault(column);
    internal string StatusText => status.Text ?? "";
    internal void ScrollToTop() => detailScroll.Offset = default;

    // ── What a builder supplies ───────────────────────────────────────────────────────────────────────

    protected abstract string ListHeading { get; }
    protected abstract string SearchHint { get; }
    protected abstract string AddLabel { get; }
    /// <summary>"a unique", "a missile": what the empty editor asks you to pick.</summary>
    protected abstract string Noun { get; }
    /// <summary>The column that names a row: shown in the header, renamed by Duplicate.</summary>
    protected abstract string NameColumn { get; }
    protected abstract IBrush TitleBrush { get; }
    protected abstract JsonObject NewRow(TableData table);
    protected virtual string CopyName(string name) => name + " Copy";
    /// <summary>The list's view of the table, read on the UI thread; the catalog reloads when it changes.</summary>
    protected abstract IReadOnlyList<object> ListRows(TableData table);
    /// <summary>Builds the catalog on a worker from the list rows. Must not touch controls or the live document.</summary>
    protected abstract TCatalog LoadCatalog(ModProject project, string profile, string locale, IReadOnlyList<object> rows, bool fresh, CancellationToken token);
    protected abstract IReadOnlyList<TEntry> EntriesOf(TCatalog catalog);
    protected abstract int RowOf(TEntry entry);
    protected abstract string SourceIdOf(TEntry entry);
    protected abstract bool IsInactive(TEntry entry);
    protected abstract string SearchTextOf(TEntry entry);
    protected virtual bool Listed(TEntry entry) => !IsInactive(entry);
    protected abstract Control EntryView(TEntry entry);
    /// <summary>The editor's cards, between the header and the remaining columns.</summary>
    protected abstract void BuildCards(StackPanel root, TableData table);
    protected abstract Task PreviewAsync();
    /// <summary>After every field shows the row's values: refresh anything derived from them.</summary>
    protected virtual void OnSynced(TableData table, int row) { }
    protected virtual void OnCatalogLoaded() { }
    /// <summary>A new row was opened: forget what belonged to the last one.</summary>
    protected virtual void OnSelect() { }
    protected virtual void OnInvalidate() { }
    /// <summary>A field is about to be focused (by a link or a new row): make it visible.</summary>
    protected virtual void Reveal(string column) { }
    /// <summary>Columns an editor card handles without having a field for each (property slots): left out of More columns.</summary>
    protected virtual IEnumerable<string> HandledColumns(TableData table) => [];
    protected virtual string MoreColumnsHint => "";
    protected virtual string TitleText(TableData table, int row) => table.Cell(row, NameColumn) is { Length: > 0 } name ? name : $"Row {row}";

    protected TableBuilderView(EditorPane pane, VisualBuilderHost host)
    {
        this.pane = pane; this.host = host;
        ColumnDefinitions = new("280,4,*");
        var side = new DockPanel { Margin = new(10, 10, 6, 10) };
        detailScroll = new ScrollViewer { Content = detail, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        // The list's headings are virtual, so it is laid out by Ready() at the end of the subclass constructor.
        void Layout()
        {
            var heading = new TextBlock { Text = ListHeading, Foreground = Heading, FontSize = 11, Margin = new(2, 0, 0, 6) };
            DockPanel.SetDock(heading, Dock.Top); side.Children.Add(heading);
            search.PlaceholderText = SearchHint;
            AutomationProperties.SetName(search, "Visual Builder search");
            DockPanel.SetDock(search, Dock.Top); side.Children.Add(search);
            var tools = new DockPanel { Margin = new(0, 6, 0, 6) };
            var add = new Button { Content = AddLabel, Padding = new(8, 3), MinHeight = 0, FontSize = 12, Margin = new(0) };
            ToolTip.SetTip(add, "Adds a row at the bottom of the table and opens it here. Undo removes it again.");
            add.Click += (_, _) => Try(AddRow);
            DockPanel.SetDock(add, Dock.Right); tools.Children.Add(add); tools.Children.Add(resultCount);
            DockPanel.SetDock(tools, Dock.Top); side.Children.Add(tools);
            results.ItemTemplate = new FuncDataTemplate<TEntry>((entry, _) => entry == null ? new TextBlock() : EntryView(entry));
            side.Children.Add(results);
            detail.Content = Placeholder();
        }
        layout = Layout;
        Children.Add(side);
        var splitter = new GridSplitter { Background = CardBorder, ResizeDirection = GridResizeDirection.Columns }; SetColumn(splitter, 1); Children.Add(splitter);
        SetColumn(detailScroll, 2); Children.Add(detailScroll);

        search.TextChanged += (_, _) => ApplyFilter();
        search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Results.FirstOrDefault(r => !IsInactive(r)) is { } first) { e.Handled = true; Select(RowOf(first)); }
            else if (e.Key == Key.Down && Results.Count > 0) { e.Handled = true; results.SelectedIndex = Math.Max(0, results.SelectedIndex); results.ContainerFromIndex(results.SelectedIndex)?.Focus(); }
        };
        results.SelectionChanged += (_, _) => { if (!selecting && results.SelectedItem is TEntry entry) Select(RowOf(entry)); };
        catalogTimer.Tick += (_, _) => { catalogTimer.Stop(); PendingCatalog = LoadCatalogAsync(); };
        previewTimer.Tick += (_, _) => { previewTimer.Stop(); PendingPreview = PreviewAsync(); };
        locale.SelectionChanged += (_, _) => { if (!loading && locale.SelectedItem != null) { ScheduleCatalog(); SchedulePreview(); } };
        Document.Changed += DocumentChanged;
        // A preview link read from this row moves to the field that holds it; links into other tables open them.
        AddHandler(PreviewLinkText.LinkClickedEvent, (_, e) =>
        {
            if (e.Targets.FirstOrDefault(t => t.Table == Table?.Name && t.SourceId == selectedSourceId && editors.ContainsKey(t.Column)) is not { } own) return;
            e.Handled = true; FocusEditor(own.Column);
        });
        pane.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty && Visible && stale) Shown(); };
        DetachedFromVisualTree += (_, _) => { catalogCancellation?.Cancel(); catalogTimer.Stop(); previewTimer.Stop(); Stop(); };
    }
    private Action? layout;
    /// <summary>Lays out the list once the subclass is constructed, since its headings are virtual.</summary>
    protected void Ready() { layout?.Invoke(); layout = null; }
    /// <summary>The builder is being taken down: cancel its own work.</summary>
    protected virtual void Stop() { }

    protected bool Visible => pane.IsVisible && pane.VisualBuilderVisible;
    protected void Try(Action action) { try { action(); } catch (Exception ex) { SetStatus(ex.Message, true); } }
    protected void SetStatus(string text, bool error = false) { status.Text = text; status.Foreground = error ? Brushes.Salmon : Muted; }

    /// <summary>The builder became visible: catch up on anything that changed while it was hidden.</summary>
    public void Shown()
    {
        stale = false;
        RefreshLocales();
        if (selected != null && Table is { } table && (builtFor != table || table.Records.IndexOf(selected) < 0)) Reselect();
        else SyncEditors();
        ScheduleCatalog(true); SchedulePreview();
        if (selected == null) Dispatcher.UIThread.Post(() => search.Focus(), DispatcherPriority.Background);
        OnShown();
    }
    protected virtual void OnShown() { }

    /// <summary>Something outside the document changed (another table saved, the profile, the game data folder): resolve again.</summary>
    public void Invalidate()
    {
        if (!Visible) { stale = true; return; }
        OnInvalidate(); ScheduleCatalog(true); SchedulePreview();
    }

    private void RefreshLocales()
    {
        var locales = host.Project()?.Locales() ?? ModProject.GameLocales;
        if (locale.ItemsSource is IReadOnlyList<string> current && current.SequenceEqual(locales)) return;
        var previous = locale.SelectedItem as string ?? "enUS";
        loading = true;
        try { locale.ItemsSource = locales; locale.SelectedItem = locales.Contains(previous) ? previous : locales.Contains("enUS") ? "enUS" : locales.FirstOrDefault(); }
        finally { loading = false; }
    }
    protected string Locale => locale.SelectedItem as string ?? "enUS";

    private void DocumentChanged()
    {
        if (!Visible) { stale = true; return; }
        if (Table == null) { detail.Content = Placeholder("Apply valid source before using the Visual Builder."); builtFor = null; return; }
        // Re-parsed tables (Apply source, reload) hold new records; row insertions and deletions move the one being edited.
        if (Document.LastChangedRows == null || builtFor != Table || selected != null && Table.Records.IndexOf(selected) < 0) Reselect();
        else SyncEditors();
        ScheduleCatalog(); SchedulePreview();
    }

    /// <summary>Finds the edited record again (by identity) and rebuilds the editor when the table itself was replaced.</summary>
    private void Reselect()
    {
        var table = Table;
        if (table == null || selected == null) { BuildDetail(); return; }
        int row = table.Records.IndexOf(selected);
        if (row < 0 && selectedSourceId.Length > 0) row = Enumerable.Range(0, table.Records.Count).FirstOrDefault(i => table.Records[i].S("sourceId") == selectedSourceId, -1);
        selected = row >= 0 ? table.Records[row] : null;
        if (selected == null) { selectedSourceId = ""; SetStatus("The row being edited is no longer in the table."); }
        // Undo restores rows as new records; the same table only needs its fields refreshed, which keeps scroll and focus.
        else if (builtFor == table && editors.Count > 0) { SyncEditors(); return; }
        BuildDetail();
    }

    /// <summary>Opens a row in the builder.</summary>
    internal void Select(int row)
    {
        var table = Table; if (table == null || row < 0 || row >= table.Records.Count) return;
        if (ReferenceEquals(selected, table.Records[row]) && builtFor == table) return;
        selected = table.Records[row]; selectedSourceId = selected.S("sourceId");
        OnSelect();
        BuildDetail();
        SyncListSelection();
        SchedulePreview(immediate: true);
    }

    /// <summary>Opens the row whose name column holds a value, if there is one.</summary>
    protected bool SelectNamed(string name) => SelectWhere(NameColumn, name);

    public bool SelectWhere(string column, string value)
    {
        var table = Table; if (table == null || value.Length == 0 || table.ColumnIndex(column) < 0) return false;
        int row = Enumerable.Range(0, table.Records.Count).FirstOrDefault(i => table.Cell(i, column) == value, -1);
        if (row < 0) { SetStatus($"No row with {column} {value} in {table.Name}.", true); return false; }
        Select(row); return true;
    }

    private void SyncListSelection()
    {
        selecting = true;
        try { results.SelectedItem = Results.FirstOrDefault(e => selectedSourceId.Length > 0 && SourceIdOf(e) == selectedSourceId) ?? Results.FirstOrDefault(e => RowOf(e) == SelectedRow); }
        finally { selecting = false; }
    }

    private void AddRow()
    {
        var table = Table; Require(table != null, "Apply valid source before adding rows.");
        int row = table!.Records.Count;
        Document.InsertRows(row, 1, [NewRow(table)]);
        search.Text = "";
        Select(row);
        SetStatus("Added a row at the bottom of the table.");
        FocusEditor(NameColumn);
    }

    private void Duplicate()
    {
        int row = SelectedRow; var table = Table; if (row < 0 || table == null) return;
        int copy = Document.CloneRows([row], true);
        Document.SetCells([(copy, NameColumn, CopyName(table.Cell(row, NameColumn)))]);
        Select(copy);
        SetStatus("Duplicated to the bottom of the table. Rename it so the game can tell them apart.");
        FocusEditor(NameColumn);
    }

    // ── Catalog and search ─────────────────────────────────────────────────────────────────────────────

    protected void ScheduleCatalog(bool force = false)
    {
        if (Table is not { } table) return;
        var rows = ListRows(table);
        var context = (host.Project()?.Root, host.Profile(), Locale, host.Workspace());
        if (!force && catalog != null && context == catalogContext && rows.SequenceEqual(catalogRows)) return;
        catalogTimer.Stop(); if (catalog == null) PendingCatalog = LoadCatalogAsync(); else catalogTimer.Start();
    }

    private async Task LoadCatalogAsync()
    {
        catalogCancellation?.Cancel();
        var work = catalogCancellation = new CancellationTokenSource(); var token = work.Token;
        var project = host.Project(); var table = Table;
        if (project == null || table == null) return;
        var rows = ListRows(table); var context = (project.Root, host.Profile(), Locale, host.Workspace());
        bool fresh = catalogContext.Project != context.Root || catalogContext.Workspace != context.Item4;
        if (catalog == null) resultCount.Text = "Reading rows…";
        try
        {
            var loaded = await catalogWork.RunAsync(ct => LoadCatalog(project, context.Item2, context.Item3, rows, fresh, ct), token);
            if (token.IsCancellationRequested) return;
            catalog = loaded; catalogRows = rows; catalogContext = context;
            ApplyFilter();
            RefreshChoices();
            OnCatalogLoaded();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) resultCount.Text = "List unavailable: " + ex.Message; }
        finally { if (ReferenceEquals(catalogCancellation, work)) catalogCancellation = null; work.Dispose(); }
    }

    private void ApplyFilter()
    {
        if (catalog == null) return;
        var query = search.Text?.Trim() ?? "";
        var entries = EntriesOf(catalog);
        var matches = entries.Where(e => query.Length == 0 ? Listed(e) : VisualBuilder.Matches(SearchTextOf(e), query)).ToList();
        selecting = true;
        try { results.ItemsSource = matches; } finally { selecting = false; }
        SyncListSelection();
        int count = entries.Count(e => !IsInactive(e));
        resultCount.Text = query.Length == 0 ? $"{count:N0} rows" : $"{matches.Count:N0} of {count:N0} match";
    }

    // ── Detail ─────────────────────────────────────────────────────────────────────────────────────────

    private Control Placeholder(string? message = null)
    {
        var panel = new StackPanel { Spacing = 10, Margin = new(40, 60), MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(new TextBlock { Text = "Visual Builder", FontSize = 26, Foreground = Heading });
        panel.Children.Add(new TextBlock { Text = message ?? $"Search for {Noun} on the left and pick it to start building. You'll see it as it appears in game, and every value is editable in place.", TextWrapping = TextWrapping.Wrap, Foreground = WhiteBrush, FontSize = 14 });
        panel.Children.Add(new TextBlock { Text = "Edits go straight into this table: Undo, Save and the Table and Source views all see them.", TextWrapping = TextWrapping.Wrap, Foreground = Muted });
        return panel;
    }

    protected void BuildDetail()
    {
        editors.Clear();
        var table = Table; int row = SelectedRow;
        builtFor = table;
        if (table == null || row < 0) { detail.Content = Placeholder(table == null ? "Apply valid source before using the Visual Builder." : null); OnBuiltEmpty(); return; }
        loading = true;
        try
        {
            var root = new StackPanel { Spacing = 14, Margin = new(20, 14, 24, 28), MaxWidth = 1180, HorizontalAlignment = HorizontalAlignment.Stretch };
            root.Children.Add(Header());
            BuildCards(root, table);
            root.Children.Add(OtherColumns(table));
            detail.Content = root;
            SyncEditors();
        }
        finally { loading = false; }
        detailScroll.Offset = default;
    }
    /// <summary>The editor was cleared (no row picked).</summary>
    protected virtual void OnBuiltEmpty() { }

    private Control Header()
    {
        var header = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Top };
        var openTable = new Button { Content = "Show in table", Padding = new(10, 4), MinHeight = 0, FontSize = 12 };
        ToolTip.SetTip(openTable, "Switch to the Table view on this row");
        openTable.Click += (_, _) => Try(() => { pane.Jump(SelectedRow, NameColumn); pane.NoteViewChoice("table"); });
        var duplicate = new Button { Content = "Duplicate", Padding = new(10, 4), MinHeight = 0, FontSize = 12 };
        ToolTip.SetTip(duplicate, "Copy this row to a new row at the bottom of the table and edit the copy");
        duplicate.Click += (_, _) => Try(Duplicate);
        buttons.Children.Add(duplicate); buttons.Children.Add(openTable);
        DockPanel.SetDock(buttons, Dock.Right); header.Children.Add(buttons);
        var text = new StackPanel { Spacing = 2 };
        title = new SelectableTextBlock { FontSize = 24, Foreground = TitleBrush, FontFamily = TooltipFont, TextWrapping = TextWrapping.Wrap };
        subtitle = new SelectableTextBlock { FontSize = 12, Foreground = Muted, TextWrapping = TextWrapping.Wrap };
        status = new TextBlock { FontSize = 12, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Text = "Edits apply immediately · Save writes them to disk · Undo is on the toolbar." };
        text.Children.Add(title); text.Children.Add(subtitle); text.Children.Add(status);
        header.Children.Add(text);
        UpdateTitle();
        return header;
    }

    protected void UpdateTitle()
    {
        var table = Table; int row = SelectedRow; if (table == null || row < 0) return;
        var name = table.Cell(row, NameColumn);
        title.Text = TitleText(table, row);
        subtitle.Text = $"{table.Name} · row {row}" + (name.Length > 0 ? $" · {NameColumn} \"{name}\"" : "") + (Document.IsDirty ? " · unsaved edits" : "");
    }

    protected static Control Labeled(string label, Control input, string? tip = null)
    {
        var panel = new StackPanel { Spacing = 3 };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = Muted });
        panel.Children.Add(input);
        if (tip != null) ToolTip.SetTip(panel, tip);
        return panel;
    }

    protected static Border Card(string heading, Control body, Control? headerRight = null, string? note = null)
    {
        var panel = new StackPanel { Spacing = 10 };
        var top = new DockPanel();
        if (headerRight != null) { DockPanel.SetDock(headerRight, Dock.Right); top.Children.Add(headerRight); }
        var titles = new StackPanel { Spacing = 2 };
        titles.Children.Add(new TextBlock { Text = heading.ToUpperInvariant(), Foreground = Heading, FontSize = 11, FontWeight = FontWeight.SemiBold });
        if (note != null) titles.Children.Add(new TextBlock { Text = note, Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        top.Children.Add(titles);
        panel.Children.Add(top); panel.Children.Add(body);
        return new Border { Background = CardBackground, BorderBrush = CardBorder, BorderThickness = new(1), CornerRadius = new(6), Padding = new(16, 12), Child = panel };
    }

    protected static Control Option(string code, string description)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = code, FontWeight = FontWeight.SemiBold, MinWidth = 70 });
        panel.Children.Add(new TextBlock { Text = description, Foreground = Muted, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 420 });
        return panel;
    }

    /// <summary>Every column no card shows, as plain fields in a closed expander.</summary>
    private Control OtherColumns(TableData table)
    {
        var shown = editors.Keys.Concat(HandledColumns(table)).ToHashSet(StringComparer.Ordinal);
        var rest = table.Columns.Where(c => !shown.Contains(c) && !c.StartsWith('*')).ToArray();
        var grid = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 12, LineSpacing = 8 };
        foreach (var column in rest) grid.Children.Add(Labeled(column, Text(column, "", 170), ColumnGuide.Find(table.Name, column)?.Description ?? column));
        return new Expander { Header = $"More columns ({rest.Length}){MoreColumnsHint}", Content = grid, HorizontalAlignment = HorizontalAlignment.Stretch };
    }

    // ── Editors ────────────────────────────────────────────────────────────────────────────────────────

    protected TextBox Text(string column, string placeholder, double width)
    {
        var box = new TextBox { PlaceholderText = placeholder, Width = width, MinHeight = 30, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(box, column);
        GroupEdits(box);
        box.TextChanged += (_, _) => Commit(column, box.Text ?? "");
        editors[column] = box;
        return box;
    }

    /// <summary>A labelled text field for a column, when the table has it; its data-guide description is the tooltip.</summary>
    protected Control? Field(TableData table, string column, string label, double width = 110, string placeholder = "")
    {
        if (table.ColumnIndex(column) < 0) return null;
        return Labeled(label, Text(column, placeholder, width), ColumnGuide.Find(table.Name, column)?.Description is { } guide ? $"{column}: {guide}" : column);
    }

    /// <summary>
    /// A code picker: typing filters the choices by code or description. A known code (or a cleared field) is written as
    /// soon as it is typed, so the preview follows; anything else is written when the field is left, since modded tables
    /// may use codes this project's tables do not list.
    /// </summary>
    protected AutoCompleteBox Choice<T>(string column, string placeholder, double width, Func<IEnumerable<T>> items, Func<T, string> searchText, Func<T, Control> template, Func<string, bool> known) where T : class
    {
        var box = new AutoCompleteBox
        {
            PlaceholderText = placeholder, Width = width, MinHeight = 30, FilterMode = AutoCompleteFilterMode.Custom, MinimumPrefixLength = 1, MaxDropDownHeight = 340,
            ItemFilter = (text, item) => item is T value && VisualBuilder.Matches(searchText(value), text ?? ""),
            ItemTemplate = new FuncDataTemplate<T>((value, _) => value == null ? new TextBlock() : template(value)),
            ItemsSource = items()
        };
        box.Tag = (Func<IEnumerable>)(() => items());
        AutomationProperties.SetName(box, column);
        GroupEdits(box);
        box.TextChanged += (_, _) => { var text = box.Text ?? ""; if (text.Length == 0 || known(text)) Commit(column, text); };
        box.LostFocus += (_, _) => { if (!box.IsKeyboardFocusWithin) Commit(column, (box.Text ?? "").Trim()); };
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(column, (box.Text ?? "").Trim()); };
        editors[column] = box;
        return box;
    }

    /// <summary>A picker over plain names (sounds, missiles, skills): each shows as itself.</summary>
    protected AutoCompleteBox NameChoice(string column, string placeholder, double width, Func<IEnumerable<string>> names) =>
        Choice(column, placeholder, width, () => names().Select(n => new NamedChoice(n)), n => n.Name.ToLowerInvariant(), n => new TextBlock { Text = n.Name }, text => names().Contains(text));
    private sealed record NamedChoice(string Name) { public override string ToString() => Name; }

    protected Control Toggle(string column, string label, string tip)
    {
        var box = new CheckBox { Content = label, VerticalAlignment = VerticalAlignment.Bottom, Margin = new(0, 14, 0, 0) };
        ToolTip.SetTip(box, tip + $" ({column})");
        AutomationProperties.SetName(box, column);
        box.IsCheckedChanged += (_, _) => { if (!loading) Commit(column, box.IsChecked == true ? "1" : "0"); };
        editors[column] = box;
        return box;
    }

    /// <summary>A numbered choice written as its index (add func, Trans).</summary>
    protected ComboBox Numbered(string column, double width, params string[] choices)
    {
        var box = new ComboBox { Width = width, ItemsSource = choices };
        AutomationProperties.SetName(box, column);
        box.SelectionChanged += (_, _) => { if (!loading && box.SelectedIndex >= 0) Commit(column, box.SelectedIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)); };
        editors[column] = box;
        return box;
    }

    /// <summary>Keystrokes in one field are one undo step: the group opens when the field takes focus and closes when it leaves.</summary>
    private void GroupEdits(Control input)
    {
        bool open = false;
        void Close() { if (open) { open = false; Document.EndEditGroup(); } }
        input.GotFocus += (_, _) => { if (!open) { open = true; Document.BeginEditGroup(); } };
        input.LostFocus += (_, _) => { if (!input.IsKeyboardFocusWithin) Close(); };
        input.DetachedFromVisualTree += (_, _) => Close();
    }

    protected void Commit(string column, string value)
    {
        if (loading) return;
        var table = Table; int row = SelectedRow;
        if (table == null || row < 0 || table.ColumnIndex(column) < 0 || table.Cell(row, column) == value) return;
        try
        {
            Document.SetCells([(row, column, value)]);
            pane.RefreshRowValues(row);
            SetStatus($"{column} = {(value.Length == 0 ? "(empty)" : value)} · edits apply immediately · Save writes them to disk.");
        }
        catch (Exception ex) { SetStatus(ex.Message, true); SyncEditors(); }
    }

    /// <summary>Writes several cells as one undo step.</summary>
    protected void CommitAll(IEnumerable<(string Column, string Value)> changes)
    {
        int row = SelectedRow; if (row < 0) return;
        Try(() => { Document.SetCells(changes.Select(c => (row, c.Column, c.Value))); pane.RefreshRowValues(row); });
    }

    /// <summary>Shows the row's current values in every field: after an undo, a table edit, or a value refused by the document.</summary>
    protected void SyncEditors()
    {
        var table = Table; int row = SelectedRow; if (table == null || row < 0) return;
        bool wasLoading = loading; loading = true;
        try
        {
            foreach (var (column, editor) in editors)
            {
                var value = table.Cell(row, column);
                switch (editor)
                {
                    case TextBox box when box.Text != value: box.Text = value; break;
                    // A code being typed is not overwritten mid-word by an unrelated refresh.
                    case AutoCompleteBox choice when choice.Text != value && !choice.IsKeyboardFocusWithin: choice.Text = value; break;
                    case CheckBox check when check.IsChecked != (value == "1"): check.IsChecked = value == "1"; break;
                    case ComboBox combo:
                        int picked = int.TryParse(value.Length == 0 ? "0" : value, out var number) && number >= 0 && number < combo.ItemCount ? number : -1;
                        if (combo.SelectedIndex != picked) combo.SelectedIndex = picked;
                        break;
                }
            }
            OnSynced(table, row);
            UpdateTitle();
        }
        finally { loading = wasLoading; }
    }

    /// <summary>Choice lists follow the catalog, which loads after the editor is built.</summary>
    protected void RefreshChoices()
    {
        foreach (var box in editors.Values.OfType<AutoCompleteBox>())
            if (box.Tag is Func<IEnumerable> items) box.ItemsSource = items();
    }

    protected void FocusEditor(string column)
    {
        if (!editors.TryGetValue(column, out var editor)) return;
        if (editor.FindAncestorOfType<Expander>() is { } expander) expander.IsExpanded = true;
        Reveal(column);
        Dispatcher.UIThread.Post(() =>
        {
            editor.BringIntoView(); editor.Focus();
            if (editor is TextBox box) box.SelectAll();
            // A brief outline shows which field the link landed on.
            if (editor is TemplatedControl templated)
            {
                var (brush, thickness) = (templated.BorderBrush, templated.BorderThickness);
                templated.BorderBrush = Highlight; templated.BorderThickness = new(2);
                DispatcherTimer.RunOnce(() => { templated.BorderBrush = brush; templated.BorderThickness = thickness; }, TimeSpan.FromMilliseconds(1200));
            }
        }, DispatcherPriority.Background);
    }

    protected void SchedulePreview(bool immediate = false)
    {
        if (selected == null) return;
        previewTimer.Stop();
        if (immediate) PendingPreview = PreviewAsync(); else previewTimer.Start();
    }

    /// <summary>The picked row's cells, copied for a worker: live records are never shared with one.</summary>
    protected JsonObject? SelectedRecord() => SelectedRow is var row and >= 0 ? (JsonObject)Table!.Records[row]!.DeepClone() : null;
}

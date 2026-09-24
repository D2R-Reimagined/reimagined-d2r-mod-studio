using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ModStudio.Core;
using static ModStudio.Core.Storage;

namespace ModStudio.App;

/// <summary>What the Visual Builder reads from the window: the open project, the profile, the workspace revision and where base-game files live.</summary>
internal sealed record VisualBuilderHost(Func<ModProject?> Project, Func<string> Profile, Func<int> Workspace, Func<IReadOnlyList<string>> GameData,
    Func<EditorPane, string?> DirtyDependency, Func<Task> ChooseGameData);

/// <summary>
/// The Visual Builder view of a unique or set item table: search for an item, then edit it beside its in-game picture and
/// tooltip. Every field writes straight into the pane's document, so edits share its undo history and dirty state with the
/// table and source views; the tooltip re-resolves after each edit, and its numbers jump to the field they were read from.
/// </summary>
internal sealed class VisualBuilderView : Grid
{
    private static readonly IBrush UniqueBrush = new SolidColorBrush(Color.Parse("#C7B377")), SetBrush = new SolidColorBrush(Color.Parse("#3DDB3D")),
        MagicBrush = new SolidColorBrush(Color.Parse("#7878FF")), MagicLink = new SolidColorBrush(Color.Parse("#A3A3FF")), WhiteBrush = new SolidColorBrush(Color.Parse("#E6E6E6")),
        Muted = new SolidColorBrush(Color.Parse("#A8A29A")), Heading = new SolidColorBrush(Color.Parse("#D8BC86")), CardBackground = new SolidColorBrush(Color.Parse("#1D1E20")),
        CardBorder = new SolidColorBrush(Color.Parse("#34363A")), Highlight = new SolidColorBrush(Color.Parse("#D8BC86"));
    private static readonly FontFamily TooltipFont = new("Palatino Linotype, Book Antiqua, Georgia, serif");
    /// <summary>Common properties offered as one-click additions, when the project's properties table has them.</summary>
    private static readonly (string Code, string Label)[] QuickProperties = [
        ("dmg%", "Enhanced damage"), ("dmg-norm", "+Damage"), ("ac%", "Enhanced defense"), ("ac", "+Defense"), ("allskills", "All skills"), ("res-all", "All resistances"),
        ("hp", "Life"), ("mana", "Mana"), ("str", "Strength"), ("dex", "Dexterity"), ("swing2", "Attack speed"), ("cast2", "Cast rate"), ("balance2", "Hit recovery"),
        ("move2", "Run/walk"), ("lifesteal", "Life leech"), ("manasteal", "Mana leech"), ("mag%", "Magic find"), ("sock", "Sockets")];

    private readonly EditorPane pane;
    private readonly VisualBuilderHost host;
    private Document Document => pane.Document;
    private TableData? Table => Document.PendingSource ? null : Document.Table;
    private bool IsSetTable => Table?.Name == "setitems";
    private string CodeColumn => IsSetTable ? "item" : "code";

    private readonly TextBox search = new() { PlaceholderText = "Search by name, key, base or code" };
    private readonly ListBox results = new() { SelectionMode = SelectionMode.Single };
    private readonly TextBlock resultCount = new() { FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap };
    private readonly ContentControl detail = new();
    private readonly ScrollViewer detailScroll;
    private readonly ComboBox locale = new() { MinWidth = 90 };
    private readonly NumericUpDown characterLevel = new() { Minimum = 1, Maximum = 99, Value = 80, Width = 120, FormatString = "0" };

    private VisualBuilderCatalog? catalog;
    private BuilderRow[] catalogRows = [];
    private (string? Project, string Profile, string Locale, int Workspace) catalogContext;
    private readonly VisualBuilderResolver catalogResolver = new();
    private readonly PreviewWorkQueue catalogWork = new();
    private CancellationTokenSource? catalogCancellation;
    private readonly DispatcherTimer catalogTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };

    private readonly ItemPreviewResolver previewResolver = new();
    private readonly PreviewWorkQueue previewWork = new();
    private CancellationTokenSource? previewCancellation;
    private readonly DispatcherTimer previewTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private (string? Project, int Workspace) previewContext;

    private CancellationTokenSource? spriteCancellation;
    private string? spriteKey;
    private WriteableBitmap? spriteBitmap;

    /// <summary>The record being edited. Rows are followed by record, since indexes shift when rows are added or removed above.</summary>
    private System.Text.Json.Nodes.JsonNode? selected;
    private string selectedSourceId = "";
    private TableData? builtFor;
    private bool loading, selecting, stale = true;
    private readonly Dictionary<string, Control> editors = new(StringComparer.Ordinal);
    private readonly List<SlotView> slots = [];
    private readonly HashSet<string> revealed = new(StringComparer.Ordinal);
    private ContentControl tooltipHost = new(), spriteHost = new();
    private SelectableTextBlock title = new(), subtitle = new(), issues = new();
    private TextBlock status = new(), spriteNote = new(), gameName = new(), baseName = new();
    private WrapPanel quickAdd = new();
    private TextBlock propertyCount = new(), bonusCount = new();

    private sealed record SlotView(PropertySlot Slot, Grid Row, ContentControl Line, AutoCompleteBox Code, TextBox Parameter, TextBox Min, TextBox Max);

    internal Task PendingPreview { get; private set; } = Task.CompletedTask;
    internal Task PendingCatalog { get; private set; } = Task.CompletedTask;
    internal Task PendingSprite { get; private set; } = Task.CompletedTask;
    internal ItemPreviewResult? LastPreview { get; private set; }
    internal ItemSprite? LastSprite { get; private set; }
    internal IReadOnlyList<BuilderEntry> Results => results.ItemsSource as IReadOnlyList<BuilderEntry> ?? [];
    internal TextBox Search => search;
    internal int SelectedRow => selected == null || Table is not { } table ? -1 : table.Records.IndexOf(selected);
    internal Control? Editor(string column) => editors.GetValueOrDefault(column);
    internal string StatusText => status.Text ?? "";
    internal void ScrollToTop() => detailScroll.Offset = default;

    public VisualBuilderView(EditorPane pane, VisualBuilderHost host)
    {
        this.pane = pane; this.host = host;
        ColumnDefinitions = new("280,4,*");
        // The search column: every item in the table, narrowed as you type.
        var side = new DockPanel { Margin = new(10, 10, 6, 10) };
        var heading = new TextBlock { Text = IsSetTable ? "SET ITEMS" : "UNIQUE ITEMS", Foreground = Heading, FontSize = 11, Margin = new(2, 0, 0, 6) };
        DockPanel.SetDock(heading, Dock.Top); side.Children.Add(heading);
        search.PlaceholderText = IsSetTable ? "Search set items by name, set, base or code" : "Search uniques by name, key, base or code";
        AutomationProperties.SetName(search, "Visual Builder search");
        DockPanel.SetDock(search, Dock.Top); side.Children.Add(search);
        var sideTools = new DockPanel { Margin = new(0, 6, 0, 6) };
        var add = new Button { Content = IsSetTable ? "+ New set item" : "+ New unique", Padding = new(8, 3), MinHeight = 0, FontSize = 12, Margin = new(0) };
        ToolTip.SetTip(add, "Adds a row at the bottom of the table and opens it here. Undo removes it again.");
        add.Click += (_, _) => Try(AddItem);
        DockPanel.SetDock(add, Dock.Right); sideTools.Children.Add(add);
        resultCount.VerticalAlignment = VerticalAlignment.Center; sideTools.Children.Add(resultCount);
        DockPanel.SetDock(sideTools, Dock.Top); side.Children.Add(sideTools);
        results.ItemTemplate = new FuncDataTemplate<BuilderEntry>((entry, _) => entry == null ? new TextBlock() : EntryView(entry));
        results.Background = Brushes.Transparent;
        side.Children.Add(results);
        Children.Add(side);
        var splitter = new GridSplitter { Background = CardBorder, ResizeDirection = GridResizeDirection.Columns }; SetColumn(splitter, 1); Children.Add(splitter);
        detailScroll = new ScrollViewer { Content = detail, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        SetColumn(detailScroll, 2); Children.Add(detailScroll);
        detail.Content = Placeholder();

        search.TextChanged += (_, _) => ApplyFilter();
        search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Results.FirstOrDefault(r => !r.Inactive) is { } first) { e.Handled = true; Select(first.Row); }
            else if (e.Key == Key.Down && Results.Count > 0) { e.Handled = true; results.SelectedIndex = Math.Max(0, results.SelectedIndex); results.ContainerFromIndex(results.SelectedIndex)?.Focus(); }
        };
        results.SelectionChanged += (_, _) => { if (!selecting && results.SelectedItem is BuilderEntry entry) Select(entry.Row); };
        catalogTimer.Tick += (_, _) => { catalogTimer.Stop(); PendingCatalog = LoadCatalogAsync(); };
        previewTimer.Tick += (_, _) => { previewTimer.Stop(); PendingPreview = PreviewAsync(); };
        characterLevel.ValueChanged += (_, _) => SchedulePreview();
        locale.SelectionChanged += (_, _) => { if (!loading && locale.SelectedItem != null) { ScheduleCatalog(); SchedulePreview(); } };
        Document.Changed += DocumentChanged;
        // A link in the tooltip read from this row moves to the field that holds it; links into other tables open them.
        AddHandler(PreviewLinkText.LinkClickedEvent, (_, e) =>
        {
            if (e.Targets.FirstOrDefault(t => t.Table == Table?.Name && t.SourceId == selectedSourceId && editors.ContainsKey(t.Column)) is not { } own) return;
            e.Handled = true; FocusEditor(own.Column);
        });
        pane.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty && Visible && stale) Shown(); };
        DetachedFromVisualTree += (_, _) => { catalogCancellation?.Cancel(); previewCancellation?.Cancel(); spriteCancellation?.Cancel(); catalogTimer.Stop(); previewTimer.Stop(); };
    }

    private bool Visible => pane.IsVisible && pane.VisualBuilderVisible;
    private void Try(Action action) { try { action(); } catch (Exception ex) { SetStatus(ex.Message, true); } }
    private void SetStatus(string text, bool error = false) { status.Text = text; status.Foreground = error ? Brushes.Salmon : Muted; }

    /// <summary>The builder became visible: catch up on anything that changed while it was hidden.</summary>
    public void Shown()
    {
        stale = false;
        RefreshLocales();
        if (selected != null && Table is { } table && (builtFor != table || table.Records.IndexOf(selected) < 0)) Reselect();
        else SyncEditors();
        ScheduleCatalog(true); SchedulePreview();
        if (selected == null) Dispatcher.UIThread.Post(() => search.Focus(), DispatcherPriority.Background);
    }

    /// <summary>Something outside the document changed (another table saved, the profile, the game data folder): resolve again.</summary>
    public void Invalidate()
    {
        if (!Visible) { stale = true; return; }
        spriteKey = null; ScheduleCatalog(true); SchedulePreview();
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
    private string Locale => locale.SelectedItem as string ?? "enUS";

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
        if (selected == null) { selectedSourceId = ""; SetStatus("The item being edited is no longer in the table."); }
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
        revealed.Clear(); LastPreview = null; LastSprite = null; spriteKey = null;
        BuildDetail();
        SyncListSelection();
        SchedulePreview(immediate: true);
    }

    private void SyncListSelection()
    {
        selecting = true;
        try { results.SelectedItem = Results.FirstOrDefault(e => e.SourceId == selectedSourceId && selectedSourceId.Length > 0) ?? Results.FirstOrDefault(e => e.Row == SelectedRow); }
        finally { selecting = false; }
    }

    private void AddItem()
    {
        var table = Table; Storage.Require(table != null, "Apply valid source before adding items.");
        var fields = new System.Text.Json.Nodes.JsonObject { ["index"] = IsSetTable ? "New Set Item" : "New Unique", ["lvl"] = "1", ["lvl req"] = "1" };
        if (table!.ColumnIndex("rarity") >= 0) fields["rarity"] = "1";
        if (table.ColumnIndex("spawnable") >= 0) fields["spawnable"] = "1";
        int row = table.Records.Count;
        Document.InsertRows(row, 1, [fields]);
        search.Text = "";
        Select(row);
        SetStatus("Added a row at the bottom of the table. Pick a base item, then add properties.");
        FocusEditor(CodeColumn);
    }

    private void Duplicate()
    {
        int row = SelectedRow; var table = Table; if (row < 0 || table == null) return;
        int copy = Document.CloneRows([row], true);
        Document.SetCells([(copy, "index", table.Cell(row, "index") + " Copy")]);
        Select(copy);
        SetStatus("Duplicated to the bottom of the table. Rename its key so the game can tell them apart.");
        FocusEditor("index");
    }

    // ── Catalog and search ─────────────────────────────────────────────────────────────────────────────

    private void ScheduleCatalog(bool force = false)
    {
        if (Table is not { } table) return;
        var rows = VisualBuilder.Rows(table);
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
        var rows = VisualBuilder.Rows(table); var context = (project.Root, host.Profile(), Locale, host.Workspace());
        if (catalog == null) resultCount.Text = "Reading items…";
        try
        {
            if (catalogContext.Project != context.Root || catalogContext.Workspace != context.Item4) catalogResolver.Clear();
            var loaded = await catalogWork.RunAsync(ct => catalogResolver.Catalog(project, context.Item2, context.Item3, rows, ct), token);
            if (token.IsCancellationRequested) return;
            catalog = loaded; catalogRows = rows; catalogContext = context;
            ApplyFilter();
            RefreshChoices();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) resultCount.Text = "Items unavailable: " + ex.Message; }
        finally { if (ReferenceEquals(catalogCancellation, work)) catalogCancellation = null; work.Dispose(); }
    }

    private void ApplyFilter()
    {
        if (catalog == null) return;
        var query = search.Text?.Trim() ?? "";
        var matches = catalog.Entries.Where(e => query.Length == 0 ? !e.Inactive || e.Index.Length > 0 : VisualBuilder.Matches(e.SearchText, query)).ToList();
        selecting = true;
        try { results.ItemsSource = matches; } finally { selecting = false; }
        SyncListSelection();
        int items = catalog.Entries.Count(e => !e.Inactive);
        resultCount.Text = query.Length == 0 ? $"{items:N0} items" : $"{matches.Count:N0} of {items:N0} match";
    }

    private Control EntryView(BuilderEntry entry)
    {
        var panel = new StackPanel { Margin = new(2, 3) };
        var name = entry.Name.Length > 0 ? entry.Name : entry.Index.Length > 0 ? entry.Index : $"Row {entry.Row}";
        panel.Children.Add(new TextBlock { Text = name, FontSize = 13, Foreground = entry.Inactive ? Muted : IsSetTable ? SetBrush : UniqueBrush, TextTrimming = TextTrimming.CharacterEllipsis, FontStyle = entry.Inactive ? FontStyle.Italic : FontStyle.Normal });
        var detailText = entry.Inactive ? "no base item · header row" : string.Join(" · ", new[] { entry.BaseName.Length > 0 ? entry.BaseName : entry.Code, entry.RequiredLevel.Length > 0 ? "lvl " + entry.RequiredLevel : "", IsSetTable ? entry.Set : "" }.Where(s => s.Length > 0));
        panel.Children.Add(new TextBlock { Text = detailText, FontSize = 11, Foreground = Muted, TextTrimming = TextTrimming.CharacterEllipsis });
        if (entry.Name != entry.Index && entry.Index.Length > 0) ToolTip.SetTip(panel, $"{entry.Index} · row {entry.Row}");
        return panel;
    }

    // ── Detail ─────────────────────────────────────────────────────────────────────────────────────────

    private Control Placeholder(string? message = null)
    {
        var panel = new StackPanel { Spacing = 10, Margin = new(40, 60), MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(new TextBlock { Text = "Visual Builder", FontSize = 26, Foreground = Heading });
        panel.Children.Add(new TextBlock { Text = message ?? $"Search for {(IsSetTable ? "a set item" : "a unique")} on the left and pick it to start building. You'll see it as it appears in game, with its picture, and every stat is editable in place.", TextWrapping = TextWrapping.Wrap, Foreground = WhiteBrush, FontSize = 14 });
        panel.Children.Add(new TextBlock { Text = "Edits go straight into this table: Undo, Save and the Table and Source views all see them.", TextWrapping = TextWrapping.Wrap, Foreground = Muted });
        return panel;
    }

    private void BuildDetail()
    {
        editors.Clear(); slots.Clear();
        var table = Table; int row = SelectedRow;
        builtFor = table;
        if (table == null || row < 0) { detail.Content = Placeholder(table == null ? "Apply valid source before using the Visual Builder." : null); return; }
        loading = true;
        try
        {
            var root = new StackPanel { Spacing = 14, Margin = new(20, 14, 24, 28), MaxWidth = 1180, HorizontalAlignment = HorizontalAlignment.Stretch };
            root.Children.Add(Header());
            root.Children.Add(Hero());
            root.Children.Add(IdentityCard(table));
            root.Children.Add(PropertiesCard(table, false));
            if (IsSetTable) root.Children.Add(PropertiesCard(table, true));
            root.Children.Add(OtherColumns(table));
            detail.Content = root;
            SyncEditors();
        }
        finally { loading = false; }
        detailScroll.Offset = default;
    }

    private Control Header()
    {
        var header = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Top };
        var openTable = new Button { Content = "Show in table", Padding = new(10, 4), MinHeight = 0, FontSize = 12 };
        ToolTip.SetTip(openTable, "Switch to the Table view on this row");
        openTable.Click += (_, _) => Try(() => pane.Jump(SelectedRow, "index"));
        var duplicate = new Button { Content = "Duplicate", Padding = new(10, 4), MinHeight = 0, FontSize = 12 };
        ToolTip.SetTip(duplicate, "Copy this item to a new row at the bottom of the table and edit the copy");
        duplicate.Click += (_, _) => Try(Duplicate);
        buttons.Children.Add(duplicate); buttons.Children.Add(openTable);
        DockPanel.SetDock(buttons, Dock.Right); header.Children.Add(buttons);
        var text = new StackPanel { Spacing = 2 };
        title = new SelectableTextBlock { FontSize = 24, Foreground = IsSetTable ? SetBrush : UniqueBrush, FontFamily = TooltipFont, TextWrapping = TextWrapping.Wrap };
        subtitle = new SelectableTextBlock { FontSize = 12, Foreground = Muted, TextWrapping = TextWrapping.Wrap };
        status = new TextBlock { FontSize = 12, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Text = "Edits apply immediately · Save writes them to disk · Undo is on the toolbar." };
        text.Children.Add(title); text.Children.Add(subtitle); text.Children.Add(status);
        header.Children.Add(text);
        UpdateTitle();
        return header;
    }

    private void UpdateTitle()
    {
        var table = Table; int row = SelectedRow; if (table == null || row < 0) return;
        var index = table.Cell(row, "index");
        title.Text = LastPreview?.Tooltip != null ? LastPreview.Name : index.Length > 0 ? index : $"Row {row}";
        subtitle.Text = $"{table.Name} · row {row}" + (index.Length > 0 ? $" · key \"{index}\"" : "") + (Document.IsDirty ? " · unsaved edits" : "");
    }

    /// <summary>The item's picture beside its tooltip, with the tooltip's options underneath.</summary>
    private Control Hero()
    {
        var hero = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 20 };
        var pictureColumn = new StackPanel { Spacing = 6, Width = 214 };
        spriteHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        pictureColumn.Children.Add(new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#1A1712"), 0), new GradientStop(Color.Parse("#0C0B09"), 1) }
            },
            BorderBrush = new SolidColorBrush(Color.Parse("#4A4030")), BorderThickness = new(1), CornerRadius = new(4), MinHeight = 214, Padding = new(8, 16), Child = spriteHost
        });
        spriteNote = new TextBlock { FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap };
        pictureColumn.Children.Add(spriteNote);
        spriteHost.Content = new TextBlock { Text = "Loading picture…", Foreground = Muted, FontSize = 12 };
        hero.Children.Add(pictureColumn);
        var tooltipColumn = new StackPanel { Spacing = 8 }; SetColumn(tooltipColumn, 1);
        tooltipHost = new ContentControl { HorizontalAlignment = HorizontalAlignment.Left, Content = new TextBlock { Text = "Resolving item…", Foreground = Muted } };
        tooltipColumn.Children.Add(tooltipHost);
        var options = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 10, LineSpacing = 6 };
        // The level and locale pickers outlive a rebuild of the editor; they move into the new one.
        foreach (var shared in new Control[] { characterLevel, locale }) (shared.Parent as Panel)?.Children.Remove(shared);
        options.Children.Add(Labeled("Character level", characterLevel, "Per-level properties are shown for this level"));
        options.Children.Add(Labeled("Locale", locale, "Language the names and property text are shown in"));
        tooltipColumn.Children.Add(options);
        tooltipColumn.Children.Add(new TextBlock { Text = "Click a number in the tooltip to jump to the field it comes from. Ranges show as (min–max).", FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap });
        issues = new SelectableTextBlock { Foreground = Brushes.Salmon, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        tooltipColumn.Children.Add(issues);
        hero.Children.Add(tooltipColumn);
        return hero;
    }

    private static Control Labeled(string label, Control input, string? tip = null)
    {
        var panel = new StackPanel { Spacing = 3 };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = Muted });
        panel.Children.Add(input);
        if (tip != null) ToolTip.SetTip(panel, tip);
        return panel;
    }

    private static Border Card(string heading, Control body, Control? headerRight = null, string? note = null)
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

    private Control IdentityCard(TableData table)
    {
        var grid = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 14, LineSpacing = 10 };
        void Add(string column, string label, Control input, string? tip = null) { if (table.ColumnIndex(column) >= 0) grid.Children.Add(Labeled(label, input, tip ?? ColumnGuide.Find(table.Name, column)?.Description)); }
        var nameColumn = new StackPanel { Spacing = 3 };
        if (table.ColumnIndex("index") >= 0)
        {
            var name = Text("index", "String key", 260);
            gameName = new TextBlock { FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap, MaxWidth = 260 };
            nameColumn.Children.Add(new TextBlock { Text = "Name (string key)", FontSize = 11, Foreground = Muted });
            nameColumn.Children.Add(name); nameColumn.Children.Add(gameName);
            ToolTip.SetTip(nameColumn, "The key the game looks the item's name up by in the string tables. It also names the item's HD picture in uniques.json / sets.json.");
            grid.Children.Add(nameColumn);
        }
        if (table.ColumnIndex(CodeColumn) >= 0)
        {
            var baseColumn = new StackPanel { Spacing = 3 };
            baseColumn.Children.Add(new TextBlock { Text = "Base item", FontSize = 11, Foreground = Muted });
            baseColumn.Children.Add(Choice(CodeColumn, "Base code", 220, () => catalog?.Bases ?? [], b => b.SearchText,
                b => Option(b.Code, b.Name + " · " + b.Table + (b.RequiredLevel.Length > 0 ? " · lvl " + b.RequiredLevel : "")), code => catalog?.Bases.Any(b => b.Code == code) == true));
            baseName = new TextBlock { FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap, MaxWidth = 220 };
            baseColumn.Children.Add(baseName);
            ToolTip.SetTip(baseColumn, "The base item this is made from (weapons, armor or misc code). It sets the picture, damage or defense, and base requirements. Type a code or part of its name.");
            grid.Children.Add(baseColumn);
        }
        if (IsSetTable) Add("set", "Set", Choice("set", "Set key", 240, () => catalog?.Sets ?? [], s => s.SearchText, s => Option(s.Index, s.Name), key => catalog?.Sets.Any(s => s.Index == key) == true), "The set (sets.txt index) this item belongs to.");
        Add("lvl", "Item level", Text("lvl", "", 100), "Item level: the lowest monster/area level that can drop it, and its affix level.");
        Add("lvl req", "Required level", Text("lvl req", "", 100), "Character level needed to equip it. The base item's own requirement applies if higher.");
        Add("rarity", "Rarity", Text("rarity", "", 90), "Weight against the other items of the same base when this quality rolls.");
        foreach (var (column, label, tip) in new[] { ("spawnable", "Can drop", "Unchecked items never drop; they can still be made in the cube."), ("disabled", "Disabled", "Disabled items are not generated at all."), ("nolimit", "No limit", "Can drop again even if already found in this game.") })
            if (table.ColumnIndex(column) >= 0) grid.Children.Add(Toggle(column, label, tip));
        return Card(IsSetTable ? "Item" : "Unique", grid);
    }

    private Control Toggle(string column, string label, string tip)
    {
        var box = new CheckBox { Content = label, VerticalAlignment = VerticalAlignment.Bottom, Margin = new(0, 14, 0, 0) };
        ToolTip.SetTip(box, tip + $" ({column})");
        AutomationProperties.SetName(box, column);
        box.IsCheckedChanged += (_, _) => { if (!loading) Commit(column, box.IsChecked == true ? "1" : "0"); };
        editors[column] = box;
        return box;
    }

    private static Control Option(string code, string description)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = code, FontWeight = FontWeight.SemiBold, MinWidth = 70 });
        panel.Children.Add(new TextBlock { Text = description, Foreground = Muted, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 420 });
        return panel;
    }

    private Control PropertiesCard(TableData table, bool bonuses)
    {
        var tableSlots = VisualBuilder.Slots(table.Name, table.Columns).Where(s => s.SetBonus == bonuses).ToArray();
        var list = new StackPanel { Spacing = 4 };
        list.Children.Add(SlotHeader(bonuses));
        foreach (var slot in tableSlots) { var view = SlotRow(slot, bonuses); slots.Add(view); list.Children.Add(view.Row); }
        var add = new Button { Content = bonuses ? "+ Add set bonus" : "+ Add property", Padding = new(10, 4), MinHeight = 0, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 4, 0, 0) };
        add.Click += (_, _) => Try(() => RevealSlot(bonuses, null));
        list.Children.Add(add);
        var count = new TextBlock { FontSize = 11, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center };
        if (bonuses) bonusCount = count; else propertyCount = count;
        if (!bonuses)
        {
            quickAdd = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 4, LineSpacing = 4, Margin = new(0, 6, 0, 0) };
            list.Children.Add(quickAdd);
            RefreshChoices();
        }
        else if (table.ColumnIndex("add func") is >= 0)
        {
            var mode = new ComboBox { Width = 380, ItemsSource = new[] { "0 · no item bonuses", "1 · bonus with a specific other set item", "2 · bonus by number of set items worn" } };
            AutomationProperties.SetName(mode, "add func");
            mode.SelectionChanged += (_, _) => { if (!loading && mode.SelectedIndex >= 0) Commit("add func", mode.SelectedIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)); };
            editors["add func"] = mode;
            list.Children.Insert(0, Labeled("How the bonuses apply (add func)", mode, ColumnGuide.Find(table.Name, "add func")?.Description));
        }
        return Card(bonuses ? "Set bonuses on this item" : "Properties", list, count,
            bonuses ? "Extra properties this item gains while other pieces of its set are worn. The set's own partial and full bonuses are edited in sets." : "Everything the item rolls. Min and max give the roll range; the parameter is a skill, class, chance or other detail the property reads.");
    }

    // Code, parameter and range share the width; the tooltip line sits under them, so a narrow editor still fits.
    private const string SlotColumns = "64,2*,1.4*,*,*,Auto";
    private static Grid SlotHeader(bool bonuses)
    {
        var grid = new Grid { ColumnDefinitions = new(SlotColumns), ColumnSpacing = 6 };
        string[] labels = [bonuses ? "Worn with" : "Slot", "Property", "Parameter", "Min", "Max"];
        for (int i = 0; i < labels.Length; i++) { var text = new TextBlock { Text = labels[i], FontSize = 11, Foreground = Muted }; SetColumn(text, i); grid.Children.Add(text); }
        return grid;
    }

    private SlotView SlotRow(PropertySlot slot, bool bonuses)
    {
        var grid = new Grid { ColumnDefinitions = new(SlotColumns), RowDefinitions = new("Auto,Auto"), ColumnSpacing = 6, IsVisible = false, Margin = new(0, 0, 0, 4) };
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Foreground = Muted, Text = bonuses ? $"{slot.Pieces} items" : slot.Prop.Replace("prop", "#") };
        ToolTip.SetTip(label, string.Join(", ", slot.Columns));
        grid.Children.Add(label);
        var code = Choice(slot.Prop, "Property code", double.NaN, () => catalog?.Properties ?? [], p => p.SearchText,
            p => Option(p.Code, p.Tooltip.Length > 0 ? p.Tooltip : p.Notes), text => catalog?.Properties.Any(p => p.Code == text) == true);
        SetColumn(code, 1); grid.Children.Add(code);
        var parameter = Text(slot.Parameter, "", double.NaN); SetColumn(parameter, 2); grid.Children.Add(parameter);
        var min = Text(slot.Min, "", double.NaN); SetColumn(min, 3); grid.Children.Add(min);
        var max = Text(slot.Max, "", double.NaN); SetColumn(max, 4); grid.Children.Add(max);
        var line = new ContentControl { Margin = new(4, 3, 0, 0) }; SetRow(line, 1); SetColumn(line, 1); SetColumnSpan(line, 4); grid.Children.Add(line);
        var clear = new Button { Content = "✕", Padding = new(8, 2), MinHeight = 0, FontSize = 12, Foreground = Muted, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(clear, "Remove this property (clears its four cells)");
        clear.Click += (_, _) => Try(() =>
        {
            int row = SelectedRow; if (row < 0) return;
            revealed.Remove(slot.Prop);
            Document.SetCells(slot.Columns.Select(c => (row, c, "")));
            pane.RefreshRowValues(row);
        });
        SetColumn(clear, 5); grid.Children.Add(clear);
        var view = new SlotView(slot, grid, line, code, parameter, min, max);
        code.TextChanged += (_, _) => DescribeSlot(view);
        return view;
    }

    /// <summary>Placeholders and the tooltip template of the slot's property, from properties.txt's documentation columns.</summary>
    private void DescribeSlot(SlotView view)
    {
        var property = catalog?.Properties.FirstOrDefault(p => p.Code == (view.Code.Text ?? ""));
        view.Parameter.PlaceholderText = property?.Parameter ?? ""; view.Min.PlaceholderText = property?.Min ?? ""; view.Max.PlaceholderText = property?.Max ?? "";
        ToolTip.SetTip(view.Code, property == null ? null : string.Join("\n", new[] { property.Code, property.Tooltip, property.Notes }.Where(s => s.Length > 0)));
    }

    /// <summary>Shows the next empty slot (or the first one when the property is quick-added) and focuses where to type next.</summary>
    private void RevealSlot(bool bonuses, string? code)
    {
        int row = SelectedRow; var table = Table; if (row < 0 || table == null) return;
        var free = slots.FirstOrDefault(s => s.Slot.SetBonus == bonuses && !s.Row.IsVisible);
        if (free == null) { SetStatus(bonuses ? "Every set bonus slot is in use." : $"All {slots.Count(s => !s.Slot.SetBonus)} property slots are in use.", true); return; }
        revealed.Add(free.Slot.Prop); free.Row.IsVisible = true;
        if (code != null) Commit(free.Slot.Prop, code);
        UpdateSlotCounts();
        var target = code == null ? (Control)free.Code : free.Min;
        Dispatcher.UIThread.Post(() => { target.BringIntoView(); target.Focus(); }, DispatcherPriority.Background);
    }

    private Control OtherColumns(TableData table)
    {
        var shown = editors.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var slot in VisualBuilder.Slots(table.Name, table.Columns)) shown.UnionWith(slot.Columns);
        var rest = table.Columns.Where(c => !shown.Contains(c) && !c.StartsWith('*')).ToArray();
        var grid = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 12, LineSpacing = 8 };
        foreach (var column in rest) grid.Children.Add(Labeled(column, Text(column, "", 170), ColumnGuide.Find(table.Name, column)?.Description ?? column));
        var expander = new Expander { Header = $"More columns ({rest.Length}) · drop sounds, transforms, cost, ladder…", Content = grid, HorizontalAlignment = HorizontalAlignment.Stretch };
        return expander;
    }

    // ── Editors ────────────────────────────────────────────────────────────────────────────────────────

    private TextBox Text(string column, string placeholder, double width)
    {
        var box = new TextBox { PlaceholderText = placeholder, Width = width, MinHeight = 30, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(box, column);
        GroupEdits(box);
        box.TextChanged += (_, _) => Commit(column, box.Text ?? "");
        editors[column] = box;
        return box;
    }

    /// <summary>
    /// A code picker: typing filters the choices by code or description. A known code (or a cleared field) is written as
    /// soon as it is typed, so the tooltip follows; anything else is written when the field is left, since modded tables
    /// may use codes this project's tables do not list.
    /// </summary>
    private AutoCompleteBox Choice<T>(string column, string placeholder, double width, Func<IEnumerable<T>> items, Func<T, string> searchText, Func<T, Control> template, Func<string, bool> known) where T : class
    {
        var box = new AutoCompleteBox
        {
            PlaceholderText = placeholder, Width = width, MinHeight = 30, FilterMode = AutoCompleteFilterMode.Custom, MinimumPrefixLength = 1, MaxDropDownHeight = 340,
            ItemFilter = (text, item) => item is T value && VisualBuilder.Matches(searchText(value), text ?? ""),
            ItemTemplate = new FuncDataTemplate<T>((value, _) => value == null ? new TextBlock() : template(value)),
            ItemsSource = items()
        };
        box.Tag = items;
        AutomationProperties.SetName(box, column);
        GroupEdits(box);
        box.TextChanged += (_, _) => { var text = box.Text ?? ""; if (text.Length == 0 || known(text)) Commit(column, text); };
        box.LostFocus += (_, _) => { if (!box.IsKeyboardFocusWithin) Commit(column, (box.Text ?? "").Trim()); };
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(column, (box.Text ?? "").Trim()); };
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

    private void Commit(string column, string value)
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

    /// <summary>Shows the row's current values in every field: after an undo, a table edit, or a value refused by the document.</summary>
    private void SyncEditors()
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
                        int picked = int.TryParse(value, out var number) && number >= 0 && number < combo.ItemCount ? number : -1;
                        if (combo.SelectedIndex != picked) combo.SelectedIndex = picked;
                        break;
                }
            }
            foreach (var slot in slots) { slot.Row.IsVisible = revealed.Contains(slot.Slot.Prop) || slot.Slot.Columns.Any(c => table.Cell(row, c).Length > 0); DescribeSlot(slot); }
            UpdateSlotCounts();
            UpdateTitle();
            DescribeBase();
        }
        finally { loading = wasLoading; }
    }

    /// <summary>The base code's name, table and level under the base item field.</summary>
    private void DescribeBase()
    {
        var table = Table; int row = SelectedRow; if (table == null || row < 0) return;
        var code = table.Cell(row, CodeColumn);
        baseName.Text = code.Length == 0 ? "No base item: the row is inactive until one is set."
            : catalog?.Bases.FirstOrDefault(b => b.Code == code) is { } found ? $"{found.Name} · {found.Table}" + (found.RequiredLevel.Length > 0 ? $" · base level {found.RequiredLevel}" : "")
            : catalog == null ? "" : "Not a weapons, armor or misc code in this project.";
    }

    private void UpdateSlotCounts()
    {
        int Used(bool bonus) => slots.Count(s => s.Slot.SetBonus == bonus && s.Row.IsVisible);
        propertyCount.Text = $"{Used(false)} of {slots.Count(s => !s.Slot.SetBonus)} slots";
        bonusCount.Text = $"{Used(true)} of {slots.Count(s => s.Slot.SetBonus)} slots";
    }

    /// <summary>Choice lists and quick-add buttons follow the catalog, which loads after the editor is built.</summary>
    private void RefreshChoices()
    {
        foreach (var box in editors.Values.OfType<AutoCompleteBox>())
            if (box.Tag is Func<IEnumerable<BuilderBase>> bases) box.ItemsSource = bases();
            else if (box.Tag is Func<IEnumerable<BuilderSet>> sets) box.ItemsSource = sets();
            else if (box.Tag is Func<IEnumerable<BuilderProperty>> properties) box.ItemsSource = properties();
        foreach (var slot in slots) DescribeSlot(slot);
        DescribeBase();
        quickAdd.Children.Clear();
        if (catalog == null) return;
        quickAdd.Children.Add(new TextBlock { Text = "Quick add:", FontSize = 11, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center });
        foreach (var (code, label) in QuickProperties)
        {
            if (catalog.Properties.FirstOrDefault(p => p.Code == code) is not { } property) continue;
            var chip = new Button { Content = label, Padding = new(8, 2), MinHeight = 0, FontSize = 11, Margin = new(0) };
            ToolTip.SetTip(chip, code + (property.Tooltip.Length > 0 ? " · " + property.Tooltip : ""));
            chip.Click += (_, _) => Try(() => RevealSlot(false, code));
            quickAdd.Children.Add(chip);
        }
    }

    private void FocusEditor(string column)
    {
        if (!editors.TryGetValue(column, out var editor)) return;
        if (editor.FindAncestorOfType<Expander>() is { } expander) expander.IsExpanded = true;
        if (slots.FirstOrDefault(s => s.Slot.Columns.Contains(column)) is { } slot) { slot.Row.IsVisible = true; revealed.Add(slot.Slot.Prop); }
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

    // ── Preview and picture ────────────────────────────────────────────────────────────────────────────

    private void SchedulePreview(bool immediate = false)
    {
        if (selected == null) return;
        previewTimer.Stop();
        if (immediate) PendingPreview = PreviewAsync(); else previewTimer.Start();
    }

    private async Task PreviewAsync()
    {
        previewCancellation?.Cancel();
        var work = previewCancellation = new CancellationTokenSource(); var token = work.Token;
        var project = host.Project(); var table = Table; int row = SelectedRow;
        if (project == null || table == null || row < 0) { work.Dispose(); if (ReferenceEquals(previewCancellation, work)) previewCancellation = null; return; }
        var record = (System.Text.Json.Nodes.JsonObject)table.Records[row]!.DeepClone();
        string profile = host.Profile(), language = Locale, name = table.Name; int level = (int)(characterLevel.Value ?? 80), workspace = host.Workspace(), revision = Document.Revision;
        try
        {
            if (host.DirtyDependency(pane) is { } dirty) { ShowUnavailable("Save the edited dependency to preview: " + dirty); return; }
            if (previewContext != (project.Root, workspace)) { previewResolver.Clear(); previewContext = (project.Root, workspace); }
            var result = await previewWork.RunAsync(ct => previewResolver.Resolve(project, name, record, profile, level, language, ct), token);
            if (token.IsCancellationRequested || revision != Document.Revision || SelectedRow != row) return;
            LastPreview = result;
            tooltipHost.Content = TooltipCard(result);
            issues.Text = result.Issues.Length == 0 ? "" : "Incomplete preview\n" + string.Join("\n", result.Issues);
            issues.IsVisible = result.Issues.Length > 0;
            gameName.Text = result.Tooltip != null ? "Shown in game as: " + result.Name : "";
            ShowSlotLines(result, table.Name);
            UpdateTitle();
            RequestSprite(project, table, row);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) ShowUnavailable(ex.Message); }
        finally { if (ReferenceEquals(previewCancellation, work)) previewCancellation = null; work.Dispose(); }
    }

    private void ShowUnavailable(string message)
    {
        LastPreview = new ItemPreviewResult("Preview unavailable", IsSetTable, [], [message]);
        tooltipHost.Content = new TextBlock { Text = message, Foreground = Brushes.Salmon, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
        issues.IsVisible = false;
    }

    /// <summary>The item drawn like the game's tooltip: centred lines on black, name and base in the quality colour, properties in blue.</summary>
    private Control TooltipCard(ItemPreviewResult result)
    {
        var panel = new StackPanel { Spacing = 2, Margin = new(22, 16, 22, 18), HorizontalAlignment = HorizontalAlignment.Center };
        var quality = result.IsSet ? SetBrush : UniqueBrush;
        void Add(IReadOnlyList<PreviewText> lines, IBrush brush, IBrush link, double size = 15, Thickness margin = default)
        {
            if (lines.Count == 0) return;
            panel.Children.Add(new PreviewLinkText(lines, link) { Foreground = brush, FontSize = size, FontFamily = TooltipFont, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = margin });
        }
        Add([new PreviewText(result.Name, [])], quality, quality, 18);
        if (result.Tooltip is not { } tooltip)
        {
            Add(result.Text, WhiteBrush, PreviewLinkText.LinkBrush, 13);
        }
        else
        {
            if (tooltip.BaseName != null) Add([tooltip.BaseName], quality, quality, 16);
            Add(tooltip.Stats, WhiteBrush, Brushes.White, 15, new(0, 4, 0, 0));
            Add(tooltip.Properties, MagicBrush, MagicLink, 15, new(0, 2, 0, 0));
            Add(tooltip.SetLines, SetBrush, Brushes.LightGreen, 14, new(0, 8, 0, 0));
        }
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(242, 8, 8, 9)), BorderBrush = new SolidColorBrush(Color.Parse("#3B3325")), BorderThickness = new(1),
            CornerRadius = new(2), MinWidth = 300, MaxWidth = 560, Child = panel,
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 18, Color = Color.FromArgb(160, 0, 0, 0) })
        };
    }

    /// <summary>Beside each slot, the tooltip lines read from its cells; the property's template when it resolves to none.</summary>
    private void ShowSlotLines(ItemPreviewResult result, string table)
    {
        var lines = result.Tooltip is { } tooltip ? tooltip.Properties.Concat(tooltip.SetLines).ToArray() : result.Text;
        foreach (var slot in slots)
        {
            var columns = slot.Slot.Columns.ToHashSet(StringComparer.Ordinal);
            var own = lines.Where(l => l.Links.Any(link => link.Targets.Any(t => t.Table == table && t.SourceId == selectedSourceId && columns.Contains(t.Column)))).ToArray();
            slot.Line.Content = own.Length > 0
                ? new PreviewLinkText(own, MagicLink) { Foreground = MagicBrush, FontSize = 13, TextWrapping = TextWrapping.Wrap }
                : new TextBlock { Text = (slot.Code.Text ?? "").Length == 0 ? "Pick a property code" : "Not shown in the tooltip", Foreground = Muted, FontSize = 11, FontStyle = FontStyle.Italic };
        }
    }

    private void RequestSprite(ModProject project, TableData table, int row)
    {
        var code = table.Cell(row, CodeColumn); var index = table.Cell(row, "index");
        var baseItem = catalog?.Bases.FirstOrDefault(b => b.Code == code);
        var gameData = host.GameData();
        var key = string.Join('|', project.Root, table.Name, index, code, baseItem?.Table, baseItem?.Tier, string.Join(';', gameData));
        if (key == spriteKey) return;
        spriteKey = key;
        PendingSprite = LoadSpriteAsync(project, gameData, table.Name, index, code, baseItem);
    }

    private async Task LoadSpriteAsync(ModProject project, IReadOnlyList<string> gameData, string table, string index, string code, BuilderBase? baseItem)
    {
        spriteCancellation?.Cancel();
        var work = spriteCancellation = new CancellationTokenSource(); var token = work.Token;
        try
        {
            var sprite = await Task.Run(() => ItemSprites.Resolve(project, gameData, table, index, code, baseItem?.Table, baseItem?.Tier ?? "normal", token), token);
            if (token.IsCancellationRequested) return;
            LastSprite = sprite;
            ShowSprite(sprite, gameData.Count > 0);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) { spriteHost.Content = new TextBlock { Text = "Picture unavailable", Foreground = Muted }; spriteNote.Text = ex.Message; } }
        finally { if (ReferenceEquals(spriteCancellation, work)) spriteCancellation = null; work.Dispose(); }
    }

    private void ShowSprite(ItemSprite sprite, bool haveGameData)
    {
        if (sprite.Pixels is { } pixels)
        {
            var bitmap = new WriteableBitmap(new PixelSize(pixels.Width, pixels.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
            using (var target = bitmap.Lock()) for (int y = 0; y < pixels.Height; y++) Marshal.Copy(pixels.Rgba, y * pixels.Width * 4, target.Address + y * target.RowBytes, pixels.Width * 4);
            var image = new Image { Source = bitmap, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, MaxWidth = 196, MaxHeight = 392 };
            RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
            spriteHost.Content = image; spriteBitmap?.Dispose(); spriteBitmap = bitmap;
            spriteNote.Text = $"{sprite.File!.Relative.Replace("data/hd/global/ui/items/", "")} · from {sprite.File.Origin} · via {sprite.Via}";
            ToolTip.SetTip(spriteNote, sprite.File.Path);
            return;
        }
        var panel = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = "No picture", Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center });
        if (!haveGameData)
        {
            var choose = new Button { Content = "Choose game data folder…", Padding = new(8, 3), MinHeight = 0, FontSize = 11 };
            ToolTip.SetTip(choose, "Your extracted D2R data (for example with CascView): the folder holding hd/ and global/. Pictures the mod does not ship are read from it.");
            choose.Click += async (_, _) => { try { await host.ChooseGameData(); } catch (Exception ex) { SetStatus(ex.Message, true); } };
            panel.Children.Add(choose);
        }
        spriteHost.Content = panel; spriteBitmap?.Dispose(); spriteBitmap = null;
        spriteNote.Text = string.Join("\n", sprite.Notes);
        ToolTip.SetTip(spriteNote, null);
    }
}

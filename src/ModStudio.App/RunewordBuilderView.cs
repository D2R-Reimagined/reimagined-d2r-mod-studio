using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ModStudio.Core;
using static ModStudio.Core.Storage;

namespace ModStudio.App;

/// <summary>
/// The Visual Builder for runewords: the runeword socketed into a base it can be made in (its picture with the runes in
/// its sockets) beside the game's runeword tooltip (name, rune order, the base's damage or defense, the required level
/// its runes and base set, and its properties), and editors for its runes, the types it is allowed on and excluded from,
/// and its seven properties. Any base that can hold it can be picked to preview it on.
/// </summary>
internal sealed class RunewordBuilderView : TableBuilderView<RunewordEntry, RunewordBuilderCatalog>
{
    private static readonly IBrush Gold = new SolidColorBrush(Color.Parse("#C7B377")), MagicBrush = new SolidColorBrush(Color.Parse("#7878FF")), MagicLink = new SolidColorBrush(Color.Parse("#A3A3FF")),
        Gray = new SolidColorBrush(Color.Parse("#8C8C8C"));

    private readonly RunewordBuilderResolver catalogResolver = new(), fitResolver = new();
    private readonly RecipePreviewResolver previewResolver = new();
    private readonly BaseItemPreviewResolver baseResolver = new();
    private readonly PreviewWorkQueue previewWork = new();
    private CancellationTokenSource? previewCancellation, pictureCancellation;
    private (string? Project, int Workspace) previewContext;
    private readonly Dictionary<string, WriteableBitmap?> pictures = new(StringComparer.OrdinalIgnoreCase);
    private string pictureContext = "";
    private string? chosenBase;
    private ContentControl socketed = new(), tooltipHost = new(), bases = new(), warnings = new(), details = new();
    private ComboBox baseChoice = new();
    private TextBlock baseNote = new();
    private readonly Dictionary<int, (ContentControl Line, AutoCompleteBox Code, TextBox Param, TextBox Min, TextBox Max)> slots = [];
    private readonly Dictionary<int, TextBlock> runeNames = [];
    private bool choosingBase;

    internal RecipePreviewResult? LastPreview { get; private set; }
    internal RunewordFit? LastFit { get; private set; }
    internal BaseItemPreviewResult? LastBase { get; private set; }
    internal string? ChosenBase { get => chosenBase; set { chosenBase = value; SchedulePreview(immediate: true); } }
    internal Task PendingPictures { get; private set; } = Task.CompletedTask;
    internal Control Socketed => socketed;
    internal Control Tooltip => tooltipHost;
    internal Control Bases => bases;

    public RunewordBuilderView(EditorPane pane, VisualBuilderHost host) : base(pane, host)
    {
        baseChoice = new ComboBox { MinWidth = 214, MaxWidth = 214 };
        baseChoice.SelectionChanged += (_, _) => { if (!choosingBase && baseChoice.SelectedItem is BaseChoice choice) ChosenBase = choice.Base.Code; };
        Ready();
    }

    private sealed record BaseChoice(RunewordBase Base) { public override string ToString() => $"{Base.Name} · qlvl {Base.Qlvl}"; }

    protected override string ListHeading => "RUNEWORDS";
    protected override string SearchHint => "Search by name or runes";
    protected override string AddLabel => "+ New runeword";
    protected override string Noun => "a runeword";
    protected override string NameColumn => "Name";
    protected override IBrush TitleBrush => Gold;
    protected override string CopyName(string name) => name + "_copy";
    protected override string MoreColumnsHint => " · everything else in runes";
    protected override string TitleText(TableData table, int row) => LastPreview is { Name.Length: > 0 } preview && preview.Name != "Inactive row" ? preview.Name : base.TitleText(table, row);

    protected override JsonObject NewRow(TableData table)
    {
        var fields = new JsonObject();
        foreach (var (column, value) in new[] { ("Name", "NewRuneword"), ("*Rune Name", "New Runeword"), ("complete", "1"), ("itype1", "weap"), ("Rune1", "r01"), ("Rune2", "r02") })
            if (table.ColumnIndex(column) >= 0) fields[column] = value;
        return fields;
    }

    protected override IReadOnlyList<object> ListRows(TableData table) => RunewordBuilderResolver.Rows(table);
    protected override RunewordBuilderCatalog LoadCatalog(ModProject project, string profile, string locale, IReadOnlyList<object> rows, bool fresh, CancellationToken token)
    {
        if (fresh) catalogResolver.Clear();
        return catalogResolver.Catalog(project, profile, locale, [.. rows.Cast<RunewordRow>()], token);
    }
    protected override IReadOnlyList<RunewordEntry> EntriesOf(RunewordBuilderCatalog catalog) => catalog.Entries;
    protected override int RowOf(RunewordEntry entry) => entry.Row;
    protected override string SourceIdOf(RunewordEntry entry) => entry.SourceId;
    protected override bool IsInactive(RunewordEntry entry) => entry.Inactive;
    protected override string SearchTextOf(RunewordEntry entry) => entry.SearchText;

    protected override Control EntryView(RunewordEntry entry)
    {
        var panel = new StackPanel { Margin = new(2, 3) };
        panel.Children.Add(new TextBlock { Text = entry.Name.Length > 0 ? entry.Name : entry.Key, FontSize = 13, Foreground = entry.Complete ? Gold : Muted, TextTrimming = TextTrimming.CharacterEllipsis });
        panel.Children.Add(new TextBlock { Text = (entry.Complete ? "" : "not complete · ") + (entry.Runes.Length > 0 ? entry.Runes : "no runes") + " · " + entry.Key, FontSize = 11, Foreground = Muted, TextTrimming = TextTrimming.CharacterEllipsis });
        return panel;
    }

    protected override void OnSelect() { LastPreview = null; LastFit = null; LastBase = null; chosenBase = null; }
    protected override void OnInvalidate() => pictureContext = "";
    protected override void Stop() { previewCancellation?.Cancel(); pictureCancellation?.Cancel(); }

    // ── Cards ──────────────────────────────────────────────────────────────────────────────────────────

    protected override void BuildCards(StackPanel root, TableData table)
    {
        slots.Clear(); runeNames.Clear();
        root.Children.Add(Hero());
        root.Children.Add(warnings = new ContentControl());
        var identity = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 12, LineSpacing = 10 };
        if (Field(table, "Name", "Name (string key)", 160) is { } key) identity.Children.Add(key);
        foreach (var (column, label) in new[] { ("complete", "Can be made"), ("disallowCraftingInLadder", "Not in ladder"), ("disallowCraftingInNonLadder", "Not in non-ladder") })
            if (table.ColumnIndex(column) >= 0) identity.Children.Add(Toggle(column, label, ColumnGuide.Find("runes", column)?.Description ?? column));
        foreach (var (column, label) in new[] { ("firstLadderSeason", "First ladder season"), ("lastLadderSeason", "Last ladder season") })
            if (Field(table, column, label, 90) is { } field) identity.Children.Add(field);
        root.Children.Add(Card("Runeword", identity, note: "The name key is looked up in the string tables; complete must be 1 for the game to make it."));

        var runes = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 12, LineSpacing = 10 };
        for (int i = 1; i <= 6; i++)
        {
            if (table.ColumnIndex("Rune" + i) < 0) continue;
            var column = new StackPanel { Spacing = 3 };
            column.Children.Add(new TextBlock { Text = "Socket " + i, FontSize = 11, Foreground = Muted });
            column.Children.Add(Choice("Rune" + i, "rune", 120, () => catalog?.Runes ?? [], r => r.SearchText, r => Option(r.Code, $"{r.Name} · level {r.RequiredLevel}"), code => catalog?.Runes.Any(r => r.Code == code) == true));
            var name = new TextBlock { FontSize = 11, Foreground = Gold }; runeNames[i] = name; column.Children.Add(name);
            runes.Children.Add(column);
        }
        root.Children.Add(Card("Runes", runes, note: "Socketed in this order into an item with exactly this many sockets. The runeword requires the highest level among its runes."));

        var types = new StackPanel { Spacing = 10 };
        foreach (var (prefix, count, label) in new[] { ("itype", 6, "Allowed on"), ("etype", 3, "Not on") })
        {
            var row = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 10, LineSpacing = 6 };
            for (int i = 1; i <= count; i++)
                if (table.ColumnIndex(prefix + i) >= 0)
                    row.Children.Add(Choice(prefix + i, "item type", 120, () => catalog?.Types ?? [], t => t.SearchText, t => Option(t.Code, t.Name), code => catalog?.Types.Any(t => t.Code == code) == true));
            types.Children.Add(Labeled(label, row));
        }
        root.Children.Add(Card("Item types", types, note: "The base must be one of the allowed types (or a type within one) and none of the excluded ones."));
        bases = new ContentControl();
        root.Children.Add(Card("Bases it can be made in", bases, note: "Every weapon and armor of its types that can roll enough sockets, lowest drop level first. Pick one to preview the runeword on it."));

        var properties = new StackPanel { Spacing = 4 };
        var head = new Grid { ColumnDefinitions = new("60,2*,1.4*,*,*"), ColumnSpacing = 6 };
        string[] heads = ["", "Property", "Parameter", "Min", "Max"];
        for (int c = 1; c < heads.Length; c++) { var text = new TextBlock { Text = heads[c], FontSize = 11, Foreground = Muted }; SetColumn(text, c); head.Children.Add(text); }
        properties.Children.Add(head);
        for (int i = 1; i <= 7; i++)
        {
            if (table.ColumnIndex("T1Code" + i) < 0) continue;
            var row = new Grid { ColumnDefinitions = new("60,2*,1.4*,*,*"), RowDefinitions = new("Auto,Auto"), ColumnSpacing = 6, Margin = new(0, 0, 0, 4) };
            row.Children.Add(new TextBlock { Text = "#" + i, VerticalAlignment = VerticalAlignment.Center, Foreground = Muted });
            var code = Choice("T1Code" + i, "property", double.NaN, () => catalog?.Properties ?? [], p => p.SearchText, p => Option(p.Code, p.Tooltip.Length > 0 ? p.Tooltip : p.Notes), text => catalog?.Properties.Any(p => p.Code == text) == true);
            var param = Text("T1Param" + i, "", double.NaN); var min = Text("T1Min" + i, "", double.NaN); var max = Text("T1Max" + i, "", double.NaN);
            Control[] cells = [code, param, min, max];
            for (int c = 0; c < cells.Length; c++) { SetColumn(cells[c], c + 1); row.Children.Add(cells[c]); }
            var line = new ContentControl { Margin = new(4, 2, 0, 0) }; SetRow(line, 1); SetColumn(line, 1); SetColumnSpan(line, 4); row.Children.Add(line);
            slots[i] = (line, code, param, min, max);
            properties.Children.Add(row);
        }
        root.Children.Add(Card("Properties", properties, note: "What the runeword adds to the item, on top of what its runes give in that item's sockets."));
        details = new ContentControl { Content = new TextBlock { Text = "Resolving…", Foreground = Muted } };
        root.Children.Add(new Expander { Header = "Everything the recipe preview reads from this row", Content = details, HorizontalAlignment = HorizontalAlignment.Stretch });
    }

    private Control Hero()
    {
        var hero = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 20 };
        var left = new StackPanel { Spacing = 6, Width = 214 };
        socketed = new ContentControl();
        left.Children.Add(new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#1A1712"), 0), new GradientStop(Color.Parse("#0C0B09"), 1) }
            },
            BorderBrush = new SolidColorBrush(Color.Parse("#4A4030")), BorderThickness = new(1), CornerRadius = new(4), MinHeight = 214, Padding = new(8, 16), Child = socketed
        });
        if (baseChoice.Parent is Panel old) old.Children.Remove(baseChoice);
        left.Children.Add(Labeled("Preview on", baseChoice, "Any base the runeword can be made in"));
        baseNote = new TextBlock { FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap };
        left.Children.Add(baseNote);
        hero.Children.Add(left);
        var right = new StackPanel { Spacing = 8 }; SetColumn(right, 1);
        tooltipHost = new ContentControl { HorizontalAlignment = HorizontalAlignment.Left, Content = new TextBlock { Text = "Resolving runeword…", Foreground = Muted } };
        right.Children.Add(tooltipHost);
        right.Children.Add(new TextBlock { Text = "Click a number to jump to the field it comes from.", FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap });
        hero.Children.Add(right);
        return hero;
    }

    protected override void OnSynced(TableData table, int row)
    {
        foreach (var (i, name) in runeNames)
        {
            var code = table.Cell(row, "Rune" + i);
            var rune = catalog?.Runes.FirstOrDefault(r => r.Code == code);
            name.Text = code.Length == 0 ? "" : rune != null ? $"{rune.Name} · lvl {rune.RequiredLevel}" : "not a rune";
            name.Foreground = rune != null || code.Length == 0 ? Gold : Brushes.Salmon;
        }
        foreach (var (_, slot) in slots)
        {
            var property = catalog?.Properties.FirstOrDefault(p => p.Code == (slot.Code.Text ?? ""));
            slot.Param.PlaceholderText = property?.Parameter ?? ""; slot.Min.PlaceholderText = property?.Min ?? ""; slot.Max.PlaceholderText = property?.Max ?? "";
        }
    }
    protected override void OnCatalogLoaded() { if (Table is { } table && SelectedRow >= 0) OnSynced(table, SelectedRow); pictureContext = ""; SchedulePreview(); }

    // ── Preview ────────────────────────────────────────────────────────────────────────────────────────

    protected override async Task PreviewAsync()
    {
        previewCancellation?.Cancel();
        var work = previewCancellation = new CancellationTokenSource(); var token = work.Token;
        var project = host.Project(); var record = SelectedRecord(); int row = SelectedRow;
        if (project == null || record == null) { work.Dispose(); if (ReferenceEquals(previewCancellation, work)) previewCancellation = null; return; }
        string profile = host.Profile(), language = Locale; int workspace = host.Workspace(), revision = Document.Revision; var wanted = chosenBase;
        try
        {
            if (host.DirtyDependency(pane) is { } dirty) { warnings.Content = new TextBlock { Text = "Save the edited dependency to preview: " + dirty, Foreground = Brushes.Salmon, TextWrapping = TextWrapping.Wrap }; return; }
            if (previewContext != (project.Root, workspace)) { previewResolver.Clear(); fitResolver.Clear(); baseResolver.Clear(); previewContext = (project.Root, workspace); }
            var (preview, fit, baseItem) = await previewWork.RunAsync(ct =>
            {
                var preview = previewResolver.Resolve(project, "runes", record, profile, language, ct);
                var fit = fitResolver.Fit(project, record, profile, language, ct);
                var chosen = fit.Bases.FirstOrDefault(b => b.Code == wanted) ?? fit.Bases.FirstOrDefault();
                BaseItemPreviewResult? baseItem = null;
                if (chosen != null && ReadBase(project, chosen.Table, chosen.Code) is { } baseRecord) baseItem = baseResolver.Resolve(project, chosen.Table, baseRecord, profile, language, ct);
                return (preview, fit, baseItem);
            }, token);
            if (token.IsCancellationRequested || revision != Document.Revision || SelectedRow != row) return;
            LastPreview = preview; LastFit = fit; LastBase = baseItem;
            var chosenFit = fit.Bases.FirstOrDefault(b => b.Code == wanted) ?? fit.Bases.FirstOrDefault();
            chosenBase = chosenFit?.Code;
            ShowBases(fit, chosenFit);
            ShowTooltip(preview, fit, chosenFit, baseItem);
            warnings.Content = preview.Issues.Length == 0 ? null : new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(40, 229, 83, 61)), BorderBrush = new SolidColorBrush(Color.Parse("#7A3A30")), BorderThickness = new(1), CornerRadius = new(4), Padding = new(12, 8),
                Child = new SelectableTextBlock { Text = "⚠ " + string.Join("\n⚠ ", preview.Issues), Foreground = Brushes.Salmon, FontSize = 12, TextWrapping = TextWrapping.Wrap }
            };
            var lines = preview.Sections.FirstOrDefault(s => s.Title == "Properties")?.Text ?? [];
            foreach (var (i, slot) in slots)
            {
                var own = lines.Where(l => l.Links.Any(k => k.Targets.Any(t => t.Column.EndsWith(i.ToString(), StringComparison.Ordinal) && t.Column.StartsWith("T1", StringComparison.Ordinal)))).ToArray();
                slot.Line.Content = own.Length > 0 ? new PreviewLinkText(own, MagicLink) { Foreground = MagicBrush, FontSize = 12, TextWrapping = TextWrapping.Wrap } : null;
            }
            details.Content = MainWindow.RecipeCard(preview);
            UpdateTitle();
            var table = Table;
            if (table != null && row >= 0) RequestPictures(Enumerable.Range(1, 6).Select(i => table.Cell(row, "Rune" + i)).Where(r => r.Length > 0).Select(r => (r, "misc", "normal"))
                .Concat(chosenFit != null ? [(chosenFit.Code, chosenFit.Table, chosenFit.Tier)] : []), chosenFit, fit.Sockets);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) tooltipHost.Content = new TextBlock { Text = ex.Message, Foreground = Brushes.Salmon, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 }; }
        finally { if (ReferenceEquals(previewCancellation, work)) previewCancellation = null; work.Dispose(); }
    }

    /// <summary>A base item's record as the table file holds it, so its tooltip links to its own cells.</summary>
    private static JsonObject? ReadBase(ModProject project, string table, string code)
    {
        var file = TableData.FileFor(project, "tables", table);
        if (!File.Exists(file)) return null;
        // The table file is read again only when it changes on disk; edits in the builder do not touch it.
        var written = File.GetLastWriteTimeUtc(file);
        JsonArray records;
        lock (baseTables)
        {
            if (!baseTables.TryGetValue(file, out var cached) || cached.Written != written) baseTables[file] = cached = (written, Read(file)["records"] as JsonArray ?? []);
            records = cached.Records;
        }
        return records.OfType<JsonObject>().FirstOrDefault(r => r["fields"]?["code"]?.GetValue<string>() == code)?.DeepClone() as JsonObject;
    }
    private static readonly Dictionary<string, (DateTime Written, JsonArray Records)> baseTables = new(StringComparer.OrdinalIgnoreCase);

    private void ShowBases(RunewordFit fit, RunewordBase? chosen)
    {
        choosingBase = true;
        try
        {
            var choices = fit.Bases.Select(b => new BaseChoice(b)).ToArray();
            baseChoice.ItemsSource = choices;
            baseChoice.SelectedItem = choices.FirstOrDefault(c => c.Base.Code == chosen?.Code);
        }
        finally { choosingBase = false; }
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = $"{fit.Bases.Length} base{(fit.Bases.Length == 1 ? "" : "s")} can hold {fit.Sockets} rune{(fit.Sockets == 1 ? "" : "s")}", Foreground = fit.Bases.Length > 0 ? GameTooltip.White : Brushes.Salmon, FontSize = 12 });
        var list = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 4 };
        foreach (var item in fit.Bases.Take(60))
        {
            var button = new Button { Content = $"{item.Name} · qlvl {item.Qlvl}", Padding = new(8, 2), MinHeight = 0, FontSize = 11, IsEnabled = item.Code != chosen?.Code };
            ToolTip.SetTip(button, $"{item.Code} · {item.Tier} · {fit.Sockets} sockets from item level {item.FromItemLevel} · required level {item.RequiredLevel}");
            button.Click += (_, _) => ChosenBase = item.Code;
            list.Children.Add(button);
        }
        panel.Children.Add(list);
        if (fit.Bases.Length > 60) panel.Children.Add(new TextBlock { Text = $"… and {fit.Bases.Length - 60} more in the Preview on list", Foreground = Muted, FontSize = 11 });
        if (fit.NeverEnoughSockets.Length > 0) panel.Children.Add(new TextBlock { Text = "Never enough sockets: " + string.Join(", ", fit.NeverEnoughSockets.Take(20)) + (fit.NeverEnoughSockets.Length > 20 ? $" +{fit.NeverEnoughSockets.Length - 20} more" : ""), Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        bases.Content = panel;
        baseNote.Text = chosen == null ? "No base can hold it." : $"{chosen.Tier switch { "ultra" => "Elite", "uber" => "Exceptional", _ => "Normal" }} · {fit.Sockets} sockets from item level {chosen.FromItemLevel}";
    }

    /// <summary>The game's runeword tooltip: gold name, the base's name, the runes in gold quotes, the base's stats, the required level, then the properties in blue.</summary>
    private void ShowTooltip(RecipePreviewResult preview, RunewordFit fit, RunewordBase? chosen, BaseItemPreviewResult? baseItem)
    {
        var runes = Table is { } table && SelectedRow >= 0
            ? string.Concat(Enumerable.Range(1, 6).Select(i => table.Cell(SelectedRow, "Rune" + i)).Where(r => r.Length > 0).Select(r => catalog?.Runes.FirstOrDefault(x => x.Code == r)?.Name.Replace(" Rune", "") ?? r)) : "";
        var stats = baseItem?.Tooltip.Skip(1).Where(t => !t.Text.StartsWith("Up to ", StringComparison.Ordinal) && !t.Text.StartsWith("Required Level", StringComparison.Ordinal)).ToList() ?? [];
        int level = Math.Max(fit.RuneLevel, chosen?.RequiredLevel ?? 0);
        if (level > 0) stats.Add(new PreviewText("Required Level: " + level, []));
        var properties = (preview.Sections.FirstOrDefault(s => s.Title == "Properties")?.Text ?? []).ToList();
        if (fit.Sockets > 0) properties.Add(new PreviewText($"Socketed ({fit.Sockets})", []));
        tooltipHost.Content = GameTooltip.Card([
            new([new PreviewText(preview.Name, [])], Gold, Gold, 18),
            new(chosen != null ? [new PreviewText(chosen.Name, [])] : [], Gray, Gray, 15),
            new(runes.Length > 0 ? [new PreviewText($"'{runes}'", [])] : [], Gold, Gold, 15),
            new(stats, GameTooltip.White, Brushes.White, 15, 4),
            new(properties, MagicBrush, MagicLink, 15, 2)]);
    }

    // ── Pictures ───────────────────────────────────────────────────────────────────────────────────────

    private void RequestPictures(IEnumerable<(string Code, string Table, string Tier)> wanted, RunewordBase? chosen, int sockets)
    {
        var project = host.Project(); if (project == null) return;
        var gameData = host.GameData(); var context = project.Root + "|" + string.Join(';', gameData);
        if (context != pictureContext) { pictures.Clear(); pictureContext = context; }
        var missing = wanted.Where(w => !pictures.ContainsKey(w.Code)).DistinctBy(w => w.Code).ToArray();
        if (missing.Length == 0) { DrawSocketed(chosen); return; }
        PendingPictures = LoadPicturesAsync(project, gameData, missing, chosen);
    }

    private async Task LoadPicturesAsync(ModProject project, IReadOnlyList<string> gameData, (string Code, string Table, string Tier)[] wanted, RunewordBase? chosen)
    {
        pictureCancellation?.Cancel();
        var work = pictureCancellation = new CancellationTokenSource(); var token = work.Token;
        try
        {
            var loaded = await Task.Run(() => wanted.Select(w => (w.Code, Sprite: ItemSprites.Resolve(project, gameData, "uniqueitems", "", w.Code, w.Table, w.Tier, token))).ToArray(), token);
            if (token.IsCancellationRequested) return;
            foreach (var (code, sprite) in loaded) pictures[code] = sprite.Pixels is { } pixels ? Bitmaps.From(pixels.Width, pixels.Height, pixels.Rgba) : null;
            if (chosen != null && loaded.FirstOrDefault(l => l.Code == chosen.Code).Sprite is { Pixels: null } none && gameData.Count == 0)
                baseNote.Text += "\n" + string.Join(" ", none.Notes);
            DrawSocketed(chosen);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { if (ReferenceEquals(pictureCancellation, work)) pictureCancellation = null; work.Dispose(); }
    }

    /// <summary>The base's picture with its sockets laid over it, each holding its rune, top to bottom in rune order.</summary>
    private void DrawSocketed(RunewordBase? chosen)
    {
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (chosen != null && pictures.GetValueOrDefault(chosen.Code) is { } baseBitmap)
        {
            var image = new Image { Source = baseBitmap, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, MaxWidth = 196, MaxHeight = 300 };
            RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
            grid.Children.Add(image);
        }
        else grid.Children.Add(new TextBlock { Text = chosen == null ? "No base" : "No picture", Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 60) });
        var sockets = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (Table is { } table && SelectedRow >= 0)
            foreach (var code in Enumerable.Range(1, 6).Select(i => table.Cell(SelectedRow, "Rune" + i)).Where(r => r.Length > 0))
            {
                Control inside = pictures.GetValueOrDefault(code) is { } rune
                    ? new Image { Source = rune, Width = 34, Height = 34, Stretch = Stretch.Uniform }
                    : new TextBlock { Text = code, Foreground = Gold, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                if (inside is Image picture) RenderOptions.SetBitmapInterpolationMode(picture, BitmapInterpolationMode.HighQuality);
                var socket = new Border { Width = 40, Height = 40, CornerRadius = new(20), Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)), BorderBrush = new SolidColorBrush(Color.Parse("#6B5A3A")), BorderThickness = new(1), Child = inside };
                ToolTip.SetTip(socket, catalog?.Runes.FirstOrDefault(r => r.Code == code)?.Name ?? code);
                sockets.Children.Add(socket);
            }
        grid.Children.Add(sockets);
        socketed.Content = grid;
    }
}

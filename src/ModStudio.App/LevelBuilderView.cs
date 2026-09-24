using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ModStudio.Core;

namespace ModStudio.App;

/// <summary>
/// The Visual Builder for levels: the area as the game announces it (its name, the popup on entering, waypoint, teleport
/// and town portal rules) with the area level, density, unique packs and size of each difficulty, beside a map of the
/// levels it leads to and that lead to it. Editors cover its names, difficulty numbers, the monsters each difficulty
/// spawns, critters, links and warps, object groups, generation, rules and lighting. Map nodes open their level.
/// </summary>
internal sealed class LevelBuilderView : TableBuilderView<LevelEntry, LevelBuilderCatalog>
{
    private static readonly IBrush Gold = new SolidColorBrush(Color.Parse("#C7B377")), OneWay = new SolidColorBrush(Color.Parse("#D9924A")),
        Inward = new SolidColorBrush(Color.Parse("#8FB3FF")), Outdoor = new SolidColorBrush(Color.Parse("#69C6B3")), NodeBackground = new SolidColorBrush(Color.Parse("#17181A")), Good = new SolidColorBrush(Color.Parse("#8FCB7A"));
    private static readonly string[] LookupTables = ["monstats", "lvlwarp", "lvltypes", "objgroup", "soundenviron", "levelgroups"];
    private static readonly (string Prefix, string Heading, string Note)[] Pools =
    [
        ("mon", "Normal", "Normal monsters keep their own monstats Level."),
        ("nmon", "Nightmare and Hell", ""),
        ("umon", "Unique and champion packs (Normal)", "Champions are 2 levels above the monster, uniques 3.")
    ];

    private readonly LevelBuilderResolver catalogResolver = new(), previewResolver = new();
    private readonly PreviewWorkQueue previewWork = new();
    private CancellationTokenSource? previewCancellation;
    private (string? Project, int Workspace) previewContext;
    private ContentControl banner = new(), map = new(), tiles = new(), warnings = new(), inbound = new();
    private TextBlock nightmareNote = new(), tintNote = new();
    private Border tint = new();
    private readonly Dictionary<string, (WrapPanel Panel, int Shown)> pools = [];
    /// <summary>The line under a field that says what its value names (a monster, a level, an object group).</summary>
    private readonly Dictionary<string, TextBlock> labels = [];

    internal LevelPreview? LastPreview { get; private set; }
    internal Control Map => map;
    internal Control Banner => banner;
    internal Control Tiles => tiles;
    internal int ShownSlots(string prefix) => pools.TryGetValue(prefix, out var pool) ? pool.Shown : 0;
    internal string Label(string column) => labels.GetValueOrDefault(column)?.Text ?? "";

    public LevelBuilderView(EditorPane pane, VisualBuilderHost host) : base(pane, host) => Ready();

    protected override string ListHeading => "LEVELS";
    protected override string SearchHint => "Search by name, area or Id";
    protected override string AddLabel => "+ New level";
    protected override string Noun => "a level";
    protected override string NameColumn => "Name";
    protected override IBrush TitleBrush => Gold;
    protected override string MoreColumnsHint => " · everything else in levels";
    protected override string TitleText(TableData table, int row) => LastPreview is { Title.Length: > 0 } preview ? preview.Title : base.TitleText(table, row);
    protected override IEnumerable<string> HandledColumns(TableData table) => Pools.SelectMany(p => Enumerable.Range(1, 25).Select(i => p.Prefix + i));

    protected override JsonObject NewRow(TableData table)
    {
        int id = Enumerable.Range(0, table.Records.Count).Select(i => int.TryParse(table.Cell(i, "Id"), out var value) ? value : 0).DefaultIfEmpty(0).Max() + 1;
        var fields = new JsonObject();
        var values = new List<(string, string)> { ("Name", "New Level"), ("Id", id.ToString(CultureInfo.InvariantCulture)), ("Act", "0"), ("Waypoint", "255"), ("Teleport", "1"), ("DrlgType", "1"),
            ("BlankScreen", "1"), ("SaveMonsters", "1"), ("SubType", "-1"), ("SubTheme", "-1"), ("SubWaypoint", "-1"), ("SubShrine", "-1") };
        values.AddRange(Enumerable.Range(0, 8).Select(i => ("Warp" + i, "-1")));
        foreach (var (column, value) in values) if (table.ColumnIndex(column) >= 0) fields[column] = value;
        return fields;
    }

    protected override IReadOnlyList<object> ListRows(TableData table) => LevelBuilderResolver.Rows(table);
    protected override LevelBuilderCatalog LoadCatalog(ModProject project, string profile, string locale, IReadOnlyList<object> rows, bool fresh, CancellationToken token)
    {
        if (fresh) catalogResolver.Clear();
        return catalogResolver.Catalog(project, profile, locale, [.. rows.Cast<LevelRow>()], token);
    }
    protected override IReadOnlyList<LevelEntry> EntriesOf(LevelBuilderCatalog catalog) => catalog.Entries;
    protected override int RowOf(LevelEntry entry) => entry.Row;
    protected override string SourceIdOf(LevelEntry entry) => entry.SourceId;
    protected override bool IsInactive(LevelEntry entry) => entry.Inactive;
    protected override string SearchTextOf(LevelEntry entry) => entry.SearchText;

    protected override Control EntryView(LevelEntry entry)
    {
        var panel = new StackPanel { Margin = new(2, 3) };
        panel.Children.Add(new TextBlock { Text = entry.Title.Length > 0 ? entry.Title : entry.Name, FontSize = 13, Foreground = WhiteBrush, TextTrimming = TextTrimming.CharacterEllipsis });
        panel.Children.Add(new TextBlock { Text = $"Act {entry.Act + 1} · lvl {entry.AreaLevels}{(entry.Waypoint ? " · waypoint" : "")} · {entry.Name}", FontSize = 11, Foreground = Muted, TextTrimming = TextTrimming.CharacterEllipsis });
        return panel;
    }

    protected override void OnSelect() => LastPreview = null;
    protected override void Stop() => previewCancellation?.Cancel();

    // ── Cards ──────────────────────────────────────────────────────────────────────────────────────────

    protected override void BuildCards(StackPanel root, TableData table)
    {
        pools.Clear(); labels.Clear();
        root.Children.Add(Hero());
        var openScene = new Button { Content = OperatingSystem.IsWindows() ? "Open in Level Editor" : "Level Editor requires Windows", IsEnabled = OperatingSystem.IsWindows() && host.OpenLevelEditor != null };
        openScene.Click += async (_, _) => { if (host.OpenLevelEditor != null) await host.OpenLevelEditor(pane); };
        root.Children.Add(openScene);
        root.Children.Add(warnings = new ContentControl());

        var identity = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 12, LineSpacing = 10 };
        if (Field(table, "Name", "Name (pointer)", 200) is { } name) identity.Children.Add(name);
        if (Field(table, "Id", "Id", 70) is { } id) identity.Children.Add(id);
        if (table.ColumnIndex("Act") >= 0) identity.Children.Add(Labeled("Act", Numbered("Act", 100, LevelBuilderResolver.Acts), Guide("Act")));
        if (Field(table, "Waypoint", "Waypoint (255 = none)", 130) is { } waypoint) identity.Children.Add(waypoint);
        if (table.ColumnIndex("LevelGroup") >= 0)
            identity.Children.Add(Labeled("Level group", Choice("LevelGroup", "group", 180, () => catalog?.Groups ?? [], g => g.SearchText, g => Option(g.Code, g.Name), code => catalog?.Groups.Any(g => g.Code == code) == true), Guide("LevelGroup")));
        foreach (var (column, label) in new[] { ("LevelName", "Name on the automap"), ("LevelEntry", "Popup on entering"), ("LevelWarp", "On warps leading here") })
            if (Field(table, column, label, 220, "string key") is { } field) identity.Children.Add(Described(column, field));
        root.Children.Add(Card("Area", identity, note: "Name is how other tables point at this level and Id how levels link to each other. The three names are string keys, shown translated below them."));

        root.Children.Add(Card("Difficulty", DifficultyGrid(table), note: "The area level caps the item level of what drops here and sets the level of Nightmare and Hell monsters; MonLvl is only read where MonLvlEx is empty. Density is out of 100,000 per room; 0 spawns no random monsters."));

        var monsters = new StackPanel { Spacing = 12 };
        var settings = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 12, LineSpacing = 10 };
        if (Field(table, "NumMon", "Kinds picked (NumMon)", 150) is { } kinds) settings.Children.Add(kinds);
        if (Field(table, "MonSpcWalk", "Pathing distance (MonSpcWalk)", 170) is { } walk) settings.Children.Add(walk);
        foreach (var (column, label) in new[] { ("rangedspawn", "First pick is ranged"), ("MonWndr", "Wandering monsters") })
            if (table.ColumnIndex(column) >= 0) settings.Children.Add(Toggle(column, label, ColumnGuide.Find("levels", column)?.Description ?? column));
        monsters.Children.Add(settings);
        foreach (var (prefix, heading, note) in Pools)
        {
            if (table.ColumnIndex(prefix + "1") < 0) continue;
            var head = new DockPanel();
            if (prefix != "mon" && table.ColumnIndex("mon1") >= 0)
            {
                var copy = new Button { Content = "Copy the Normal list", Padding = new(8, 2), MinHeight = 0, FontSize = 11 };
                ToolTip.SetTip(copy, $"Set {prefix}1–{prefix}25 to the monsters in mon1–mon25, as one undo step");
                copy.Click += (_, _) => CopyPool("mon", prefix);
                DockPanel.SetDock(copy, Dock.Right); head.Children.Add(copy);
            }
            var titles = new StackPanel { Spacing = 1 };
            titles.Children.Add(new TextBlock { Text = heading + $" ({prefix}#)", Foreground = WhiteBrush, FontSize = 12, FontWeight = FontWeight.SemiBold });
            var line = prefix == "nmon" ? nightmareNote = new TextBlock() : new TextBlock { Text = note };
            line.FontSize = 11; line.Foreground = Muted; line.TextWrapping = TextWrapping.Wrap;
            titles.Children.Add(line);
            head.Children.Add(titles);
            var panel = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 10, LineSpacing = 8 };
            pools[prefix] = (panel, 0);
            monsters.Children.Add(head); monsters.Children.Add(panel);
        }
        root.Children.Add(Card("Monsters", monsters, note: "Each game picks NumMon kinds from the difficulty's list. A slot opens after the last one used; click a monster's name to open it in the monster builder."));

        if (table.ColumnIndex("cmon1") >= 0)
        {
            var critters = new StackPanel { Spacing = 4 };
            critters.Children.Add(Row("60,180,90,90,*", "", "Critter (cmon#)", "Chance %", "Amount", ""));
            for (int i = 1; i <= 4; i++)
                if (table.ColumnIndex("cmon" + i) >= 0)
                    critters.Children.Add(Row("60,180,90,90,*", "#" + i, MonsterChoice("cmon" + i, 180), Text("cpct" + i, "", double.NaN), Text("camt" + i, "", double.NaN), MonsterLabel("cmon" + i)));
            root.Children.Add(Card("Critters", critters, note: "Harmless animals: each has its chance out of 100 to spawn, then spawns its amount."));
        }

        var links = new StackPanel { Spacing = 4 };
        links.Children.Add(Row("40,190,190,*", "", "Leads to (Vis#)", "Through warp (Warp#)", ""));
        for (int i = 0; i < 8; i++)
        {
            if (table.ColumnIndex("Vis" + i) < 0) continue;
            var vis = LevelChoice("Vis" + i, 190);
            Control warp = table.ColumnIndex("Warp" + i) >= 0
                ? Choice("Warp" + i, "-1", 190, () => catalog?.Warps ?? [], w => w.SearchText, w => Option(w.Code, w.Name), code => code == "-1" || catalog?.Warps.Any(w => w.Code == code) == true) : new TextBlock();
            links.Children.Add(Row("40,190,190,*", "#" + i, vis, warp, NoteLine("Vis" + i)));
        }
        var dependRow = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 12, Margin = new(0, 8, 0, 0) };
        if (table.ColumnIndex("Depend") >= 0) dependRow.Children.Add(Described("Depend", Labeled("Built from (Depend)", LevelChoice("Depend", 190), Guide("Depend"))));
        links.Children.Add(dependRow);
        links.Children.Add(inbound = new ContentControl { Margin = new(0, 6, 0, 0) });
        root.Children.Add(Card("Connections", links, note: "Each Vis# is a level you can walk to from here, through the level warp of the same number, or across the border when that is -1. Levels that should connect both ways each list the other."));

        if (table.ColumnIndex("ObjGrp0") >= 0)
        {
            var objects = new StackPanel { Spacing = 4 };
            objects.Children.Add(Row("40,220,90,*", "", "Object group (ObjGrp#)", "Chance %", ""));
            for (int i = 0; i < 8; i++)
                if (table.ColumnIndex("ObjGrp" + i) >= 0)
                    objects.Children.Add(Row("40,220,90,*", "#" + i,
                        Choice("ObjGrp" + i, "0", 220, () => catalog?.ObjectGroups ?? [], g => g.SearchText, g => Option(g.Code, g.Name), code => catalog?.ObjectGroups.Any(g => g.Code == code) == true),
                        table.ColumnIndex("ObjPrb" + i) >= 0 ? Text("ObjPrb" + i, "", double.NaN) : new TextBlock(), NoteLine("ObjGrp" + i)));
            root.Children.Add(Card("Objects", objects, note: "Object groups (objgroup rows) that may be placed here, each with its chance out of 100. 0 is none."));
        }

        var rules = new StackPanel { Spacing = 10 };
        var generation = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 12, LineSpacing = 10 };
        if (table.ColumnIndex("DrlgType") >= 0) generation.Children.Add(Labeled("Generation (DrlgType)", Numbered("DrlgType", 150, LevelBuilderResolver.DrlgTypes), Guide("DrlgType")));
        if (table.ColumnIndex("LevelType") >= 0)
            generation.Children.Add(Described("LevelType", Labeled("Tiles (LevelType)", Choice("LevelType", "type", 200, () => catalog?.Types ?? [], t => t.SearchText, t => Option(t.Code, t.Name), code => catalog?.Types.Any(t => t.Code == code) == true), Guide("LevelType"))));
        if (table.ColumnIndex("SoundEnv") >= 0)
            generation.Children.Add(Described("SoundEnv", Labeled("Music (SoundEnv)", Choice("SoundEnv", "sound", 200, () => catalog?.Sounds ?? [], s => s.SearchText, s => Option(s.Code, s.Name), code => catalog?.Sounds.Any(s => s.Code == code) == true), Guide("SoundEnv"))));
        if (table.ColumnIndex("Teleport") >= 0) generation.Children.Add(Labeled("Teleport", Numbered("Teleport", 170, LevelBuilderResolver.TeleportRules), Guide("Teleport")));
        rules.Children.Add(generation);
        var flags = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 14, LineSpacing = 0 };
        foreach (var (column, label) in new[] { ("PreventTownPortal", "No town portals"), ("Rain", "Rain (snow in Act 5)"), ("Mud", "Water bubbles"), ("NoPer", "Perspective allowed"), ("LOSDraw", "Line-of-sight drawing"),
            ("FloorFilter", "Smooth floor"), ("BlankScreen", "Draw the level"), ("DrawEdges", "Draw uncovered edges"), ("Portal", "Portal level"), ("Position", "Special start position"), ("SaveMonsters", "Keep monsters when left") })
            if (table.ColumnIndex(column) >= 0) flags.Children.Add(Toggle(column, label, ColumnGuide.Find("levels", column)?.Description ?? column));
        rules.Children.Add(flags);
        root.Children.Add(Card("Generation and rules", rules, note: "Maze levels are built from rooms (lvlmaze), preset ones from fixed maps (lvlprest), outdoor ones from wilderness tiles."));

        if (table.ColumnIndex("Intensity") >= 0)
        {
            var light = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 12, LineSpacing = 10 };
            tint = new Border { Width = 64, Height = 48, CornerRadius = new(4), BorderBrush = CardBorder, BorderThickness = new(1), VerticalAlignment = VerticalAlignment.Bottom };
            light.Children.Add(tint);
            foreach (var (column, label) in new[] { ("Intensity", "Intensity (0–128)"), ("Red", "Red"), ("Green", "Green"), ("Blue", "Blue") })
                if (Field(table, column, label, 100) is { } field) light.Children.Add(field);
            var body = new StackPanel { Spacing = 6 };
            body.Children.Add(light);
            body.Children.Add(tintNote = new TextBlock { FontSize = 11, Foreground = Muted });
            root.Children.Add(Card("Lighting", body, note: "The ambient color of the level's rooms. All four at 0 leaves the game's default."));
        }
    }

    private Control Hero()
    {
        var hero = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 20 };
        var left = new StackPanel { Spacing = 10 };
        banner = new ContentControl { Content = new TextBlock { Text = "Resolving level…", Foreground = Muted } };
        left.Children.Add(new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#1A1712"), 0), new GradientStop(Color.Parse("#0C0B09"), 1) }
            },
            BorderBrush = new SolidColorBrush(Color.Parse("#4A4030")), BorderThickness = new(1), CornerRadius = new(4), Padding = new(18, 14), Child = banner
        });
        left.Children.Add(tiles = new ContentControl());
        hero.Children.Add(left);
        map = new ContentControl();
        var right = new Border { Background = new SolidColorBrush(Color.Parse("#101113")), BorderBrush = CardBorder, BorderThickness = new(1), CornerRadius = new(4), Padding = new(8), Child = map };
        SetColumn(right, 1); hero.Children.Add(right);
        return hero;
    }

    private Control DifficultyGrid(TableData table)
    {
        var grid = new Grid { ColumnDefinitions = new("190,150,150,150"), ColumnSpacing = 10, RowSpacing = 6, HorizontalAlignment = HorizontalAlignment.Left };
        void Put(Control control, int row, int column) { SetRow(control, row); SetColumn(control, column); grid.Children.Add(control); }
        var rows = new[] { ("Area level", "MonLvlEx"), ("Area level, classic", "MonLvl"), ("Monster density", "MonDen"), ("Unique packs, at least", "MonUMin"), ("Unique packs, at most", "MonUMax"),
            ("Width in tiles", "SizeX"), ("Height in tiles", "SizeY") }.Where(r => table.ColumnIndex(r.Item2) >= 0).ToArray();
        grid.RowDefinitions = new(string.Join(",", Enumerable.Repeat("Auto", rows.Length + 1)));
        for (int d = 0; d < 3; d++) Put(new TextBlock { Text = MonsterData.Difficulties[d].Name, FontSize = 11, Foreground = Muted }, 0, d + 1);
        for (int r = 0; r < rows.Length; r++)
        {
            var (label, prefix) = rows[r];
            var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Foreground = WhiteBrush, FontSize = 12 };
            ToolTip.SetTip(name, Guide(prefix == "MonLvl" ? "monlvl" : prefix));
            Put(name, r + 1, 0);
            for (int d = 0; d < 3; d++)
            {
                var column = prefix + MonsterData.Difficulties[d].Suffix;
                if (table.ColumnIndex(column) >= 0) Put(Text(column, "", double.NaN), r + 1, d + 1);
            }
        }
        return grid;
    }

    private static string Guide(string column) => ColumnGuide.Find("levels", column)?.Description is { } guide ? $"{column}: {guide}" : column;

    /// <summary>A field with the line that says what its value names under it.</summary>
    private Control Described(string column, Control field)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(field); panel.Children.Add(NoteLine(column));
        return panel;
    }

    private TextBlock NoteLine(string column)
    {
        var line = new TextBlock { FontSize = 11, Foreground = Gold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Left };
        labels[column] = line;
        return line;
    }

    /// <summary>One line of a slot table: text cells become labels, controls are placed as they are.</summary>
    private static Grid Row(string columns, params object[] cells)
    {
        var row = new Grid { ColumnDefinitions = new(columns), ColumnSpacing = 8 };
        for (int c = 0; c < cells.Length; c++)
        {
            var control = cells[c] as Control ?? new TextBlock { Text = (string)cells[c], FontSize = 11, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center };
            SetColumn(control, c); row.Children.Add(control);
        }
        return row;
    }

    private AutoCompleteBox MonsterChoice(string column, double width) =>
        Choice(column, "monster", width, () => catalog?.Monsters ?? [], m => m.SearchText, m => Option(m.Id, m.Name), code => catalog?.Monsters.Any(m => m.Id == code) == true);

    private AutoCompleteBox LevelChoice(string column, double width) =>
        Choice(column, "0", width, () => catalog?.Levels ?? [], l => l.SearchText, l => Option(l.Id, $"{l.Title} · {l.Name}"), code => code == "0" || catalog?.Levels.Any(l => l.Id == code) == true);

    /// <summary>The monster a slot names; clicking it opens that monster in its builder.</summary>
    private TextBlock MonsterLabel(string column)
    {
        var line = NoteLine(column);
        line.Cursor = new Cursor(StandardCursorType.Hand);
        line.PointerPressed += async (_, e) =>
        {
            if (Table is not { } table || SelectedRow < 0 || table.Cell(SelectedRow, column) is not { Length: > 0 } id || host.OpenInBuilder == null) return;
            e.Handled = true;
            try { await host.OpenInBuilder("monstats", "Id", id); } catch (Exception ex) { SetStatus(ex.Message, true); }
        };
        return line;
    }

    private Control Slot(string prefix, int i)
    {
        var column = new StackPanel { Spacing = 2, Width = 160 };
        column.Children.Add(new TextBlock { Text = prefix + i, FontSize = 10, Foreground = Muted });
        column.Children.Add(MonsterChoice(prefix + i, 160));
        column.Children.Add(MonsterLabel(prefix + i));
        return column;
    }

    /// <summary>Shows a pool's slots up to one past the last one used; slots are only added, so a field being typed in stays put.</summary>
    private void EnsureSlots(TableData table, int row)
    {
        foreach (var (prefix, (panel, shown)) in pools.ToArray())
        {
            int last = Enumerable.Range(1, 25).LastOrDefault(i => table.ColumnIndex(prefix + i) >= 0 && table.Cell(row, prefix + i).Length > 0);
            int wanted = Math.Min(25, last + 1);
            for (int i = shown + 1; i <= wanted; i++)
            {
                if (table.ColumnIndex(prefix + i) < 0) continue;
                panel.Children.Add(Slot(prefix, i));
                if (editors[prefix + i] is AutoCompleteBox box) box.Text = table.Cell(row, prefix + i);
            }
            pools[prefix] = (panel, Math.Max(shown, wanted));
        }
    }

    private void CopyPool(string from, string to)
    {
        if (Table is not { } table || SelectedRow < 0) return;
        int row = SelectedRow;
        CommitAll(Enumerable.Range(1, 25).Where(i => table.ColumnIndex(from + i) >= 0 && table.ColumnIndex(to + i) >= 0).Select(i => (to + i, table.Cell(row, from + i))));
        SetStatus($"Copied mon1–mon25 to {to}1–{to}25 · Undo puts them back.");
    }

    protected override void OnSynced(TableData table, int row)
    {
        EnsureSlots(table, row);
        foreach (var (column, line) in labels)
        {
            var value = table.Cell(row, column);
            var (text, known) = column switch
            {
                _ when column.StartsWith("mon", StringComparison.Ordinal) || column.StartsWith("umon", StringComparison.Ordinal) => Monster(value, true),
                _ when column.StartsWith("nmon", StringComparison.Ordinal) || column.StartsWith("cmon", StringComparison.Ordinal) => Monster(value, false),
                _ when column.StartsWith("Vis", StringComparison.Ordinal) => Link(value, table.ColumnIndex("Warp" + column[3..]) >= 0 ? table.Cell(row, "Warp" + column[3..]) : ""),
                "Depend" => Link(value, ""),
                _ when column.StartsWith("ObjGrp", StringComparison.Ordinal) => value is "" or "0" ? ("", true)
                    : catalog?.ObjectGroups.FirstOrDefault(g => g.Code == value) is { } group ? (group.Name, true) : (catalog == null ? "" : "not an objgroup row", false),
                "LevelType" => Named(value, catalog?.Types),
                "SoundEnv" => Named(value, catalog?.Sounds),
                // String keys are translated by the preview.
                _ => (line.Text ?? "", true)
            };
            line.Text = text; line.Foreground = known ? Gold : Brushes.Salmon;
        }
        if (tintNote.Parent != null)
        {
            int Value(string column) => table.ColumnIndex(column) >= 0 && int.TryParse(table.Cell(row, column), out var value) ? Math.Clamp(value, 0, 255) : 0;
            int intensity = Value("Intensity"), red = Value("Red"), green = Value("Green"), blue = Value("Blue");
            bool none = intensity + red + green + blue == 0;
            tint.Background = none ? Brushes.Transparent : new SolidColorBrush(Color.FromRgb((byte)red, (byte)green, (byte)blue));
            tintNote.Text = none ? "All 0: the game's default lighting." : $"Tint {red}, {green}, {blue} at intensity {intensity} of 128.";
        }
    }

    private (string, bool) Monster(string id, bool ownLevel)
    {
        if (id.Length == 0) return ("", true);
        if (catalog == null) return ("", true);
        if (catalog.Monsters.FirstOrDefault(m => m.Id == id) is not { } monster) return ("not a monstats Id", false);
        return (ownLevel ? $"{monster.Name} · lvl {monster.Level}" : monster.Name, true);
    }

    private (string, bool) Link(string id, string warp)
    {
        if (id is "" or "0" || catalog == null) return ("", true);
        if (catalog.Levels.FirstOrDefault(l => l.Id == id) is not { } level) return ("no level has this Id", false);
        var through = warp is "" or "-1" ? " · across the border" :catalog.Warps.FirstOrDefault(w => w.Code == warp)?.Name is { Length: > 0 } name ? " · via " + name : " · warp " + warp;
        return ($"{level.Title} · Act {level.Act + 1}{(level.Waypoint ? " · waypoint" : "")}{through}", true);
    }

    private static (string, bool) Named(string code, LevelCode[]? codes) =>
        code.Length == 0 || codes == null || codes.Length == 0 ? ("", true) : codes.FirstOrDefault(c => c.Code == code) is { } found ? (found.Name, true) : ("not in its table", false);

    protected override void OnCatalogLoaded() { if (Table is { } table && SelectedRow >= 0) OnSynced(table, SelectedRow); SchedulePreview(); }

    // ── Preview ────────────────────────────────────────────────────────────────────────────────────────

    protected override async Task PreviewAsync()
    {
        previewCancellation?.Cancel();
        var work = previewCancellation = new CancellationTokenSource(); var token = work.Token;
        var project = host.Project(); var record = SelectedRecord(); int row = SelectedRow; var table = Table;
        if (project == null || record == null || table == null) { work.Dispose(); if (ReferenceEquals(previewCancellation, work)) previewCancellation = null; return; }
        string profile = host.Profile(), language = Locale; int workspace = host.Workspace(), revision = Document.Revision;
        var rows = LevelBuilderResolver.Rows(table);
        try
        {
            if (previewContext != (project.Root, workspace)) { previewResolver.Clear(); previewContext = (project.Root, workspace); }
            var preview = await previewWork.RunAsync(ct => previewResolver.Resolve(project, record, rows, profile, language, ct), token);
            if (token.IsCancellationRequested || revision != Document.Revision || SelectedRow != row) return;
            LastPreview = preview;
            ShowPreview(preview, table, row);
            UpdateTitle();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) banner.Content = new TextBlock { Text = ex.Message, Foreground = Brushes.Salmon, TextWrapping = TextWrapping.Wrap }; }
        finally { if (ReferenceEquals(previewCancellation, work)) previewCancellation = null; work.Dispose(); }
    }

    private void ShowPreview(LevelPreview preview, TableData table, int row)
    {
        var unsaved = LookupTables.Where(name => host.FindTable?.Invoke(name)?.IsDirty == true).Select(name => $"{name} has unsaved edits; the preview reads its saved file.");
        var notes = preview.Issues.Concat(unsaved).ToArray();
        warnings.Content = notes.Length == 0 ? null : new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(40, 229, 83, 61)), BorderBrush = new SolidColorBrush(Color.Parse("#7A3A30")), BorderThickness = new(1), CornerRadius = new(4), Padding = new(12, 8),
            Child = new SelectableTextBlock { Text = "⚠ " + string.Join("\n⚠ ", notes), Foreground = Brushes.Salmon, FontSize = 12, TextWrapping = TextWrapping.Wrap }
        };
        foreach (var (column, text) in new[] { ("LevelName", preview.Title), ("LevelEntry", preview.Entry), ("LevelWarp", preview.WarpText) })
            if (labels.TryGetValue(column, out var line)) { line.Text = table.Cell(row, column).Length > 0 ? "“" + text + "”" : ""; line.Foreground = Gold; }
        nightmareNote.Text = $"Ordinary monsters spawn at the area level: {preview.Difficulties[1].AreaLevel} in Nightmare, {preview.Difficulties[2].AreaLevel} in Hell.";
        banner.Content = BannerFor(preview);
        tiles.Content = TilesFor(preview);
        map.Content = DrawMap(preview, table.Cell(row, "Name"), table.Cell(row, "Id"));
        inbound.Content = Inbound(preview);
    }

    /// <summary>The area as the game announces it: act, name, the popup on entering, and its rules as badges.</summary>
    private Control BannerFor(LevelPreview preview)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = (preview.Act >= 0 && preview.Act < LevelBuilderResolver.Acts.Length ? LevelBuilderResolver.Acts[preview.Act] : "Act " + (preview.Act + 1)).ToUpperInvariant(), Foreground = Muted, FontSize = 11 });
        panel.Children.Add(new TextBlock { Text = preview.Title.Length > 0 ? preview.Title : "Unnamed level", FontFamily = TooltipFont, FontSize = 30, Foreground = Gold, TextWrapping = TextWrapping.Wrap });
        if (preview.Entry.Length > 0) panel.Children.Add(new TextBlock { Text = preview.Entry, FontFamily = TooltipFont, FontSize = 16, FontStyle = FontStyle.Italic, Foreground = WhiteBrush, TextWrapping = TextWrapping.Wrap });
        if (preview.WarpText.Length > 0) panel.Children.Add(new TextBlock { Text = "Warps here read “" + preview.WarpText + "”", FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap });
        var badges = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 6, Margin = new(0, 8, 0, 0) };
        badges.Children.Add(Badge(preview.Waypoint ? "Waypoint" : "No waypoint", preview.Waypoint ? Good : Muted, "Waypoint"));
        badges.Children.Add(Badge("Teleport: " + (preview.Teleport >= 0 && preview.Teleport < LevelBuilderResolver.TeleportRules.Length ? LevelBuilderResolver.TeleportRules[preview.Teleport] : preview.Teleport.ToString(CultureInfo.InvariantCulture)).ToLowerInvariant(),
            preview.Teleport == 0 ? Brushes.Salmon : Muted, "Teleport"));
        badges.Children.Add(Badge(preview.TownPortal ? "Town portals allowed" : "No town portals", preview.TownPortal ? Muted : Brushes.Salmon, "PreventTownPortal"));
        badges.Children.Add(Badge(preview.Drlg + (preview.LevelType.Length > 0 ? " · " + preview.LevelType : ""), Muted, "DrlgType"));
        if (preview.Sound.Length > 0) badges.Children.Add(Badge(preview.Sound, Muted, "SoundEnv"));
        if (preview.Group.Length > 0) badges.Children.Add(Badge("Group: " + preview.Group, Muted, "LevelGroup"));
        panel.Children.Add(badges);
        return panel;
    }

    private Control Badge(string text, IBrush brush, string column)
    {
        var badge = new Border
        {
            BorderBrush = brush, BorderThickness = new(1), CornerRadius = new(10), Padding = new(8, 2), Cursor = new Cursor(StandardCursorType.Hand),
            Child = new TextBlock { Text = text, FontSize = 11, Foreground = brush }
        };
        ToolTip.SetTip(badge, "Edit " + column);
        badge.PointerPressed += (_, e) => { e.Handled = true; FocusEditor(column); };
        return badge;
    }

    /// <summary>One tile per difficulty: its area level, density, unique packs and size. A tile opens the column its area level is read from.</summary>
    private Control TilesFor(LevelPreview preview)
    {
        var grid = new Grid { ColumnDefinitions = new("*,*,*"), ColumnSpacing = 8 };
        for (int d = 0; d < preview.Difficulties.Length; d++)
        {
            var difficulty = preview.Difficulties[d];
            var panel = new StackPanel { Spacing = 2 };
            panel.Children.Add(new TextBlock { Text = difficulty.Name.ToUpperInvariant(), FontSize = 10, Foreground = Heading });
            var level = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            level.Children.Add(new TextBlock { Text = difficulty.AreaLevel.ToString(CultureInfo.InvariantCulture), FontSize = 26, Foreground = WhiteBrush, FontFamily = TooltipFont });
            level.Children.Add(new TextBlock { Text = "area level", FontSize = 11, Foreground = Muted, VerticalAlignment = VerticalAlignment.Bottom, Margin = new(0, 0, 0, 6) });
            panel.Children.Add(level);
            panel.Children.Add(new TextBlock { Text = difficulty.Density > 0 ? $"Density {difficulty.Density:N0}" : "No random monsters", FontSize = 11, Foreground = difficulty.Density > 0 ? WhiteBrush : Muted });
            panel.Children.Add(new TextBlock { Text = difficulty.UniqueMax > 0 ? $"{difficulty.UniqueMin}–{difficulty.UniqueMax} unique packs" : "No unique packs", FontSize = 11, Foreground = Muted });
            panel.Children.Add(new TextBlock { Text = difficulty.SizeX > 0 || difficulty.SizeY > 0 ? $"{difficulty.SizeX} × {difficulty.SizeY} tiles" : "Size from its map", FontSize = 11, Foreground = Muted });
            var tile = new Border { Background = CardBackground, BorderBrush = CardBorder, BorderThickness = new(1), CornerRadius = new(4), Padding = new(12, 8), Child = panel, Cursor = new Cursor(StandardCursorType.Hand) };
            ToolTip.SetTip(tile, $"Area level read from {difficulty.AreaLevelColumn}");
            var column = difficulty.AreaLevelColumn;
            tile.PointerPressed += (_, e) => { e.Handled = true; FocusEditor(column); };
            SetColumn(tile, d); grid.Children.Add(tile);
        }
        return grid;
    }

    /// <summary>
    /// This level in the middle of the levels it leads to (solid when they lead back, dashed when one way), the levels
    /// that lead here without a way back (dotted) and the level it is built from. A node opens its level.
    /// </summary>
    private Control DrawMap(LevelPreview preview, string name, string id)
    {
        const double MapWidth = 420, MapHeight = 280, NodeWidth = 118, NodeHeight = 44;
        var nodes = new List<(string Id, string Title, string Sub, IBrush Edge, double[]? Dash, string Tip, bool Known)>();
        // Several Vis# slots may lead to one level through alternative warps (one per entrance variant): one node for them all.
        foreach (var group in preview.Links.GroupBy(l => l.Id))
        {
            var link = group.First();
            var ways = string.Join("\n", group.Select(l => $"Vis{l.Slot}{(l.WarpName.Length > 0 ? " through " + l.WarpName : l.Warp is "" or "-1" ? " across the border" : " through warp " + l.Warp)}"));
            if (preview.OutdoorLinks.FirstOrDefault(l => l.Level.Id == link.Id) is { } border)
                ways += "\n" + OutdoorTip(border);
            nodes.Add((link.Id, link.Known ? link.Title : "Id " + link.Id, link.Known ? $"Id {link.Id} · Act {link.Act + 1}{(link.Waypoint ? " · WP" : "")}" : "no such level",
                link.Known ? link.Back ? Gold : OneWay : Brushes.Salmon, link.Back ? null : [4, 3],
                $"{(link.Known ? link.Name : "Id " + link.Id)}{(link.Back ? ", both ways" : ", one way")}\n{ways}", link.Known));
        }
        foreach (var border in preview.OutdoorLinks.Where(l => nodes.All(n => n.Id != l.Level.Id)))
        {
            var level = border.Level;
            nodes.Add((level.Id, level.Title, $"Id {level.Id} · {(border.VariesBySeed ? "possible outdoor" : "outdoor border")}", Outdoor,
                border.VariesBySeed ? [3, 3] : null, $"{level.Title}\n{OutdoorTip(border)}", true));
        }
        foreach (var level in preview.Inbound.Where(l => nodes.All(n => n.Id != l.Id)))
            nodes.Add((level.Id, level.Title, $"Id {level.Id} · Act {level.Act + 1}{(level.Waypoint ? " · WP" : "")}", Inward, [1, 3], $"{level.Name} leads here; this level has no Vis# back to it", true));
        if (preview.Depend is { } depend && nodes.All(n => n.Id != depend.Id))
            nodes.Add((depend.Id, depend.Title, $"Id {depend.Id} · built from", Muted, [7, 3], $"Depend: this level is placed and built from {depend.Name}", true));

        var canvas = new Canvas { Width = MapWidth, Height = MapHeight };
        double cx = MapWidth / 2, cy = MapHeight / 2, rx = (MapWidth - NodeWidth) / 2 - 2, ry = (MapHeight - NodeHeight) / 2 - 2;
        var placed = nodes.Select((node, k) =>
        {
            double angle = -Math.PI / 2 + 2 * Math.PI * k / Math.Max(1, nodes.Count);
            return (Node: node, X: cx + rx * Math.Cos(angle), Y: cy + ry * Math.Sin(angle));
        }).ToArray();
        foreach (var (node, x, y) in placed)
            canvas.Children.Add(new Line { StartPoint = new(cx, cy), EndPoint = new(x, y), Stroke = node.Edge, StrokeThickness = 1.5, StrokeDashArray = node.Dash == null ? null : new AvaloniaList<double>(node.Dash) });
        var boxes = new List<(Border Box, double X, double Y)>();
        foreach (var (node, x, y) in placed)
        {
            var box = Node(node.Title, node.Sub, node.Edge, 1, NodeWidth, NodeHeight);
            ToolTip.SetTip(box, node.Tip + (node.Known ? " · click to open it" : ""));
            if (node.Known)
            {
                box.Cursor = new Cursor(StandardCursorType.Hand);
                var target = node.Id;
                box.PointerPressed += (_, e) => { e.Handled = true; SelectWhere("Id", target); };
            }
            canvas.Children.Add(box);
            boxes.Add((box, x, y));
        }
        var center = Node(preview.Title.Length > 0 ? preview.Title : name, $"Id {id} · this level", Gold, 2, NodeWidth + 16, NodeHeight + 6);
        canvas.Children.Add(center);
        boxes.Add((center, cx, cy));
        foreach (var (box, x, y) in boxes)
        {
            // Fit the label in unused horizontal space. Nodes sharing a vertical band each stop
            // short of their midpoint, so growing both labels cannot make the boxes overlap.
            double available = 2 * Math.Min(x - 2, MapWidth - 2 - x);
            foreach (var (other, ox, oy) in boxes)
                if (other != box && Math.Abs(y - oy) < (box.Height + other.Height) / 2 + 8)
                    available = Math.Min(available, Math.Max(0, Math.Abs(x - ox) - 8));
            box.Measure(Size.Infinity);
            box.MinWidth = 0;
            box.Width = Math.Min(Math.Ceiling(box.DesiredSize.Width), available);
            Canvas.SetLeft(box, x - box.Width / 2); Canvas.SetTop(box, y - box.Height / 2);
        }

        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = "CONNECTIONS", FontSize = 11, Foreground = Heading, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(canvas);
        var legend = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 12, MaxWidth = MapWidth };
        foreach (var (text, brush) in new[] { ("── both ways", Gold), ("- - one way", OneWay), ("··· leads here", Inward), ("— — built from", Muted) })
            legend.Children.Add(new TextBlock { Text = text, FontSize = 10, Foreground = brush });
        if (preview.OutdoorLinks.Length > 0)
            legend.Children.Add(new TextBlock { Text = "── outdoor border", FontSize = 10, Foreground = Outdoor });
        if (preview.OutdoorLinks.Any(l => l.VariesBySeed))
            legend.Children.Add(new TextBlock { Text = "- - outdoor (varies by seed)", FontSize = 10, Foreground = Outdoor });
        panel.Children.Add(nodes.Count == 0 ? new TextBlock { Text = "No links: set Vis# under Connections.", FontSize = 11, Foreground = Muted } : legend);
        return panel;
    }

    private static string OutdoorTip(LevelOutdoorLink link) => link.VariesBySeed
        ? "Possible walkable outdoor border; the connection varies by map seed."
        : "Walkable outdoor border, connected both ways by the game's area generation.";

    private static Border Node(string title, string sub, IBrush edge, double thickness, double width, double height)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontSize = 12, Foreground = WhiteBrush, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center });
        text.Children.Add(new TextBlock { Text = sub, FontSize = 10, Foreground = Muted, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center });
        return new Border { MinWidth = width, Height = height, Background = NodeBackground, BorderBrush = edge, BorderThickness = new(thickness), CornerRadius = new(4), Padding = new(6, 2), Child = text };
    }

    private Control Inbound(LevelPreview preview)
    {
        if (preview.Inbound.Length == 0) return new TextBlock { Text = "Every level that leads here is linked back.", FontSize = 11, Foreground = Muted };
        var panel = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 4 };
        panel.Children.Add(new TextBlock { Text = "Also leads here, one way:", FontSize = 11, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center });
        foreach (var level in preview.Inbound)
        {
            var button = new Button { Content = $"{level.Title} · Id {level.Id}", Padding = new(8, 2), MinHeight = 0, FontSize = 11 };
            ToolTip.SetTip(button, $"Open {level.Name}. Add its Id to a Vis# here to link back.");
            var target = level.Id;
            button.Click += (_, _) => SelectWhere("Id", target);
            panel.Children.Add(button);
        }
        return panel;
    }
}

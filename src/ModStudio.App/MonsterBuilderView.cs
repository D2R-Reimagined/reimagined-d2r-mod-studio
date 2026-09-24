using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ModStudio.Core;

namespace ModStudio.App;

/// <summary>
/// The Visual Builder for monsters: the monster animated as the legacy renderer draws it (its composite of body parts in
/// every mode it has, recoloured by TransLvl, walking and running at its authored speeds), an infobox of what it is at each
/// difficulty, and its fields grouped by what they do, with the per-difficulty ones side by side. Its look lives in
/// monstats2, which is edited in its own tab.
/// </summary>
internal sealed class MonsterBuilderView : TableBuilderView<MonsterEntry, MonsterBuilderCatalog>
{
    private static readonly IBrush MonsterBrush = new SolidColorBrush(Color.Parse("#E4DCCB")), EventBrush = new SolidColorBrush(Color.Parse("#E8A04C"));
    private static readonly (string Label, string Column, IBrush Brush)[] ResistColumns = [
        ("Phys", "ResDm", new SolidColorBrush(Color.Parse("#D8D2C4"))), ("Magic", "ResMa", new SolidColorBrush(Color.Parse("#E8A04C"))), ("Fire", "ResFi", new SolidColorBrush(Color.Parse("#E5533D"))),
        ("Light", "ResLi", new SolidColorBrush(Color.Parse("#F1D24A"))), ("Cold", "ResCo", new SolidColorBrush(Color.Parse("#6FA8FF"))), ("Poison", "ResPo", new SolidColorBrush(Color.Parse("#62C94F")))];
    private static readonly string[] DifficultyNames = ["Normal", "Nightmare", "Hell"];
    private const int SceneWidth = 320, SceneHeight = 240;
    /// <summary>A player's walk and run velocities (charstats), the yardstick for monster speeds.</summary>
    private const double PlayerWalk = 6, PlayerRun = 9;

    private static readonly (string Title, string Note, (string Column, string Label)[] Fields)[] Groups = [
        ("Monster", "Who it is: names, family, AI, graphics token and colour shift.", [
            ("Id", "Id"), ("BaseId", "Base monster"), ("NextInClass", "Next in class"), ("NameStr", "Name (string key)"), ("DescStr", "Description key"), ("MonType", "Type"), ("AI", "AI"),
            ("Code", "Graphics (Code)"), ("TransLvl", "Colour shift (TransLvl)"), ("MonStatsEx", "Look row (MonStatsEx)"), ("MonProp", "Properties (MonProp)"), ("MonSound", "Sounds"),
            ("UMonSound", "Unique sounds"), ("enabled", "Enabled"), ("Align", "Alignment"), ("threat", "Threat"), ("Rarity", "Rarity")]),
        ("Movement", "Walk and run velocity, in the same units as a player's (walk 6, run 9).", [("Velocity", "Walk velocity"), ("Run", "Run velocity")]),
        ("Spawning and groups", "How it appears: group sizes, minions, what it spawns.", [
            ("MinGrp", "Group min"), ("MaxGrp", "Group max"), ("PartyMin", "Minions min"), ("PartyMax", "Minions max"), ("minion1", "Minion 1"), ("minion2", "Minion 2"),
            ("spawn", "Spawns"), ("spawnx", "Spawn x"), ("spawny", "Spawn y"), ("spawnmode", "Spawn mode"), ("SetBoss", "Set boss"), ("BossXfer", "Boss transfer"), ("sparsePopulate", "Sparse populate")]),
        ("Attacks and missiles", "The missiles each attack mode fires.", [
            ("MissA1", "Attack 1"), ("MissA2", "Attack 2"), ("MissS1", "Skill 1"), ("MissS2", "Skill 2"), ("MissS3", "Skill 3"), ("MissS4", "Skill 4"), ("MissC", "Cast"), ("MissSQ", "Sequence"),
            ("SkillDamage", "Skill damage"), ("DamageRegen", "Life regen"), ("Crit", "Critical strike %"), ("noRatio", "Not scaled by level (noRatio)")])];
    private static readonly (string Title, string Note, (string Label, string Column)[] Rows)[] Grids = [
        ("Stats by difficulty", "Life, defense, attack rating, damage and experience are percentages of monlvl's values for the monster's level, unless noRatio is set.", [
            ("Level", "Level"), ("Life min", "minHP"), ("Life max", "maxHP"), ("Defense", "AC"), ("Experience", "Exp"),
            ("Attack 1 min", "A1MinD"), ("Attack 1 max", "A1MaxD"), ("Attack 1 rating", "A1TH"), ("Attack 2 min", "A2MinD"), ("Attack 2 max", "A2MaxD"), ("Attack 2 rating", "A2TH"),
            ("Skill 1 min", "S1MinD"), ("Skill 1 max", "S1MaxD"), ("Skill 1 rating", "S1TH"), ("Block %", "ToBlock"), ("Life/mana drain", "Drain"), ("Cold effect", "coldeffect")]),
        ("Resistances by difficulty", "Percent; 100 or more is immune.", [.. ResistColumns.Select(r => (r.Label, r.Column))]),
        ("Elemental attacks by difficulty", "Each adds elemental damage to one mode: El Mode picks the mode, El Type the element.", [
            ("El 1 mode", "El1Mode"), ("El 1 type", "El1Type"), ("El 1 chance %", "El1Pct"), ("El 1 min", "El1MinD"), ("El 1 max", "El1MaxD"), ("El 1 length", "El1Dur"),
            ("El 2 mode", "El2Mode"), ("El 2 type", "El2Type"), ("El 2 chance %", "El2Pct"), ("El 2 min", "El2MinD"), ("El 2 max", "El2MaxD"), ("El 2 length", "El2Dur"),
            ("El 3 mode", "El3Mode"), ("El 3 type", "El3Type"), ("El 3 chance %", "El3Pct"), ("El 3 min", "El3MinD"), ("El 3 max", "El3MaxD"), ("El 3 length", "El3Dur")]),
        ("AI by difficulty", "The AI's delay, distance and parameters; what each parameter does depends on the AI.", [
            ("Delay", "aidel"), ("Distance", "aidist"), .. Enumerable.Range(1, 8).Select(i => ($"Param {i}", $"aip{i}"))]),
        ("Drops by difficulty", "Treasure classes rolled when it dies.", [
            ("Normal", "TreasureClass"), ("Champion", "TreasureClassChamp"), ("Unique", "TreasureClassUnique"), ("Quest", "TreasureClassQuest"),
            ("Desecrated", "TreasureClassDesecrated"), ("Desecrated champion", "TreasureClassDesecratedChamp"), ("Desecrated unique", "TreasureClassDesecratedUnique"), ("Herald", "TreasureClassHerald")])];

    private readonly MonsterBuilderResolver catalogResolver = new();
    private readonly MonsterPreviewResolver previewResolver = new();
    private readonly PreviewWorkQueue previewWork = new();
    private CancellationTokenSource? previewCancellation, compositeCancellation;
    private (string? Project, int Workspace) previewContext;
    private readonly MonsterScene scene = new(SceneWidth, SceneHeight);
    private readonly WriteableBitmap frame;
    private readonly Image sceneImage;
    private readonly DispatcherTimer animation = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly ComboBox mode = new() { MinWidth = 150 }, lookChoice = new() { MinWidth = 90 }, speed = new() { ItemsSource = new[] { "1×", "½×", "¼×" }, SelectedIndex = 0, MinWidth = 70 },
        zoom = new() { ItemsSource = new[] { "Fit", "1×", "2×" }, SelectedIndex = 0, MinWidth = 70 }, act = new() { ItemsSource = new[] { "Act 1", "Act 2", "Act 3", "Act 4", "Act 5" }, SelectedIndex = 0, MinWidth = 90 };
    private readonly Slider facing = new() { Minimum = 0, Maximum = 63, Value = 4, Width = 160 };
    private readonly Button play = new() { Content = "Pause", Padding = new(10, 3), MinHeight = 0, FontSize = 12, Width = 70 };
    private readonly TextBlock sceneCaption = new() { FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap, MaxWidth = 720 };
    private ContentControl infobox = new(), details = new(), sceneNotice = new();
    private StackPanel stage = new();
    /// <summary>The scene column: one and a half times game size in Fit, so the infobox fits beside it; the chosen size otherwise.</summary>
    private double StageWidth() => zoom.SelectedIndex switch { 0 => SceneWidth * 1.5 + 2, var factor => SceneWidth * factor + 2 };
    private TextBlock compositeNote = new(), walkHint = new(), runHint = new(), lookNote = new();
    private int tick, frameSkip, difficulty;
    private bool playing = true, choosingMode, modePicked;
    private string? compositeKey;
    private MonsterComposite? composite;
    private MonsterLook? look;
    private byte[]? palette;
    /// <summary>The monstats2 table the look is read from: the open tab's document when there is one, else a private read-only copy.</summary>
    private Document? looks;
    private bool looksEditable;
    private readonly Dictionary<string, Control> linked = new(StringComparer.Ordinal);
    private Button makeEditable = new();

    internal MonsterComposite? LastComposite => composite;
    internal MonsterLook? LastLook => look;
    internal MonsterPreviewResult? LastPreview { get; private set; }
    internal MonsterSceneState? LastScene { get; private set; }
    internal Task PendingComposite { get; private set; } = Task.CompletedTask;
    internal Task PendingLooks { get; private set; } = Task.CompletedTask;
    internal string Mode { get => (mode.SelectedItem as ModeChoice)?.Mode.Code ?? "NU"; set => mode.SelectedItem = (mode.ItemsSource as IEnumerable<ModeChoice>)?.FirstOrDefault(m => m.Mode.Code == value); }
    internal int Facing { get => (int)facing.Value; set => facing.Value = value; }
    internal int Difficulty { get => difficulty; set { difficulty = value; ShowInfobox(); } }
    internal Control? LinkedEditor(string column) => linked.GetValueOrDefault(column);
    internal bool LooksEditable => looksEditable;
    internal MonsterSceneState RenderScene(int at) { tick = at; return DrawScene(); }
    internal Control Infobox => infobox;
    internal string CompositeNote => compositeNote.Text ?? "";

    private sealed record ModeChoice(MonsterMode Mode, bool Enabled)
    {
        public override string ToString() => $"{Mode.Name} ({Mode.Code})" + (Enabled ? "" : " · off");
    }

    public MonsterBuilderView(EditorPane pane, VisualBuilderHost host) : base(pane, host)
    {
        frame = Bitmaps.From(SceneWidth, SceneHeight, scene.Pixels);
        // Fit scales the scene to the room there is, up to twice game size; 1× and 2× are exact.
        sceneImage = new Image { Source = frame, Stretch = Stretch.Uniform, MaxWidth = SceneWidth * 2, Cursor = new Cursor(StandardCursorType.Cross), HorizontalAlignment = HorizontalAlignment.Left };
        RenderOptions.SetBitmapInterpolationMode(sceneImage, BitmapInterpolationMode.None);
        ToolTip.SetTip(sceneImage, "Click to turn the monster toward where you click.");
        sceneImage.PointerPressed += (_, e) =>
        {
            var point = e.GetPosition(sceneImage); double scale = SceneWidth / Math.Max(1, sceneImage.Bounds.Width);
            facing.Value = scene.FacingToward(point.X * scale, point.Y * scale);
        };
        facing.ValueChanged += (_, _) => { if (!playing) DrawScene(); };
        zoom.SelectionChanged += (_, _) =>
        {
            if (zoom.SelectedIndex == 0) { sceneImage.ClearValue(WidthProperty); sceneImage.MaxWidth = SceneWidth * 2; }
            else { sceneImage.Width = SceneWidth * zoom.SelectedIndex; sceneImage.MaxWidth = double.PositiveInfinity; }
            stage.MaxWidth = StageWidth();
        };
        mode.SelectionChanged += (_, _) => { if (!choosingMode) { modePicked = true; tick = 0; compositeKey = null; RequestComposite(); } };
        lookChoice.SelectionChanged += (_, _) => { if (!choosingMode) { compositeKey = null; RequestComposite(); } };
        act.SelectionChanged += (_, _) => { compositeKey = null; RequestComposite(); };
        speed.SelectionChanged += (_, _) => frameSkip = 0;
        play.Click += (_, _) => { playing = !playing; play.Content = playing ? "Pause" : "Play"; if (playing) animation.Start(); };
        animation.Tick += (_, _) => Animate();
        Ready();
    }

    protected override string ListHeading => "MONSTERS";
    protected override string SearchHint => "Search by name, id, type or graphics code";
    protected override string AddLabel => "+ New monster";
    protected override string Noun => "a monster";
    protected override string NameColumn => "Id";
    protected override IBrush TitleBrush => MonsterBrush;
    protected override string CopyName(string name) => name + "_copy";
    protected override string MoreColumnsHint => " · everything else in monstats";
    protected override string TitleText(TableData table, int row) => LastPreview is { Levels.Length: > 0 } preview ? preview.Name : base.TitleText(table, row);

    protected override JsonObject NewRow(TableData table)
    {
        var fields = new JsonObject();
        foreach (var (column, value) in new[] { ("Id", "newmonster"), ("BaseId", "newmonster"), ("NameStr", "newmonster"), ("MonStatsEx", "zombie1"), ("Code", "ZM"), ("enabled", "1"),
            ("Level", "1"), ("Level(N)", "36"), ("Level(H)", "67"), ("minHP", "100"), ("maxHP", "150"), ("AC", "100"), ("Exp", "100"), ("A1MinD", "100"), ("A1MaxD", "150"), ("A1TH", "100"),
            ("Velocity", "3"), ("Run", "6"), ("AI", "Zombie"), ("killable", "1"), ("isSpawn", "1"), ("isMelee", "1"), ("MinGrp", "1"), ("MaxGrp", "3") })
            if (table.ColumnIndex(column) >= 0) fields[column] = value;
        return fields;
    }

    protected override IReadOnlyList<object> ListRows(TableData table) => MonsterGraphics.Rows(table);
    protected override MonsterBuilderCatalog LoadCatalog(ModProject project, string profile, string locale, IReadOnlyList<object> rows, bool fresh, CancellationToken token)
    {
        if (fresh) catalogResolver.Clear();
        return catalogResolver.Catalog(project, profile, locale, host.GameData(), [.. rows.Cast<MonsterRow>()], token);
    }
    protected override IReadOnlyList<MonsterEntry> EntriesOf(MonsterBuilderCatalog catalog) => catalog.Entries;
    protected override int RowOf(MonsterEntry entry) => entry.Row;
    protected override string SourceIdOf(MonsterEntry entry) => entry.SourceId;
    protected override bool IsInactive(MonsterEntry entry) => entry.Inactive;
    protected override string SearchTextOf(MonsterEntry entry) => entry.SearchText;

    protected override Control EntryView(MonsterEntry entry)
    {
        var panel = new StackPanel { Margin = new(2, 3) };
        panel.Children.Add(new TextBlock { Text = entry.Name.Length > 0 ? entry.Name : entry.Id.Length > 0 ? entry.Id : $"Row {entry.Row}", FontSize = 13, Foreground = MonsterBrush, TextTrimming = TextTrimming.CharacterEllipsis });
        var parts = new[] { entry.Id, entry.MonType, entry.Code.Length > 0 ? "gfx " + entry.Code : "", entry.Levels.Length > 0 ? "lvl " + entry.Levels : "" }.Where(s => s.Length > 0);
        panel.Children.Add(new TextBlock { Text = string.Join(" · ", parts), FontSize = 11, Foreground = Muted, TextTrimming = TextTrimming.CharacterEllipsis });
        return panel;
    }

    protected override void OnSelect() { LastPreview = null; compositeKey = null; composite = null; tick = 0; modePicked = false; }
    protected override void Stop() { previewCancellation?.Cancel(); compositeCancellation?.Cancel(); animation.Stop(); }
    protected override void OnInvalidate() { compositeKey = null; if (!looksEditable) looks = null; }
    protected override void OnBuiltEmpty() { linked.Clear(); animation.Stop(); }
    protected override void OnShown() { if (SelectedRow >= 0) animation.Start(); }
    protected override void OnCatalogLoaded() => compositeKey = null;

    // ── Cards ──────────────────────────────────────────────────────────────────────────────────────────

    protected override void BuildCards(StackPanel root, TableData table)
    {
        linked.Clear();
        root.Children.Add(Hero());
        foreach (var group in Groups)
        {
            var fields = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 12, LineSpacing = 10 };
            foreach (var (column, label) in group.Fields) if (FieldFor(table, column, label) is { } field) fields.Children.Add(field);
            if (fields.Children.Count > 0) root.Children.Add(Card(group.Title, fields, note: group.Note));
        }
        foreach (var grid in Grids) if (DifficultyGrid(table, grid.Rows) is { } body) root.Children.Add(Card(grid.Title, body, note: grid.Note));
        root.Children.Add(SkillsCard(table));
        root.Children.Add(FlagsCard(table));
        root.Children.Add(LookCard());
        details = new ContentControl { Content = new TextBlock { Text = "Resolving…", Foreground = Muted } };
        root.Children.Add(new Expander { Header = "Spawns, drops and everything the monster preview knows", Content = details, HorizontalAlignment = HorizontalAlignment.Stretch });
        animation.Start();
    }

    /// <summary>A picker for columns that name something in another table, a switch for the data guide's booleans, else a text field.</summary>
    private Control? FieldFor(TableData table, string column, string label, double width = 110)
    {
        if (table.ColumnIndex(column) < 0) return null;
        var guide = ColumnGuide.Find(table.Name, column)?.Description;
        Control input = column switch
        {
            "MonType" => NameChoice(column, "type", 150, () => catalog?.MonTypes ?? []),
            "Code" => NameChoice(column, "token", 90, () => catalog?.Tokens ?? []),
            "MonStatsEx" => NameChoice(column, "monstats2 Id", 150, () => catalog?.Looks ?? []),
            "MonProp" => NameChoice(column, "monprop Id", 150, () => catalog?.MonProps ?? []),
            "MonSound" or "UMonSound" => NameChoice(column, "monsounds Id", 150, () => catalog?.MonSounds ?? []),
            "BaseId" or "NextInClass" or "minion1" or "minion2" or "spawn" => NameChoice(column, "monster Id", 150, () => catalog?.Entries.Where(e => !e.Inactive).Select(e => e.Id) ?? []),
            _ when column.StartsWith("Miss", StringComparison.Ordinal) => NameChoice(column, "missile", 150, () => catalog?.Missiles ?? []),
            _ when guide?.StartsWith("Boolean", StringComparison.OrdinalIgnoreCase) == true => Toggle(column, label, guide),
            _ => Text(column, "", column is "Id" or "BaseId" or "NameStr" or "DescStr" or "AI" ? 150 : width)
        };
        if (input is CheckBox) return input;
        var panel = (StackPanel)Labeled(label, input, guide is { } g ? $"{column}: {g}" : column);
        if (column == "Velocity") { walkHint = new TextBlock { FontSize = 11, Foreground = Muted }; panel.Children.Add(walkHint); }
        if (column == "Run") { runHint = new TextBlock { FontSize = 11, Foreground = Muted }; panel.Children.Add(runHint); }
        return panel;
    }

    /// <summary>Normal, Nightmare and Hell side by side: a row per stat, a field per difficulty the table has a column for.</summary>
    private Grid? DifficultyGrid(TableData table, (string Label, string Column)[] rows)
    {
        string? Find(string name) => table.Columns.FirstOrDefault(c => c.Equals(name, StringComparison.OrdinalIgnoreCase));
        var present = rows.Select(r => (r.Label, r.Column, Columns: new[] { Find(r.Column), Find(r.Column + "(N)"), Find(r.Column + "(H)") })).Where(r => r.Columns.Any(c => c != null)).ToArray();
        if (present.Length == 0) return null;
        var grid = new Grid { ColumnDefinitions = new("150,*,*,*"), ColumnSpacing = 8, RowSpacing = 4 };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (int d = 0; d < 3; d++) { var head = new TextBlock { Text = DifficultyNames[d], FontSize = 11, Foreground = Muted }; SetColumn(head, d + 1); grid.Children.Add(head); }
        foreach (var (label, column, columns) in present)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); int r = grid.RowDefinitions.Count - 1;
            var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Foreground = WhiteBrush };
            ToolTip.SetTip(name, (ColumnGuide.Find(table.Name, columns.First(c => c != null)!)?.Description is { } g ? g + "\n" : "") + string.Join(", ", columns.OfType<string>()));
            SetRow(name, r); grid.Children.Add(name);
            // A value authored once (El1Mode, El1Type) applies to every difficulty: its one field spans the three.
            bool once = columns[1] == null && columns[2] == null;
            for (int d = 0; d < (once ? 1 : 3); d++)
            {
                if (columns[d] is not { } cell) { var none = new TextBlock { Text = "—", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center }; SetRow(none, r); SetColumn(none, d + 1); grid.Children.Add(none); continue; }
                Control input = cell.StartsWith("TreasureClass", StringComparison.OrdinalIgnoreCase) ? NameChoice(cell, "treasure class", double.NaN, () => catalog?.TreasureClasses ?? []) : Text(cell, "", double.NaN);
                SetRow(input, r); SetColumn(input, d + 1);
                if (once) { SetColumnSpan(input, 3); input.HorizontalAlignment = HorizontalAlignment.Left; input.Width = 160; }
                grid.Children.Add(input);
            }
        }
        return grid;
    }

    private Control SkillsCard(TableData table)
    {
        var grid = new Grid { ColumnDefinitions = new("80,2*,*,*"), ColumnSpacing = 8, RowSpacing = 4 };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        foreach (var (text, index) in new[] { ("Skill", 1), ("Mode", 2), ("Level", 3) }) { var head = new TextBlock { Text = text, FontSize = 11, Foreground = Muted }; SetColumn(head, index); grid.Children.Add(head); }
        for (int i = 1; i <= 8; i++)
        {
            if (table.ColumnIndex("Skill" + i) < 0) continue;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); int r = grid.RowDefinitions.Count - 1;
            var label = new TextBlock { Text = "Skill " + i, VerticalAlignment = VerticalAlignment.Center, Foreground = WhiteBrush, FontSize = 12 }; SetRow(label, r); grid.Children.Add(label);
            Control[] cells = [NameChoice("Skill" + i, "skill", double.NaN, () => catalog?.Skills ?? []), table.ColumnIndex($"Sk{i}mode") >= 0 ? Text($"Sk{i}mode", "mode", double.NaN) : new TextBlock(), table.ColumnIndex($"Sk{i}lvl") >= 0 ? Text($"Sk{i}lvl", "level", double.NaN) : new TextBlock()];
            for (int c = 0; c < 3; c++) { SetRow(cells[c], r); SetColumn(cells[c], c + 1); grid.Children.Add(cells[c]); }
        }
        return Card("Skills", grid, note: "Skills the monster uses, the animation mode each plays in, and its level.");
    }

    /// <summary>Every boolean monstats column no other card shows, as switches.</summary>
    private Control FlagsCard(TableData table)
    {
        var fields = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 14, LineSpacing = 2 };
        foreach (var column in table.Columns)
            if (!editors.ContainsKey(column) && ColumnGuide.Find(table.Name, column)?.Description is { } guide && guide.StartsWith("Boolean", StringComparison.OrdinalIgnoreCase))
                fields.Children.Add(Toggle(column, column, guide));
        return Card("Flags", fields, note: "Hover a switch for what it does.");
    }

    // ── Look (monstats2) ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The monstats2 fields that shape the animation: parts and their variants, modes and their directions, weapon class, shadow and light.</summary>
    private Control LookCard()
    {
        var body = new StackPanel { Spacing = 10 };
        makeEditable = new Button { Content = "Edit look (opens monstats2)", Padding = new(10, 4), MinHeight = 0, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Left };
        ToolTip.SetTip(makeEditable, "Opens monstats2 in its own tab so these fields can be edited. Save that tab to keep them; its own Undo reverses them.");
        makeEditable.Click += async (_, _) => { try { await OpenLooksAsync(); } catch (Exception ex) { SetStatus(ex.Message, true); } };
        lookNote = new TextBlock { FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap };
        body.Children.Add(makeEditable); body.Children.Add(lookNote);
        var general = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 12, LineSpacing = 10 };
        foreach (var (column, label) in new[] { ("BaseW", "Weapon class"), ("Light", "Light radius"), ("light-r", "Light red"), ("light-g", "Light green"), ("light-b", "Light blue"),
            ("Height", "Height"), ("pixHeight", "Pixel height"), ("SizeX", "Size X"), ("SizeY", "Size Y"), ("MeleeRng", "Melee range"), ("HitClass", "Hit class"), ("Utrans", "Colour (Normal)"), ("Utrans(N)", "Colour (Nightmare)"), ("Utrans(H)", "Colour (Hell)") })
            general.Children.Add(Labeled(label, LinkedText(column, 96), ColumnGuide.Find("monstats2", column)?.Description));
        general.Children.Add(LinkedToggle("Shadow", "Shadow"));
        body.Children.Add(general);
        var parts = new Grid { ColumnDefinitions = new("60,Auto,*"), ColumnSpacing = 8, RowSpacing = 4 };
        parts.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        foreach (var (text, index) in new[] { ("Part", 0), ("Has it", 1), ("Variants (comma separated)", 2) }) { var head = new TextBlock { Text = text, FontSize = 11, Foreground = Muted }; SetColumn(head, index); parts.Children.Add(head); }
        foreach (var part in Cof.Components)
        {
            parts.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); int r = parts.RowDefinitions.Count - 1;
            var label = new TextBlock { Text = part, VerticalAlignment = VerticalAlignment.Center, Foreground = WhiteBrush }; SetRow(label, r); parts.Children.Add(label);
            var has = LinkedToggle(part, ""); has.Margin = new(0); SetRow(has, r); SetColumn(has, 1); parts.Children.Add(has);
            var variants = LinkedText(part switch { "RA" => "Rav", "LA" => "Lav", _ => part + "v" }, double.NaN); SetRow(variants, r); SetColumn(variants, 2); parts.Children.Add(variants);
        }
        var modes = new Grid { ColumnDefinitions = new("130,Auto,90,Auto"), ColumnSpacing = 8, RowSpacing = 4 };
        modes.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        foreach (var (text, index) in new[] { ("Mode", 0), ("On", 1), ("Directions", 2), ("While moving", 3) }) { var head = new TextBlock { Text = text, FontSize = 11, Foreground = Muted }; SetColumn(head, index); modes.Children.Add(head); }
        foreach (var m in MonsterGraphics.Modes)
        {
            modes.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); int r = modes.RowDefinitions.Count - 1;
            var label = new TextBlock { Text = $"{m.Name} ({m.Code})", VerticalAlignment = VerticalAlignment.Center, Foreground = WhiteBrush }; SetRow(label, r); modes.Children.Add(label);
            var on = LinkedToggle("m" + m.Code, ""); on.Margin = new(0); SetRow(on, r); SetColumn(on, 1); modes.Children.Add(on);
            var directions = LinkedText("d" + m.Code, double.NaN); SetRow(directions, r); SetColumn(directions, 2); modes.Children.Add(directions);
            if (m.Code is "A1" or "A2" or "SC" or "S1" or "S2" or "S3" or "S4") { var moving = LinkedToggle(m.Code + "mv", ""); moving.Margin = new(0); SetRow(moving, r); SetColumn(moving, 3); modes.Children.Add(moving); }
        }
        var columns = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 24, LineSpacing = 12 };
        columns.Children.Add(new StackPanel { Width = 380, Spacing = 4, Children = { new TextBlock { Text = "BODY PARTS", FontSize = 11, Foreground = Heading }, parts } });
        columns.Children.Add(new StackPanel { Width = 380, Spacing = 4, Children = { new TextBlock { Text = "MODES", FontSize = 11, Foreground = Heading }, modes } });
        body.Children.Add(columns);
        return Card("Look (monstats2)", body, note: "What the animation is built from. These fields are in monstats2, the row MonStatsEx names; the preview above follows them as you type.");
    }

    private TextBox LinkedText(string column, double width)
    {
        var box = new TextBox { Width = width, MinHeight = 30, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(box, "monstats2 " + column);
        box.TextChanged += (_, _) => CommitLinked(column, box.Text ?? "");
        GroupLinked(box);
        linked[column] = box;
        return box;
    }

    private CheckBox LinkedToggle(string column, string label)
    {
        var box = new CheckBox { Content = label, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 14, 0, 0) };
        if (ColumnGuide.Find("monstats2", column)?.Description is { } guide) ToolTip.SetTip(box, $"{column}: {guide}");
        AutomationProperties.SetName(box, "monstats2 " + column);
        box.IsCheckedChanged += (_, _) => CommitLinked(column, box.IsChecked == true ? "1" : "0");
        linked[column] = box;
        return box;
    }

    private void GroupLinked(Control input)
    {
        bool open = false; Document? grouped = null;
        input.GotFocus += (_, _) => { if (!open && looksEditable && looks != null) { open = true; grouped = looks; grouped.BeginEditGroup(); } };
        input.LostFocus += (_, _) => { if (open && !input.IsKeyboardFocusWithin) { open = false; grouped?.EndEditGroup(); } };
        input.DetachedFromVisualTree += (_, _) => { if (open) { open = false; grouped?.EndEditGroup(); } };
    }

    /// <summary>The monstats2 row this monster looks like: MonStatsEx, else its own Id.</summary>
    private int LookRow()
    {
        if (looks?.Table is not { } table || Table is not { } monsters || SelectedRow < 0) return -1;
        var key = monsters.ColumnIndex("MonStatsEx") >= 0 && monsters.Cell(SelectedRow, "MonStatsEx") is { Length: > 0 } ex ? ex : monsters.Cell(SelectedRow, "Id");
        return Enumerable.Range(0, table.Records.Count).FirstOrDefault(i => table.Cell(i, "Id") == key, -1);
    }

    private void CommitLinked(string column, string value)
    {
        if (loading || !looksEditable || looks?.Table is not { } table || table.ColumnIndex(column) < 0) return;
        int row = LookRow(); if (row < 0 || table.Cell(row, column) == value) return;
        try { looks.SetCells([(row, column, value)]); SetStatus($"monstats2 {column} = {(value.Length == 0 ? "(empty)" : value)} · save monstats2 to keep it."); }
        catch (Exception ex) { SetStatus(ex.Message, true); SyncLinked(); }
    }

    /// <summary>Loads monstats2: the open tab's document when there is one, else a private copy read from disk (read-only here).</summary>
    private void EnsureLooks()
    {
        if (looks != null) return;
        if (host.FindTable?.Invoke("monstats2") is { } open) { Attach(open, true); return; }
        var project = host.Project(); if (project == null) return;
        var file = TableData.FileFor(project, "tables", "monstats2");
        if (!File.Exists(file)) { lookNote.Text = "This project has no monstats2 table, so the monster's look cannot be read."; return; }
        PendingLooks = LoadLooksAsync(file);
    }

    private async Task LoadLooksAsync(string file)
    {
        try
        {
            var copy = await Task.Run(() => new Document(file));
            if (looks == null) Attach(copy, false);
        }
        catch (Exception ex) { lookNote.Text = "monstats2 unavailable: " + ex.Message; }
    }

    private async Task OpenLooksAsync()
    {
        Storage.Require(host.OpenTable != null, "This window cannot open monstats2.");
        var document = await host.OpenTable!("monstats2");
        Storage.Require(document != null, "This project has no monstats2 table.");
        Attach(document!, true);
    }

    private void Attach(Document document, bool editable)
    {
        if (looks != null) looks.Changed -= LooksChanged;
        looks = document; looksEditable = editable;
        looks.Changed += LooksChanged;
        LooksChanged();
    }

    private void LooksChanged()
    {
        if (!Visible) return;
        SyncLinked();
        compositeKey = null; RequestComposite();
    }

    private void SyncLinked()
    {
        makeEditable.IsVisible = !looksEditable;
        int row = LookRow(); var table = looks?.Table;
        lookNote.Text = table == null ? lookNote.Text : row < 0 ? $"No monstats2 row with Id {(Table is { } t && SelectedRow >= 0 ? t.Cell(SelectedRow, "MonStatsEx") : "")}." : looksEditable ? $"monstats2 row {row} · edits go to the monstats2 tab; save it to keep them." : $"monstats2 row {row} · read-only until monstats2 is opened.";
        bool wasLoading = loading; loading = true;
        try
        {
            foreach (var (column, editor) in linked)
            {
                var value = table != null && row >= 0 && table.ColumnIndex(column) >= 0 ? table.Cell(row, column) : "";
                editor.IsEnabled = looksEditable && row >= 0 && table?.ColumnIndex(column) >= 0;
                if (editor is TextBox box && box.Text != value) box.Text = value;
                if (editor is CheckBox check && check.IsChecked != (value == "1")) check.IsChecked = value == "1";
            }
        }
        finally { loading = wasLoading; }
    }

    protected override void OnSynced(TableData table, int row)
    {
        string Cell(string column) => table.ColumnIndex(column) >= 0 ? table.Cell(row, column) : "";
        double Number(string column) => double.TryParse(Cell(column), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
        walkHint.Text = $"{Number("Velocity") / PlayerWalk:P0} of a player's walk";
        runHint.Text = $"{Number("Run") / PlayerRun:P0} of a player's run";
        EnsureLooks();
        SyncLinked();
        RequestComposite();
    }

    // ── The animation ──────────────────────────────────────────────────────────────────────────────────

    private Control Hero()
    {
        foreach (var shared in new Control[] { sceneImage, sceneCaption, mode, lookChoice, speed, zoom, act, facing, play }) Detach(shared);
        stage = new StackPanel { Spacing = 8, MinWidth = 320, MaxWidth = StageWidth() };
        var picture = new Grid();
        picture.Children.Add(new Border { BorderBrush = new SolidColorBrush(Color.Parse("#4A4030")), BorderThickness = new(1), CornerRadius = new(4), ClipToBounds = true, Child = sceneImage, HorizontalAlignment = HorizontalAlignment.Left });
        sceneNotice = new ContentControl { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new(12) };
        picture.Children.Add(sceneNotice);
        stage.Children.Add(picture);
        stage.Children.Add(sceneCaption);
        var controls = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 10, LineSpacing = 6 };
        controls.Children.Add(Labeled("Mode", mode, "Animation modes the graphics have. Modes monstats2 turns off are marked; the game never plays them."));
        controls.Children.Add(Labeled("Look", lookChoice, "Monsters roll one of each part's variants when they spawn; page through them here."));
        controls.Children.Add(Labeled("Playback", play));
        controls.Children.Add(Labeled("Speed", speed));
        controls.Children.Add(Labeled("Zoom", zoom));
        controls.Children.Add(Labeled("Facing (or click)", facing));
        controls.Children.Add(Labeled("Palette", act));
        stage.Children.Add(controls);
        compositeNote = new TextBlock { FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap, MaxWidth = 720 };
        stage.Children.Add(compositeNote);
        infobox = new ContentControl { Content = new TextBlock { Text = "Resolving…", Foreground = Muted } };
        var box = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(242, 8, 8, 9)), BorderBrush = new SolidColorBrush(Color.Parse("#3B3325")), BorderThickness = new(1), CornerRadius = new(2),
            Padding = new(18, 14), MinWidth = 300, MaxWidth = 380, VerticalAlignment = VerticalAlignment.Top, Child = infobox
        };
        return new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 16, LineSpacing = 12, Children = { stage, box } };
    }

    private static void Detach(Control control)
    {
        switch (control.Parent)
        {
            case Panel panel: panel.Children.Remove(control); break;
            case Decorator decorator: decorator.Child = null; break;
            case ContentControl content: content.Content = null; break;
        }
    }

    /// <summary>Reassembles the animation when the mode, look, token, colour shift, palette or monstats2 row changed.</summary>
    private void RequestComposite()
    {
        var project = host.Project(); var table = Table; int row = SelectedRow;
        if (project == null || table == null || row < 0) return;
        string Cell(string column) => table.ColumnIndex(column) >= 0 ? table.Cell(row, column) : "";
        int lookRow = LookRow(); var lookTable = looks?.Table;
        var current = MonsterGraphics.Look(Cell("Code"), int.TryParse(Cell("TransLvl"), out var shift) ? shift : 0,
            c => lookTable != null && lookRow >= 0 && lookTable.ColumnIndex(c) >= 0 ? lookTable.Cell(lookRow, c) : "");
        look = current;
        RefreshChoices(current);
        var gameData = host.GameData(); int palette = act.SelectedIndex + 1; var modeCode = Mode; int variant = Math.Max(0, lookChoice.SelectedIndex);
        var key = string.Join('|', project.Root, current.Token, current.BaseWeapon, current.TransLvl, string.Join(';', current.Variants.Select(v => v.Key + "=" + string.Join(',', v.Value))),
            modeCode, variant, palette, string.Join(';', gameData));
        if (key == compositeKey) { if (!playing) DrawScene(); return; }
        compositeKey = key;
        PendingComposite = LoadCompositeAsync(project, gameData, current, modeCode, variant, palette);
    }

    /// <summary>The mode and look pickers follow the monster: every mode, marked when monstats2 turns it off, and as many looks as its parts offer.</summary>
    private void RefreshChoices(MonsterLook current)
    {
        choosingMode = true;
        try
        {
            var choices = MonsterGraphics.Modes.Select(m => new ModeChoice(m, current.Modes.Contains(m.Code))).ToArray();
            var selected = Mode;
            if (mode.ItemsSource is not ModeChoice[] old || !old.SequenceEqual(choices))
            {
                mode.ItemsSource = choices;
                // Until a mode is picked the monster walks, the mode that shows most about it.
                mode.SelectedItem = choices.FirstOrDefault(c => modePicked && c.Mode.Code == selected && (c.Enabled || current.Modes.Length == 0))
                    ?? choices.FirstOrDefault(c => c.Mode.Code == "WL" && c.Enabled) ?? choices.FirstOrDefault(c => c.Enabled) ?? choices[0];
            }
            var looksOffered = Enumerable.Range(1, current.LookCount).Select(i => $"Look {i}").ToArray();
            if (lookChoice.ItemsSource is not string[] offered || !offered.SequenceEqual(looksOffered))
            {
                int index = Math.Clamp(lookChoice.SelectedIndex, 0, looksOffered.Length - 1);
                lookChoice.ItemsSource = looksOffered; lookChoice.SelectedIndex = index;
            }
        }
        finally { choosingMode = false; }
    }

    private async Task LoadCompositeAsync(ModProject project, IReadOnlyList<string> gameData, MonsterLook current, string modeCode, int variant, int paletteAct)
    {
        compositeCancellation?.Cancel();
        var work = compositeCancellation = new CancellationTokenSource(); var token = work.Token;
        int step = Facing;
        try
        {
            var (loaded, colors) = await Task.Run(() =>
            {
                var result = MonsterGraphics.Composite(project, gameData, current, modeCode, variant, token);
                // Decode the facing on screen now, so the first frames do not stall the animation.
                if (result.Cof != null) foreach (var layer in result.Layers.Where(l => l.Animation != null)) layer.Animation!.Direction(Dcc.Direction(step, layer.Animation.DirectionCount));
                return (result, MissileGraphics.Palette(project, gameData, paletteAct));
            }, token);
            if (token.IsCancellationRequested) return;
            composite = loaded; palette = colors;
            var notes = new List<string>();
            if (loaded.Cof is { } cof)
                notes.Add($"{Path.GetFileName(loaded.CofFile!.Path)} · {loaded.Layers.Count(l => l.Animation != null)} of {cof.Layers.Length} parts · {cof.Directions} directions × {cof.FramesPerDirection} frames · speed {cof.Speed}/256 ({cof.Speed * 25 / 256.0:0.#} fps) · from {loaded.CofFile.Origin}"
                    + (loaded.Shift != null ? $" · colour shift {current.TransLvl}" : "")
                    + (cof.Events.Select((e, i) => (e, i)).FirstOrDefault(x => x.e == 1) is { e: 1 } hit ? $" · attack lands on frame {hit.i + 1}" : ""));
            notes.AddRange(loaded.Notes);
            if (colors == null) notes.Add($"No act {paletteAct} palette (data/global/palette/act{paletteAct}/pal.dat) in the project or the game data.");
            compositeNote.Text = string.Join("\n", notes);
            sceneNotice.Content = !loaded.Drawable && gameData.Count == 0 ? NoticeWithChooser(loaded.Notes.FirstOrDefault() ?? "") : null;
            DrawScene();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) compositeNote.Text = "Animation unavailable: " + ex.Message; }
        finally { if (ReferenceEquals(compositeCancellation, work)) compositeCancellation = null; work.Dispose(); }
    }

    private Control NoticeWithChooser(string message)
    {
        var panel = new StackPanel { Spacing = 6, MaxWidth = 320 };
        panel.Children.Add(new TextBlock { Text = message, Foreground = WhiteBrush, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(Bitmaps.ChooseGameDataButton(host, SetStatus));
        return new Border { Background = new SolidColorBrush(Color.FromArgb(220, 20, 18, 16)), Padding = new(10), CornerRadius = new(4), Child = panel };
    }

    private void Animate()
    {
        if (!Visible || SelectedRow < 0) { animation.Stop(); return; }
        if (!playing) return;
        int hold = speed.SelectedIndex switch { 1 => 2, 2 => 4, _ => 1 };
        if (++frameSkip < hold) return;
        frameSkip = 0; tick++;
        DrawScene();
    }

    private MonsterSceneState DrawScene()
    {
        var current = look ?? MonsterGraphics.Look("", 0, _ => "");
        var chosen = MonsterGraphics.Modes.FirstOrDefault(m => m.Code == Mode) ?? MonsterGraphics.Modes[0];
        var table = Table; int row = SelectedRow;
        double velocity = 0;
        if (chosen.Moves && table != null && row >= 0 && double.TryParse(table.Cell(row, chosen.Code == "RN" ? "Run" : "Velocity"), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) velocity = v;
        // A death plays once and holds its last frame a moment before starting again.
        int at = tick;
        if (chosen.Once && composite?.Cof is { } cof) { int length = (cof.FramesPerDirection * 256 + cof.Speed - 1) / Math.Max(1, cof.Speed) + 25; at = tick % length; }
        var state = scene.Render(at, (int)facing.Value, composite ?? new MonsterComposite(Mode, null, null, [], null, []), current, palette, velocity, chosen.Once);
        LastScene = state;
        Bitmaps.Copy(frame, SceneWidth, SceneHeight, scene.Pixels);
        sceneImage.InvalidateVisual();
        sceneCaption.Text = state.Frame < 0 ? "Nothing to draw for this mode." :
            $"{chosen.Name} · frame {state.Frame + 1} of {state.Frames} · direction {state.Direction}"
            + (velocity > 0 ? $" · moving {velocity:0.#} ({velocity / (chosen.Code == "RN" ? PlayerRun : PlayerWalk):P0} of a player's {(chosen.Code == "RN" ? "run" : "walk")})" : "")
            + state.Event switch { 1 => " · ⚔ attack lands", 2 => " · missile fires", 3 => " · sound", 4 => " · skill", _ => "" };
        sceneCaption.Foreground = state.Event != 0 ? EventBrush : Muted;
        return state;
    }

    // ── Infobox and details ────────────────────────────────────────────────────────────────────────────

    protected override async Task PreviewAsync()
    {
        previewCancellation?.Cancel();
        var work = previewCancellation = new CancellationTokenSource(); var token = work.Token;
        var project = host.Project(); var record = SelectedRecord(); int row = SelectedRow;
        if (project == null || record == null) { work.Dispose(); if (ReferenceEquals(previewCancellation, work)) previewCancellation = null; return; }
        string profile = host.Profile(), language = Locale; int workspace = host.Workspace(), revision = Document.Revision; var gameData = host.GameData();
        try
        {
            if (host.DirtyDependency(pane) is { } dirty) { infobox.Content = new TextBlock { Text = "Save the edited dependency to preview: " + dirty, Foreground = Brushes.Salmon, TextWrapping = TextWrapping.Wrap }; return; }
            if (previewContext != (project.Root, workspace)) { previewResolver.Clear(); previewContext = (project.Root, workspace); }
            var result = await previewWork.RunAsync(ct => previewResolver.Resolve(project, "monstats", record, profile, language, ct, null, gameData), token);
            if (token.IsCancellationRequested || revision != Document.Revision || SelectedRow != row) return;
            LastPreview = result;
            ShowInfobox();
            details.Content = host.MonsterDetails?.Invoke(result) ?? PreviewCards.Card(result.Name, [PreviewCards.Prose(result.Text)]);
            UpdateTitle();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) infobox.Content = new TextBlock { Text = ex.Message, Foreground = Brushes.Salmon, TextWrapping = TextWrapping.Wrap }; }
        finally { if (ReferenceEquals(previewCancellation, work)) previewCancellation = null; work.Dispose(); }
    }

    /// <summary>The monster at one difficulty, drawn like a game panel: name, type, then its numbers, each linked to the fields it is read from.</summary>
    private void ShowInfobox()
    {
        if (LastPreview is not { } result) return;
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new SelectableTextBlock { Text = result.Name, FontSize = 20, FontFamily = TooltipFont, Foreground = MonsterBrush, TextWrapping = TextWrapping.Wrap });
        if (result.Text.Length > 0) panel.Children.Add(new PreviewLinkText([result.Text[0]], Muted) { Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new(0, 4, 0, 4) };
        for (int d = 0; d < 3; d++)
        {
            int chosen = d;
            var tab = new ToggleButton { Content = DifficultyNames[d], IsChecked = d == difficulty, Padding = new(10, 3), MinHeight = 0, FontSize = 12 };
            tab.Click += (_, _) => { difficulty = chosen; ShowInfobox(); };
            tabs.Children.Add(tab);
        }
        panel.Children.Add(tabs);
        var levels = result.Levels.Where(l => l.Difficulty.StartsWith(DifficultyNames[difficulty], StringComparison.Ordinal)).ToArray();
        if (levels.Length == 0) panel.Children.Add(new TextBlock { Text = "No stats for this difficulty.", Foreground = Muted });
        foreach (var level in levels)
        {
            var stats = new Grid { ColumnDefinitions = new("110,*"), RowSpacing = 3 };
            void Row(string label, string value, string source, IBrush? brush = null)
            {
                if (value.Length == 0) return;
                stats.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); int r = stats.RowDefinitions.Count - 1;
                var name = new TextBlock { Text = label, Foreground = Muted, FontSize = 13 }; SetRow(name, r); stats.Children.Add(name);
                var links = level.Sources?.GetValueOrDefault(source) ?? [];
                var text = new PreviewLinkText([new PreviewText(value, links.Length > 0 ? [new LinkSpan(0, value.Length, links)] : [])], brush ?? WhiteBrush)
                    { Foreground = brush ?? WhiteBrush, FontSize = 14, FontFamily = TooltipFont, TextWrapping = TextWrapping.Wrap };
                SetRow(text, r); SetColumn(text, 1); stats.Children.Add(text);
            }
            Row("Level", level.Level, "Lvl");
            Row("Life", level.Life, "Life", new SolidColorBrush(Color.Parse("#E5533D")));
            Row("Defense", level.Defense, "Defense");
            Row("Attack rating", level.AttackRating, "Attack");
            Row("Damage", level.Damage, "Damage");
            Row("Experience", level.Experience, "Exp", new SolidColorBrush(Color.Parse("#C7B377")));
            panel.Children.Add(stats);
            var resists = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 10, LineSpacing = 4 };
            foreach (var (label, value, brush) in new[] { level.Physical, level.Magic, level.Fire, level.Lightning, level.Cold, level.Poison }.Select((v, i) => (ResistColumns[i].Label, v, ResistColumns[i].Brush)))
            {
                var shown = value.Length == 0 ? "0" : value;
                var immune = int.TryParse(shown, out var number) && number >= 100;
                var links = level.Sources?.GetValueOrDefault(label) ?? [];
                resists.Children.Add(new PreviewLinkText([new PreviewText($"{label} {(immune ? "immune" : shown + "%")}", links.Length > 0 ? [new LinkSpan(0, label.Length + 1 + (immune ? 6 : shown.Length + 1), links)] : [])], brush)
                    { Foreground = brush, FontSize = 12, FontWeight = immune ? FontWeight.Bold : FontWeight.Normal });
            }
            panel.Children.Add(resists);
        }
        if (Table is { } table && SelectedRow >= 0)
        {
            string Cell(string column) => table.ColumnIndex(column) >= 0 ? table.Cell(SelectedRow, column) : "";
            static string OrZero(string value) => value.Length == 0 ? "0" : value;
            panel.Children.Add(new TextBlock { Text = $"Speed · walk {OrZero(Cell("Velocity"))} · run {OrZero(Cell("Run"))}  (a player walks 6, runs 9)", Foreground = Muted, FontSize = 12, Margin = new(0, 4, 0, 0) });
        }
        if (result.Issues.Length > 0) panel.Children.Add(new SelectableTextBlock { Text = string.Join("\n", result.Issues.Take(4)), Foreground = Brushes.Salmon, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "Click a number to edit the field it comes from.", Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        infobox.Content = panel;
    }
}

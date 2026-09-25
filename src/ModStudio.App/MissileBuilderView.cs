using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Automation;
using Avalonia.Threading;
using ModStudio.Core;
using static ModStudio.Core.Storage;

namespace ModStudio.App;

/// <summary>
/// The Visual Builder for missiles: the missile flying across an isometric floor as the legacy renderer draws it (its DCC
/// animation in an act palette, blended per Trans, with its light radius, velocity, range and explosion), what HD loads
/// for it, and every field grouped by what it does. The flight, the per-level damage and the function hints follow each edit.
/// </summary>
internal sealed class MissileBuilderView : TableBuilderView<MissileEntry, MissileBuilderCatalog>
{
    private static readonly IBrush MissileBrush = new SolidColorBrush(Color.Parse("#E8A04C"));
    private const int SceneWidth = 640, SceneHeight = 300;
    /// <summary>Compass names of the 16 facings the scene labels, clockwise from down the screen.</summary>
    private static readonly string[] Compass = ["S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW", "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE"];
    private static readonly (string Title, string Note, (string Column, string Label)[] Fields)[] Groups = [
        ("Graphics", "The legacy animation and how it plays. Legacy only: HD ignores these and draws the effect chosen in the HD card above.", [
            ("CelFile", "Animation (CelFile)"), ("Trans", "Blend (Trans)"), ("AnimSpeed", "Anim speed (16ths)"), ("animrate", "Anim rate"), ("AnimLen", "Anim length"), ("LoopAnim", "Loop"), ("RandStart", "Random start"),
            ("SubLoop", "Sub-loop"), ("SubStart", "Sub-loop start"), ("SubStop", "Sub-loop stop"), ("InitSteps", "Invisible for (frames)"), ("xoffset", "X offset"), ("yoffset", "Y offset"),
            ("zoffset", "Height (Z offset)"), ("NumDirections", "Directions"), ("LocalBlood", "Local blood"), ("MissileWeaponVFX", "Weapon VFX")]),
        ("Light", "The light radius the missile carries, in sub-tiles, and its colour.", [("Light", "Radius"), ("Flicker", "Flicker"), ("Red", "Red"), ("Green", "Green"), ("Blue", "Blue")]),
        ("Movement and collision", "Velocity is pixels per frame; range and delays are frames (25 a second).", [
            ("Vel", "Velocity"), ("MaxVel", "Max velocity"), ("VelLev", "Velocity per level"), ("Accel", "Acceleration"), ("Range", "Range (frames)"), ("LevRange", "Range per level"),
            ("Radius", "Radius"), ("Size", "Size"), ("Activate", "Active after (frames)"), ("CollideType", "Collide type"), ("CollideKill", "Dies on collision"), ("CollideFriend", "Hits allies"),
            ("LastCollide", "Last collide"), ("Collision", "Collision"), ("ClientCol", "Client collision"), ("CollisionOverlap", "Overlap"), ("Pierce", "Pierce"), ("CanDestroy", "Can be destroyed"),
            ("NextHit", "Next hit"), ("NextDelay", "Next delay"), ("Explosion", "Explodes"), ("AlwaysExplode", "Always explodes"), ("ToHit", "Uses to-hit"), ("Town", "Works in town"),
            ("SrcTown", "Source town"), ("CltSrcTown", "Client source town"), ("NoMultiShot", "No multishot"), ("NoUniqueMod", "No unique mods"), ("Holy", "Holy"), ("CanSlow", "Can slow"),
            ("ReturnFire", "Return fire"), ("GetHit", "Causes get-hit"), ("SoftHit", "Soft hit"), ("KnockBack", "Knockback")]),
        ("Damage", "Physical and elemental damage in 256ths scaled by HitShift, growing by level tier. Skill borrows another skill's damage instead.", [
            ("Skill", "Borrow damage from skill"), ("SrcDamage", "Source damage"), ("HitShift", "HitShift"), ("MinDamage", "Min damage"), ("MinLevDam1", "Min /lvl 2-8"), ("MinLevDam2", "Min /lvl 9-16"),
            ("MinLevDam3", "Min /lvl 17-22"), ("MinLevDam4", "Min /lvl 23-28"), ("MinLevDam5", "Min /lvl 29+"), ("MaxDamage", "Max damage"), ("MaxLevDam1", "Max /lvl 2-8"), ("MaxLevDam2", "Max /lvl 9-16"),
            ("MaxLevDam3", "Max /lvl 17-22"), ("MaxLevDam4", "Max /lvl 23-28"), ("MaxLevDam5", "Max /lvl 29+"), ("DmgSymPerCalc", "Damage synergy calc"), ("EType", "Element"), ("EMin", "Elem min"),
            ("MinELev1", "Elem min /lvl 2-8"), ("MinELev2", "Elem min /lvl 9-16"), ("MinELev3", "Elem min /lvl 17-22"), ("MinELev4", "Elem min /lvl 23-28"), ("MinELev5", "Elem min /lvl 29+"),
            ("EMax", "Elem max"), ("MaxELev1", "Elem max /lvl 2-8"), ("MaxELev2", "Elem max /lvl 9-16"), ("MaxELev3", "Elem max /lvl 17-22"), ("MaxELev4", "Elem max /lvl 23-28"), ("MaxELev5", "Elem max /lvl 29+"),
            ("EDmgSymPerCalc", "Elem synergy calc"), ("ELen", "Elem length"), ("ELevLen1", "Length /lvl 2-8"), ("ELevLen2", "Length /lvl 9-16"), ("ELevLen3", "Length /lvl 17+"),
            ("HitClass", "Hit class"), ("DamageRate", "Damage rate"), ("ApplyMastery", "Apply mastery"), ("Half2HSrc", "Half 2H source"), ("SrcMissDmg", "Source missile damage"),
            ("ResultFlags", "Result flags"), ("HitFlags", "Hit flags"), ("MissileSkill", "Missile skill")]),
        ("Functions", "What the game code does each frame and on hit, and the parameters and calculations it reads.", [
            ("pCltDoFunc", "Client do func"), ("pCltHitFunc", "Client hit func"), ("pSrvDoFunc", "Server do func"), ("pSrvHitFunc", "Server hit func"), ("pSrvDmgFunc", "Server damage func"),
            ("SrvCalc1", "Server calc"), ("Param1", "Param 1"), ("Param2", "Param 2"), ("Param3", "Param 3"), ("Param4", "Param 4"), ("Param5", "Param 5"),
            ("CltCalc1", "Client calc"), ("CltParam1", "Client param 1"), ("CltParam2", "Client param 2"), ("CltParam3", "Client param 3"), ("CltParam4", "Client param 4"), ("CltParam5", "Client param 5"),
            ("SHitCalc1", "Server hit calc"), ("sHitPar1", "Server hit param 1"), ("sHitPar2", "Server hit param 2"), ("sHitPar3", "Server hit param 3"),
            ("CHitCalc1", "Client hit calc"), ("cHitPar1", "Client hit param 1"), ("cHitPar2", "Client hit param 2"), ("cHitPar3", "Client hit param 3"),
            ("DmgCalc1", "Damage calc"), ("dParam1", "Damage param 1"), ("dParam2", "Damage param 2")]),
        ("Sounds", "Sounds are names from sounds.txt; the overlay is from overlay.txt.", [("TravelSound", "Travel"), ("HitSound", "Hit"), ("OnDiedSound", "On died"), ("ProgSound", "Progressive"), ("ProgOverlay", "Progressive overlay")])];
    private static readonly string[] SpawnColumns = ["ExplosionMissile", "SubMissile1", "SubMissile2", "SubMissile3", "HitSubMissile1", "HitSubMissile2", "HitSubMissile3", "HitSubMissile4",
        "CltSubMissile1", "CltSubMissile2", "CltSubMissile3", "CltHitSubMissile1", "CltHitSubMissile2", "CltHitSubMissile3", "CltHitSubMissile4"];

    private readonly MissileBuilderResolver catalogResolver = new();
    private readonly MissilePreviewResolver previewResolver = new();
    private readonly PreviewWorkQueue previewWork = new();
    private CancellationTokenSource? previewCancellation, artCancellation;
    private (string? Project, int Workspace) previewContext;
    private readonly MissileScene scene = new(SceneWidth, SceneHeight);
    private readonly WriteableBitmap frame;
    private readonly Image sceneImage;
    private readonly DispatcherTimer animation = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly Slider facing = new() { Minimum = 0, Maximum = 63, Value = 44, SmallChange = 1, LargeChange = 4, Width = 200, TickFrequency = 4, IsSnapToTickEnabled = false };
    private readonly ComboBox act = new() { ItemsSource = new[] { "Act 1", "Act 2", "Act 3", "Act 4", "Act 5" }, SelectedIndex = 0, MinWidth = 90 };
    private readonly ComboBox speed = new() { ItemsSource = new[] { "1×", "½×", "¼×" }, SelectedIndex = 0, MinWidth = 70 };
    private readonly NumericUpDown skillLevel = new() { Minimum = 1, Maximum = 99, Value = 1, Width = 120, FormatString = "0" };
    private readonly Button play = new() { Content = "Pause", Padding = new(10, 3), MinHeight = 0, FontSize = 12, Width = 70 };
    private readonly TextBlock sceneCaption = new() { FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap }, facingLabel = new() { FontSize = 11, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center };
    private TextBlock artNote = new(), lightSwatchLabel = new();
    private Border lightSwatch = new();
    private ContentControl hdHost = new(), damageHost = new(), sceneNotice = new();
    private readonly Dictionary<string, TextBlock> hints = new(StringComparer.Ordinal);
    private int tick, frameSkip;
    private bool playing = true;
    private string? artKey;
    private MissileMotion motion = MissileMotion.Read(_ => "");
    private SceneSprite? sprite, explosionSprite;

    internal MissileArt? LastArt { get; private set; }
    internal MissileArt? LastExplosionArt { get; private set; }
    internal HdMissile? LastHd { get; private set; }
    internal MissilePreviewResult? LastPreview { get; private set; }
    internal SceneState? LastScene { get; private set; }
    internal MissileMotion Motion => motion;
    internal Task PendingArt { get; private set; } = Task.CompletedTask;
    internal int Facing { get => (int)facing.Value; set => facing.Value = value; }
    /// <summary>Draws one frame of the flight now (tests and captures step the scene instead of waiting on the timer).</summary>
    internal SceneState RenderScene(int at) { tick = at; return DrawScene(); }

    public MissileBuilderView(EditorPane pane, VisualBuilderHost host) : base(pane, host)
    {
        frame = Bitmaps.From(SceneWidth, SceneHeight, scene.Pixels);
        sceneImage = new Image { Source = frame, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, MaxWidth = SceneWidth, HorizontalAlignment = HorizontalAlignment.Left, Cursor = new Cursor(StandardCursorType.Cross) };
        RenderOptions.SetBitmapInterpolationMode(sceneImage, BitmapInterpolationMode.None);
        ToolTip.SetTip(sceneImage, "Click to aim the missile: it flies from the caster toward where you click.");
        sceneImage.PointerPressed += (_, e) =>
        {
            var point = e.GetPosition(sceneImage);
            double scale = sceneImage.Bounds.Width > 0 ? SceneWidth / sceneImage.Bounds.Width : 1;
            facing.Value = scene.FacingToward(point.X * scale, point.Y * scale); tick = 0;
        };
        facing.ValueChanged += (_, _) => { UpdateFacingLabel(); tick = 0; if (!playing) DrawScene(); };
        act.SelectionChanged += (_, _) => { artKey = null; RequestArt(); };
        speed.SelectionChanged += (_, _) => { frameSkip = 0; };
        skillLevel.ValueChanged += (_, _) => SchedulePreview();
        play.Click += (_, _) => { playing = !playing; play.Content = playing ? "Pause" : "Play"; if (playing) animation.Start(); };
        animation.Tick += (_, _) => Animate();
        UpdateFacingLabel();
        Ready();
    }

    protected override string ListHeading => "MISSILES";
    protected override string SearchHint => "Search by id, animation, skill or explosion";
    protected override string AddLabel => "+ New missile";
    protected override string Noun => "a missile";
    protected override string NameColumn => "Missile";
    protected override IBrush TitleBrush => MissileBrush;
    protected override string CopyName(string name) => name + "_copy";
    protected override string MoreColumnsHint => " · everything else in missiles.txt";

    protected override JsonObject NewRow(TableData table)
    {
        var fields = new JsonObject();
        foreach (var (column, value) in new[] { ("Missile", "newmissile"), ("Vel", "10"), ("Range", "50"), ("AnimSpeed", "16"), ("LoopAnim", "1"), ("Trans", "1"), ("Light", "4"),
            ("Red", "255"), ("Green", "255"), ("Blue", "255"), ("Size", "1"), ("CollideType", "3"), ("CollideKill", "1"), ("Collision", "1") })
            if (table.ColumnIndex(column) >= 0) fields[column] = value;
        return fields;
    }

    protected override IReadOnlyList<object> ListRows(TableData table) => MissileGraphics.Rows(table);
    protected override MissileBuilderCatalog LoadCatalog(ModProject project, string profile, string locale, IReadOnlyList<object> rows, bool fresh, CancellationToken token)
    {
        if (fresh) catalogResolver.Clear();
        return catalogResolver.Catalog(project, profile, locale, host.GameData(), [.. rows.Cast<MissileRow>()], token);
    }
    protected override IReadOnlyList<MissileEntry> EntriesOf(MissileBuilderCatalog catalog) => catalog.Entries;
    protected override int RowOf(MissileEntry entry) => entry.Row;
    protected override string SourceIdOf(MissileEntry entry) => entry.SourceId;
    protected override bool IsInactive(MissileEntry entry) => entry.Inactive;
    protected override string SearchTextOf(MissileEntry entry) => entry.SearchText;

    protected override Control EntryView(MissileEntry entry)
    {
        var panel = new StackPanel { Margin = new(2, 3) };
        panel.Children.Add(new TextBlock { Text = entry.Id.Length > 0 ? entry.Id : $"Row {entry.Row}", FontSize = 13, Foreground = MissileBrush, TextTrimming = TextTrimming.CharacterEllipsis });
        var parts = new[] { entry.CelFile.Length > 0 && !entry.CelFile.Equals("null", StringComparison.OrdinalIgnoreCase) ? entry.CelFile : "no animation",
            entry.FiredBy.Length > 0 ? "fired by " + entry.FiredBy : "", entry.Explosion.Length > 0 ? "→ " + entry.Explosion : "" }.Where(s => s.Length > 0);
        panel.Children.Add(new TextBlock { Text = string.Join(" · ", parts), FontSize = 11, Foreground = Muted, TextTrimming = TextTrimming.CharacterEllipsis });
        ToolTip.SetTip(panel, $"row {entry.Row}" + (entry.FiredBy.Length > 0 ? $"\nFired by {entry.FiredBy}" : ""));
        return panel;
    }

    private string[] MissileIds() => catalog?.Entries.Where(e => !e.Inactive).Select(e => e.Id).ToArray() ?? [];

    protected override void OnSelect() { LastArt = LastExplosionArt = null; LastHd = null; LastPreview = null; artKey = null; tick = 0; sprite = explosionSprite = null; }
    protected override void Stop() { previewCancellation?.Cancel(); artCancellation?.Cancel(); animation.Stop(); }
    protected override void OnInvalidate() => artKey = null;
    protected override void OnBuiltEmpty() { hints.Clear(); animation.Stop(); }

    protected override void BuildCards(StackPanel root, TableData table)
    {
        hints.Clear();
        root.Children.Add(Hero());
        root.Children.Add(SpawnsCard(table));
        foreach (var group in Groups)
        {
            var fields = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 12, LineSpacing = 10 };
            foreach (var (column, label) in group.Fields)
                if (FieldFor(table, column, label, group.Title == "Functions") is { } field) fields.Children.Add(field);
            if (fields.Children.Count == 0) continue;
            Control body = fields;
            if (group.Title == "Light") body = LightBody(fields);
            if (group.Title == "Damage")
            {
                var stack = new StackPanel { Spacing = 12 };
                var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                Detach(skillLevel);
                options.Children.Add(Labeled("Preview at skill level", skillLevel, "The level of the skill firing the missile; the table below covers every level."));
                damageHost = new ContentControl { Content = new TextBlock { Text = "Resolving damage…", Foreground = Muted } };
                stack.Children.Add(fields); stack.Children.Add(options); stack.Children.Add(damageHost);
                body = stack;
            }
            root.Children.Add(Card(group.Title, body, note: group.Note));
        }
        animation.Start();
    }

    /// <summary>
    /// The editor for one column: a picker for named references (animation, skill, sounds), a switch for the data guide's
    /// boolean fields, a numbered choice for Trans, else a text field. Function fields get a hint line naming what they select.
    /// </summary>
    private Control? FieldFor(TableData table, string column, string label, bool hinted)
    {
        if (table.ColumnIndex(column) < 0) return null;
        var guide = ColumnGuide.Find(table.Name, column)?.Description;
        string tip = guide is { } g ? $"{column}: {g}" : column;
        Control input = column switch
        {
            "CelFile" => NameChoice(column, "DCC name", 200, () => catalog?.CelFiles ?? []),
            "Skill" => NameChoice(column, "skill id", 200, () => catalog?.Skills ?? []),
            "Trans" => Numbered(column, 190, "0 · normal", "1 · additive light", "2 · translucent"),
            "TravelSound" or "HitSound" or "OnDiedSound" or "ProgSound" => NameChoice(column, "sound", 190, () => catalog?.Sounds ?? []),
            _ when guide?.StartsWith("Boolean", StringComparison.OrdinalIgnoreCase) == true => Toggle(column, label, guide),
            _ => Text(column, "", column.Contains("Calc", StringComparison.OrdinalIgnoreCase) ? 260 : column.StartsWith('p') && column.EndsWith("Func") ? 110 : 96)
        };
        if (input is CheckBox) return input;
        if (!hinted) return Labeled(label, input, tip);
        var hint = new TextBlock { FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap, MaxWidth = 260 };
        hints[column] = hint;
        var panel = (StackPanel)Labeled(label, input, tip);
        panel.Children.Add(hint);
        return panel;
    }

    private Control LightBody(WrapPanel fields)
    {
        lightSwatch = new Border { Width = 46, Height = 30, CornerRadius = new(4), BorderBrush = CardBorder, BorderThickness = new(1), VerticalAlignment = VerticalAlignment.Bottom };
        lightSwatchLabel = new TextBlock { FontSize = 11, Foreground = Muted, VerticalAlignment = VerticalAlignment.Bottom, Margin = new(0, 0, 0, 7) };
        var swatch = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Bottom };
        swatch.Children.Add(lightSwatch); swatch.Children.Add(lightSwatchLabel);
        fields.Children.Add(swatch);
        return fields;
    }

    /// <summary>The missiles this one spawns, each a picker over the table's missiles with a button that opens it here.</summary>
    private Control SpawnsCard(TableData table)
    {
        var grid = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 12, LineSpacing = 10 };
        foreach (var column in SpawnColumns.Where(c => table.ColumnIndex(c) >= 0))
        {
            var input = NameChoice(column, "missile", 170, MissileIds);
            var open = new Button { Content = "→", Padding = new(8, 2), MinHeight = 0, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(open, "Open this missile in the builder");
            open.Click += (_, _) => { if (Table is { } t && SelectedRow >= 0) SelectNamed(t.Cell(SelectedRow, column)); };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            row.Children.Add(input); row.Children.Add(open);
            grid.Children.Add(Labeled(column == "ExplosionMissile" ? "Explosion (shown in the preview)" : column, row, ColumnGuide.Find(table.Name, column)?.Description));
        }
        return Card("Spawns", grid, note: "Missiles created when this one explodes, travels or hits. The explosion plays at the end of the flight above.");
    }

    /// <summary>The flight, its controls, and what HD loads.</summary>
    private Control Hero()
    {
        var panel = new StackPanel { Spacing = 8 };
        // The scene and its controls outlive a rebuild of the editor; they move into the new one.
        foreach (var shared in new Control[] { sceneImage, sceneCaption, play, speed, facing, act, facingLabel }) Detach(shared);
        var picture = new Grid();
        picture.Children.Add(new Border { BorderBrush = new SolidColorBrush(Color.Parse("#4A4030")), BorderThickness = new(1), CornerRadius = new(4), ClipToBounds = true, Child = sceneImage, HorizontalAlignment = HorizontalAlignment.Left });
        sceneNotice = new ContentControl { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new(12) };
        picture.Children.Add(sceneNotice);
        panel.Children.Add(picture);
        panel.Children.Add(sceneCaption);
        var controls = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 10, LineSpacing = 6 };
        controls.Children.Add(Labeled("Playback", play));
        controls.Children.Add(Labeled("Speed", speed, "Slow the flight down to see each animation frame"));
        var aim = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        aim.Children.Add(facing); aim.Children.Add(facingLabel);
        controls.Children.Add(Labeled("Facing (or click the scene)", aim, "The direction the missile flies; the animation's matching direction is drawn."));
        controls.Children.Add(Labeled("Palette", act, "Legacy graphics are drawn in the palette of the act they appear in"));
        panel.Children.Add(controls);
        artNote = new TextBlock { FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(artNote);
        hdHost = new ContentControl();
        var hdCard = Card("HD", hdHost, note: "HD mode ignores CelFile and the legacy animation: it draws the particle effect data/hd/missiles/missiles.json names for this missile. Choose it here.");
        hdCard.VerticalAlignment = VerticalAlignment.Top; hdCard.MaxWidth = 380;
        // The HD card sits beside the flight when there is room and under it when there is not.
        return new WrapPanel { Children = { panel, hdCard }, ItemSpacing = 16, LineSpacing = 12, Orientation = Orientation.Horizontal };
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

    private void UpdateFacingLabel()
    {
        int step = (int)facing.Value;
        facingLabel.Text = $"{Compass[(int)Math.Round(step / 4.0) % 16]} · {step}";
    }

    protected override void OnSynced(TableData table, int row)
    {
        string Cell(string column) => table.ColumnIndex(column) >= 0 ? table.Cell(row, column) : "";
        motion = MissileMotion.Read(Cell);
        lightSwatch.Background = new SolidColorBrush(Color.FromRgb(motion.Red, motion.Green, motion.Blue));
        lightSwatchLabel.Text = motion.Light > 0 ? $"radius {motion.Light}" + (motion.Flicker > 0 ? $" + flicker {motion.Flicker}" : "") : "no light";
        if (hints.Count > 0)
        {
            var readers = FunctionGuide.Readers(FunctionGuide.ForRow(table.Name, table.Columns, Cell));
            foreach (var (column, hint) in hints) hint.Text = FieldInsight.Hint(table.Name, column, Cell(column), readers);
        }
        if (sprite != null) sprite = sprite with { Motion = motion };
        RequestArt();
        if (!playing) DrawScene();
    }

    protected override void OnCatalogLoaded() => artKey = null;
    protected override void OnShown() { if (SelectedRow >= 0) animation.Start(); }

    // ── The flight ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Loads the row's animation, its explosion's, the palette and the HD definition when any of them changed.</summary>
    private void RequestArt()
    {
        var project = host.Project(); var table = Table; int row = SelectedRow;
        if (project == null || table == null || row < 0) return;
        string Cell(string column) => table.ColumnIndex(column) >= 0 ? table.Cell(row, column) : "";
        var id = Cell("Missile"); var cel = Cell("CelFile"); var explosionId = Cell("ExplosionMissile");
        int explosionRow = explosionId.Length == 0 ? -1 : Enumerable.Range(0, table.Records.Count).FirstOrDefault(i => table.Cell(i, "Missile") == explosionId, -1);
        var explosionMotion = explosionRow < 0 ? null : MissileMotion.Read(c => table.ColumnIndex(c) >= 0 ? table.Cell(explosionRow, c) : "");
        var explosionCel = explosionRow < 0 ? "" : table.Cell(explosionRow, "CelFile");
        var gameData = host.GameData(); int palette = act.SelectedIndex + 1;
        // Missiles drawing the same legacy animation, whose HD effect a missile without one most likely wants (a copied row).
        var siblings = cel.Length == 0 ? [] : Enumerable.Range(0, table.Records.Count).Where(i => i != row && table.Cell(i, "CelFile").Equals(cel, StringComparison.OrdinalIgnoreCase))
            .Select(i => table.Cell(i, "Missile")).Where(m => m.Length > 0).Take(16).ToArray();
        var key = string.Join('|', project.Root, id, cel, explosionCel, palette, string.Join(';', gameData));
        // Timing and blending of the explosion follow its row even when its files are unchanged.
        if (explosionSprite != null && explosionMotion != null) explosionSprite = explosionSprite with { Motion = explosionMotion };
        if (key == artKey) return;
        artKey = key;
        PendingArt = LoadArtAsync(project, gameData, id, cel, explosionCel, explosionMotion, palette, siblings);
    }

    private async Task LoadArtAsync(ModProject project, IReadOnlyList<string> gameData, string id, string cel, string explosionCel, MissileMotion? explosionMotion, int palette, string[] siblings)
    {
        artCancellation?.Cancel();
        var work = artCancellation = new CancellationTokenSource(); var token = work.Token;
        try
        {
            var (art, explosion, colors, hd, suggested) = await Task.Run(() =>
            {
                var hd = MissileGraphics.Hd(project, gameData, id);
                (string? Missile, string? Unit) suggested = default;
                if (hd.Key == null)
                    foreach (var sibling in siblings)
                        if (MissileGraphics.Hd(project, gameData, sibling) is { File: not null } other) { suggested = (sibling, other.Unit); break; }
                return (MissileGraphics.Art(project, gameData, cel, token), explosionCel.Length == 0 ? null : MissileGraphics.Art(project, gameData, explosionCel, token),
                    MissileGraphics.Palette(project, gameData, palette), hd, suggested);
            }, token);
            if (token.IsCancellationRequested) return;
            LastArt = art; LastExplosionArt = explosion; LastHd = hd;
            sprite = art.Animation != null && colors != null ? new(art.Animation, colors, motion) : null;
            explosionSprite = explosion?.Animation != null && colors != null && explosionMotion != null ? new(explosion.Animation, colors, explosionMotion) : null;
            var notes = new List<string>();
            if (art.Animation is { } a && art.File is { } f)
                notes.Add($"{Path.GetFileName(f.Path)} · {a.Directions.Length} direction{(a.Directions.Length == 1 ? "" : "s")} × {a.FramesPerDirection} frames · {a.Directions[0].Frames[0].Width} × {a.Directions[0].Frames[0].Height} px · from {f.Origin}");
            notes.AddRange(art.Notes);
            if (explosion != null) notes.AddRange(explosion.Animation != null ? [$"Explosion {explosion.CelFile}.dcc · {explosion.Animation.FramesPerDirection} frames"] : explosion.Notes);
            if (colors == null) notes.Add($"No act {palette} palette (data/global/palette/act{palette}/pal.dat) in the project or the game data, so nothing can be coloured.");
            artNote.Text = string.Join("\n", notes);
            sceneNotice.Content = sprite == null && !art.Invisible && gameData.Count == 0 ? NoticeWithChooser(art.Notes.FirstOrDefault() ?? "") : null;
            hdHost.Content = HdView(project, id, hd, suggested.Missile, suggested.Unit);
            DrawScene();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) artNote.Text = "Animation unavailable: " + ex.Message; }
        finally { if (ReferenceEquals(artCancellation, work)) artCancellation = null; work.Dispose(); }
    }

    private Control NoticeWithChooser(string message)
    {
        var panel = new StackPanel { Spacing = 6, MaxWidth = 360 };
        panel.Children.Add(new TextBlock { Text = message, Foreground = WhiteBrush, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(Bitmaps.ChooseGameDataButton(host, SetStatus));
        return new Border { Background = new SolidColorBrush(Color.FromArgb(220, 20, 18, 16)), Padding = new(10), CornerRadius = new(4), Child = panel };
    }

    /// <summary>
    /// What HD draws for the missile and a picker to change it. HD effects are compiled particle systems Studio cannot draw, so
    /// the effect is chosen by name; saving writes the missile's entry in the project's missiles.json.
    /// </summary>
    private Control HdView(ModProject project, string id, HdMissile hd, string? suggestedFrom, string? suggestedUnit)
    {
        var panel = new StackPanel { Spacing = 4 };
        TextBlock Line(string text, IBrush? brush = null) => new SelectableTextBlock { Text = text, FontSize = 12, Foreground = brush ?? WhiteBrush, TextWrapping = TextWrapping.Wrap };
        if (hd.Unit != null) panel.Children.Add(Line($"Effect: {hd.Unit}" + (hd.File != null ? $" · {hd.File.Origin}" : "") + (hd.Key != null && hd.Map != null ? $" · entry \"{hd.Key}\" in missiles.json ({hd.Map.Origin})" : "")));
        foreach (var (label, files) in new[] { ("Particles", hd.Particles), ("Models", hd.Models), ("Textures", hd.Textures) })
            if (files.Length > 0) panel.Children.Add(Line($"{label}: " + string.Join(", ", files.Select(Path.GetFileName)), Muted));
        foreach (var note in hd.Notes) panel.Children.Add(Line(note, hd.File == null ? Brushes.Salmon : Muted));
        if (hd.Map == null) return panel;

        var units = catalog?.HdUnits ?? [];
        var picker = new AutoCompleteBox { Width = 220, ItemsSource = units, FilterMode = AutoCompleteFilterMode.Contains, MinimumPrefixLength = 0, PlaceholderText = "HD effect", Text = hd.Unit ?? suggestedUnit ?? "" };
        AutomationProperties.SetName(picker, "HD effect");
        ToolTip.SetTip(picker, "A unit definition in data/hd/missiles (without .json). Missiles can share one.");
        var save = new Button { Content = "Save to mod", Padding = new(10, 3), MinHeight = 0 };
        var remove = new Button { Content = "Remove", Padding = new(10, 3), MinHeight = 0, IsEnabled = hd.Key != null };
        ToolTip.SetTip(remove, "Remove the missile's entry from missiles.json: HD then draws nothing for it.");
        var status = new TextBlock { FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap };
        if (hd.Key == null && suggestedUnit != null) status.Text = $"{suggestedFrom} draws the same legacy animation and uses {suggestedUnit} in HD. Save to use it here too.";
        void Update()
        {
            var unit = picker.Text?.Trim() ?? "";
            save.IsEnabled = unit.Length > 0 && !unit.Equals(hd.Unit, StringComparison.OrdinalIgnoreCase);
            if (unit.Length > 0 && units.Length > 0 && !units.Contains(unit, StringComparer.OrdinalIgnoreCase)) status.Text = $"No data/hd/missiles/{unit}.json in the project or the game data: HD would draw nothing.";
            else if (save.IsEnabled && !hd.Map.InProject) status.Text = "Saving copies missiles.json from the game data into the project first.";
        }
        picker.TextChanged += (_, _) => Update();
        save.Click += async (_, _) => await SaveHdAsync(project, id, hd.Map, picker.Text?.Trim() ?? "", status);
        remove.Click += async (_, _) => await SaveHdAsync(project, id, hd.Map, "", status);
        var row = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 6 };
        row.Children.Add(picker); row.Children.Add(save); row.Children.Add(remove);
        panel.Children.Add(Labeled("HD effect", row, "The particle effect HD mode draws for this missile"));
        panel.Children.Add(status);
        panel.Children.Add(Line("Studio cannot draw HD particle effects; check the result in game. The entry follows the missile id, so renaming the missile needs a new entry.", Muted));
        Update();
        return panel;
    }

    /// <summary>Writes the missile's HD effect (empty: removes its entry) and reloads what HD draws.</summary>
    internal async Task SaveHdAsync(ModProject project, string id, HdFile map, string unit, TextBlock? status = null)
    {
        try
        {
            var target = map.InProject ? map.Path : Inside(project.Root, map.Relative);
            Require(host.FindFile?.Invoke(target) is not { IsDirty: true }, $"Save or discard the open edits to {Path.GetFileName(target)} first.");
            if (status != null) status.Text = "Saving…";
            var written = await Task.Run(() => MissileGraphics.SaveHdUnit(project, map, id, unit));
            SetStatus((unit.Length == 0 ? $"Removed {id}'s HD effect from " : $"{id} now uses {unit} in HD · saved ") + Path.GetRelativePath(project.Root, written) + (map.InProject ? "" : " (copied from the game data)"));
            artKey = null; RequestArt(); await PendingArt;
        }
        catch (Exception ex) { if (status != null) status.Text = ex.Message; SetStatus(ex.Message, true); }
    }

    private void Animate()
    {
        if (!Visible || SelectedRow < 0) { animation.Stop(); return; }
        if (!playing) return;
        // Half and quarter speed hold each game frame for two or four timer ticks.
        int hold = speed.SelectedIndex switch { 1 => 2, 2 => 4, _ => 1 };
        if (++frameSkip < hold) return;
        frameSkip = 0;
        tick++;
        DrawScene();
    }

    private SceneState DrawScene()
    {
        var state = scene.Render(tick, (int)facing.Value, motion, sprite, explosionSprite);
        LastScene = state;
        Bitmaps.Copy(frame, SceneWidth, SceneHeight, scene.Pixels);
        sceneImage.InvalidateVisual();
        int range = Math.Max(1, motion.Range);
        sceneCaption.Text = state.Stage switch
        {
            SceneStage.Flight => $"Flight · frame {state.Tick + 1} of {range} ({(state.Tick + 1) / 25.0:0.00} s) · {state.Distance:0} px travelled"
                + (sprite == null ? "" : state.Frame < 0 ? " · not drawn" : $" · animation frame {state.Frame + 1}/{sprite.Animation.FramesPerDirection}, direction {state.Direction}"),
            SceneStage.Explosion => $"Explosion · frame {state.Frame + 1}/{explosionSprite?.Animation.FramesPerDirection}",
            _ => $"Ended after {range} frames ({range / 25.0:0.00} s) and {state.Distance:0} px · restarting"
        };
        return state;
    }

    // ── Damage ─────────────────────────────────────────────────────────────────────────────────────────

    protected override async Task PreviewAsync()
    {
        previewCancellation?.Cancel();
        var work = previewCancellation = new CancellationTokenSource(); var token = work.Token;
        var project = host.Project(); var record = SelectedRecord(); int row = SelectedRow;
        if (project == null || record == null) { work.Dispose(); if (ReferenceEquals(previewCancellation, work)) previewCancellation = null; return; }
        string profile = host.Profile(), language = Locale; int workspace = host.Workspace(), revision = Document.Revision;
        var options = new CalcPreviewOptions((int)(skillLevel.Value ?? 1));
        try
        {
            if (host.DirtyDependency(pane) is { } dirty) { damageHost.Content = new TextBlock { Text = "Save the edited dependency to preview damage: " + dirty, Foreground = Brushes.Salmon, TextWrapping = TextWrapping.Wrap }; return; }
            if (previewContext != (project.Root, workspace)) { previewResolver.Clear(); previewContext = (project.Root, workspace); }
            var result = await previewWork.RunAsync(ct => previewResolver.Resolve(project, record, profile, language, ct, options), token);
            if (token.IsCancellationRequested || revision != Document.Revision || SelectedRow != row) return;
            LastPreview = result;
            damageHost.Content = MainWindow.MissileCard(result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) damageHost.Content = new TextBlock { Text = ex.Message, Foreground = Brushes.Salmon, TextWrapping = TextWrapping.Wrap }; }
        finally { if (ReferenceEquals(previewCancellation, work)) previewCancellation = null; work.Dispose(); }
    }
}

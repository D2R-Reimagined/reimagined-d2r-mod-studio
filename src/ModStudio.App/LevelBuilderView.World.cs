using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ModStudio.Core;
using static ModStudio.Core.Storage;

namespace ModStudio.App;

/// <summary>
/// The World card: the act of the level being edited laid out as the game places it, every level a rectangle at its offset
/// and size. Hover a level for its size, position and links; click to open it, double-click to open its map in Level
/// Editor; drag across free space to add a level there, with its levels and lvlprest rows and a map to build on.
/// </summary>
internal sealed partial class LevelBuilderView
{
    private readonly LevelWorldMap worldMap = new() { Height = 460 };
    private readonly ComboBox worldAct = new() { ItemsSource = LevelBuilderResolver.Acts, Width = 100, SelectedIndex = 0 },
        worldDifficulty = new() { ItemsSource = MonsterData.Difficulties.Select(d => d.Name).ToArray(), Width = 120, SelectedIndex = 0 };
    private readonly WrapPanel worldTray = new() { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 4 };
    private readonly TextBlock worldNote = new() { FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap };
    private Border? worldCard;
    private WorldLevelRow[] worldRows = [];
    private int worldShownAct = -1;
    private string? worldFollowing;
    private bool worldUpdating;

    internal LevelWorldMap WorldMap => worldMap;
    internal string WorldNote => worldNote.Text ?? "";
    internal IReadOnlyList<string> WorldTray => [.. worldTray.Children.OfType<Button>().Select(b => b.Content as string ?? "")];

    /// <summary>The card is kept across rows, so the act's zoom and pan stay put while levels are opened from it.</summary>
    private Control WorldCard()
    {
        if (worldCard != null) { (worldCard.Parent as Panel)?.Children.Remove(worldCard); return worldCard; }
        worldMap.Describe = DescribeLevel;
        worldMap.Collisions = (x, y, width, height) => LevelWorld.Collisions(worldRows, worldShownAct, x, y, width, height);
        worldMap.LevelClicked += id => SelectWhere("Id", id);
        worldMap.LevelOpened += async id =>
        {
            if (!SelectWhere("Id", id) || host.OpenLevelEditor == null) return;
            try { await host.OpenLevelEditor(pane, SelectedRow); } catch (Exception ex) { SetStatus(ex.Message, true); }
        };
        worldMap.CreateRequested += async (x, y, width, height) =>
        {
            try { await NewLevelAsync(x, y, width, height); } catch (Exception ex) { SetStatus(ex.Message, true); }
        };
        worldAct.SelectionChanged += (_, _) => { if (!worldUpdating && Table is { } table) RefreshWorld(table, SelectedRow); };
        worldDifficulty.SelectionChanged += (_, _) => { if (!worldUpdating && Table is { } table) RefreshWorld(table, SelectedRow); };

        Button Tool(string text, string tip, Action click)
        {
            var button = new Button { Content = text, Padding = new(8, 2), MinHeight = 0, FontSize = 12 };
            ToolTip.SetTip(button, tip); button.Click += (_, _) => click();
            return button;
        }
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Top };
        ToolTip.SetTip(worldAct, "The act shown. Opening a level shows its act.");
        ToolTip.SetTip(worldDifficulty, "Sizes differ by difficulty (SizeX, SizeX(N), SizeX(H)).");
        tools.Children.Add(worldAct); tools.Children.Add(worldDifficulty);
        tools.Children.Add(Tool("Fit", "Show the whole act", worldMap.Fit));
        tools.Children.Add(Tool("+", "Zoom in (Ctrl+wheel)", () => worldMap.ZoomBy(1.4)));
        tools.Children.Add(Tool("−", "Zoom out (Ctrl+wheel)", () => worldMap.ZoomBy(1 / 1.4)));

        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new Border { BorderBrush = CardBorder, BorderThickness = new(1), CornerRadius = new(4), ClipToBounds = true, Child = worldMap });
        var legend = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 14 };
        foreach (var (text, color) in new[] { ("■ preset", LevelWorldMap.PresetColor), ("■ maze", LevelWorldMap.MazeColor), ("■ outdoor", LevelWorldMap.OutdoorColor) })
            legend.Children.Add(new TextBlock { Text = text, FontSize = 11, Foreground = new SolidColorBrush(color) });
        legend.Children.Add(new TextBlock { Text = "- - relative (Depend) or moved by outdoor generation", FontSize = 11, Foreground = Muted });
        legend.Children.Add(new TextBlock { Text = "── links", FontSize = 11, Foreground = Gold });
        legend.Children.Add(new TextBlock { Text = "▢ overlap", FontSize = 11, Foreground = Brushes.Salmon });
        body.Children.Add(legend);
        body.Children.Add(worldNote);
        body.Children.Add(worldTray);
        worldCard = Card("World", body, tools,
            "Each act is one space measured in tiles; a level covers SizeX × SizeY tiles from OffsetX, OffsetY (from its Depend level's corner when it has one). Levels must not overlap. " +
            "Hover a level for its links, click to open it, double-click to open its map in Level Editor. Drag across empty space to add a level there. Ctrl+wheel zooms, right-drag pans.");
        return worldCard;
    }

    /// <summary>Lays the act out again from the table: after any edit, a new row, or a change of act or difficulty.</summary>
    private void RefreshWorld(TableData table, int row)
    {
        if (worldCard == null) return;
        worldRows = LevelWorld.Read(table);
        string? id = row >= 0 ? table.Cell(row, "Id") : null;
        int act = Math.Max(0, worldAct.SelectedIndex);
        // Opening another level shows its act; edits to the same level leave the act being looked at alone.
        if (row >= 0 && selectedSourceId != worldFollowing)
        {
            worldFollowing = selectedSourceId;
            act = Math.Clamp(worldRows[row].Act, 0, LevelBuilderResolver.Acts.Length - 1);
        }
        bool refit = act != worldShownAct;
        worldShownAct = act;
        worldUpdating = true;
        try { worldAct.SelectedIndex = act; } finally { worldUpdating = false; }

        var layout = LevelWorld.Layout(worldRows, act, worldDifficulty.SelectedIndex);
        worldMap.SetLayout(layout, WorldTitles(), id, refit);
        if (!refit && id != null) worldMap.Reveal(id);

        worldTray.Children.Clear();
        foreach (var level in layout.Unplaced)
        {
            var button = new Button { Content = $"{Title(level.Source)} · {level.Reason}", Padding = new(8, 2), MinHeight = 0, FontSize = 11 };
            ToolTip.SetTip(button, $"Id {level.Source.Id} · not drawn on the map · click to open it");
            var target = level.Source.Id;
            button.Click += (_, _) => SelectWhere("Id", target);
            worldTray.Children.Add(button);
        }
        string Name(string levelId) => layout.Find(levelId) is { } found ? Title(found.Source) : levelId;
        var notes = new List<string> { $"{layout.Placed.Length} levels drawn in {LevelBuilderResolver.Acts[act]}" + (layout.Unplaced.Length > 0 ? $"; {layout.Unplaced.Length} the game places itself or that need a size are listed below." : ".") };
        notes.AddRange(layout.Overlaps.Select(o => $"⚠ {Name(o.A)} and {Name(o.B)} overlap."));
        worldNote.Text = string.Join("\n", notes);
        worldNote.Foreground = layout.Overlaps.Length > 0 ? Brushes.Salmon : Muted;
    }

    private Dictionary<string, string> WorldTitles() =>
        catalog == null ? [] : catalog.Entries.Where(e => e.Id.Length > 0).GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First().Title);

    private string Title(WorldLevelRow row) => catalog?.Entries.FirstOrDefault(e => e.Row == row.Row) is { Title.Length: > 0 } entry ? entry.Title : row.Name;

    /// <summary>The hover card: name, how it is placed and sized, and every link in and out with the warp it goes through.</summary>
    private IReadOnlyList<string> DescribeLevel(string id)
    {
        if (worldMap.Layout?.Find(id) is not { } level) return [];
        var row = level.Source;
        string Named(string levelId) => worldRows.FirstOrDefault(r => r.Id == levelId && r.Placeable) is { } other ? Title(other) : "Id " + levelId;
        var lines = new List<string> { Title(row) };
        string kind = row.DrlgType is >= 0 and < 4 ? LevelBuilderResolver.DrlgTypes[row.DrlgType] : "DRLG " + row.DrlgType;
        lines.Add($"Id {row.Id} · {row.Name} · {kind}");
        lines.Add($"{level.Width} × {level.Height} tiles at {level.X}, {level.Y}" + level.Placement switch
        {
            WorldPlacement.Relative => $" ({row.OffsetX}, {row.OffsetY} from {Named(row.Depend)})",
            WorldPlacement.Outdoor => " (outdoor generation may move it)",
            _ => ""
        });
        for (int slot = 0; slot < 8; slot++)
        {
            if (row.Vis[slot] is "" or "0") continue;
            var warp = row.Warps[slot];
            var through = warp is "" or "-1" ? "across the border" : catalog?.Warps.FirstOrDefault(w => w.Code == warp) is { Name.Length: > 0 } named ? $"via {named.Name} (warp {warp})" : "via warp " + warp;
            bool back = worldRows.Any(r => r.Id == row.Vis[slot] && r.Vis.Contains(row.Id));
            lines.Add($"Vis{slot} → {Named(row.Vis[slot])} {through}{(back ? "" : " · one way")}");
        }
        foreach (var other in worldRows.Where(r => r.Placeable && r.Id != row.Id && r.Vis.Contains(row.Id) && !row.Vis.Contains(r.Id)))
            lines.Add($"← {Title(other)} leads here (no Vis back)");
        lines.Add("Click to open · double-click for Level Editor");
        return lines;
    }

    // ── New levels ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>A rectangle was drawn across free space: ask what the level is, then add it.</summary>
    private async Task NewLevelAsync(int x, int y, int width, int height)
    {
        if (Table is not { } table || host.Project() == null) return;
        var presets = await PresetDocumentAsync();
        int act = worldShownAct;
        var templates = new List<LevelTemplate> { new(-1, "", "", act, 1) };
        templates.AddRange(worldRows.Where(r => r.Placeable).OrderBy(r => r.Act != act).ThenBy(r => r.Row).Select(r => new LevelTemplate(r.Row, r.Id, Title(r), r.Act, r.DrlgType)));
        // The level being edited is the natural template when it is in this act; otherwise this act's first preset level.
        int fallback = SelectedRow >= 0 && worldRows[SelectedRow].Act == act ? SelectedRow
            : worldRows.FirstOrDefault(r => r.Placeable && r.Act == act && r.DrlgType == 2)?.Row ?? -1;
        var context = new NewLevelContext(act, x, y, width, height, LevelWorld.NextId(table), [.. templates], fallback, presets?.Table != null,
            (a, b, c, d) => LevelWorld.Collisions(worldRows, act, a, b, c, d), TemplateMap, MapProblem);
        var window = new NewLevelWindow(context);
        if (TopLevel.GetTopLevel(this) is Window owner) await window.ShowDialog(owner); else return;
        if (window.Result is { } plan) CreateLevel(plan, presets);
    }

    private async Task<Document?> PresetDocumentAsync()
    {
        var document = host.FindTable?.Invoke("lvlprest") ?? (host.OpenTable == null ? null : await host.OpenTable("lvlprest"));
        return document is { PendingSource: false, Table: not null } ? document : null;
    }

    /// <summary>
    /// Adds the level: its map files first (never over an existing file), then its levels row and lvlprest row, each one
    /// undo step in its own table. Opens the new row.
    /// </summary>
    internal void CreateLevel(NewLevelPlan plan, Document? presets)
    {
        var table = Table ?? throw new InvalidOperationException("Apply valid source before adding levels.");
        var project = host.Project() ?? throw new InvalidOperationException("Open a project first.");
        Require(LevelWorld.Collisions(LevelWorld.Read(table), plan.Level.Act, plan.Level.X, plan.Level.Y, plan.Level.Width, plan.Level.Height).Length == 0, "That space is taken now. Draw the level again.");
        int id = LevelWorld.NextId(table);
        var levelFields = LevelWorld.LevelFields(table, plan.Level, id, plan.TemplateRow);
        JsonObject? presetFields = null;
        var written = new List<string>();
        void Discard() { foreach (var file in written) try { File.Delete(file); } catch (IOException) { } }
        if (plan.Map is { } map)
        {
            Require(presets?.Table != null && !presets.PendingSource, "Open the lvlprest table (and apply its source) to give the level a map.");
            Require(MapProblem(map) == null, MapProblem(map) ?? "");
            var templateMap = plan.TemplateRow >= 0 ? TemplateMap(plan.TemplateRow) : null;
            presetFields = LevelWorld.PresetFields(presets!.Table!, plan.Level.Name, id, map, TemplatePresetRow(presets.Table!, plan.TemplateRow));
            byte[] ds1, json;
            if (plan.CopyMap && templateMap is { } source)
            {
                ds1 = File.ReadAllBytes(ResolveAsset("global/tiles/" + source.Map) ?? throw new FileNotFoundException("The template's map is missing: " + source.Map));
                json = ResolveAsset(LevelWorld.PresetFor(source.Map)) is { } preset ? File.ReadAllBytes(preset) : LevelWorld.MinimalPreset(plan.Level.Name, "");
            }
            else
            {
                // A blank map is lit like the template's: its preset names the biome.
                var biome = templateMap is { } lit && ResolveAsset(LevelWorld.PresetFor(lit.Map)) is { } litPreset ? LevelWorld.Biome(File.ReadAllBytes(litPreset)) : "";
                ds1 = LevelWorld.BlankDs1(plan.Level.Width, plan.Level.Height, plan.Level.Act);
                json = LevelWorld.MinimalPreset(plan.Level.Name, biome);
            }
            try
            {
                var ds1File = Inside(project.Root, "data/global/tiles/" + map); AtomicWrite(ds1File, ds1, requireAbsent: true); written.Add(ds1File);
                var jsonFile = Inside(project.Root, "data/" + LevelWorld.PresetFor(map)); AtomicWrite(jsonFile, json, requireAbsent: true); written.Add(jsonFile);
            }
            catch { Discard(); throw; }
        }
        int row = table.Records.Count;
        bool inserted = false;
        try
        {
            Document.InsertRows(row, 1, [levelFields]); inserted = true;
            if (presetFields != null) presets!.InsertRows(presets.Table!.Records.Count, 1, [presetFields]);
        }
        catch
        {
            if (inserted && Document.CanUndo) Document.Undo();
            Discard(); throw;
        }
        Search.Text = "";
        Select(row);
        SetStatus($"Added {plan.Level.Name} (Id {id}) at {plan.Level.X}, {plan.Level.Y}, {plan.Level.Width} × {plan.Level.Height} tiles"
            + (plan.Map != null ? $", a lvlprest row and its map {plan.Map}. Save levels and lvlprest, then Open in Level Editor to build it." : ".")
            + " Link it from another level's Connections. Undo removes each table's row" + (written.Count > 0 ? "; the map files stay on disk." : "."));
    }

    /// <summary>The lvlprest row of a template level (its first preset with a map), or -1.</summary>
    private int TemplatePresetRow(TableData presets, int templateRow)
    {
        if (templateRow < 0 || Table is not { } table || templateRow >= table.Records.Count) return -1;
        var id = table.Cell(templateRow, "Id");
        return Enumerable.Range(0, presets.Records.Count).FirstOrDefault(i => presets.Cell(i, "LevelId") == id && presets.Cell(i, "File1") is not ("" or "0"), -1);
    }

    /// <summary>A template level's map as its lvlprest row names it, and that map's size; null when it has none on disk.</summary>
    private (string Map, int SizeX, int SizeY)? TemplateMap(int templateRow)
    {
        if (host.FindTable?.Invoke("lvlprest") is not { Table: { } presets } || TemplatePresetRow(presets, templateRow) is not (>= 0 and var row)) return null;
        var map = presets.Cell(row, "File1").Replace('\\', '/');
        if (ResolveAsset("global/tiles/" + map) is not { } file) return null;
        try { var (sizeX, sizeY, _) = LevelWorld.Ds1Size(File.ReadAllBytes(file)); return (map, sizeX, sizeY); }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { return null; }
    }

    /// <summary>Why a map path cannot be used for a new level, or null when it can.</summary>
    private string? MapProblem(string map)
    {
        if (host.Project() is not { } project) return "Open a project first.";
        try
        {
            if (File.Exists(Inside(project.Root, "data/global/tiles/" + map))) return "The project already has this map. Pick another path.";
            if (File.Exists(Inside(project.Root, "data/" + LevelWorld.PresetFor(map)))) return "The project already has this map's HD preset. Pick another path.";
        }
        catch (InvalidDataException ex) { return ex.Message; }
        if (ResolveAsset("global/tiles/" + map) != null) return "The game already has a map at this path; a new one would replace it. Pick another path.";
        if (host.FindTable?.Invoke("lvlprest")?.Table is { } presets && Enumerable.Range(0, presets.Records.Count).Any(i => Enumerable.Range(1, 6).Any(n => presets.Cell(i, "File" + n).Replace('\\', '/').Equals(map, StringComparison.OrdinalIgnoreCase))))
            return "A lvlprest row already uses this map.";
        return null;
    }

    /// <summary>A game-data file: the project's own copy first, then the extracted game data folders.</summary>
    private string? ResolveAsset(string logical)
    {
        if (host.Project() is { } project)
            try { if (Inside(project.Root, "data/" + logical) is var own && File.Exists(own)) return own; }
            catch (InvalidDataException) { return null; }
        foreach (var folder in host.GameData())
            foreach (var candidate in new[] { Path.Combine(folder, logical), Path.Combine(folder, "data", logical) })
                if (File.Exists(candidate)) return candidate;
        return null;
    }
}

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>
/// One levels row as the world map reads it. Sizes are per difficulty (Normal, Nightmare, Hell); <paramref name="Vis"/> and
/// <paramref name="Warps"/> are the eight link slots.
/// </summary>
public sealed record WorldLevelRow(int Row, string Id, string Name, int Act, int DrlgType, int[] SizeX, int[] SizeY, int OffsetX, int OffsetY, string Depend,
    string[] Vis, string[] Warps)
{
    public bool Placeable => Id.Length > 0 && Id != "0" && Name.Length > 0;
}

/// <summary>
/// How the game positions a level in its act: at its own offset, relative to the level it depends on, by the outdoor
/// generator (the offset is a starting point the generator may move), or wholly by the game (offset -1, -1).
/// </summary>
public enum WorldPlacement { Fixed, Relative, Outdoor, Generated }

/// <summary>A level placed in its act's world, in tiles: it covers [X, X + Width) × [Y, Y + Height).</summary>
public sealed record WorldLevel(WorldLevelRow Source, int X, int Y, int Width, int Height, WorldPlacement Placement)
{
    public string Id => Source.Id;
    public bool Intersects(int x, int y, int width, int height) => x < X + Width && X < x + width && y < Y + Height && Y < y + height;
}

/// <summary>A level that is not drawn at a position, and why.</summary>
public sealed record WorldUnplaced(WorldLevelRow Source, WorldPlacement Placement, string Reason);

/// <summary>A Vis# link between two levels of the act; <paramref name="Back"/> when the target links back.</summary>
public sealed record WorldLink(string From, string To, int Slot, string Warp, bool Back);

public sealed record LevelWorldLayout(int Act, int Difficulty, WorldLevel[] Placed, WorldUnplaced[] Unplaced, (string A, string B)[] Overlaps, WorldLink[] Links)
{
    public WorldLevel? Find(string id) => Placed.FirstOrDefault(l => l.Id == id);
    /// <summary>The rectangle every placed level fits in; empty acts get a 200-tile square at the origin.</summary>
    public (int X, int Y, int Width, int Height) Bounds()
    {
        if (Placed.Length == 0) return (0, 0, 200, 200);
        int left = Placed.Min(l => l.X), top = Placed.Min(l => l.Y), right = Placed.Max(l => l.X + l.Width), bottom = Placed.Max(l => l.Y + l.Height);
        return (left, top, right - left, bottom - top);
    }
}

/// <summary>A new level drawn on the world map: where it goes, how big it is, and how it is built.</summary>
public sealed record NewLevelRequest(string Name, int Act, int DrlgType, int X, int Y, int Width, int Height);

/// <summary>
/// The world each act is laid out in, read from levels.txt: SizeX/SizeY (per difficulty) and OffsetX/OffsetY in tiles,
/// Depend for levels placed relative to another, and -1 offsets for levels the game places itself. Also what a new level
/// drawn into free space needs: its levels and lvlprest rows, a blank DS1 and a minimal HD preset.
/// </summary>
public static class LevelWorld
{
    public static readonly string[] SizeXColumns = ["SizeX", "SizeX(N)", "SizeX(H)"], SizeYColumns = ["SizeY", "SizeY(N)", "SizeY(H)"];

    public static WorldLevelRow[] Read(TableData table)
    {
        string Cell(int row, string column) => table.ColumnIndex(column) >= 0 ? table.Cell(row, column).Trim() : "";
        return [.. Enumerable.Range(0, table.Records.Count).Select(i => new WorldLevelRow(i, Cell(i, "Id"), Cell(i, "Name"), Whole(Cell(i, "Act")), Whole(Cell(i, "DrlgType")),
            [.. SizeXColumns.Select(c => Whole(Cell(i, c)))], [.. SizeYColumns.Select(c => Whole(Cell(i, c)))], Whole(Cell(i, "OffsetX")), Whole(Cell(i, "OffsetY")), Cell(i, "Depend"),
            [.. Enumerable.Range(0, 8).Select(n => Cell(i, "Vis" + n))], [.. Enumerable.Range(0, 8).Select(n => Cell(i, "Warp" + n))]))];
    }

    /// <summary>The act's levels as one difficulty sizes them: placed ones, ones the game places, overlaps and links.</summary>
    public static LevelWorldLayout Layout(IReadOnlyList<WorldLevelRow> rows, int act, int difficulty)
    {
        difficulty = Math.Clamp(difficulty, 0, 2);
        var levels = rows.Where(r => r.Placeable && r.Act == act).ToArray();
        var byId = rows.Where(r => r.Placeable).GroupBy(r => r.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var placed = new Dictionary<string, WorldLevel>(StringComparer.Ordinal);
        var unplaced = new Dictionary<string, WorldUnplaced>(StringComparer.Ordinal);

        // Depend chains are followed to their root; a cycle or a missing parent leaves the level unplaced.
        WorldLevel? Place(WorldLevelRow row, HashSet<string> visiting)
        {
            if (placed.TryGetValue(row.Id, out var done)) return done;
            if (unplaced.ContainsKey(row.Id)) return null;
            int width = row.SizeX[difficulty], height = row.SizeY[difficulty];
            void Skip(WorldPlacement how, string reason) => unplaced[row.Id] = new(row, how, reason);
            if (width <= 0 || height <= 0)
            {
                bool game = row.OffsetX == -1 && row.OffsetY == -1;
                Skip(game ? WorldPlacement.Generated : WorldPlacement.Fixed, game ? "Sized and placed by the game." : "No size: set SizeX and SizeY.");
                return null;
            }
            if (row.Depend is not ("" or "0"))
            {
                if (!visiting.Add(row.Id)) { Skip(WorldPlacement.Relative, "Depend loops back to itself."); return null; }
                if (!byId.TryGetValue(row.Depend, out var parentRow)) { Skip(WorldPlacement.Relative, $"Depend: no level has Id {row.Depend}."); return null; }
                if (parentRow.Act != row.Act) { Skip(WorldPlacement.Relative, $"Depend names {parentRow.Name}, which is in another act."); return null; }
                var parent = Place(parentRow, visiting);
                if (parent == null) { Skip(WorldPlacement.Relative, $"Placed relative to {parentRow.Name}, which the game places."); return null; }
                return placed[row.Id] = new(row, parent.X + row.OffsetX, parent.Y + row.OffsetY, width, height, WorldPlacement.Relative);
            }
            bool outdoor = row.DrlgType == 3 && LevelBuilderResolver.InOutdoorChain(row.Id, row.Act);
            if (row.OffsetX == -1 && row.OffsetY == -1)
            {
                Skip(outdoor ? WorldPlacement.Outdoor : WorldPlacement.Generated, outdoor ? "Placed by the outdoor generator beside its neighbours." : "Placed by the game (offset -1, -1).");
                return null;
            }
            return placed[row.Id] = new(row, row.OffsetX, row.OffsetY, width, height, outdoor ? WorldPlacement.Outdoor : WorldPlacement.Fixed);
        }
        foreach (var row in levels) Place(row, new HashSet<string>(StringComparer.Ordinal));

        var list = levels.Where(r => placed.ContainsKey(r.Id)).Select(r => placed[r.Id]).DistinctBy(l => l.Id).ToArray();
        var overlaps = new List<(string, string)>();
        for (int i = 0; i < list.Length; i++)
            for (int j = i + 1; j < list.Length; j++)
            {
                var (a, b) = (list[i], list[j]);
                // Outdoor levels share a generated strip: their offsets are where the generator starts, not where they end up.
                if (a.Placement == WorldPlacement.Outdoor && b.Placement == WorldPlacement.Outdoor) continue;
                if (a.Intersects(b.X, b.Y, b.Width, b.Height)) overlaps.Add((a.Id, b.Id));
            }
        var links = new List<WorldLink>();
        foreach (var row in levels)
            for (int slot = 0; slot < 8; slot++)
                if (row.Vis[slot] is not ("" or "0") && byId.TryGetValue(row.Vis[slot], out var target) && target.Act == act)
                    links.Add(new(row.Id, target.Id, slot, row.Warps[slot], target.Vis.Contains(row.Id)));
        return new(act, difficulty, list, [.. levels.Where(r => unplaced.ContainsKey(r.Id)).Select(r => unplaced[r.Id]).DistinctBy(u => u.Source.Id)], [.. overlaps], [.. links]);
    }

    /// <summary>Levels of the act the rectangle would overlap in any difficulty; none means the space is free.</summary>
    public static string[] Collisions(IReadOnlyList<WorldLevelRow> rows, int act, int x, int y, int width, int height, string? ignoreId = null) =>
        [.. Enumerable.Range(0, 3).SelectMany(d => Layout(rows, act, d).Placed).Where(l => l.Id != ignoreId && l.Intersects(x, y, width, height))
            .Select(l => l.Source.Name).Distinct()];

    /// <summary>The next level Id: one past the highest. levels rows are found by row, so new ones go at the bottom.</summary>
    public static int NextId(TableData table) => Enumerable.Range(0, table.Records.Count).Select(i => Whole(table.Cell(i, "Id"))).DefaultIfEmpty(0).Max() + 1;

    /// <summary>The cells a blank level starts with.</summary>
    public static JsonObject DefaultFields(TableData table, int id)
    {
        var fields = new JsonObject();
        var values = new List<(string, string)> { ("Name", "New Level"), ("Id", id.ToString(CultureInfo.InvariantCulture)), ("Act", "0"), ("Waypoint", "255"), ("Teleport", "1"), ("DrlgType", "1"),
            ("BlankScreen", "1"), ("SaveMonsters", "1"), ("SubType", "-1"), ("SubTheme", "-1"), ("SubWaypoint", "-1"), ("SubShrine", "-1") };
        values.AddRange(Enumerable.Range(0, 8).Select(i => ("Warp" + i, "-1")));
        foreach (var (column, value) in values) if (table.ColumnIndex(column) >= 0) fields[column] = value;
        return fields;
    }

    // A level copied from another keeps its monsters, sounds, tiles and rules, but not who it is, where it is or what it links to.
    private static readonly string[] Identity = ["Name", "Id", "LevelName", "LevelEntry", "LevelWarp", "Waypoint", "Depend", "OffsetX", "OffsetY", "QuestFlag", "QuestFlagEx"];

    /// <summary>The levels cells for a new level: copied from <paramref name="templateRow"/> (or blank), then placed and sized by the request.</summary>
    public static JsonObject LevelFields(TableData table, NewLevelRequest request, int id, int templateRow = -1)
    {
        var fields = DefaultFields(table, id);
        if (templateRow >= 0 && templateRow < table.Records.Count)
            foreach (var column in table.Columns.Where(c => !c.StartsWith('*') && !Identity.Contains(c) && !c.StartsWith("Vis", StringComparison.Ordinal) && !c.StartsWith("Warp", StringComparison.Ordinal)))
                if (table.Cell(templateRow, column) is { Length: > 0 } value) fields[column] = value;
        void Set(string column, string value) { if (table.ColumnIndex(column) >= 0) fields[column] = value; }
        string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
        Set("Name", request.Name); Set("Id", Number(id)); Set("Act", Number(request.Act)); Set("DrlgType", Number(request.DrlgType));
        foreach (var column in SizeXColumns) Set(column, Number(request.Width));
        foreach (var column in SizeYColumns) Set(column, Number(request.Height));
        Set("OffsetX", Number(request.X)); Set("OffsetY", Number(request.Y)); Set("Depend", "0"); Set("Waypoint", "255");
        for (int i = 0; i < 8; i++) { Set("Vis" + i, "0"); Set("Warp" + i, "-1"); }
        return fields;
    }

    /// <summary>
    /// The lvlprest cells that give a preset level its map: the next Def, the level's Id, its one map file, and the room
    /// flags and DT1 mask of <paramref name="templateRow"/> (a preset of the same tiles). Size 0 takes it from the map.
    /// </summary>
    public static JsonObject PresetFields(TableData presets, string name, int levelId, string map, int templateRow = -1)
    {
        var fields = new JsonObject();
        int def = Enumerable.Range(0, presets.Records.Count).Select(i => Whole(presets.Cell(i, "Def"))).DefaultIfEmpty(0).Max() + 1;
        var defaults = new (string Column, string Value)[] { ("Populate", "1"), ("Logicals", "0"), ("Outdoors", "0"), ("Animate", "0"), ("KillEdge", "0"), ("FillBlanks", "1"),
            ("AutoMap", "0"), ("Scan", "1"), ("Pops", "0"), ("PopPad", "0"), ("Dt1Mask", "1") };
        foreach (var (column, value) in defaults)
            if (presets.ColumnIndex(column) >= 0) fields[column] = templateRow >= 0 && presets.Cell(templateRow, column) is { Length: > 0 } copied ? copied : value;
        void Set(string column, string value) { if (presets.ColumnIndex(column) >= 0) fields[column] = value; }
        Set("Name", name); Set("Def", def.ToString(CultureInfo.InvariantCulture)); Set("LevelId", levelId.ToString(CultureInfo.InvariantCulture));
        Set("SizeX", "0"); Set("SizeY", "0"); Set("Files", "1"); Set("File1", map);
        for (int i = 2; i <= 6; i++) Set("File" + i, "0");
        return fields;
    }

    /// <summary>
    /// A blank version 18 DS1 for a level of SizeX × SizeY tiles (the header stores the size; the map holds one more cell
    /// each way): one empty wall and floor layer, no shadows' tags, units or paths. <paramref name="act"/> is levels' Act (0–4).
    /// Layout after the Reimagined Level Editor's DS1 writer (MIT, see licenses/D2RLevelEditor.txt).
    /// </summary>
    public static byte[] BlankDs1(int sizeX, int sizeY, int act)
    {
        Require(sizeX is >= 1 and < 512 && sizeY is >= 1 and < 512, "A map is 1–511 tiles each way.");
        Require(act is >= 0 and <= 4, "Act must be 0–4.");
        int cells = (sizeX + 1) * (sizeY + 1);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            // version, width - 1, height - 1, act, tag type, DT1 file count, wall layers, floor layers
            foreach (int value in new[] { 18, sizeX, sizeY, act, 0, 0, 1, 1 }) writer.Write(value);
            writer.Write(new byte[cells * 8]); // one wall layer: tiles, then orientations
            writer.Write(new byte[cells * 4]); // one floor layer
            writer.Write(new byte[cells * 4]); // shadows
            writer.Write(0); // units
            writer.Write(0); // paths
        }
        return stream.ToArray();
    }

    /// <summary>A DS1's size as levels.txt states it (the header's width and height, one less than its cells) and its act (0–4).</summary>
    public static (int SizeX, int SizeY, int Act) Ds1Size(byte[] bytes)
    {
        Require(bytes.Length >= 16, "Not a DS1 map.");
        int version = BitConverter.ToInt32(bytes, 0), sizeX = BitConverter.ToInt32(bytes, 4), sizeY = BitConverter.ToInt32(bytes, 8);
        Require(version is > 0 and < 100 && sizeX is >= 0 and < 4096 && sizeY is >= 0 and < 4096, "Not a DS1 map.");
        return (sizeX, sizeY, version >= 8 ? Math.Clamp(BitConverter.ToInt32(bytes, 12), 0, 4) : 0);
    }

    /// <summary>The smallest HD preset the game loads: no entities or terrain, lit by a biome.</summary>
    public static byte[] MinimalPreset(string name, string biome)
    {
        JsonArray Empty() => [];
        var dependencies = new JsonObject
        {
            ["particles"] = Empty(), ["models"] = Empty(), ["skeletons"] = Empty(), ["animations"] = Empty(), ["textures"] = Empty(), ["physics"] = Empty(),
            ["json"] = biome.Length > 0 ? new JsonArray(new JsonObject { ["path"] = biome }) : Empty(), ["variantdata"] = Empty(), ["objecteffects"] = Empty(), ["other"] = Empty()
        };
        var preset = new JsonObject
        {
            ["dependencies"] = dependencies, ["type"] = "Preset", ["name"] = name, ["entities"] = Empty(), ["terrain"] = new JsonObject(), ["biomeFilename"] = biome,
            ["perTileBiomeOverrides"] = Empty(), ["specialTiles"] = new JsonObject()
        };
        return Utf8.GetBytes(preset.ToJsonString(Compact));
    }

    /// <summary>A preset's biome, or "" when the file has none.</summary>
    public static string Biome(byte[] preset)
    {
        try { return JsonNode.Parse(preset).S("biomeFilename"); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { return ""; }
    }

    /// <summary>The HD preset paired with a map under global/tiles: the same path, lower case, as JSON under hd/env/preset.</summary>
    public static string PresetFor(string map) => "hd/env/preset/" + Path.ChangeExtension(map.Replace('\\', '/'), ".json").ToLowerInvariant();

    /// <summary>A file-name stem from a level name: lower-case letters, digits and underscores.</summary>
    public static string Slug(string name)
    {
        var text = new StringBuilder();
        foreach (var c in name.ToLowerInvariant()) text.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        var slug = string.Join("_", text.ToString().Split('_', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? "new_level" : slug;
    }

    /// <summary>Checks a map path as lvlprest names it: relative to global/tiles, forward slashes, ending in .ds1.</summary>
    public static string MapPath(string map)
    {
        map = map.Trim().Replace('\\', '/');
        SafeRelative(map);
        Require(map.EndsWith(".ds1", StringComparison.OrdinalIgnoreCase), "The map must be a .ds1 file.");
        Require(map.All(c => c is > ' ' and < (char)127), "Map paths are plain ASCII without spaces.");
        return map;
    }

    private static int Whole(string text) => int.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : 0;
}

using System.Globalization;
using System.Text.Json.Nodes;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>A levels.txt row's identity and links, read on the UI thread. <paramref name="Vis"/> is its Vis0–Vis7 cells joined by commas.</summary>
public sealed record LevelRow(int Row, string SourceId, string Name, string Id, string Act, string LevelName, string AreaLevels, string Waypoint, string Vis, string DrlgType = "");

/// <summary>One levels.txt row as the builder's list shows it.</summary>
public sealed record LevelEntry(int Row, string SourceId, string Name, string Id, string Title, int Act, string AreaLevels, bool Waypoint)
{
    public bool Inactive => Name.Length == 0;
    public string SearchText { get; } = $"{Name} {Title} {Id} act{Act + 1} act {Act + 1}".ToLowerInvariant();
}

/// <summary>A level another one can link to, by Id.</summary>
public sealed record LevelChoice(string Id, string Name, string Title, int Act, bool Waypoint, string[] Vis)
{
    public string SearchText { get; } = $"{Id} {Name} {Title}".ToLowerInvariant();
    public override string ToString() => Id;
}

/// <summary>A monster a level can spawn, with its own Normal/Nightmare/Hell levels.</summary>
public sealed record LevelMonster(string Id, string Name, int Level, int LevelN, int LevelH)
{
    public string SearchText { get; } = (Id + " " + Name).ToLowerInvariant();
    public override string ToString() => Id;
}

/// <summary>A code from a lookup table (lvlwarp, lvltypes, objgroup, soundenviron, levelgroups) and what it is called.</summary>
public sealed record LevelCode(string Code, string Name)
{
    public string SearchText { get; } = (Code + " " + Name).ToLowerInvariant();
    public override string ToString() => Code;
}

public sealed record LevelBuilderCatalog(LevelEntry[] Entries, LevelChoice[] Levels, LevelMonster[] Monsters, LevelCode[] Warps, LevelCode[] Types, LevelCode[] ObjectGroups,
    LevelCode[] Sounds, LevelCode[] Groups, string[] Issues);

/// <summary>What one difficulty makes of a level: the area level (and the column it is read from), density, unique packs and size.</summary>
public sealed record LevelDifficulty(string Name, int AreaLevel, string AreaLevelColumn, int Density, int UniqueMin, int UniqueMax, int SizeX, int SizeY);

/// <summary>A monster slot: the cell, the monster it names, and the level it spawns at (its own in Normal, the area's in Nightmare and Hell).</summary>
public sealed record LevelSpawn(string Column, string Id, string Name, bool Known, int[] Levels);

public sealed record LevelCritter(string Column, string Id, string Name, bool Known, int Chance, int Amount);

/// <summary>A Vis# link out of the level through its Warp#; <paramref name="Back"/> when the target links back.</summary>
public sealed record LevelLink(int Slot, string Id, string Name, string Title, int Act, bool Known, string Warp, string WarpName, bool Waypoint, bool Back);

public sealed record LevelObjectGroup(int Slot, string Id, string Name, int Chance);

/// <summary>A built-in walkable border added by outdoor generation, outside the authored Vis slots.</summary>
public sealed record LevelOutdoorLink(LevelChoice Level, bool VariesBySeed);

public sealed record LevelPreview(string Title, string Entry, string WarpText, int Act, LevelDifficulty[] Difficulties, int NumMon, LevelSpawn[] Normal, LevelSpawn[] Nightmare,
    LevelSpawn[] Unique, LevelCritter[] Critters, LevelLink[] Links, LevelChoice[] Inbound, LevelChoice? Depend, LevelObjectGroup[] Objects, string Drlg, string LevelType,
    string Sound, string Group, int Teleport, bool Waypoint, bool TownPortal, (int Intensity, int Red, int Green, int Blue) Light, string[] Issues)
{
    public LevelOutdoorLink[] OutdoorLinks { get; init; } = [];
}

/// <summary>
/// What the level builder reads besides the row being edited: the monsters it spawns, the levels it links to and from,
/// and the names of its warps, types, object groups and sounds. Worker-owned; reads authored data only, and the levels
/// table from the rows it is handed, so edits to other levels show before they are saved.
/// </summary>
public sealed class LevelBuilderResolver
{
    public static readonly string[] DrlgTypes = ["None", "Maze", "Preset", "Outdoor"];
    public static readonly string[] TeleportRules = ["Not allowed", "Allowed", "Allowed, restricted"];
    public static readonly string[] Acts = ["Act 1", "Act 2", "Act 3", "Act 4", "Act 5"];
    /// <summary>The game picks at most this many kinds of monster for a level (NumMon).</summary>
    public const int MaxKinds = 13;

    // Built-in level IDs linked by DRLGOUTPLACE_CreateLevelConnections (not editable Vis slots).
    // Source: https://github.com/ThePhrozenKeep/D2MOO/blob/master/source/D2Common/src/Drlg/DrlgOutPlace.cpp
    // Act 3 jungle adjacency is seed-dependent; show possible borders, not a particular generated map.
    private static readonly (int Act, string A, string B, bool Variable)[] OutdoorBorders =
    [
        (0, "1", "2", false), (0, "2", "3", false), (0, "3", "4", false), (0, "3", "17", false),
        (0, "5", "6", false), (0, "6", "7", false), (0, "7", "26", false),
        (1, "40", "41", false), (1, "41", "42", false), (1, "42", "43", false), (1, "43", "44", false), (1, "44", "45", false),
        (2, "75", "76", false), (2, "76", "77", true), (2, "76", "78", true), (2, "77", "78", true),
        (2, "78", "79", false), (2, "79", "80", false), (2, "80", "81", false), (2, "81", "82", false), (2, "82", "83", false),
        (3, "103", "104", false), (3, "104", "105", false), (3, "105", "106", false),
        (4, "109", "110", false), (4, "110", "111", false), (4, "111", "112", false)
    ];

    private static bool MatchesOutdoorLevel(string id, string act, string drlgType, int expectedAct) =>
        int.TryParse(act, out var parsedAct) && parsedAct == expectedAct && int.TryParse(drlgType, out var parsedType)
        && parsedType == (id is "1" or "26" or "40" or "75" or "103" or "109" ? 2 : 3);

    private readonly PreviewTables tables = new();
    public void Clear() => tables.Clear();

    public LevelBuilderCatalog Catalog(ModProject project, string profile, string locale, IReadOnlyList<LevelRow> rows, CancellationToken token)
    {
        var issues = new List<string>();
        var data = tables.Open(project, profile, locale, issues, token);
        string Title(string key, string name) => key.Length == 0 ? name : data.Localize(key, false);
        var entries = rows.Select(r => new LevelEntry(r.Row, r.SourceId, r.Name, r.Id, Title(r.LevelName, r.Name), Whole(r.Act), r.AreaLevels, IsWaypoint(r.Waypoint))).ToArray();
        token.ThrowIfCancellationRequested();
        var levels = rows.Where(r => r.Name.Length > 0 && r.Id.Length > 0)
            .Select(r => new LevelChoice(r.Id, r.Name, Title(r.LevelName, r.Name), Whole(r.Act), IsWaypoint(r.Waypoint), r.Vis.Split(',').Where(v => v is not ("" or "0")).ToArray()))
            .DistinctBy(l => l.Id).ToArray();
        var monsters = data.Rows("monstats", false).Where(m => m.S("Id").Length > 0)
            .Select(m => new LevelMonster(m.S("Id"), m.S("NameStr") is { Length: > 0 } key ? data.Localize(key, false) : m.S("Id"), Whole(m.S("Level")), Whole(m.S("Level(N)")), Whole(m.S("Level(H)"))))
            .DistinctBy(m => m.Id).ToArray();
        token.ThrowIfCancellationRequested();
        var warps = data.Rows("lvlwarp", false).Where(w => w.S("Id").Length > 0).Select(w => new LevelCode(w.S("Id"), w.S("Name"))).DistinctBy(w => w.Code).ToArray();
        var types = data.Rows("lvltypes", false).Where(t => t.S("Id").Length > 0).Select(t => new LevelCode(t.S("Id"), t.S("Name"))).DistinctBy(t => t.Code).ToArray();
        var objects = data.Rows("objgroup", false).Select((g, i) => new LevelCode(i.ToString(CultureInfo.InvariantCulture), g.S("GroupName"))).ToArray();
        var sounds = data.Rows("soundenviron", false).Select((s, i) => new LevelCode(i.ToString(CultureInfo.InvariantCulture), s.S("Handle"))).ToArray();
        var groups = data.Rows("levelgroups", false).Where(g => g.S("LevelGroupId").Length > 0)
            .Select(g => new LevelCode(g.S("LevelGroupId"), g.S("NameString") is { Length: > 0 } key ? data.Localize(key, false) : "")).DistinctBy(g => g.Code).ToArray();
        return new(entries, levels, monsters, warps, types, objects, sounds, groups, [.. issues.Distinct()]);
    }

    /// <summary>A level row as the game builds it: its names, what each difficulty makes of it, what it spawns, where it leads and what leads to it.</summary>
    public LevelPreview Resolve(ModProject project, JsonObject record, IReadOnlyList<LevelRow> rows, string profile, string locale, CancellationToken token)
    {
        var issues = new List<string>();
        var data = tables.Open(project, profile, locale, issues, token);
        var row = data.Effective("levels", record);
        int Number(string column)
        {
            var text = row.S(column).Trim();
            if (text.Length == 0) return 0;
            if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)) return value;
            issues.Add($"{column} is not a whole number: {text}."); return 0;
        }
        string Text(string column) => row.S(column) is { Length: > 0 } key ? data.Localize(key) : "";
        // The first row holding a key: vanilla lvlwarp repeats some Ids, and the game takes the first.
        var firsts = new Dictionary<string, Dictionary<string, JsonObject>>();
        JsonObject? First(string table, string column, string value)
        {
            if (!firsts.TryGetValue(table, out var byKey))
                firsts[table] = byKey = data.Rows(table, false).Where(r => r.S(column).Length > 0).GroupBy(r => r.S(column), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            return byKey.GetValueOrDefault(value);
        }
        var id = row.S("Id"); var name = row.S("Name");
        var title = row.S("LevelName") is { Length: > 0 } ? Text("LevelName") : name;

        var difficulties = MonsterData.Difficulties.Select(d =>
        {
            int ex = Number("MonLvlEx" + d.Suffix), classic = Number("MonLvl" + d.Suffix);
            var level = new LevelDifficulty(d.Name, ex > 0 ? ex : classic, (ex > 0 ? "MonLvlEx" : "MonLvl") + d.Suffix, Number("MonDen" + d.Suffix),
                Number("MonUMin" + d.Suffix), Number("MonUMax" + d.Suffix), Number("SizeX" + d.Suffix), Number("SizeY" + d.Suffix));
            if (level.UniqueMin > level.UniqueMax) issues.Add($"{d.Name}: MonUMin{d.Suffix} ({level.UniqueMin}) is above MonUMax{d.Suffix} ({level.UniqueMax}).");
            return level;
        }).ToArray();

        token.ThrowIfCancellationRequested();
        LevelSpawn[] Pool(string prefix, Func<JsonObject, int[]> levels)
        {
            var spawns = new List<LevelSpawn>();
            for (int i = 1; i <= 25; i++)
            {
                var monster = row.S(prefix + i); if (monster.Length == 0) continue;
                var found = First("monstats", "Id", monster);
                if (found == null) issues.Add($"{prefix}{i}: no monstats row has Id {monster}.");
                spawns.Add(new(prefix + i, monster, found?.S("NameStr") is { Length: > 0 } key ? data.Localize(key, false) : monster, found != null, found == null ? [] : levels(found)));
            }
            return [.. spawns];
        }
        // Normal monsters keep their own level; in Nightmare and Hell ordinary monsters take the area's.
        var normal = Pool("mon", m => [Whole(m.S("Level"))]);
        var nightmare = Pool("nmon", _ => [difficulties[1].AreaLevel, difficulties[2].AreaLevel]);
        var unique = Pool("umon", m => [Whole(m.S("Level"))]);
        var critters = Enumerable.Range(1, 4).Where(i => row.S("cmon" + i).Length > 0).Select(i =>
        {
            var monster = row.S("cmon" + i); var found = First("monstats", "Id", monster);
            if (found == null) issues.Add($"cmon{i}: no monstats row has Id {monster}.");
            return new LevelCritter("cmon" + i, monster, found?.S("NameStr") is { Length: > 0 } key ? data.Localize(key, false) : monster, found != null, Number("cpct" + i), Number("camt" + i));
        }).ToArray();
        int kinds = Number("NumMon");
        if (kinds > MaxKinds) issues.Add($"NumMon is {kinds}; the game picks at most {MaxKinds} kinds of monster.");
        if (difficulties[0].Density > 0 && normal.Length == 0) issues.Add("MonDen spawns monsters in Normal but no mon# slot names one.");
        if ((difficulties[1].Density > 0 || difficulties[2].Density > 0) && nightmare.Length == 0) issues.Add("MonDen spawns monsters in Nightmare and Hell but no nmon# slot names one.");

        token.ThrowIfCancellationRequested();
        var others = rows.Where(r => r.Name.Length > 0 && r.Id.Length > 0).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
        if (id.Length > 0 && rows.Count(r => r.Id == id && r.Name.Length > 0) > 1) issues.Add($"Id {id} is used by more than one level.");
        LevelChoice Choice(LevelRow r) => new(r.Id, r.Name, r.LevelName.Length > 0 ? data.Localize(r.LevelName, false) : r.Name, Whole(r.Act), IsWaypoint(r.Waypoint), r.Vis.Split(',').Where(v => v is not ("" or "0")).ToArray());
        bool hasWarps = data.Rows("lvlwarp", false).Count > 0;
        var links = new List<LevelLink>();
        for (int i = 0; i < 8; i++)
        {
            var target = row.S("Vis" + i); if (target is "" or "0") continue;
            var warp = row.S("Warp" + i);
            others.TryGetValue(target, out var other);
            if (other == null) issues.Add($"Vis{i}: no level has Id {target}.");
            // A link without a warp is walked across the border (the town and Blood Moor).
            var warpRow = warp is "" or "-1" || !hasWarps ? null : First("lvlwarp", "Id", warp);
            if (hasWarps && warp is not ("" or "-1") && warpRow == null) issues.Add($"Warp{i}: no lvlwarp row has Id {warp}.");
            var choice = other == null ? null : Choice(other);
            links.Add(new(i, target, other?.Name ?? "", choice?.Title ?? "", choice?.Act ?? -1, other != null, warp, warpRow?.S("Name") ?? "", choice?.Waypoint == true, choice?.Vis.Contains(id) == true));
        }
        // The Null level (Id 0) is never built; vanilla leaves its warps at 0.
        for (int i = 0; i < 8 && id is not ("" or "0"); i++)
            if (row.S("Vis" + i) is "" or "0" && row.S("Warp" + i) is { Length: > 0 } stray && stray != "-1") issues.Add($"Warp{i} is {stray} but Vis{i} leads nowhere.");
        var linked = links.Select(l => l.Id).ToHashSet();
        var outdoor = new List<LevelOutdoorLink>();
        foreach (var border in OutdoorBorders)
        {
            var target = border.A == id ? border.B : border.B == id ? border.A : null;
            if (target == null || !MatchesOutdoorLevel(id, row.S("Act"), row.S("DrlgType"), border.Act)
                || rows.Count(r => r.Id == id && r.Name.Length > 0) != 1 || rows.Count(r => r.Id == target && r.Name.Length > 0) != 1
                || !others.TryGetValue(target, out var other) || !MatchesOutdoorLevel(target, other.Act, other.DrlgType, border.Act)) continue;
            outdoor.Add(new(Choice(other), border.Variable));
            // A guaranteed generated border supplies the return path even without a Vis slot.
            if (!border.Variable)
            {
                linked.Add(target);
                for (int i = 0; i < links.Count; i++) if (links[i].Id == target) links[i] = links[i] with { Back = true };
            }
        }
        var inbound = id.Length == 0 ? [] : rows.Where(r => r.Name.Length > 0 && r.Id != id && !linked.Contains(r.Id) && r.Vis.Split(',').Contains(id)).Select(Choice).DistinctBy(c => c.Id).ToArray();
        var dependId = row.S("Depend");
        LevelChoice? depend = dependId is "" or "0" ? null : others.TryGetValue(dependId, out var dependRow) ? Choice(dependRow) : null;
        if (dependId is not ("" or "0") && depend == null) issues.Add($"Depend: no level has Id {dependId}.");

        // ObjGrp# is an objgroup row index.
        var objectGroups = data.Rows("objgroup", false);
        var objects = Enumerable.Range(0, 8).Where(i => row.S("ObjGrp" + i) is not ("" or "0")).Select(i =>
        {
            var group = row.S("ObjGrp" + i);
            bool known = int.TryParse(group, out var index) && index >= 0 && index < objectGroups.Count;
            if (objectGroups.Count > 0 && !known) issues.Add($"ObjGrp{i}: objgroup has no row {group}.");
            return new LevelObjectGroup(i, group, known ? objectGroups[index].S("GroupName") : "", Number("ObjPrb" + i));
        }).ToArray();

        int drlg = Number("DrlgType");
        var levelType = row.S("LevelType") is { Length: > 0 } typeId ? First("lvltypes", "Id", typeId)?.S("Name") ?? "" : "";
        if (row.S("LevelType") is { Length: > 0 } unknownType && levelType.Length == 0 && data.Rows("lvltypes", false).Count > 0) issues.Add($"LevelType: no lvltypes row has Id {unknownType}.");
        var sounds = data.Rows("soundenviron", false);
        var sound = int.TryParse(row.S("SoundEnv"), out var soundIndex) && soundIndex >= 0 && soundIndex < sounds.Count ? sounds[soundIndex].S("Handle") : "";
        var group = row.S("LevelGroup") is { Length: > 0 } groupId ? First("levelgroups", "LevelGroupId", groupId) is { } groupRow
            ? groupRow.S("NameString") is { Length: > 0 } groupKey ? data.Localize(groupKey, false) : groupId : groupId : "";

        return new(title, Text("LevelEntry"), Text("LevelWarp"), Number("Act"), difficulties, kinds, normal, nightmare, unique, critters, [.. links], inbound, depend, objects,
            drlg >= 0 && drlg < DrlgTypes.Length ? DrlgTypes[drlg] : "DRLG " + drlg, levelType, sound, group, Number("Teleport"), IsWaypoint(row.S("Waypoint")), Number("PreventTownPortal") == 0,
            (Number("Intensity"), Number("Red"), Number("Green"), Number("Blue")), [.. issues.Distinct()]) { OutdoorLinks = [.. outdoor] };
    }

    /// <summary>A Waypoint cell names a waypoint when it holds a number below 255.</summary>
    public static bool IsWaypoint(string value) => int.TryParse(value, out var waypoint) && waypoint is >= 0 and < 255;

    private static int Whole(string text) => int.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : 0;

    /// <summary>The area level each difficulty sets, "1 / 36 / 67": MonLvlEx where it is set, else MonLvl.</summary>
    public static string AreaLevels(Func<string, string> cell) => string.Join(" / ", MonsterData.Difficulties.Select(d => Whole(cell("MonLvlEx" + d.Suffix)) is > 0 and var ex ? ex : Whole(cell("MonLvl" + d.Suffix))));

    public static LevelRow[] Rows(TableData table)
    {
        string Cell(int row, string column) => table.ColumnIndex(column) >= 0 ? table.Cell(row, column) : "";
        return [.. Enumerable.Range(0, table.Records.Count).Select(i => new LevelRow(i, table.Records[i].S("sourceId"), Cell(i, "Name"), Cell(i, "Id"), Cell(i, "Act"), Cell(i, "LevelName"),
            AreaLevels(c => Cell(i, c)), Cell(i, "Waypoint"), string.Join(",", Enumerable.Range(0, 8).Select(n => Cell(i, "Vis" + n))), Cell(i, "DrlgType")))];
    }
}

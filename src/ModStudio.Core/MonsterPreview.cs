using System.Globalization;
using System.Text.Json.Nodes;
using static ModStudio.Core.PreviewMath;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>
/// A monster at one difficulty and level: its scaled stats and resistances. Empty strings mean not authored.
/// <paramref name="Sources"/> holds the cells each column was read from, by column header.
/// </summary>
public record MonsterLevelPreview(string Difficulty, string Level, string Life, string Defense, string AttackRating, string Damage, string Experience,
    string Physical, string Magic, string Fire, string Lightning, string Cold, string Poison, IReadOnlyDictionary<string, CellLink[]>? Sources = null);

public record MonsterPreviewResult(string Name, string[] Lines, PreviewSection[] Sections, MonsterLevelPreview[] Levels, string[] Issues)
{
    public PreviewText[] Text { get; } = PreviewText.Parse(Lines);
    public string[] Lines { get; } = PreviewText.Plain(Lines);
    /// <summary>The HD unit and colour variant the monster's BaseId and TransLvl pick; null when not resolved.</summary>
    public MonsterAppearance? Appearance { get; init; }
}

/// <summary>
/// Worker-owned resolver for a monstats.txt (or superuniques.txt) row: what the monster is at each difficulty, where it
/// spawns, what it attacks with, and what it drops. Reads authored data only; never changes records or starts a build.
/// </summary>
/// <remarks>
/// Life, defense, attack rating, damage and experience in monstats.txt are percentages of the monlvl.txt values for the
/// monster's level (the expansion L- columns), unless noRatio is set, when they are used as they are.
/// </remarks>
public sealed class MonsterPreviewResolver
{
    private readonly PreviewTables tables = new();
    public void Clear() => tables.Clear();
    private static readonly (string Column, string Label)[] Resistances =
        [("ResDm", "physical"), ("ResMa", "magic"), ("ResFi", "fire"), ("ResLi", "lightning"), ("ResCo", "cold"), ("ResPo", "poison")];

    public MonsterPreviewResult Resolve(ModProject project, string table, JsonObject record, string profile, string locale, CancellationToken token, DropPreviewOptions? options = null, IReadOnlyList<string>? gameData = null)
    {
        Require(table is "monstats" or "superuniques", "Select a row in monstats or superuniques.");
        options ??= new();
        var issues = new List<string>();
        var data = tables.Open(project, profile, locale, issues, token);
        var row = data.Effective(table, record);
        JsonObject? superunique = null, monster = row;
        if (table == "superuniques")
        {
            superunique = row;
            if (row.S("Superunique").Length == 0) return new("Inactive row", ["Inactive/header row · no superunique"], [], [], []);
            monster = data.Find("monstats", "Id", row.S("Class"), false);
            if (monster == null) return new(row.S("Superunique"), [], [], [], [$"Unresolved monstats/Id: {row.S("Class")}."]);
        }
        var id = monster.S("Id");
        if (id.Length == 0) return new("Inactive row", ["Inactive/header row · no monster id"], [], [], []);
        var monsters = new MonsterData(data);
        var name = superunique != null && superunique.S("Name") is { Length: > 0 } nameKey ? data.Localize(nameKey) : monster.S("NameStr") is { Length: > 0 } key ? data.Localize(key) : id;

        // Each part of a line links to the cell it was read from.
        string Link(string text, JsonObject? row, params string[] columns) => data.Link(text, row, columns);
        var lines = new List<string>();
        if (superunique != null) lines.Add($"Superunique on {Link($"{monsters.Name(monster)} ({id})", superunique, "Class")} · {Link($"group {superunique.S("MinGrp", "0")}–{superunique.S("MaxGrp", "0")}", superunique, "MinGrp", "MaxGrp")} · mods {string.Join(", ", new[] { "Mod1", "Mod2", "Mod3" }.Where(m => superunique.S(m) is not ("" or "0")).Select(m => Link(superunique.S(m), superunique, m)))}");
        lines.Add(string.Join(" · ", new[] { Link(id, monster, "Id"), Link(monster.S("MonType"), monster, "MonType"), monster.S("AI").Length > 0 ? Link("AI " + monster.S("AI"), monster, "AI") : "" }.Where(s => s.Length > 0)));
        var flags = new[] { ("boss", "boss"), ("primeevil", "prime evil"), ("lUndead", "undead"), ("hUndead", "undead (high)"), ("demon", "demon"), ("flying", "flying"),
            ("npc", "NPC"), ("inTown", "in town"), ("isMelee", "melee"), ("opendoors", "opens doors") }.Where(f => Flag(monster, f.Item1)).Select(f => Link(f.Item2, monster, f.Item1)).ToArray();
        if (flags.Length > 0) lines.Add(string.Join(", ", flags));
        if (monster.S("enabled", "1") == "0") issues.Add("enabled is 0, so this monster never spawns.");
        if (!Flag(monster, "killable")) lines.Add(Link("Not killable", monster, "killable"));

        // Stats per difficulty, at each distinct level the monster has there.
        bool noRatio = Flag(monster, "noRatio");
        var levels = new List<MonsterLevelPreview>();
        for (int difficulty = 0; difficulty < 3; difficulty++)
        {
            var (low, high, note, lowCell, highCell) = monsters.Levels(monster, difficulty);
            int bonus = superunique != null ? 3 : 0;
            foreach (var (level, cell) in low == high ? new[] { (low, lowCell) } : [(low, lowCell), (high, highCell)])
            {
                token.ThrowIfCancellationRequested();
                levels.Add(Stats(data, monster, difficulty, level + bonus, cell, noRatio, issues));
            }
            if (low != high) lines.Add($"{MonsterData.Difficulties[difficulty].Name}: levels {PreviewText.Mark($"{low + bonus}", [lowCell])}–{PreviewText.Mark($"{high + bonus}", [highCell])} from {note}");
        }
        var immunities = Enumerable.Range(0, 3).Select(d => (Difficulty: MonsterData.Difficulties[d].Name,
            Immune: Resistances.Where(r => DropCalculator.Int(monster, r.Column + MonsterData.Difficulties[d].Suffix) >= 100).Select(r => Link(r.Label, monster, r.Column + MonsterData.Difficulties[d].Suffix)).ToArray()))
            .Where(x => x.Immune.Length > 0).Select(x => $"{x.Difficulty}: immune to {string.Join(", ", x.Immune)}").ToArray();
        lines.AddRange(immunities);
        lines.Add($"{profile} · {locale}");

        var sections = new List<PreviewSection>();
        var spawns = monsters.Spawns(id);
        var spawnLines = Enumerable.Range(0, 3).Select(d => (d, Areas: spawns.Where(s => s.Difficulty == d).GroupBy(s => s.Name)
                .Select(g => $"{PreviewText.Mark(g.Key, g.Select(s => s.Slot))} ({PreviewText.Mark(g.First().Level.ToString(CultureInfo.InvariantCulture), [g.First().LevelCell])})").ToArray()))
            .Where(x => x.Areas.Length > 0).Select(x => $"{MonsterData.Difficulties[x.d].Name}: {string.Join(", ", x.Areas.Take(12))}{(x.Areas.Length > 12 ? $" +{x.Areas.Length - 12} more" : "")}").ToList();
        foreach (var other in data.Rows("monstats", false))
        {
            foreach (var column in new[] { "minion1", "minion2" })
                if (other.S(column) == id) { spawnLines.Add($"Minion of {Link($"{monsters.Name(other)} ({other.S("Id")})", other, column)}"); break; }
            if (other.S("spawn") == id) spawnLines.Add($"Spawned by {Link($"{monsters.Name(other)} ({other.S("Id")})", other, "spawn")}");
        }
        foreach (var skill in data.Rows("skills", false)) if (skill.S("summon") == id) spawnLines.Add($"Summoned by skill {Link(skill.S("skill"), skill, "summon")}");
        if (superunique == null)
            foreach (var su in data.Rows("superuniques", false)) if (su.S("Class") == id) spawnLines.Add($"Superunique {Link(su.S("Name") is { Length: > 0 } k ? data.Localize(k, false) : su.S("Superunique"), su, "Class")}");
        if (spawnLines.Count > 0) sections.Add(new("Spawns", [.. spawnLines], BeforeLevels: true));

        var group = new List<string>();
        if (monster.S("MinGrp").Length > 0) group.Add($"Packs of {Link(monster.S("MinGrp"), monster, "MinGrp")}–{Link(monster.S("MaxGrp", monster.S("MinGrp")), monster, "MaxGrp")}");
        foreach (var minion in new[] { "minion1", "minion2" })
            if (monster.S(minion) is { Length: > 0 } minionId)
            {
                var other = data.Find("monstats", "Id", minionId, false);
                if (other == null) issues.Add($"Unresolved monstats/Id: {minionId}.");
                group.Add($"{Link(minion, monster, minion)}: {(other != null ? Link(monsters.Name(other), other, "Id") + " " : "")}({minionId}) × {Link(monster.S("PartyMin", "0"), monster, "PartyMin")}–{Link(monster.S("PartyMax", "0"), monster, "PartyMax")}");
            }
        if (monster.S("spawn") is { Length: > 0 } spawned) group.Add($"Spawns {Link(spawned, monster, "spawn")}" + (Flag(monster, "placespawn") ? $" ({Link("placed as a spawner", monster, "placespawn")})" : ""));
        if (group.Count > 0) sections.Add(new("Group", [.. group], BeforeLevels: true));

        var attacks = new List<string>();
        for (int i = 1; i <= 8; i++)
            if (monster.S("Skill" + i) is { Length: > 0 } skill)
            {
                var skillRow = data.Find("skills", "skill", skill, false);
                if (skillRow == null) issues.Add($"Unresolved skills/skill: {skill}.");
                attacks.Add($"{Link("Skill" + i, monster, "Skill" + i)}: {(skillRow != null ? Link(skill, skillRow, "skill") : skill)} · {Link($"level {monster.S($"Sk{i}lvl", "1")}", monster, $"Sk{i}lvl")}"
                    + (monster.S($"Sk{i}mode") is { Length: > 0 } mode ? $" · {Link("mode " + mode, monster, $"Sk{i}mode")}" : ""));
            }
        for (int i = 1; i <= 3; i++)
            if (monster.S($"El{i}Mode") is { Length: > 0 } mode)
            {
                var parts = Enumerable.Range(0, 3).Where(d => DropCalculator.Int(monster, $"El{i}MaxD{MonsterData.Difficulties[d].Suffix}") > 0).Select(d =>
                {
                    var s = MonsterData.Difficulties[d].Suffix;
                    var level = levels.First(l => l.Difficulty.StartsWith(MonsterData.Difficulties[d].Name, StringComparison.Ordinal));
                    int monsterLevel = int.Parse(level.Level, CultureInfo.InvariantCulture);
                    var scale = noRatio ? 100 : Multiplier(data, monsterLevel, "DM", d);
                    int min = DropCalculator.Int(monster, $"El{i}MinD{s}") * scale / 100, max = DropCalculator.Int(monster, $"El{i}MaxD{s}") * scale / 100;
                    var duration = DropCalculator.Int(monster, $"El{i}Dur{s}");
                    var damage = PreviewText.Mark($"{min}–{max}", [data.Cell(monster, $"El{i}MinD{s}"), data.Cell(monster, $"El{i}MaxD{s}"), noRatio ? null : MultiplierCell(data, monsterLevel, "DM", d)]);
                    return $"{MonsterData.Difficulties[d].Name} {Link(monster.S($"El{i}Pct{s}", "100") + "%", monster, $"El{i}Pct{s}")} {damage}"
                        + (duration > 0 ? $" for {Link(PreviewMath.Seconds(duration), monster, $"El{i}Dur{s}")}" : "");
                });
                attacks.Add($"El{i}: {Link(monster.S($"El{i}Type"), monster, $"El{i}Type")} on {Link(mode, monster, $"El{i}Mode")} · {string.Join(" · ", parts)}");
            }
        if (attacks.Count > 0) sections.Add(new("Skills and elemental attacks", [.. attacks], BeforeLevels: true));

        // What each kind of kill drops, after the TC moves up to the monster level.
        var calc = new DropCalculator(data, token);
        var drops = new List<string>();
        foreach (var source in monsters.Sources(calc, monster, superunique))
        {
            token.ThrowIfCancellationRequested();
            var tc = calc.Find(source.TreasureClass);
            if (tc == null) { issues.Add($"Unresolved treasure class: {source.TreasureClass}."); continue; }
            var settings = new DropSettings(options.Players, options.Party, options.MagicFind, source.Level);
            var leaves = calc.Expand(source.TreasureClass, settings);
            var named = calc.Named(leaves, settings);
            double unique = named.Where(n => n.Key.Table == "uniqueitems").Sum(n => n.Value), set = named.Where(n => n.Key.Table == "setitems").Sum(n => n.Value);
            var best = named.OrderByDescending(n => n.Value).FirstOrDefault();
            var authored = PreviewText.Mark(source.Authored, [source.Cell]);
            var used = source.Authored == source.TreasureClass ? authored : Link(source.TreasureClass, tc.Row, "Treasure Class");
            drops.Add($"{source.DifficultyName} {source.Kind} · level {source.Level} · {used}" + (source.Authored != source.TreasureClass ? $" (from {authored})" : ""));
            drops.Add($"    {leaves.Values.Sum():0.##} items per kill · any unique {DropCalculator.Odds(unique)} · any set {DropCalculator.Odds(set)}"
                + (best.Value > 0 ? $" · likeliest {calc.Localize(best.Key.Index)} {DropCalculator.Odds(best.Value)}" : ""));
        }
        issues.AddRange(calc.Issues);
        if (drops.Count > 0) sections.Add(new("Drops", [.. drops]));
        sections.Add(new("Assumptions", [
            noRatio ? "noRatio is set, so life, defense, attack, damage and experience are the monstats values as written."
                : "Life, defense, attack, damage and experience are the monstats values times the monlvl L- percentage for the level, rounded down, so a small edit can leave a low-level number unchanged.",
            "Normal uses monstats Level; Nightmare and Hell ordinary monsters take the level of the area they spawn in (the lowest and highest are shown); bosses keep their own.",
            "Champion and unique packs add monumod bonuses (more life, damage, resistances) that are not applied here.",
            $"Drops use /players {options.Players}, party {options.Party} and {options.MagicFind}% magic find; open a treasure class for its full drop table.",
            "Elemental attack damage is scaled by monlvl DM like physical damage."]));
        MonsterAppearance? appearance = null;
        try { appearance = HdAppearance.Resolve(project, gameData ?? [], monster, data.Rows("monstats", false), token); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException) { issues.Add("HD appearance: " + ex.Message); }
        return new(name, [.. lines], [.. sections], [.. levels], [.. issues.Distinct()]) { Appearance = appearance };
    }

    /// <summary>A monlvl.txt multiplier for a level and difficulty, from the expansion L- column when there is one.</summary>
    private static int Multiplier(PreviewTables.Session data, int level, string stat, int difficulty)
    {
        var row = data.Find("monlvl", "Level", level.ToString(CultureInfo.InvariantCulture), false);
        return row == null ? 0 : DropCalculator.Int(row, MultiplierColumn(row, stat, difficulty));
    }
    private static string MultiplierColumn(JsonObject row, string stat, int difficulty)
    {
        var suffix = MonsterData.Difficulties[difficulty].Suffix;
        return row.S($"L-{stat}{suffix}").Length > 0 ? $"L-{stat}{suffix}" : stat + suffix;
    }
    /// <summary>The monlvl.txt cell <see cref="Multiplier"/> reads.</summary>
    private static CellLink? MultiplierCell(PreviewTables.Session data, int level, string stat, int difficulty) =>
        data.Find("monlvl", "Level", level.ToString(CultureInfo.InvariantCulture), false) is { } row ? data.Cell(row, MultiplierColumn(row, stat, difficulty)) : null;

    private static MonsterLevelPreview Stats(PreviewTables.Session data, JsonObject monster, int difficulty, int level, CellLink? levelCell, bool noRatio, List<string> issues)
    {
        var suffix = MonsterData.Difficulties[difficulty].Suffix;
        if (!noRatio && data.Find("monlvl", "Level", level.ToString(CultureInfo.InvariantCulture), false) == null) issues.Add($"monlvl has no row for level {level}.");
        // Column spelling varies by difficulty (minHP but MinHP(N)), so the difficulty's column is matched ignoring case.
        string Column(string column)
        {
            var wanted = difficulty == 0 ? column : column + suffix;
            return monster.Select(f => f.Key).FirstOrDefault(k => k.Equals(wanted, StringComparison.OrdinalIgnoreCase)) ?? wanted;
        }
        int Scale(string column, string stat)
        {
            int value = DropCalculator.Int(monster, Column(column));
            return noRatio ? value : (int)((long)value * Multiplier(data, level, stat, difficulty) / 100);
        }
        bool Has(string column) => monster.S(Column(column)).Length > 0;
        string Range(int low, int high) => low == high ? low.ToString("N0", CultureInfo.InvariantCulture) : $"{low.ToString("N0", CultureInfo.InvariantCulture)}–{high.ToString("N0", CultureInfo.InvariantCulture)}";
        var damage = new[] { "A1", "A2", "S1" }.Where(a => Has(a + "MaxD")).Select(a => $"{a} {Range(Scale(a + "MinD", "DM"), Scale(a + "MaxD", "DM"))}").ToArray();
        string Resist(string column) => monster.S(Column(column));
        // A scaled value is read from its monstats columns and the monlvl multiplier for the level.
        CellLink[] From(string stat, params string[] columns) =>
            [.. columns.Where(Has).Select(c => data.Cell(monster, Column(c))).Append(noRatio ? null : MultiplierCell(data, level, stat, difficulty)).OfType<CellLink>()];
        var sources = new Dictionary<string, CellLink[]>
        {
            ["Lvl"] = levelCell != null ? [levelCell] : [],
            ["Life"] = From("HP", "minHP", "maxHP"), ["Defense"] = From("AC", "AC"), ["Attack"] = From("TH", "A1TH"), ["Exp"] = From("XP", "Exp"),
            ["Damage"] = From("DM", [.. new[] { "A1", "A2", "S1" }.SelectMany(a => new[] { a + "MinD", a + "MaxD" })]),
        };
        foreach (var (column, header) in new[] { ("ResDm", "Phys"), ("ResMa", "Magic"), ("ResFi", "Fire"), ("ResLi", "Light"), ("ResCo", "Cold"), ("ResPo", "Poison") })
            sources[header] = data.Cell(monster, Column(column)) is { } cell ? [cell] : [];
        return new(MonsterData.Difficulties[difficulty].Name, level.ToString(CultureInfo.InvariantCulture),
            Range(Scale("minHP", "HP"), Scale("maxHP", "HP")),
            Has("AC") ? Scale("AC", "AC").ToString("N0", CultureInfo.InvariantCulture) : "",
            Has("A1TH") ? Scale("A1TH", "TH").ToString("N0", CultureInfo.InvariantCulture) : "",
            string.Join(", ", damage),
            Has("Exp") ? Scale("Exp", "XP").ToString("N0", CultureInfo.InvariantCulture) : "",
            Resist("ResDm"), Resist("ResMa"), Resist("ResFi"), Resist("ResLi"), Resist("ResCo"), Resist("ResPo"), sources);
    }
}

using System.Globalization;
using System.Text.Json.Nodes;
using static ModStudio.Core.PreviewMath;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>A monster at one difficulty and level: its scaled stats and resistances. Empty strings mean not authored.</summary>
public record MonsterLevelPreview(string Difficulty, string Level, string Life, string Defense, string AttackRating, string Damage, string Experience,
    string Physical, string Magic, string Fire, string Lightning, string Cold, string Poison);

public record MonsterPreviewResult(string Name, string[] Lines, PreviewSection[] Sections, MonsterLevelPreview[] Levels, string[] Issues);

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

    public MonsterPreviewResult Resolve(ModProject project, string table, JsonObject record, string profile, string locale, CancellationToken token, DropPreviewOptions? options = null)
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

        var lines = new List<string>();
        if (superunique != null) lines.Add($"Superunique on {monsters.Name(monster)} ({id}) · group {superunique.S("MinGrp", "0")}–{superunique.S("MaxGrp", "0")} · mods {string.Join(", ", new[] { "Mod1", "Mod2", "Mod3" }.Select(m => superunique.S(m)).Where(m => m is not ("" or "0")))}");
        lines.Add(string.Join(" · ", new[] { id, monster.S("MonType"), monster.S("AI").Length > 0 ? "AI " + monster.S("AI") : "" }.Where(s => s.Length > 0)));
        var flags = new[] { ("boss", "boss"), ("primeevil", "prime evil"), ("lUndead", "undead"), ("hUndead", "undead (high)"), ("demon", "demon"), ("flying", "flying"),
            ("npc", "NPC"), ("inTown", "in town"), ("isMelee", "melee"), ("opendoors", "opens doors") }.Where(f => Flag(monster, f.Item1)).Select(f => f.Item2).ToArray();
        if (flags.Length > 0) lines.Add(string.Join(", ", flags));
        if (monster.S("enabled", "1") == "0") issues.Add("enabled is 0, so this monster never spawns.");
        if (!Flag(monster, "killable")) lines.Add("Not killable");

        // Stats per difficulty, at each distinct level the monster has there.
        bool noRatio = Flag(monster, "noRatio");
        var levels = new List<MonsterLevelPreview>();
        for (int difficulty = 0; difficulty < 3; difficulty++)
        {
            var (low, high, note) = monsters.Levels(monster, difficulty);
            int bonus = superunique != null ? 3 : 0;
            foreach (var level in (low == high ? new[] { low } : [low, high]).Select(l => l + bonus))
            {
                token.ThrowIfCancellationRequested();
                levels.Add(Stats(data, monster, difficulty, level, noRatio, issues));
            }
            if (low != high) lines.Add($"{MonsterData.Difficulties[difficulty].Name}: levels {low + bonus}–{high + bonus} from {note}");
        }
        var immunities = Enumerable.Range(0, 3).Select(d => (Difficulty: MonsterData.Difficulties[d].Name,
            Immune: Resistances.Where(r => DropCalculator.Int(monster, r.Column + MonsterData.Difficulties[d].Suffix) >= 100).Select(r => r.Label).ToArray()))
            .Where(x => x.Immune.Length > 0).Select(x => $"{x.Difficulty}: immune to {string.Join(", ", x.Immune)}").ToArray();
        lines.AddRange(immunities);
        lines.Add($"{profile} · {locale}");

        var sections = new List<PreviewSection>();
        var spawns = monsters.Spawns(id);
        var spawnLines = Enumerable.Range(0, 3).Select(d => (d, Areas: spawns.Where(s => s.Difficulty == d).GroupBy(s => s.Name).Select(g => $"{g.Key} ({g.First().Level})").ToArray()))
            .Where(x => x.Areas.Length > 0).Select(x => $"{MonsterData.Difficulties[x.d].Name}: {string.Join(", ", x.Areas.Take(12))}{(x.Areas.Length > 12 ? $" +{x.Areas.Length - 12} more" : "")}").ToList();
        foreach (var other in data.Rows("monstats", false))
        {
            if (other.S("minion1") == id || other.S("minion2") == id) spawnLines.Add($"Minion of {monsters.Name(other)} ({other.S("Id")})");
            if (other.S("spawn") == id) spawnLines.Add($"Spawned by {monsters.Name(other)} ({other.S("Id")})");
        }
        foreach (var skill in data.Rows("skills", false)) if (skill.S("summon") == id) spawnLines.Add($"Summoned by skill {skill.S("skill")}");
        if (superunique == null)
            foreach (var su in data.Rows("superuniques", false)) if (su.S("Class") == id) spawnLines.Add($"Superunique {(su.S("Name") is { Length: > 0 } k ? data.Localize(k, false) : su.S("Superunique"))}");
        if (spawnLines.Count > 0) sections.Add(new("Spawns", [.. spawnLines], BeforeLevels: true));

        var group = new List<string>();
        if (monster.S("MinGrp").Length > 0) group.Add($"Packs of {monster.S("MinGrp")}–{monster.S("MaxGrp", monster.S("MinGrp"))}");
        foreach (var minion in new[] { "minion1", "minion2" })
            if (monster.S(minion) is { Length: > 0 } minionId)
            {
                var other = data.Find("monstats", "Id", minionId, false);
                if (other == null) issues.Add($"Unresolved monstats/Id: {minionId}.");
                group.Add($"{minion}: {(other != null ? monsters.Name(other) + " " : "")}({minionId}) × {monster.S("PartyMin", "0")}–{monster.S("PartyMax", "0")}");
            }
        if (monster.S("spawn") is { Length: > 0 } spawned) group.Add($"Spawns {spawned}" + (Flag(monster, "placespawn") ? " (placed as a spawner)" : ""));
        if (group.Count > 0) sections.Add(new("Group", [.. group], BeforeLevels: true));

        var attacks = new List<string>();
        for (int i = 1; i <= 8; i++)
            if (monster.S("Skill" + i) is { Length: > 0 } skill)
            {
                if (data.Find("skills", "skill", skill, false) == null) issues.Add($"Unresolved skills/skill: {skill}.");
                attacks.Add($"Skill{i}: {skill} · level {monster.S($"Sk{i}lvl", "1")}" + (monster.S($"Sk{i}mode") is { Length: > 0 } mode ? $" · mode {mode}" : ""));
            }
        for (int i = 1; i <= 3; i++)
            if (monster.S($"El{i}Mode") is { Length: > 0 } mode)
            {
                var parts = Enumerable.Range(0, 3).Where(d => DropCalculator.Int(monster, $"El{i}MaxD{MonsterData.Difficulties[d].Suffix}") > 0).Select(d =>
                {
                    var s = MonsterData.Difficulties[d].Suffix;
                    var level = levels.First(l => l.Difficulty.StartsWith(MonsterData.Difficulties[d].Name, StringComparison.Ordinal));
                    var scale = noRatio ? 100 : Multiplier(data, int.Parse(level.Level, CultureInfo.InvariantCulture), "DM", d);
                    int min = DropCalculator.Int(monster, $"El{i}MinD{s}") * scale / 100, max = DropCalculator.Int(monster, $"El{i}MaxD{s}") * scale / 100;
                    var duration = DropCalculator.Int(monster, $"El{i}Dur{s}");
                    return $"{MonsterData.Difficulties[d].Name} {monster.S($"El{i}Pct{s}", "100")}% {min}–{max}" + (duration > 0 ? $" for {PreviewMath.Seconds(duration)}" : "");
                });
                attacks.Add($"El{i}: {monster.S($"El{i}Type")} on {mode} · {string.Join(" · ", parts)}");
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
            drops.Add($"{source.DifficultyName} {source.Kind} · level {source.Level} · {source.TreasureClass}" + (source.Authored != source.TreasureClass ? $" (from {source.Authored})" : ""));
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
        return new(name, [.. lines], [.. sections], [.. levels], [.. issues.Distinct()]);
    }

    /// <summary>A monlvl.txt multiplier for a level and difficulty, from the expansion L- column when there is one.</summary>
    private static int Multiplier(PreviewTables.Session data, int level, string stat, int difficulty)
    {
        var row = data.Find("monlvl", "Level", level.ToString(CultureInfo.InvariantCulture), false);
        if (row == null) return 0;
        var suffix = MonsterData.Difficulties[difficulty].Suffix;
        return row.S($"L-{stat}{suffix}").Length > 0 ? DropCalculator.Int(row, $"L-{stat}{suffix}") : DropCalculator.Int(row, stat + suffix);
    }

    private static MonsterLevelPreview Stats(PreviewTables.Session data, JsonObject monster, int difficulty, int level, bool noRatio, List<string> issues)
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
        return new(MonsterData.Difficulties[difficulty].Name, level.ToString(CultureInfo.InvariantCulture),
            Range(Scale("minHP", "HP"), Scale("maxHP", "HP")),
            Has("AC") ? Scale("AC", "AC").ToString("N0", CultureInfo.InvariantCulture) : "",
            Has("A1TH") ? Scale("A1TH", "TH").ToString("N0", CultureInfo.InvariantCulture) : "",
            string.Join(", ", damage),
            Has("Exp") ? Scale("Exp", "XP").ToString("N0", CultureInfo.InvariantCulture) : "",
            Resist("ResDm"), Resist("ResMa"), Resist("ResFi"), Resist("ResLi"), Resist("ResCo"), Resist("ResPo"));
    }
}

using System.Text.Json.Nodes;
using static ModStudio.Core.PreviewMath;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>An area a monster can spawn in on one difficulty, and the monster level that area sets.</summary>
public sealed record SpawnArea(string Area, string Name, int Difficulty, int Level, string Pool);

/// <summary>One way a kill rolls a treasure class: which monster, difficulty and kind, at what monster level.</summary>
/// <param name="Authored">The TC the row names; <paramref name="TreasureClass"/> is what the game uses after upgrading it to the monster level.</param>
public sealed record DropSource(string Monster, string Name, int Difficulty, string Kind, int Level, string Authored, string TreasureClass, string LevelNote)
{
    public string DifficultyName => MonsterData.Difficulties[Difficulty].Name;
}

/// <summary>
/// Where monsters spawn and what level they are, read from levels.txt, monstats.txt and superuniques.txt. In Normal a
/// monster is its monstats Level; in Nightmare and Hell ordinary monsters take the area's MonLvlEx instead, while
/// bosses keep their own. Champions are 2 levels higher and unique monsters 3.
/// </summary>
public sealed class MonsterData
{
    public static readonly (string Suffix, string Name)[] Difficulties = [("", "Normal"), ("(N)", "Nightmare"), ("(H)", "Hell")];
    /// <summary>monstats TC columns a kill uses, with the level bonus of that kind of monster.</summary>
    public static readonly (string Column, string Kind, int Bonus)[] Kinds =
        [("TreasureClass", "regular", 0), ("TreasureClassChamp", "champion", 2), ("TreasureClassUnique", "unique", 3), ("TreasureClassQuest", "quest", 0)];
    private readonly PreviewTables.Session data;
    private readonly Dictionary<string, List<SpawnArea>> spawns = new(StringComparer.OrdinalIgnoreCase);
    public MonsterData(PreviewTables.Session data)
    {
        this.data = data;
        foreach (var level in data.Rows("levels", false))
        {
            var area = level.S("Name"); if (area.Length == 0) continue;
            var name = level.S("LevelName") is { Length: > 0 } key ? data.Localize(key, false) : area;
            for (int difficulty = 0; difficulty < 3; difficulty++)
            {
                var suffix = Difficulties[difficulty].Suffix;
                int monsterLevel = DropCalculator.Int(level, "MonLvlEx" + suffix) is > 0 and var ex ? ex : DropCalculator.Int(level, "MonLvl" + suffix);
                // Normal draws its pool from mon#, Nightmare and Hell from nmon#; unique and champion packs come from umon#.
                foreach (var (prefix, pool) in new[] { (difficulty == 0 ? "mon" : "nmon", "regular"), ("umon", "unique") })
                    for (int i = 1; i <= 25; i++)
                        if (level.S(prefix + i) is { Length: > 0 } id)
                        {
                            if (!spawns.TryGetValue(id, out var list)) spawns[id] = list = [];
                            list.Add(new(area, name, difficulty, monsterLevel, pool));
                        }
            }
        }
    }

    public IReadOnlyList<SpawnArea> Spawns(string monster) => spawns.GetValueOrDefault(monster) ?? [];
    public string Name(JsonObject monster) => monster.S("NameStr") is { Length: > 0 } key ? data.Localize(key, false) : monster.S("Id");

    /// <summary>
    /// The monster levels a monster has on a difficulty, lowest and highest, with where they come from: its own Level
    /// column in Normal or for bosses, else the levels of the areas it spawns in.
    /// </summary>
    public (int Low, int High, string Note) Levels(JsonObject monster, int difficulty)
    {
        int own = DropCalculator.Int(monster, "Level" + Difficulties[difficulty].Suffix);
        if (difficulty == 0 || Flag(monster, "boss")) return (own, own, difficulty == 0 ? "monstats Level" : "boss: monstats Level");
        var areas = Spawns(monster.S("Id")).Where(s => s.Difficulty == difficulty && s.Level > 0).ToArray();
        if (areas.Length == 0) return (own, own, "monstats Level; spawns in no area");
        return (areas.Min(a => a.Level), areas.Max(a => a.Level), areas.Length == 1 ? "area " + areas[0].Name : $"areas {areas.MinBy(a => a.Level)!.Name} … {areas.MaxBy(a => a.Level)!.Name}");
    }

    /// <summary>
    /// Every TC roll a monster row makes, per difficulty and kind, at the highest level it reaches there. Superunique rows
    /// use their own TC columns on their class monster, 3 levels up.
    /// </summary>
    public IEnumerable<DropSource> Sources(DropCalculator calc, JsonObject monster, JsonObject? superunique = null)
    {
        var id = monster.S("Id"); var name = superunique != null && superunique.S("Name") is { Length: > 0 } key ? data.Localize(key, false) : Name(monster);
        for (int difficulty = 0; difficulty < 3; difficulty++)
        {
            var (_, high, note) = Levels(monster, difficulty);
            var suffix = Difficulties[difficulty].Suffix;
            if (superunique != null)
            {
                if (superunique.S("TC" + suffix) is { Length: > 0 } tc)
                    yield return new(id, name, difficulty, "superunique", high + 3, tc, calc.Upgrade(tc, high + 3), note + " + 3");
                continue;
            }
            foreach (var (column, kind, bonus) in Kinds)
                if (monster.S(column + suffix) is { Length: > 0 } tc)
                {
                    int level = high + bonus;
                    // Quest and boss drops keep the TC they name; ordinary kills move up their TC group with the monster level.
                    var used = kind == "quest" || Flag(monster, "boss") ? tc : calc.Upgrade(tc, level);
                    yield return new(id, name, difficulty, kind, level, tc, used, note + (bonus > 0 ? $" + {bonus}" : ""));
                }
        }
    }

    /// <summary>Every enabled monster and superunique's drop sources.</summary>
    public IEnumerable<DropSource> AllSources(DropCalculator calc)
    {
        var monsters = data.Rows("monstats", false);
        foreach (var monster in monsters)
            if (monster.S("Id").Length > 0 && monster.S("enabled", "1") != "0")
                foreach (var source in Sources(calc, monster)) yield return source;
        foreach (var superunique in data.Rows("superuniques", false))
            if (data.Find("monstats", "Id", superunique.S("Class"), false) is { } monster)
                foreach (var source in Sources(calc, monster, superunique)) yield return source;
    }
}

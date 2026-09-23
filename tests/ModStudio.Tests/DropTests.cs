using System.Text.Json.Nodes;
using ModStudio.Core;
using static ModStudio.Core.Storage;

/// <summary>Treasure class expansion, quality odds, TC upgrades, and the Drop and Monster previews over a hand-checked fixture.</summary>
internal static class DropTests
{
    public static void Run(string root, Action<bool, string> check, Action<Action, string> throws)
    {
        var project = new ModProject(Path.Combine(root, "drop-previews"), "test", "Test"); Directory.CreateDirectory(project.Root);
        JsonObject Row(string id, params string[] values) { var fields = new JsonObject(); for (int i = 0; i < values.Length; i += 2) fields[values[i]] = values[i + 1]; return new() { ["sourceId"] = id, ["fields"] = fields }; }
        void Write(string relative, JsonNode value) { var file = Path.Combine(project.Root, relative); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, value.ToJsonString(Pretty)); }
        void Table(string name, params JsonObject[] rows) => Write("source/tables/" + name + ".json", new JsonObject { ["schema"] = new JsonObject { ["name"] = name }, ["records"] = new JsonArray(rows.Cast<JsonNode?>().ToArray()) });
        Write("source/strings/test.json", new JsonObject
        {
            ["schema"] = new JsonObject { ["category"] = "test" },
            ["records"] = new JsonArray(new[] { ("Zombie", "Zombie"), ("Boss", "Big Boss"), ("area1", "Area One"), ("area2", "Area Two"), ("cap", "Cap"), ("Biggin's Bonnet", "Biggin's Bonnet") }
                .Select(x => (JsonNode)new JsonObject { ["Key"] = x.Item1, ["translations"] = new JsonObject { ["enUS"] = x.Item2 } }).ToArray())
        });
        Table("itemtypes", Row("armo", "Code", "armo", "TreasureClass", "1"), Row("helm", "Code", "helm", "Equiv1", "armo"), Row("weap", "Code", "weap", "TreasureClass", "1"),
            Row("axe", "Code", "axe", "Equiv1", "weap"), Row("ring", "Code", "ring"));
        Table("armor", Row("cap", "code", "cap", "namestr", "cap", "type", "helm", "level", "1", "rarity", "1", "spawnable", "1", "normcode", "cap"));
        Table("weapons", Row("hax", "code", "hax", "type", "axe", "level", "3", "rarity", "3", "spawnable", "1", "normcode", "hax"),
            Row("axe", "code", "axe", "type", "axe", "level", "2", "rarity", "1", "spawnable", "1", "normcode", "axe"),
            Row("big", "code", "big", "type", "axe", "level", "9", "rarity", "1", "spawnable", "1", "normcode", "big"));
        Table("misc", Row("rin", "code", "rin", "type", "ring", "level", "1"), Row("gld", "code", "gld", "type", "gold", "level", "1"));
        // The base game's expansion rows for ordinary items.
        Table("itemratio", Row("r", "Version", "1", "Uber", "0", "Class Specific", "0", "Unique", "400", "UniqueDivisor", "1", "UniqueMin", "6400", "Rare", "100", "RareDivisor", "2", "RareMin", "3200",
            "Set", "160", "SetDivisor", "2", "SetMin", "5600", "Magic", "34", "MagicDivisor", "3", "MagicMin", "192"));
        Table("uniqueitems", Row("biggin", "index", "Biggin's Bonnet", "code", "cap", "lvl", "3", "rarity", "1"), Row("tarn", "index", "Tarnhelm", "code", "cap", "lvl", "20", "rarity", "3"),
            Row("forced", "index", "Forced One", "code", "hax", "lvl", "1", "rarity", "1"), Row("off", "index", "Retired", "code", "cap", "lvl", "1", "rarity", "5", "disabled", "1"));
        Table("setitems", Row("sander", "index", "Sander's Paragon", "item", "cap", "lvl", "1", "rarity", "1"));
        Table("treasureclassex",
            Row("simple", "Treasure Class", "Simple", "Picks", "1", "NoDrop", "1", "Item1", "hax", "Prob1", "1"),
            Row("chain", "Treasure Class", "Chain", "Picks", "2", "Unique", "900", "Item1", "Simple", "Prob1", "1", "Item2", "weap3", "Prob2", "1", "Item3", "Forced One", "Prob3", "2"),
            Row("negative", "Treasure Class", "Negative", "Picks", "-3", "Item1", "cap", "Prob1", "2", "Item2", "rin", "Prob2", "5"),
            Row("ga", "Treasure Class", "G A", "group", "7", "level", "5", "Picks", "1", "Item1", "cap", "Prob1", "1"),
            Row("gb", "Treasure Class", "G B", "group", "7", "level", "10", "Picks", "1", "Item1", "cap", "Prob1", "1"),
            Row("gc", "Treasure Class", "G C", "group", "7", "level", "20", "Picks", "1", "Item1", "cap", "Prob1", "1"),
            Row("broken", "Treasure Class", "Broken", "Picks", "1", "Item1", "nope", "Prob1", "1"),
            Row("loopa", "Treasure Class", "Loop A", "Picks", "1", "Item1", "Loop B", "Prob1", "1"),
            Row("loopb", "Treasure Class", "Loop B", "Picks", "1", "Item1", "Loop A", "Prob1", "1"),
            Row("gold", "Treasure Class", "Gold", "Picks", "1", "Item1", "\"gld,mul=2048\"", "Prob1", "1"));
        Table("monlvl", Row("1", "Level", "1", "L-HP", "7", "L-AC", "6", "L-TH", "8", "L-DM", "2", "L-XP", "30"),
            Row("12", "Level", "12", "L-HP(N)", "200"), Row("15", "Level", "15", "L-HP(N)", "300"), Row("20", "Level", "20"), Row("25", "Level", "25"), Row("90", "Level", "90"));
        Table("monstats",
            Row("zom", "Id", "zom", "NameStr", "Zombie", "Level", "1", "Level(N)", "36", "Level(H)", "67", "minHP", "100", "maxHP", "200", "MinHP(N)", "100", "MaxHP(N)", "100",
                "AC", "100", "Exp", "100", "A1MinD", "100", "A1MaxD", "200", "A1TH", "100", "ResFi(H)", "100", "MinGrp", "2", "MaxGrp", "4",
                "TreasureClass", "G A", "TreasureClassChamp", "G A", "TreasureClass(N)", "G A", "TreasureClassChamp(N)", "G A", "TreasureClass(H)", "G A"),
            Row("boss", "Id", "boss", "NameStr", "Boss", "boss", "1", "Level", "10", "Level(N)", "50", "Level(H)", "90", "minion1", "zom", "PartyMin", "2", "PartyMax", "3", "TreasureClassQuest(H)", "Negative"));
        Table("levels", Row("a1", "Name", "Act 1 - One", "LevelName", "area1", "MonLvlEx", "2", "MonLvlEx(N)", "12", "MonLvlEx(H)", "20", "mon1", "zom", "nmon1", "zom"),
            Row("a2", "Name", "Act 1 - Two", "LevelName", "area2", "MonLvlEx(N)", "15", "MonLvlEx(H)", "25", "nmon1", "zom"));

        var issues = new List<string>();
        var data = new PreviewTables().Open(project, "standard", "enUS", issues, default);
        var calc = new DropCalculator(data, default);
        double Count(Dictionary<DropLeaf, double> leaves, string code) => leaves.Where(l => l.Key.Code == code && l.Key.Forced.Length == 0).Sum(l => l.Value);
        bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;

        check(DropCalculator.AdjustedNoDrop(100, 50, 2) == 40 && DropCalculator.AdjustedNoDrop(100, 50, 1) == 100 && DropCalculator.AdjustedNoDrop(0, 50, 8) == 0,
            "NoDrop shrinks as the chance of nothing is raised to the counted players");
        check(new DropSettings(8, 1).NoDropPlayers == 4 && new DropSettings(3, 3).NoDropPlayers == 3 && new DropSettings(2, 1).NoDropPlayers == 1,
            "Party members nearby count fully and other players half, rounded down");
        var simple = calc.Expand("Simple", new());
        check(Near(Count(simple, "hax"), 0.5), "One pick against equal NoDrop drops the item half the time");
        var chain = calc.Expand("Chain", new(ItemLevel: 10));
        check(Near(Count(chain, "hax"), 0.25 + 0.375) && Near(Count(chain, "axe"), 0.125), $"Picks multiply into sub-TCs and automatic TCs weigh items by rarity: hax {Count(chain, "hax")}, axe {Count(chain, "axe")}");
        check(chain.Keys.All(l => l.Factors.Unique == 900), "Quality factors carry down to the leaves of sub-TCs");
        check(chain.Any(l => l.Key.Forced == "Forced One" && Near(l.Value, 1)), "A unique named directly in a TC is dropped as that unique");
        check(calc.Automatic("weap3")!.Select(m => m.Code).Order().SequenceEqual(["axe", "hax"]) && calc.Automatic("weap9")!.Single().Code == "big" && calc.Automatic("helm3") == null,
            "Automatic TCs hold spawnable items of the type and its children in a three-level band");
        var negative = calc.Expand("Negative", new(8, 8));
        check(Near(Count(negative, "cap"), 2) && Near(Count(negative, "rin"), 1), "Negative picks drop entries Prob times in order until the picks run out, ignoring players");
        check(calc.Upgrade("G A", 12) == "G B" && calc.Upgrade("G A", 3) == "G A" && calc.Upgrade("G B", 99) == "G C" && calc.Upgrade("Simple", 99) == "Simple",
            "Monsters above a TC's level move to the highest TC of its group they reach");
        calc.Expand("Broken", new()); calc.Expand("Loop A", new());
        check(calc.Issues.Any(i => i.Contains("entry: nope")) && calc.Issues.Any(i => i.Contains("reaches itself")), "Missing entries and TC loops are reported: " + string.Join(" | ", calc.Issues));
        var gold = calc.Expand("Gold", new());
        check(gold.Keys.Single() is { Code: "gld", Modifier: "mul=2048" }, "Quoted entries with modifiers are read as the game reads them");

        // Cap (qlvl 1) at item level 10: unique (400 − 9) × 128, set (160 − 9 ÷ 2) × 128.
        var quality = calc.Quality("cap", default, 10, 0);
        double unique = 128.0 / 50048, set = (1 - unique) * 128.0 / 19968;
        check(Near(quality.Unique, unique) && Near(quality.Set, set), $"Quality odds follow itemratio: unique {quality.Unique}, set {quality.Set}");
        check(Near(calc.Quality("cap", default, 10, 250).Unique, 128.0 / 22243), "Magic find raises unique odds with diminishing returns");
        check(Near(calc.Quality("cap", new(983, 0, 0, 0), 10, 0).Unique, 128.0 / (50048 - 50048 * 983 / 1024)), "A TC's quality factor lowers the chance by factor ÷ 1024");
        check(!calc.Quality("rin", default, 10, 0).Applies && !calc.Quality("gld", default, 10, 0).Applies && calc.Quality("hax", default, 10, 0).Applies,
            "Only equipment and jewelry with uniques or sets roll qualities");
        var choices = calc.Choices("uniqueitems", "cap", 25);
        check(calc.Choices("uniqueitems", "cap", 10).Single().Row.S("index") == "Biggin's Bonnet" && Near(choices.Single(c => c.Row.S("index") == "Tarnhelm").Share, 0.75) && choices.Count == 2,
            "A unique roll picks among enabled uniques the item level reaches, by rarity");

        var drops = new DropPreviewResolver();
        JsonObject Record(string table, string id) => (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(project.Root, $"source/tables/{table}.json")))!["records"]!.AsArray().First(r => r.S("sourceId") == id)!.DeepClone();
        string[] Section(PreviewSection[] sections, string title) => sections.Single(s => s.Title.StartsWith(title, StringComparison.Ordinal)).Lines;
        var tc = drops.Resolve(project, "treasureclassex", Record("treasureclassex", "chain"), "standard", "enUS", default, new(ItemLevel: 10));
        check(tc.ItemLevel == 10 && tc.Lines.Any(l => l.StartsWith("Picks 2: 2 rolls among the entries, never nothing")), "Drop preview explains the picks: " + string.Join(" | ", tc.Lines));
        check(Section(tc.Sections, "Entries").Contains("weap3 · prob 1 · 25.0% per pick · automatic TC of 2 items") && Section(tc.Sections, "Entries").Any(l => l.StartsWith("Forced One") && l.EndsWith("unique item")),
            "Drop preview says what each entry is and its share: " + string.Join(" | ", Section(tc.Sections, "Entries")));
        check(tc.Drops[0].Item == "Forced One (unique hax)" && tc.Drops[0].PerKill == "×1" && tc.Drops.Any(d => d.Code == "hax" && d.PerKill == "1 in 2" && d.Unique.StartsWith("1 in ")),
            "Drop table lists per-kill odds with quality odds: " + string.Join(" | ", tc.Drops.Select(d => d.Item + " " + d.PerKill)));
        check(Section(tc.Sections, "Uniques and sets").Any(l => l.Contains("Forced One")), "Named uniques are listed with their odds");
        var filtered = drops.Resolve(project, "treasureclassex", Record("treasureclassex", "chain"), "standard", "enUS", default, new(ItemLevel: 10, Filter: "forced"));
        check(filtered.Drops.Single().Item == "Forced One (unique hax)" && Section(filtered.Sections, "Uniques and sets").Length == 1, "The filter narrows the drop table and the named items");
        var auto = drops.Resolve(project, "treasureclassex", Record("treasureclassex", "ga"), "standard", "enUS", default);
        check(auto.ItemLevel == 5 && Section(auto.Sections, "Used by").Any(l => l.StartsWith("Zombie · Normal regular")), "A TC's own level is its default item level, and the monsters naming it are listed");
        var source = drops.Resolve(project, "uniqueitems", Record("uniqueitems", "biggin"), "standard", "enUS", default);
        var from = Section(source.Sections, "Drops from");
        check(from.Any(l => l.Contains("Hell quest · Big Boss · level 90 · Negative")) && from.Any(l => l.Contains("Normal champion · Zombie · level 3 · G A")) && !from.Any(l => l.Contains("Normal regular")),
            "Reverse lookup lists the monsters that drop an item, skipping kills below its item level: " + string.Join(" | ", from));
        check(Section(source.Sections, "Listed in").Contains("Negative"), "Reverse lookup lists the TCs that name the item's base");
        IEnumerable<CellLink> Links(PreviewSection section) => section.Text.SelectMany(t => t.Links).SelectMany(l => l.Targets);
        var entryLinks = Links(tc.Sections.Single(s => s.Title == "Entries")).ToArray();
        check(entryLinks.Any(c => c is { Table: "treasureclassex", SourceId: "chain", Column: "Item1" }) && entryLinks.Any(c => c is { Table: "treasureclassex", SourceId: "chain", Column: "Prob1" })
            && entryLinks.Any(c => c is { Table: "uniqueitems", Column: "index" }) && tc.Drops.Single(d => d.Code == "hax").ItemCell is { Table: "weapons", SourceId: "hax", Column: "code" },
            "Drop entries link their ItemN/ProbN cells and the rows they name: " + string.Join(", ", entryLinks.Select(c => $"{c.Table}/{c.SourceId}/{c.Column}")));
        check(Links(source.Sections.Single(s => s.Title == "Drops from")).Any(c => c is { Table: "monstats", Column: "TreasureClassChamp" }),
            "Reverse drop lookups link the monster's treasure class cell");
        check(drops.Resolve(project, "uniqueitems", Record("uniqueitems", "off"), "standard", "enUS", default).Issues.Any(i => i.Contains("disabled")), "Disabled uniques are reported");

        var monsters = new MonsterPreviewResolver();
        var zombie = monsters.Resolve(project, "monstats", Record("monstats", "zom"), "standard", "enUS", default);
        var normal = zombie.Levels[0];
        check(zombie.Name == "Zombie" && normal.Level == "1" && normal.Life == "7–14" && normal.Defense == "6" && normal.AttackRating == "8" && normal.Damage == "A1 2–4" && normal.Experience == "30",
            $"Monster stats are monstats percentages of monlvl: {normal.Life} / {normal.Defense} / {normal.AttackRating} / {normal.Damage} / {normal.Experience}");
        var nightmare = zombie.Levels.Where(l => l.Difficulty == "Nightmare").ToArray();
        check(nightmare.Select(l => l.Level).SequenceEqual(["12", "15"]) && nightmare[0].Life == "200" && nightmare[1].Life == "300" && zombie.Lines.Any(l => l.StartsWith("Nightmare: levels 12–15")),
            "Nightmare and Hell ordinary monsters take the levels of their areas, lowest and highest");
        check(zombie.Lines.Contains("Hell: immune to fire"), "Resistances of 100 or more are named as immunities");
        check(Section(zombie.Sections, "Spawns").Contains("Normal: Area One (2)") && Section(zombie.Sections, "Spawns").Contains("Nightmare: Area One (12), Area Two (15)")
            && Section(zombie.Sections, "Spawns").Any(l => l.StartsWith("Minion of Big Boss")), "The monster card lists its spawn areas and who brings it along");
        var zombieDrops = Section(zombie.Sections, "Drops");
        check(zombieDrops.Contains("Hell regular · level 25 · G C (from G A)") && zombieDrops.Contains("Nightmare champion · level 17 · G B (from G A)"),
            "Monster drops upgrade their TC to the monster level: " + string.Join(" | ", zombieDrops));
        var boss = monsters.Resolve(project, "monstats", Record("monstats", "boss"), "standard", "enUS", default);
        check(boss.Levels.Where(l => l.Difficulty == "Hell").Single().Level == "90" && Section(boss.Sections, "Group").Any(l => l.Contains("minion1: Zombie (zom) × 2–3")),
            "Bosses keep their own level and list their minions");

        // Preview links: marks round-trip to plain text plus runs, and each part of the monster card points at its cells.
        var marked = "a " + PreviewText.Mark("b", [new CellLink("t", "row-1", "c"), new CellLink("u", "row-2", "d")]) + " " + PreviewText.Mark("e", [null]);
        var parsed = PreviewText.Parse(marked);
        check(PreviewText.Plain(marked) == "a b e" && parsed.Text == "a b e" && parsed.Links.Single() is { Start: 2, Length: 1, Targets.Length: 2 } && parsed.Links[0].Targets[1] == new CellLink("u", "row-2", "d"),
            "Preview marks parse to plain text and the linked run's cells");
        check(PreviewText.Mark(marked, [new CellLink("t", "row-1", "c")]) == marked, "Marking text that already holds links leaves it unchanged");
        var normalSources = zombie.Levels[0].Sources!;
        check(normalSources["Life"].Select(c => (c.Table, c.Column)).SequenceEqual([("monstats", "minHP"), ("monstats", "maxHP"), ("monlvl", "L-HP")]) && normalSources["Life"][0].SourceId == "zom",
            "Monster life links its monstats columns and the monlvl multiplier: " + string.Join(", ", normalSources["Life"].Select(c => c.ToString())));
        check(nightmare[0].Sources!["Lvl"].Single() is { Table: "levels", Column: "MonLvlEx(N)" } && zombie.Text.Any(t => t.Links.Any(l => l.Targets.Any(c => c.Table == "levels"))),
            "A monster's area levels link the levels.txt cell they come from");
        check(!zombie.Lines.Concat(zombie.Sections.SelectMany(s => s.Lines)).Any(l => l.Any(c => c is >= '' and <= '')), "Plain preview lines carry no link marks");
        check(Section(boss.Sections, "Group").Any(l => l.Contains("minion1: Zombie (zom)")) && boss.Sections.Single(s => s.Title == "Group").Text.Any(t => t.Links.Any(l => l.Targets.Any(c => c is { SourceId: "zom", Column: "Id" }))),
            "A boss's minion links to the minion's monstats row");
        throws(() => monsters.Resolve(project, "skills", Record("monstats", "zom"), "standard", "enUS", default), "Monster preview refuses other tables");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        throws(() => drops.Resolve(project, "treasureclassex", Record("treasureclassex", "chain"), "standard", "enUS", cancel.Token), "Canceled drop resolution stops before producing results");
    }
}

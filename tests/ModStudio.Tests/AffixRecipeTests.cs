using System.Text.Json.Nodes;
using ModStudio.Core;
using static ModStudio.Core.Storage;

/// <summary>Affix pools and affix levels, cube recipes and their shadowing, and runewords, over a hand-checked fixture.</summary>
internal static class AffixRecipeTests
{
    public static void Run(string root, Action<bool, string> check, Action<Action, string> throws)
    {
        var project = new ModProject(Path.Combine(root, "affix-recipes"), "test", "Test"); Directory.CreateDirectory(project.Root);
        JsonObject Row(string id, params string[] values) { var fields = new JsonObject(); for (int i = 0; i < values.Length; i += 2) fields[values[i]] = values[i + 1]; return new() { ["sourceId"] = id, ["fields"] = fields }; }
        void Write(string relative, JsonNode value) { var file = Path.Combine(project.Root, relative); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, value.ToJsonString(Pretty)); }
        void Table(string name, params JsonObject[] rows) => Write("source/tables/" + name + ".json", new JsonObject { ["schema"] = new JsonObject { ["name"] = name }, ["records"] = new JsonArray(rows.Cast<JsonNode?>().ToArray()) });
        Write("source/strings/test.json", new JsonObject
        {
            ["schema"] = new JsonObject { ["category"] = "test" },
            ["records"] = new JsonArray(new[] { ("Sturdy", "Sturdy"), ("Strong", "Strong"), ("Glowing", "Glowing"), ("of Life", "of Life"), ("ModStr2v", "Enhanced Defense"),
                ("ModStr1u", "%+d to Life"), ("ModStr1a", "%+d to Strength"), ("increaseswithplaylevelX", "(Based on Character Level)"), ("Runeword1", "Steel"), ("Runeword2", "Copy"),
                ("Runeword3", "Big"), ("cap", "Cap"), ("ssd", "Short Sword"), ("crs", "Crystal Sword"), ("r01", "El Rune"), ("r03", "Tir Rune") }
                .Select(x => (JsonNode)new JsonObject { ["Key"] = x.Item1, ["translations"] = new JsonObject { ["enUS"] = x.Item2 } }).ToArray())
        });
        Table("itemtypes", Row("armo", "Code", "armo", "ItemType", "Any Armor"), Row("helm", "Code", "helm", "ItemType", "Helm", "Equiv1", "armo"),
            Row("circ", "Code", "circ", "ItemType", "Circlet", "Equiv1", "helm"), Row("glov", "Code", "glov", "ItemType", "Gloves", "Equiv1", "armo"),
            Row("weap", "Code", "weap", "ItemType", "Weapon"), Row("abow", "Code", "abow", "ItemType", "Amazon Bow", "Equiv1", "weap", "Class", "ama"),
            Row("swor", "Code", "swor", "ItemType", "Sword", "Equiv1", "weap", "MaxSockets1", "3", "MaxSocketsLevelThreshold1", "25", "MaxSockets2", "4", "MaxSocketsLevelThreshold2", "40", "MaxSockets3", "6"),
            Row("axe", "Code", "axe", "ItemType", "Axe", "Equiv1", "weap"), Row("rune", "Code", "rune", "ItemType", "Rune"));
        Table("armor", Row("cap", "code", "cap", "namestr", "cap", "type", "helm", "level", "1"), Row("ci0", "code", "ci0", "type", "circ", "level", "24", "magic lvl", "3"),
            Row("glv", "code", "glv", "type", "glov", "level", "3"));
        Table("weapons", Row("ssd", "code", "ssd", "namestr", "ssd", "type", "swor", "level", "1", "gemsockets", "2"),
            Row("crs", "code", "crs", "namestr", "crs", "type", "swor", "level", "26", "gemsockets", "6"),
            Row("amb", "code", "amb", "type", "abow", "level", "20"), Row("axe", "code", "axe", "type", "axe", "level", "1"), Row("2ax", "code", "2ax", "type", "axe", "level", "5"));
        Table("misc", Row("r01", "code", "r01", "namestr", "r01", "type", "rune", "levelreq", "11"), Row("r03", "code", "r03", "namestr", "r03", "type", "rune", "levelreq", "13"));
        Table("itemstatcost", Row("strength", "Stat", "strength"),
            Row("armorpct", "Stat", "item_armor_percent", "descfunc", "4", "descval", "2", "descstrpos", "ModStr2v", "descstrneg", "ModStr2v"),
            Row("maxhp", "Stat", "maxhp", "descfunc", "19", "descstrpos", "ModStr1u", "descstrneg", "ModStr1u"),
            Row("strlvl", "Stat", "item_strength_perlevel", "op", "2", "op param", "3", "op base", "level", "descfunc", "19", "descstrpos", "ModStr1a", "descstrneg", "ModStr1a", "descstr2", "increaseswithplaylevelX"));
        Table("properties", Row("ac", "code", "ac%", "func1", "2", "stat1", "item_armor_percent"), Row("hp", "code", "hp", "func1", "1", "stat1", "maxhp"),
            Row("strlvl", "code", "str/lvl", "func1", "17", "stat1", "item_strength_perlevel"), Row("aura", "code", "aura", "func1", "12", "stat1", "item_aura", "*Tooltip", "Level # [Skill] Aura When Equipped"));
        Table("propertygroups", Row("g", "code", "Fortune", "PickMode", "2", "Prop1", "hp", "ModMin1", "5", "ModMax1", "10", "Chance1", "3", "Prop2", "ac%", "ModMin2", "20", "ModMax2", "20", "Chance2", "1"));
        Table("magicprefix",
            Row("p0", "Name", "Sturdy", "spawnable", "1", "rare", "1", "level", "1", "frequency", "3", "group", "101", "mod1code", "ac%", "mod1min", "10", "mod1max", "20", "itype1", "armo"),
            Row("p1", "Name", "Strong", "spawnable", "1", "rare", "0", "level", "10", "frequency", "2", "group", "101", "mod1code", "ac%", "mod1min", "21", "mod1max", "30", "itype1", "armo", "etype1", "glov"),
            Row("p2", "Name", "Glowing", "spawnable", "1", "rare", "1", "level", "1", "maxlevel", "5", "frequency", "1", "mod1code", "hp", "mod1min", "1", "mod1max", "1", "itype1", "armo"),
            Row("p3", "Name", "Hidden", "spawnable", "1", "level", "1", "frequency", "0", "mod1code", "hp", "mod1min", "1", "mod1max", "1", "itype1", "armo"),
            Row("p4", "Name", "Hunter's", "spawnable", "1", "level", "1", "frequency", "1", "classspecific", "ama", "mod1code", "hp", "mod1min", "2", "mod1max", "2", "itype1", "weap"));
        Table("magicsuffix", Row("s0", "Name", "of Life", "spawnable", "1", "rare", "1", "level", "5", "frequency", "1", "mod1code", "hp", "mod1min", "3", "mod1max", "5", "itype1", "armo"));
        Table("runes",
            Row("rw1", "Name", "Runeword1", "complete", "1", "itype1", "swor", "Rune1", "r03", "Rune2", "r01", "T1Code1", "str/lvl", "T1Param1", "8", "T1Code2", "hp", "T1Min2", "10", "T1Max2", "20",
                "T1Code3", "aura", "T1Param3", "Might", "T1Min3", "3", "T1Max3", "3", "T1Code4", "Fortune"),
            Row("rw2", "Name", "Runeword2", "complete", "1", "itype1", "weap", "Rune1", "r03", "Rune2", "r01"),
            Row("rw3", "Name", "Runeword3", "complete", "1", "itype1", "swor", "Rune1", "r01", "Rune2", "r01", "Rune3", "r01", "Rune4", "r01", "Rune5", "r01"),
            Row("rw4", "Name", "Runeword4", "complete", "0"));
        Table("cubemain",
            Row("c0", "description", "Any helm", "enabled", "1", "numinputs", "2", "input 1", "helm", "input 2", "r01", "output", "useitem,rep"),
            Row("c1", "description", "Magic cap", "enabled", "1", "numinputs", "2", "input 1", "\"cap,mag\"", "input 2", "r01", "output", "\"cap,uni\""),
            Row("c2", "description", "Wrong count", "enabled", "1", "numinputs", "3", "input 1", "\"r01,qty=2\"", "output", "r03"),
            Row("c3", "description", "Crafted sword", "enabled", "1", "min diff", "2", "op", "18", "param", "1", "value", "5", "numinputs", "1", "input 1", "\"ssd,mag\"",
                "output", "\"usetype,crf,pre=0\"", "plvl", "10", "ilvl", "50", "mod 1", "hp", "mod 1 min", "5", "mod 1 max", "10", "mod 1 chance", "50"),
            Row("c4", "description", "Nonsense", "enabled", "0", "numinputs", "1", "input 1", "nothing", "output", "Cow Portal"),
            Row("c5", "description", "Axe upgrade", "enabled", "1", "numinputs", "1", "input 1", "axe", "output", "2ax"),
            Row("c6", "description", "Double axe", "enabled", "1", "numinputs", "1", "input 1", "2ax", "output", "axe"));

        check(AffixPreviewResolver.AffixLevel(85, 58, 0) == 71 && AffixPreviewResolver.AffixLevel(30, 58, 0) == 29 && AffixPreviewResolver.AffixLevel(20, 24, 3) == 27
            && AffixPreviewResolver.AffixLevel(99, 1, 0) == 99 && AffixPreviewResolver.AffixLevel(40, 10, 0) == 35,
            "Affix level follows item level, qlvl and magic lvl the way the game computes it");
        var affixes = new AffixPreviewResolver();
        JsonObject Record(string table, string id) => (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(project.Root, $"source/tables/{table}.json")))!["records"]!.AsArray().First(r => r.S("sourceId") == id)!.DeepClone();
        AffixPreviewResult Pool(string table, string id, int level, bool rare = false, string filter = "") => affixes.Resolve(project, table, Record(table, id), "standard", "enUS", default, new(level, rare, filter));
        string[] Section(PreviewSection[] sections, string title) => sections.Single(s => s.Title.StartsWith(title, StringComparison.Ordinal)).Lines;

        var low = Pool("armor", "cap", 3);
        check(low.Prefixes.Select(p => p.Name).SequenceEqual(["Sturdy", "Glowing"]) && low.Prefixes[0].Chance == "75.0%" && low.Prefixes[1].Chance == "25.0%" && low.Suffixes.Length == 0,
            "A base item's pool holds the affixes its type and affix level allow, weighted by frequency: " + string.Join(", ", low.Prefixes.Select(p => p.Name + " " + p.Chance)));
        check(low.Prefixes[0].Effect == "Enhanced Defense +10–20%" && low.Prefixes[0].Levels == "1+" && low.Prefixes[1].Levels == "1–5", "Pool lines show each affix's effect and level range: " + low.Prefixes[0].Effect);
        check(Section(low.Sections, "Locked").Any(l => l.StartsWith("Prefixes: 1 more need a higher affix level; next at 10: Strong")) && Section(low.Sections, "Locked").Any(l => l.StartsWith("Suffixes: 1 more")),
            "The pool says which affixes unlock next");
        check(low.Prefixes[0].Sources!["Effect"].Select(c => c.Column).SequenceEqual(["mod1code", "mod1min", "mod1max"]) && low.Prefixes[0].Sources["Chance"].Single() is { Table: "magicprefix", Column: "frequency" }
            && low.Text.Any(t => t.Links.Any(l => l.Targets.Any(c => c is { Table: "armor", Column: "level" }))),
            "Affix pool rows link their affix cells and the item's qlvl links its level cell");
        check(Pool("armor", "cap", 12).Prefixes.Select(p => p.Name).SequenceEqual(["Sturdy", "Strong"]), "Affixes past their maxlevel drop out of the pool");
        check(Pool("armor", "glv", 20).Prefixes.Select(p => p.Name).SequenceEqual(["Sturdy"]), "Excluded types (etype) keep an affix off an item");
        check(Pool("armor", "cap", 12, rare: true).Prefixes.Select(p => p.Name).SequenceEqual(["Sturdy"]), "Rare items only take affixes marked rare");
        check(Pool("weapons", "amb", 20).Prefixes.Single().Name == "Hunter's" && Pool("weapons", "ssd", 20).Prefixes.Length == 0, "Class-specific affixes only roll on that class's items");
        check(Pool("armor", "ci0", 20).Lines.Any(l => l.Contains("affix level 27")), "Items with a magic lvl add it to the item level");
        check(Pool("armor", "cap", 12, filter: "strong").Prefixes.Single().Name == "Strong", "The filter narrows the pool");
        var sturdy = affixes.Resolve(project, "magicprefix", Record("magicprefix", "p0"), "standard", "enUS", default);
        check(sturdy.Lines[0].StartsWith("Prefix #0") && Section(sturdy.Sections, "Fits").Any(l => l.Contains("Cap (cap) · qlvl 1 · item level 1+")) && Section(sturdy.Sections, "Fits").Contains("3 base items"),
            "An affix card lists the base items it fits and the item level each needs: " + string.Join(" | ", Section(sturdy.Sections, "Fits")));
        check(Section(sturdy.Sections, "Group 101").Single().Contains("Strong (10+)"), "An affix card names the affixes sharing its group");
        var strong = affixes.Resolve(project, "magicprefix", Record("magicprefix", "p1"), "standard", "enUS", default);
        check(Section(strong.Sections, "Fits").Any(l => l.Contains("Cap (cap) · qlvl 1 · item level 10+")) && !Section(strong.Sections, "Fits").Any(l => l.Contains("glv")), "An affix card honours level and excluded types");
        check(affixes.Resolve(project, "magicprefix", Record("magicprefix", "p3"), "standard", "enUS", default).Issues.Any(i => i.Contains("frequency is 0")), "Affixes that never roll are reported");

        var recipes = new RecipePreviewResolver();
        RecipePreviewResult Recipe(string table, string id) => recipes.Resolve(project, table, Record(table, id), "standard", "enUS", default);
        var shadowed = Recipe("cubemain", "c1");
        check(shadowed.Issues.Any(i => i.Contains("earlier recipe") && i.Contains("row 0: Any helm")) && Section(shadowed.Sections, "Inputs")[0] == "1 × Cap (cap) · magic",
            "A recipe an earlier, looser recipe accepts first is reported: " + string.Join(" | ", shadowed.Issues));
        check(Section(Recipe("cubemain", "c0").Sections, "Takes items first from").Any(l => l.Contains("Magic cap")), "A recipe lists the later recipes it takes items from");
        check(!Recipe("cubemain", "c6").Issues.Any(i => i.Contains("earlier recipe")), "An item code in a recipe only covers that item even when an item type shares the code");
        var count = Recipe("cubemain", "c2");
        check(count.Issues.Any(i => i.Contains("numinputs is 3 but the inputs add up to 2")) && Section(count.Sections, "Inputs")[0] == "2 × El Rune (r01)", "Input counts are checked against numinputs");
        var crafted = Recipe("cubemain", "c3");
        check(crafted.Lines.Any(l => l.StartsWith("Enabled · all versions · Hell only")) && crafted.Lines.Any(l => l.StartsWith("Only when (op 18)") && l.Contains("param 1 = item_armor_percent") && l.Contains("value 5")),
            "Recipe conditions name the stat an op reads: " + string.Join(" | ", crafted.Lines));
        var output = Section(crafted.Sections, "Output");
        check(output[0] == "1 × a new item of input 1's type · crafted" && output.Any(l => l.Contains("prefix #0: Sturdy — Enhanced Defense +10–20%"))
            && output.Any(l => l.Contains("item level = 10% of the player's level + 50% of input 1's item level")) && output.Any(l => l.Contains("+5–10 to Life (50% chance)")),
            "Outputs read as the item, its forced affixes, level and properties: " + string.Join(" | ", output));
        var nonsense = Recipe("cubemain", "c4");
        check(nonsense.Issues.Any(i => i.Contains("Unresolved cube input: nothing")) && nonsense.Issues.Any(i => i.Contains("enabled is not 1")) && Section(nonsense.Sections, "Output")[0].Contains("Moo Moo Farm"),
            "Unknown inputs, disabled recipes and portal outputs are explained");

        var steel = Recipe("runes", "rw1");
        check(steel.Name == "Steel" && steel.Lines[0] == "Tir Rune + El Rune · 2 sockets · required level 13 from its runes", "A runeword card reads its runes, sockets and level: " + steel.Lines[0]);
        check(steel.Text[0].Links.Select(l => l.Targets.Single().Column).SequenceEqual(["Rune1", "Rune2", "levelreq"]) && steel.Text[0].Links[2].Targets[0].Table == "misc",
            "A runeword's runes link their Rune# cells and its level links the rune that sets it");
        var properties = Section(steel.Sections, "Properties");
        check(properties.Contains("+99 to Strength (Based on Character Level)  (at character level 99)") && properties.Contains("+10–20 to Life") && properties.Contains("Level 3 Might Aura When Equipped")
            && properties.Any(l => l.StartsWith("property group Fortune, rolled from: +5–10 to Life (75%) | Enhanced Defense +20% (25%)")),
            "Runeword properties read as tooltips, per-level stats at level 99, templates and property groups: " + string.Join(" | ", properties));
        check(Section(steel.Sections, "Bases").Any(l => l.Contains("Short Sword (ssd) · qlvl 1 · 2 sockets from item level 1")) && steel.Issues.Any(i => i.Contains("Copy uses the same runes")),
            "Runeword bases and duplicate rune orders are shown: " + string.Join(" | ", steel.Issues));
        var big = Recipe("runes", "rw3");
        check(Section(big.Sections, "Bases").Any(l => l.Contains("Crystal Sword (crs) · qlvl 26 · 5 sockets from item level 41")) && Section(big.Sections, "Bases").Any(l => l.StartsWith("Never enough sockets: Short Sword (ssd) (max 2)")),
            "Socket limits come from the base and its type's level bands: " + string.Join(" | ", Section(big.Sections, "Bases")));
        var placeholder = Recipe("runes", "rw4");
        check(placeholder.Issues.Length == 0 && placeholder.Lines.Single().StartsWith("Placeholder row"), "Placeholder runeword rows are not reported as broken");
        throws(() => recipes.Resolve(project, "skills", Record("runes", "rw1"), "standard", "enUS", default), "Recipe preview refuses other tables");
        throws(() => affixes.Resolve(project, "skills", Record("armor", "cap"), "standard", "enUS", default), "Affix preview refuses other tables");
    }
}

using System.Text.Json.Nodes;
using ModStudio.Core;
using static ModStudio.Core.Storage;

internal static class StatPreviewTests
{
    public static void Run(string root, Action<bool, string> check, Action<Action, string> throws)
    {
        var project = new ModProject(Path.Combine(root, "stat-previews"), "test", "Test"); Directory.CreateDirectory(project.Root);
        JsonObject Row(string id, params string[] values) { var fields = new JsonObject(); for (int i = 0; i < values.Length; i += 2) fields[values[i]] = values[i + 1]; return new() { ["sourceId"] = id, ["fields"] = fields }; }
        void Write(string relative, JsonNode value) { var file = Path.Combine(project.Root, relative); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, value.ToJsonString(Pretty)); }
        void Table(string name, params JsonObject[] rows) => Write("source/tables/" + name + ".json", new JsonObject { ["schema"] = new JsonObject { ["name"] = name }, ["records"] = new JsonArray(rows.Cast<JsonNode?>().ToArray()) });
        Write("source/strings/test.json", new JsonObject
        {
            ["schema"] = new JsonObject { ["category"] = "test" },
            ["records"] = new JsonArray(new[] {
                ("ModStr1a", "%+d to Strength"), ("ModStr1b", "%+d to Dexterity"), ("Moditem2allattrib", "%+d to all Attributes"), ("ModStr1i", "%+d Defense"),
                ("increaseswithplaylevelX", "(Based on Character Level)"), ("ModStr1j", "Fire Resist %+d%%"), ("ModStr1h", "to Attack Rating") }
                .Select(x => (JsonNode)new JsonObject { ["Key"] = x.Item1, ["translations"] = new JsonObject { ["enUS"] = x.Item2 } }).ToArray())
        });
        // Vanilla strength saves 8 bits with Save Add 32, so -32 to 223 fit on an item.
        Table("itemstatcost",
            Row("strength", "Stat", "strength", "Save Bits", "8", "Save Add", "32", "Send Bits", "11", "Saved", "1", "CSvBits", "10", "descpriority", "67",
                "descfunc", "19", "descstrpos", "ModStr1a", "descstrneg", "ModStr1a", "dgrp", "1", "dgrpfunc", "19", "dgrpstrpos", "Moditem2allattrib", "dgrpstrneg", "Moditem2allattrib"),
            Row("dexterity", "Stat", "dexterity", "Save Bits", "8", "Save Add", "32", "descfunc", "19", "descstrpos", "ModStr1b", "descstrneg", "ModStr1b", "dgrp", "1"),
            Row("armorclass", "Stat", "armorclass", "Save Bits", "11", "Save Add", "10"),
            Row("perlevel", "Stat", "item_armor_perlevel", "Signed", "1", "Save Bits", "6", "op", "4", "op param", "3", "op base", "level", "op stat1", "armorclass",
                "descfunc", "19", "descstrpos", "ModStr1i", "descstrneg", "ModStr1i", "descstr2", "increaseswithplaylevelX"),
            Row("fireresist", "Stat", "fireresist", "Save Bits", "9", "Save Add", "200", "descfunc", "19", "descstrpos", "ModStr1j", "descstrneg", "ModStr1j"),
            Row("tohit", "Stat", "tohit", "Save Bits", "10", "descfunc", "1", "descval", "1", "descstrpos", "ModStr1h", "descstrneg", "ModStr1h"));
        Table("properties",
            Row("str", "code", "str", "func1", "1", "stat1", "strength"),
            Row("aclvl", "code", "ac/lvl", "func1", "17", "stat1", "item_armor_perlevel"),
            Row("res", "code", "res-fire", "func1", "1", "stat1", "fireresist"),
            Row("att", "code", "att", "func1", "1", "stat1", "tohit"),
            Row("nothing", "code", "nothing"));
        Table("uniqueitems", Row("ring", "index", "Test Ring", "code", "rin", "prop1", "str", "min1", "5", "max1", "300", "prop2", "ac/lvl", "par2", "8", "prop3", "att", "min3", "10", "max3", "20"));
        Table("runes", Row("word", "Name", "Runeword1", "*Rune Name", "Test Word", "T1Code1", "str", "T1Min1", "10", "T1Max1", "10"));

        var resolver = new StatPreviewResolver();
        JsonObject Record(string table, string id) => (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(project.Root, $"source/tables/{table}.json")))!["records"]!.AsArray().First(r => r.S("sourceId") == id)!.DeepClone();
        StatPreviewResult Preview(string table, string id, StatPreviewOptions? options = null) => resolver.Resolve(project, table, Record(table, id), "standard", "enUS", default, options);
        string[] Section(StatPreviewResult result, string title) => result.Sections.Single(s => s.Title == title).Lines;

        var str = Preview("properties", "str");
        check(str.Name == "str" && str.Sample.Contains("Test Ring") && Section(str, "Functions")[0].StartsWith("func1 1 · ItemModsSetValueRegular") && Section(str, "Functions")[1].Contains("stat1 = strength"),
            "Property preview decodes each func# and the stat it writes: " + string.Join(" | ", Section(str, "Functions")));
        var strength = Section(str, "Stat strength");
        check(strength.Contains("  5 → +5 to Strength") && strength.Contains("  300 → +300 to Strength") && strength.Any(l => l.StartsWith("Group 1 (strength, dexterity)")) && strength.Contains("  300 → +300 to all Attributes"),
            "Property preview renders the stat's tooltip at the sample's ends and its group line: " + string.Join(" | ", strength));
        check(strength.Any(l => l.Contains("Saved on items as -32 to 223")), "Property preview says what range the stat can save");
        var used = Section(str, "Used by");
        check(used[0] == "2 uses: uniqueitems 1 · runes 1" && used.Contains("  uniqueitems · Test Ring · prop1 str 5–300") && used.Contains("  runes · Test Word · T1Code1 str 10"),
            "Property preview lists every authored use: " + string.Join(" | ", used));
        check(str.Issues.Any(i => i.Contains("Test Ring") && i.Contains("value 300 is outside the -32 to 223")) && !str.Issues.Any(i => i.Contains("Test Word")),
            "Values a stat cannot save are reported, and values it can are not: " + string.Join(" | ", str.Issues));

        var perLevel = Preview("itemstatcost", "perlevel");
        check(perLevel.Name == "item_armor_perlevel" && perLevel.Sample.Contains("parameter of Test Ring") && perLevel.Lines.Any(l => l == "Set by properties: ac/lvl"),
            "Stat preview names the properties that set it and samples a per-level parameter: " + perLevel.Sample);
        check(Section(perLevel, "Tooltip").Contains("  8 per level → +30 Defense (Based on Character Level)  (at character level 30)"),
            "Per-level stats show their tooltip at the character level: " + string.Join(" | ", Section(perLevel, "Tooltip")));
        var value = Section(perLevel, "Value");
        check(value[0].StartsWith("op 4 · By Level Source Operator → armorclass") && value[1].Contains("8 × character level ÷ 8 (op param 3) = 30 at level 30") && value[2].Contains("99: 99"),
            "Stat preview explains the op with per-level numbers: " + string.Join(" | ", value));
        check(Section(perLevel, "Storage")[0] == "Saved on items as 0 to 63 (Save Bits 6, Save Add 0)" && perLevel.Issues.Length == 0, "A per-level parameter within its bits is not an overflow");
        var atLevel = Preview("itemstatcost", "perlevel", new(CharacterLevel: 99));
        check(Section(atLevel, "Tooltip").Any(l => l.Contains("+99 Defense")), "The character level input moves per-level tooltips");

        var fire = Preview("itemstatcost", "fireresist");
        check(fire.Sample.Contains("10–20") && Section(fire, "Tooltip").Contains("  10 → Fire Resist +10%") && Section(fire, "Storage")[0].Contains("-200 to 311")
            && Section(fire, "Used by")[0].StartsWith("Nothing in"), "A stat nothing uses previews sample values and says it is unused");
        var negative = Preview("itemstatcost", "strength", new(-5, -5));
        check(Section(negative, "Tooltip").Contains("  -5 → -5 to Strength") && negative.Sample.Contains("set above"), "Sample values from the inputs replace authored ones");
        var descval = Preview("properties", "att");
        check(Section(descval, "Stat tohit").Contains("  10 → +10 to Attack Rating"), "descfunc 1 with descval 1 puts the signed value before the string");
        check(Preview("properties", "nothing").Issues.Any(i => i.Contains("No func#")), "A property without functions is reported");
        check(Preview("properties", "str", new(Max: 100)).Sample.StartsWith("min 100, max 100"), "A single sample input fills both ends");
        var header = resolver.Resolve(project, "properties", Row("blank", "code", ""), "standard", "enUS", default);
        check(header.Lines.Contains("Inactive/header row · no property code"), "Inactive property rows preview without problems");
        throws(() => resolver.Resolve(project, "skills", Record("properties", "str"), "standard", "enUS", default), "Stat preview refuses other tables");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        throws(() => resolver.Resolve(project, "properties", Record("properties", "str"), "standard", "enUS", cancel.Token), "Canceled stat resolution stops before producing results");
    }
}

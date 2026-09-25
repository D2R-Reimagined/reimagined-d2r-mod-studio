using System.Text.Json.Nodes;
using ModStudio.Core;
using static ModStudio.Core.Storage;

internal static class ItemPreviewTests
{
    public static void Run(string root, Action<bool, string> check, Action<Action, string> throws)
    {
        var project = new ModProject(Path.Combine(root, "item-previews"), "test", "Test"); Directory.CreateDirectory(project.Root);
        JsonObject Fields(params string[] values) { var obj = new JsonObject(); for (int i = 0; i < values.Length; i += 2) obj[values[i]] = values[i + 1]; return obj; }
        JsonObject Row(string id, params string[] fields) => new() { ["sourceId"] = id, ["fields"] = Fields(fields) };
        void Write(string relative, JsonNode value) { var file = Path.Combine(project.Root, relative); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, value.ToJsonString(Pretty)); }
        void Table(string name, params JsonObject[] rows) => Write("source/tables/" + name + ".json", new JsonObject { ["schema"] = new JsonObject { ["name"] = name }, ["records"] = new JsonArray(rows.Cast<JsonNode?>().ToArray()) });
        Table("weapons", Row("base", "code", "axe", "namestr", "axe", "mindam", "3", "maxdam", "6", "levelreq", "10", "reqstr", "20"));
        Table("properties", Row("str", "code", "str", "func1", "1", "stat1", "strength"), Row("ed", "code", "dmg%", "func1", "7"), Row("ac", "code", "ac%", "func1", "2", "stat1", "item_armor_percent"),
            Row("oskill", "code", "oskill_hide", "func1", "22", "stat1", "item_nonclassskill", "*Tooltip", "+# to [Skill]"),
            Row("speed", "code", "swing1", "func1", "8", "stat1", "item_fasterattackrate", "*Tooltip", "+#% Increased Attack Speed"),
            Row("element", "code", "dmg-fire", "func1", "15", "stat1", "firemindam", "func2", "16", "stat2", "firemaxdam", "*Tooltip", "Adds #-# Fire Damage"),
            Row("socket", "code", "sock", "func1", "14", "*Tooltip", "Socketed (#)"));
        Table("itemstatcost", Row("edmin", "Stat", "item_mindamage_percent", "op", "13", "descfunc", "19", "descstrpos", "damage"), Row("str", "Stat", "strength", "descfunc", "19", "descstrpos", "strength", "descstrneg", "strength"), Row("ed", "Stat", "item_maxdamage_percent", "op", "13", "descfunc", "19", "descstrpos", "damage"), Row("ac", "Stat", "item_armor_percent", "op", "13", "descfunc", "19", "descstrpos", "defense"));
        Write("source/strings/test.json", new JsonObject { ["schema"] = new JsonObject { ["category"] = "test" }, ["records"] = new JsonArray(new[] { ("item", "Test Axe"), ("axe", "Axe"), ("strength", "%+d to Strength"), ("damage", "+%d%% Enhanced Damage"), ("defense", "+%d%% Enhanced Defense"), ("set", "Test Set"), ("armor", "Armor") }.Select(x => (JsonNode)new JsonObject { ["Key"] = x.Item1, ["translations"] = new JsonObject { ["enUS"] = x.Item2 } }).ToArray()) });
        var item = Row("item", "index", "item", "code", "axe", "lvl req", "5", "prop1", "str", "min1", "2", "max1", "4", "prop2", "dmg%", "min2", "100", "max2", "200", "prop3", "oskill_hide", "par3", "Hidden Skill", "min3", "1", "max3", "1", "prop4", "dmg-fire", "min4", "5", "max4", "10", "prop5", "sock", "min5", "1", "max5", "2");
        var resolver = new ItemPreviewResolver();
        ItemPreviewResult Preview(JsonObject? row = null, string table = "uniqueitems", string profile = "standard") => resolver.Resolve(project, table, row ?? item, profile, 80, "enUS", default);
        var result = Preview();
        check(result.Name == "Test Axe" && result.Lines.Contains("Required level: 10") && result.Lines.Contains("+(2–4) to Strength"), "Item preview resolves base, localization, requirements and roll ranges");
        check(result.Issues.Length == 0 && result.Lines.Any(x => x.Contains("min (6–9) / max (12–18)")), "Item physical damage calculates enhanced damage ranges with integer rounding");
        check(result.Lines.Contains("+1 to Hidden Skill") && result.Lines.Contains("Adds 5-10 Fire Damage") && result.Lines.Contains("Socketed (1–2)"), "Skill, elemental-damage and socket properties render without incomplete warnings");
        CellLink[] Links(ItemPreviewResult r, string line) => [.. r.Text.Single(t => t.Text == line).Links.SelectMany(l => l.Targets)];
        check(Links(result, "+(2–4) to Strength") is [{ Table: "uniqueitems", SourceId: "item", Column: "min1" }, { Column: "max1" }]
            && Links(result, "+1 to Hidden Skill").Any(c => c.Column == "par3") && Links(result, "Required level: 10").Single() is { Table: "weapons", Column: "levelreq" }
            && Links(result, "Axe").Any(c => c is { Table: "weapons", Column: "namestr" }) && result.Text.Single(t => t.Text.Contains("One-hand")).Links.Length == 4,
            "Item preview values link to the item and base cells they were read from");
        var warm = Preview(); check(warm == result || warm.Lines.SequenceEqual(result.Lines), "Cached item resolution preserves preview output");
        Write("compatibility/test/profile.json", new JsonObject { ["tableOverrides"] = new JsonArray("item.json") });
        var rule = new JsonObject { ["table"] = "uniqueitems", ["record"] = "item", ["changes"] = new JsonObject { ["min1"] = new JsonObject { ["expect"] = "2", ["value"] = "3" } } }; Write("compatibility/test/item.json", rule);
        check(Preview(profile: "test").Lines.Contains("+(3–4) to Strength"), "Item preview applies selected profile overrides without changing shared rows");
        check(item["fields"].S("min1") == "2", "Preview leaves selected source unchanged");
        rule["targets"] = new JsonArray("global/excel/uniqueitems.txt"); Write("compatibility/test/item.json", rule); resolver.Clear();
        throws(() => Preview(profile: "test"), "Ambiguous bank-scoped item preview is blocked");
        rule.Remove("targets"); rule["changes"]!["min1"]!["expect"] = "stale"; Write("compatibility/test/item.json", rule); resolver.Clear();
        throws(() => Preview(profile: "test"), "Stale preview overrides are blocked");
        var broken = (JsonObject)item.DeepClone(); broken["fields"]!["prop3"] = "missing";
        check(Preview(broken).Issues.Any(x => x.Contains("missing")) && Preview(broken).Lines.Any(x => x.StartsWith("Derived totals unavailable")), "Unknown properties remain visible and block misleading derived totals");
        var inactive = Row("inactive", "index", "Expansion");
        check(Preview(inactive).Issues.Length == 0 && Preview(inactive).Lines.Contains("Inactive/header row · no base item code"), "Inactive separator rows do not report incomplete item previews");
        broken = (JsonObject)item.DeepClone(); broken["fields"]!["min1"] = "100";
        check(Preview(broken).Issues.Any(x => x.Contains("Minimum exceeds")), "Invalid roll ranges produce useful preview errors");
        Table("armor", Row("armor", "code", "arm", "namestr", "armor", "minac", "5", "maxac", "10")); resolver.Clear();
        var armor = Row("armor", "index", "item", "code", "arm", "prop1", "ac%", "min1", "100", "max1", "200");
        check(Preview(armor).Lines.Contains("Defense: (22–33)"), "Enhanced armor defense uses maximum base defense plus one");
        Table("sets", Row("set", "index", "set", "PCode2a", "str", "PMin2a", "2", "PMax2a", "2", "FCode1", "str", "FMin1", "4", "FMax1", "4"));
        var set = Row("setitem", "index", "item", "item", "axe", "set", "set", "add func", "2", "aprop1a", "str", "amin1a", "1", "amax1a", "1");
        result = Preview(set, "setitems");
        check(result.IsSet && result.Lines.Contains("Item bonus with 2 set pieces:") && result.Lines.Contains("Set bonus with 2 pieces:") && result.Lines.Contains("Full set bonuses:"), "Set preview separates item partial, set partial and full-set bonuses");
        var setLinks = result.Text.SelectMany(t => t.Links).SelectMany(l => l.Targets).ToArray();
        check(setLinks.Any(c => c is { Table: "setitems", Column: "amin1a" }) && setLinks.Any(c => c is { Table: "sets", Column: "PMin2a" }) && setLinks.Any(c => c is { Table: "sets", Column: "FMax1" }),
            "Set bonuses link to their setitems and sets cells");
        Table("properties", Row("scale", "code", "ac/lvl", "func1", "17", "stat1", "item_armor_perlevel"));
        Table("itemstatcost", Row("scale", "Stat", "item_armor_perlevel", "op", "4", "op base", "level", "op param", "3", "op stat1", "armorclass", "descfunc", "19", "descstrpos", "defense"));
        var scaled = Row("scaled", "index", "item", "code", "arm", "prop1", "ac/lvl", "par1", "3"); resolver.Clear();
        check(Preview(scaled).Lines.Any(x => x.Contains("30") && x.Contains("at level 80")) && Preview(scaled).Lines.Contains("Defense: (35–40)"), "Level-scaled properties use the stat divisor and update defense totals");
        Table("armor", Row("armor", "code", "arm", "namestr", "armor", "minac", "100", "maxac", "200"));
        check(Preview(scaled).Lines.Contains("Defense: (130–230)"), "Changed dependency files invalidate cached item data");
        Table("properties", Row("ease", "code", "ease", "func1", "1", "stat1", "item_req_percent", "*Tooltip", "Requirements -#%"));
        Table("itemstatcost", Row("ease", "Stat", "item_req_percent", "descfunc", "19", "descstrpos", "ease", "descstrneg", "ease"));
        Write("source/strings/test.json", new JsonObject { ["schema"] = new JsonObject { ["category"] = "test" }, ["records"] = new JsonArray(new[] { ("item", "Test Axe"), ("armor", "Armor"), ("ease", "Requirements %+d%%") }.Select(x => (JsonNode)new JsonObject { ["Key"] = x.Item1, ["translations"] = new JsonObject { ["enUS"] = x.Item2 } }).ToArray()) });
        JsonObject Ease(string min, string max) => Row("ease", "index", "item", "code", "arm", "prop1", "ease", "min1", min, "max1", max);
        resolver.Clear();
        check(Preview(Ease("20", "20")).Lines.Contains("Requirements +20%") && Preview(Ease("-60", "-20")).Lines.Contains("Requirements -20–60%"),
            "A *Tooltip comment's hard-coded sign follows the rolled value as the game shows it");
        check(resolver.Resolve(project, "uniqueitems", scaled, "standard", 80, "missing", default).Issues.Any(x => x.Contains("localization")), "Missing localization is explicit instead of silently showing another locale");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        throws(() => resolver.Resolve(project, "uniqueitems", item, "standard", 80, "enUS", cancel.Token), "Canceled item resolution stops before producing results");
    }
}

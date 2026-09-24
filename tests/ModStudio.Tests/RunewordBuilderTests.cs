using System.Text.Json.Nodes;
using ModStudio.Core;
using static ModStudio.Core.Storage;

/// <summary>Runeword builder: which bases a runeword fits (types, exclusions, sockets by item level), the level its runes require, and the catalog.</summary>
internal static class RunewordBuilderTests
{
    public static void Run(string root, Action<bool, string> check, Action<Action, string> throws)
    {
        var project = new ModProject(Path.Combine(root, "runeword-builder"), "test", "Test"); Directory.CreateDirectory(project.Root);
        JsonObject Row(string id, params string[] fields) { var f = new JsonObject(); for (int i = 0; i < fields.Length; i += 2) f[fields[i]] = fields[i + 1]; return new() { ["sourceId"] = id, ["fields"] = f }; }
        void Table(string name, params JsonObject[] rows) => File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(project.Root, "source", "tables")).FullName, name + ".json"),
            new JsonObject { ["schema"] = new JsonObject { ["name"] = name }, ["records"] = new JsonArray(rows.Cast<JsonNode?>().ToArray()) }.ToJsonString(Pretty));
        Table("itemtypes", Row("w", "Code", "weap", "ItemType", "Weapon"), Row("s", "Code", "swor", "ItemType", "Sword", "Equiv1", "weap", "MaxSockets1", "2", "MaxSocketsLevelThreshold1", "25", "MaxSockets2", "4", "MaxSocketsLevelThreshold2", "40", "MaxSockets3", "6"),
            Row("a", "Code", "axe", "ItemType", "Axe", "Equiv1", "weap", "MaxSockets1", "6", "MaxSocketsLevelThreshold1", "25", "MaxSockets2", "6", "MaxSocketsLevelThreshold2", "40", "MaxSockets3", "6"),
            Row("r", "Code", "rune", "ItemType", "Rune"));
        Table("weapons", Row("ls", "code", "lsd", "namestr", "lsd", "type", "swor", "gemsockets", "4", "level", "20", "levelreq", "0"),
            Row("ss", "code", "ssd", "namestr", "ssd", "type", "swor", "gemsockets", "2", "level", "1"),
            Row("ax", "code", "axe", "namestr", "axe", "type", "axe", "gemsockets", "4", "level", "7", "levelreq", "5"));
        Table("misc", Row("t", "code", "r07", "namestr", "r07", "type", "rune", "levelreq", "17"), Row("o", "code", "r09", "namestr", "r09", "type", "rune", "levelreq", "21"));
        Table("properties", Row("p", "code", "str"));
        var strings = Directory.CreateDirectory(Path.Combine(project.Root, "source", "strings")).FullName;
        File.WriteAllText(Path.Combine(strings, "s.json"), new JsonObject { ["schema"] = new JsonObject { ["category"] = "s" }, ["records"] = new JsonArray(
            new[] { ("lsd", "Long Sword"), ("ssd", "Short Sword"), ("axe", "Axe"), ("r07", "Tal Rune"), ("r09", "Ort Rune"), ("Runeword1", "Spirit") }.Select(x => (JsonNode)new JsonObject { ["Key"] = x.Item1, ["translations"] = new JsonObject { ["enUS"] = x.Item2 } }).ToArray()) }.ToJsonString(Pretty));
        var resolver = new RunewordBuilderResolver();
        RunewordFit Fit(params string[] fields) => resolver.Fit(project, Row("rw", fields), "standard", "enUS", default);

        var three = Fit("Name", "Runeword1", "itype1", "weap", "Rune1", "r07", "Rune2", "r09", "Rune3", "r07");
        check(three is { Sockets: 3, RuneLevel: 21 } && three.Bases.Select(b => (b.Code, b.FromItemLevel)).SequenceEqual([("axe", 1), ("lsd", 26)]) && three.NeverEnoughSockets.SequenceEqual(["Short Sword"]),
            "A runeword fits bases of its types that roll enough sockets, lowest drop level first, from the item level they can; its runes set its level");
        check(Fit("itype1", "weap", "etype1", "axe", "Rune1", "r07").Bases.Select(b => b.Code).SequenceEqual(["ssd", "lsd"]), "Excluded types are left out, even under an allowed type");
        check(Fit("itype1", "swor", "Rune1", "r07").Bases.All(b => b.Table == "weapons" && b.Name.EndsWith("Sword")) && Fit("itype1", "rune", "Rune1", "r07").Bases.Length == 0, "Only weapons and armor are bases; misc items never are");
        check(Fit("itype1", "weap") is { Sockets: 0, RuneLevel: 0 } && Fit("itype1", "weap", "Rune1", "zzz").RuneLevel == 0, "No runes, or unknown ones, require no level");

        var catalog = resolver.Catalog(project, "standard", "enUS", [new(0, "a", "Runeword1", "Spirit comment", "1", "r07,r09"), new(1, "b", "Runeword2", "Comment Only", "0", ""), new(2, "c", "", "", "", "")], default);
        check(catalog.Entries[0] is { Name: "Spirit", Runes: "Tal Ort", Complete: true } && catalog.Entries[1] is { Name: "Comment Only", Complete: false } && catalog.Entries[2].Inactive
            && VisualBuilder.Matches(catalog.Entries[0].SearchText, "spirit tal"), "Runewords are listed by localized name (else their comment) and their runes");
        check(catalog.Runes.Select(r => (r.Code, r.RequiredLevel)).SequenceEqual([("r07", 17), ("r09", 21)]) && catalog.Types.Any(t => t.Code == "swor") && catalog.Properties.Single().Code == "str",
            "The catalog offers the runes with their levels, the item types and the properties");
    }
}

using System.Text.Json.Nodes;
using ModStudio.Core;
using static ModStudio.Core.Storage;

/// <summary>Base item builder: the game-style tooltip and facts for weapons, armor and misc, tiers, sockets by level, and the catalog.</summary>
internal static class BaseItemBuilderTests
{
    public static void Run(string root, Action<bool, string> check, Action<Action, string> throws)
    {
        var project = new ModProject(Path.Combine(root, "base-items"), "test", "Test"); Directory.CreateDirectory(project.Root);
        JsonObject Row(string id, params string[] fields) { var f = new JsonObject(); for (int i = 0; i < fields.Length; i += 2) f[fields[i]] = fields[i + 1]; return new() { ["sourceId"] = id, ["fields"] = f }; }
        void Table(string name, params JsonObject[] rows) => File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(project.Root, "source", "tables")).FullName, name + ".json"),
            new JsonObject { ["schema"] = new JsonObject { ["name"] = name }, ["records"] = new JsonArray(rows.Cast<JsonNode?>().ToArray()) }.ToJsonString(Pretty));
        Table("itemtypes", Row("axe", "ItemType", "Axe", "Code", "axe", "MaxSockets1", "4", "MaxSocketsLevelThreshold1", "25", "MaxSockets2", "5", "MaxSocketsLevelThreshold2", "40", "MaxSockets3", "6"),
            Row("boot", "ItemType", "Boots", "Code", "boot"), Row("shie", "ItemType", "Shield", "Code", "shie"));
        var strings = Directory.CreateDirectory(Path.Combine(project.Root, "source", "strings")).FullName;
        File.WriteAllText(Path.Combine(strings, "items.json"), new JsonObject { ["schema"] = new JsonObject { ["category"] = "items" }, ["records"] = new JsonArray(
            new[] { ("gix", "Giant Axe"), ("9gi", "Ancient Axe"), ("lbt", "Boots"), ("buc", "Buckler"), ("hp1", "Minor Healing Potion"), ("hpdesc", "Heals 30 life") }
                .Select(x => (JsonNode)new JsonObject { ["Key"] = x.Item1, ["translations"] = new JsonObject { ["enUS"] = x.Item2 } }).ToArray()) }.ToJsonString(Pretty));
        var giant = Row("giant", "name", "Giant Axe", "namestr", "gix", "code", "gix", "type", "axe", "normcode", "gix", "ubercode", "9gi", "ultracode", "7gi", "2handed", "1", "mindam", "1",
            "2handmindam", "22", "2handmaxdam", "45", "durability", "50", "reqstr", "70", "reqdex", "0", "levelreq", "0", "speed", "10", "gemsockets", "4", "level", "27", "cost", "445",
            "gamble cost", "12000", "StrBonus", "100", "invwidth", "2", "invheight", "3");
        var ancient = Row("ancient", "name", "Ancient Axe", "namestr", "9gi", "code", "9gi", "type", "axe", "normcode", "gix", "ubercode", "9gi", "ultracode", "7gi", "1or2handed", "1", "2handed", "1",
            "mindam", "10", "maxdam", "20", "2handmindam", "43", "2handmaxdam", "85", "minmisdam", "5", "maxmisdam", "5", "nodurability", "1", "durability", "60", "reqstr", "125", "reqdex", "30", "levelreq", "40", "gemsockets", "6");
        Table("weapons", giant, ancient);
        Table("armor", Row("boots", "namestr", "lbt", "code", "lbt", "type", "boot", "minac", "2", "maxac", "3", "mindam", "3", "maxdam", "8", "durability", "12"),
            Row("buckler", "namestr", "buc", "code", "buc", "type", "shie", "minac", "4", "maxac", "4", "block", "25", "mindam", "1", "maxdam", "3"));
        Table("misc", Row("potion", "namestr", "hp1", "code", "hp1", "stackable", "1", "minstack", "1", "maxstack", "1", "spelldescstr", "hpdesc", "nodurability", "1"));
        Table("uniqueitems", Row("u", "index", "The Humongous", "code", "gix"));
        Table("setitems", Row("s", "index", "Immortal King's Stone Crusher", "item", "7gi"));

        var resolver = new BaseItemPreviewResolver();
        BaseItemPreviewResult Preview(string table, JsonObject row) => resolver.Resolve(project, table, row, "standard", "enUS", default);
        string[] Lines(BaseItemPreviewResult r) => [.. r.Tooltip.Select(t => t.Text)];
        string Fact(BaseItemPreviewResult r, string label) => r.Facts.Single(f => f.Label == label).Value.Text;
        var normal = Preview("weapons", giant);
        check(normal is { Name: "Giant Axe", Tier: "Normal" } && Lines(normal).SequenceEqual(["Giant Axe", "Two-Hand Damage: 22 to 45", "Durability: 50 of 50", "Required Strength: 70", "Attack speed modifier: 10", "Up to 4 sockets"]),
            "A two-handed weapon shows two-hand damage, durability and requirements, skipping the zero ones: " + string.Join(" | ", Lines(normal)));
        check(Fact(normal, "Tier") == "Normal · gix / 9gi / 7gi" && Fact(normal, "Type") == "Axe (axe)" && Fact(normal, "Sockets") == "4 up to ilvl 25, 4 up to 40, 4 above"
            && Fact(normal, "Cost") == "445 · gamble 12000" && Fact(normal, "Damage bonus").StartsWith("100% per 100 Strength") && Fact(normal, "Inventory size") == "2 × 3",
            "Facts give the tier codes, the type, sockets capped by the item and its type per item level, price, damage bonus and size");
        check(normal.Tooltip[1].Links.SelectMany(l => l.Targets).Select(t => t.Column).SequenceEqual(["2handmindam", "2handmaxdam"]), "Tooltip values link to their cells");
        var exceptional = Preview("weapons", ancient);
        check(exceptional.Tier == "Exceptional" && Lines(exceptional).SequenceEqual(["Ancient Axe", "One-Hand Damage: 10 to 20", "Two-Hand Damage: 43 to 85", "Throw Damage: 5", "Required Dexterity: 30",
            "Required Strength: 125", "Required Level: 40", "Up to 6 sockets"]) && Fact(exceptional, "Sockets") == "4 up to ilvl 25, 5 up to 40, 6 above",
            "A one-or-two-handed weapon shows both damages; throw damage, Dexterity before Strength, and no durability when it has none: " + string.Join(" | ", Lines(exceptional)));
        check(Lines(Preview("armor", Row("b", "namestr", "lbt", "code", "lbt", "type", "boot", "minac", "2", "maxac", "3", "mindam", "3", "maxdam", "8", "durability", "12"))).SequenceEqual(["Boots", "Defense: 2–3", "Kick Damage: 3 to 8", "Durability: 12 of 12"])
            && Lines(Preview("armor", Row("s", "namestr", "buc", "code", "buc", "type", "shie", "minac", "4", "maxac", "4", "block", "25", "mindam", "1", "maxdam", "3"))).SequenceEqual(["Buckler", "Defense: 4", "Chance to Block: 25%", "Smite Damage: 1 to 3"]),
            "Armor shows defense and block; boots kick and shields smite");
        check(Lines(Preview("misc", Row("p", "namestr", "hp1", "code", "hp1", "stackable", "1", "minstack", "1", "maxstack", "1", "spelldescstr", "hpdesc", "nodurability", "1"))).SequenceEqual(["Minor Healing Potion", "Quantity: 1", "Heals 30 life"]),
            "Misc items show their quantity and use description");
        check(Preview("weapons", Row("h", "name", "Expansion")) is { Name: "Expansion", Tier: "" } header && header.Tooltip.Single().Text.Contains("no code"), "Header rows are inactive");
        throws(() => Preview("uniqueitems", giant), "Only base item tables are resolved");
        var unknown = Preview("weapons", Row("x", "code", "zzz", "type", "nope"));
        check(unknown.Issues.Any(i => i.Contains("itemtypes")), "An unknown type is reported");

        var catalog = resolver.Catalog(project, "standard", "enUS", [new(0, "giant", "gix", "Giant Axe", "gix", "axe", "27", "gix", "9gi", "7gi"), new(1, "ancient", "9gi", "Ancient Axe", "9gi", "axe", "", "gix", "9gi", "7gi"), new(2, "h", "", "Expansion", "", "", "", "", "", "")], default);
        check(catalog.Entries[0] is { Name: "Giant Axe", Type: "Axe", Tier: "Normal" } && catalog.Entries[1].Tier == "Exceptional" && catalog.Entries[2].Inactive && VisualBuilder.Matches(catalog.Entries[1].SearchText, "ancient exceptional"),
            "The catalog lists localized names, type names and tiers");
        check(catalog.Codes.Contains("lbt") && catalog.Codes.Contains("hp1") && catalog.ItemTypes.Length == 3 && catalog.Uses["gix"].Single() is { Table: "uniqueitems", Name: "The Humongous" } && catalog.Uses["7gi"].Single().Table == "setitems",
            "The catalog offers every base code and type, and knows the uniques and sets made on each code");
    }
}

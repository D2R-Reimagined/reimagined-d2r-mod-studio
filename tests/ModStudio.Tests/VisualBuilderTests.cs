using System.Buffers.Binary;
using System.Text.Json.Nodes;
using ModStudio.Core;
using static ModStudio.Core.Storage;

/// <summary>Visual Builder: HD item pictures (project, then game data), property slots, the search catalog and the game-style tooltip.</summary>
internal static class VisualBuilderTests
{
    public static void Run(string root, Action<bool, string> check, Action<Action, string> throws)
    {
        var project = new ModProject(Path.Combine(root, "visual-builder"), "test", "Test"); Directory.CreateDirectory(project.Root);
        var gameData = Path.Combine(root, "visual-builder-game", "data");
        void WriteText(string file, string text) { Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, text, Utf8); }
        // A one-frame uncompressed SpA1 sprite whose pixels are all one colour.
        void Sprite(string file, int width, int height, byte red)
        {
            var bytes = new byte[40 + width * height * 4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, 0x31417053); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 31); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)width);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), width); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), height); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), 1);
            for (int i = 40; i < bytes.Length; i += 4) { bytes[i] = red; bytes[i + 3] = 255; }
            Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllBytes(file, bytes);
        }
        // Base game: a name-keyed unique picture, a base-item picture per tier, and the sprites. Folder names differ in case, as extractions do.
        WriteText(Path.Combine(gameData, "HD", "items", "uniques.json"), "[\n { \"bul_kathos_wedding_band\": { \"normal\": \"ring/bk_ring\", \"uber\": \"ring/bk_ring\", \"ultra\": \"ring/bk_ring\" } },\n { \"the_gnasher\": { \"normal\": \"axe/the_gnasher\" } },\n]");
        WriteText(Path.Combine(gameData, "HD", "items", "items.json"), "[ { \"hax\": { \"asset\": \"axe/hand_axe\" } }, { \"9ha\": { \"asset\": \"axe/hatchet\" } } ]");
        Sprite(Path.Combine(gameData, "HD", "Global", "UI", "items", "misc", "ring", "bk_ring.sprite"), 2, 2, 10);
        Sprite(Path.Combine(gameData, "HD", "Global", "UI", "items", "weapon", "axe", "the_gnasher.sprite"), 2, 6, 20);
        Sprite(Path.Combine(gameData, "HD", "Global", "UI", "items", "weapon", "axe", "hand_axe.sprite"), 2, 4, 30);

        check(ItemSprites.Key("Bul-Kathos' Wedding Band") == ItemSprites.Key("bul_kathos_wedding_band") && ItemSprites.Key("The Gnasher") == "thegnasher", "Item names and HD list keys meet once punctuation, spaces and case are dropped");
        var none = ItemSprites.Resolve(project, [], "uniqueitems", "The Gnasher", "hax", "weapons");
        check(none.Pixels == null && none.Notes.Single().Contains("game data folder"), "Without the project's or the game's item lists the picture asks for the game data folder");
        var gnasher = ItemSprites.Resolve(project, [gameData], "uniqueitems", "The Gnasher", "hax", "weapons");
        check(gnasher is { Asset: "axe/the_gnasher", File.InProject: false, Pixels: { Width: 2, Height: 6 } } && gnasher.Pixels.Rgba[0] == 20 && gnasher.Via.StartsWith("uniques.json"),
            "A unique's own picture is named by uniques.json and read from the game data");
        var band = ItemSprites.Resolve(project, [gameData], "uniqueitems", "Bul-Kathos' Wedding Band", "rin", "misc");
        check(band is { Asset: "ring/bk_ring", Pixels.Width: 2 } && band.File!.Relative.Contains("/misc/"), "Hyphenated names with apostrophes find their picture, under the misc folder");
        var fallback = ItemSprites.Resolve(project, [gameData], "uniqueitems", "Unlisted Axe", "hax", "weapons");
        check(fallback is { Asset: "axe/hand_axe", Pixels.Height: 4 } && fallback.Via.Contains("base hax"), "Items the list does not name show their base item's picture");
        var missing = ItemSprites.Resolve(project, [gameData], "uniqueitems", "Unlisted", "9ha", "weapons");
        check(missing.Pixels == null && missing.Asset == "axe/hatchet" && missing.Notes.Any(n => n.Contains("axe/hatchet")), "An asset whose sprite is missing names what was looked for");
        Sprite(Path.Combine(gameData, "HD", "Global", "UI", "items", "weapon", "axe", "hatchet.lowend.sprite"), 1, 2, 40);
        check(ItemSprites.Resolve(project, [gameData], "uniqueitems", "Unlisted", "9ha", "weapons") is { Pixels.Height: 2 } lowEnd && lowEnd.File!.Relative.EndsWith(".lowend.sprite"),
            "The low-end sprite stands in when the full-resolution one is missing");
        // The mod ships its own list and picture: they win over the game's.
        WriteText(Path.Combine(project.Root, "data", "hd", "items", "uniques.json"), "[ { \"the_gnasher\": { \"normal\": \"axe/new_gnasher\" } } ]");
        Sprite(Path.Combine(project.Root, "data", "hd", "global", "ui", "items", "weapon", "axe", "new_gnasher.sprite"), 4, 4, 99);
        var modded = ItemSprites.Resolve(project, [gameData], "uniqueitems", "The Gnasher", "hax", "weapons");
        check(modded is { Asset: "axe/new_gnasher", File.InProject: true } && modded.Pixels!.Rgba[0] == 99, "The project's item list and sprite win over the game data");
        var elite = new JsonObject { ["code"] = "hax", ["normcode"] = "hax", ["ubercode"] = "9ha", ["ultracode"] = "7ha" };
        check(ItemSprites.Tier(elite, "hax") == "normal" && ItemSprites.Tier(elite, "9ha") == "uber" && ItemSprites.Tier(elite, "7ha") == "ultra" && ItemSprites.Tier(null, "x") == "normal", "Base tier codes pick the list's normal, uber or ultra picture");

        string[] uniqueColumns = ["index", "code", "lvl req", "prop1", "par1", "min1", "max1", "prop2", "par2", "min2", "max2", "prop13", "par13", "min13", "max13", "prop14", "min14"];
        var slots = VisualBuilder.Slots("uniqueitems", uniqueColumns);
        check(slots.Select(s => s.Prop).SequenceEqual(["prop1", "prop2", "prop13"]) && slots.All(s => !s.SetBonus) && slots[2] is { Parameter: "par13", Min: "min13", Max: "max13" },
            "Property slots follow the table's columns, including modded extra slots, and skip incomplete ones");
        string[] setColumns = ["index", "set", "item", "add func", "prop1", "par1", "min1", "max1", "aprop1a", "apar1a", "amin1a", "amax1a", "aprop1b", "apar1b", "amin1b", "amax1b", "aprop2a", "apar2a", "amin2a", "amax2a"];
        var setSlots = VisualBuilder.Slots("setitems", setColumns);
        check(setSlots.Count(s => !s.SetBonus) == 1 && setSlots.Where(s => s.SetBonus).Select(s => (s.Prop, s.Pieces)).SequenceEqual([("aprop1a", 2), ("aprop1b", 2), ("aprop2a", 3)]),
            "Set item bonus slots are numbered by the pieces worn");
        check(VisualBuilder.Slots("uniqueitems", setColumns).All(s => !s.SetBonus), "Only set items have bonus slots");
        check(VisualBuilder.Matches("the gnasher hand axe hax", "gnash AXE") && !VisualBuilder.Matches("the gnasher hand axe hax", "gnasher bow"), "Search matches every word, ignoring case");

        JsonObject Fields(params string[] values) { var obj = new JsonObject(); for (int i = 0; i < values.Length; i += 2) obj[values[i]] = values[i + 1]; return obj; }
        JsonObject Row(string id, params string[] fields) => new() { ["sourceId"] = id, ["fields"] = Fields(fields) };
        void Table(string name, params JsonObject[] rows) => WriteText(Path.Combine(project.Root, "source", "tables", name + ".json"), new JsonObject { ["schema"] = new JsonObject { ["name"] = name }, ["records"] = new JsonArray(rows.Cast<JsonNode?>().ToArray()) }.ToJsonString(Pretty));
        Table("weapons", Row("hax", "code", "hax", "namestr", "hax", "normcode", "hax", "ubercode", "9ha", "mindam", "3", "maxdam", "6", "levelreq", "1", "reqdex", "0", "reqstr", "10"), Row("hatchet", "code", "9ha", "namestr", "9ha", "normcode", "hax", "ubercode", "9ha", "levelreq", "19"));
        Table("armor"); Table("misc");
        Table("properties", Row("str", "code", "str", "func1", "1", "stat1", "strength", "*Tooltip", "+# to Strength", "*Min", "1", "*Max", "30"), Row("ed", "code", "dmg%", "func1", "7"));
        Table("itemstatcost", Row("str", "Stat", "strength", "descfunc", "19", "descstrpos", "strength"), Row("edmin", "Stat", "item_mindamage_percent", "descfunc", "19", "descstrpos", "damage"), Row("ed", "Stat", "item_maxdamage_percent", "descfunc", "19", "descstrpos", "damage"));
        Table("sets", Row("set", "index", "Axe Set"));
        WriteText(Path.Combine(project.Root, "source", "strings", "test.json"), new JsonObject { ["schema"] = new JsonObject { ["category"] = "test" }, ["records"] = new JsonArray(new[] { ("The Gnasher", "The Gnasher"), ("hax", "Hand Axe"), ("9ha", "Hatchet"), ("strength", "%+d to Strength"), ("damage", "+%d%% Enhanced Damage"), ("Axe Set", "Axe Set") }.Select(x => (JsonNode)new JsonObject { ["Key"] = x.Item1, ["translations"] = new JsonObject { ["enUS"] = x.Item2 } }).ToArray()) }.ToJsonString(Pretty));
        var catalog = new VisualBuilderResolver().Catalog(project, "standard", "enUS", [new(0, "row-00000", "The Gnasher", "hax", "5", ""), new(1, "row-00001", "Expansion", "", "", "")], default);
        check(catalog.Entries[0] is { Name: "The Gnasher", BaseName: "Hand Axe", Inactive: false } && catalog.Entries[1].Inactive && VisualBuilder.Matches(catalog.Entries[0].SearchText, "hand gnash"),
            "The catalog localizes item and base names and marks header rows");
        check(catalog.Bases.Select(b => (b.Code, b.Tier)).SequenceEqual([("hax", "normal"), ("9ha", "uber")]) && catalog.Properties.Single(p => p.Code == "str") is { Tooltip: "+# to Strength", Min: "1", Max: "30" } && catalog.Sets.Single().Name == "Axe Set",
            "The catalog offers base codes with their tier, documented property codes and sets");

        var item = Row("gnasher", "index", "The Gnasher", "code", "hax", "lvl req", "5", "prop1", "str", "min1", "2", "max1", "4", "prop2", "dmg%", "min2", "50", "max2", "50");
        var result = new ItemPreviewResolver().Resolve(project, "uniqueitems", item, "standard", 80, "enUS", default);
        var tooltip = result.Tooltip!;
        check(tooltip.BaseName?.Text == "Hand Axe" && tooltip.Stats.Select(s => s.Text).SequenceEqual(["One-Hand Damage: 4 to 9", "Required Strength: 10", "Required Level: 5"]),
            "The tooltip lays out damage, requirements (skipping zero Dexterity) and the required level as the game does: " + string.Join(" | ", tooltip.Stats.Select(s => s.Text)));
        check(tooltip.Properties.Select(p => p.Text).SequenceEqual(["+2–4 to Strength", "+50% Enhanced Damage"]) && tooltip.SetLines.Length == 0 && !tooltip.Properties.Any(p => p.Text.Contains("Calculations") || p.Text.Contains("Required")),
            "Only the item's own properties are listed as properties: " + string.Join(" | ", tooltip.Properties.Select(p => p.Text)));
        check(tooltip.Properties[0].Links.SelectMany(l => l.Targets).All(t => t.Table == "uniqueitems" && t.Column is "min1" or "max1"), "Tooltip property values link to the slot's cells");
        var set = Row("setitem", "index", "The Gnasher", "item", "hax", "set", "Axe Set", "add func", "2", "prop1", "str", "min1", "1", "max1", "1", "aprop1a", "str", "amin1a", "3", "amax1a", "3");
        var setTooltip = new ItemPreviewResolver().Resolve(project, "setitems", set, "standard", 80, "enUS", default).Tooltip!;
        check(setTooltip.Properties.Single().Text == "+1 to Strength" && setTooltip.SetLines.Any(l => l.Text == "+3 to Strength" && l.Links.Any(k => k.Targets.Any(t => t.Column == "amin1a"))),
            "Set item bonuses go in the set section and link to their bonus slot");
        var inactive = new ItemPreviewResolver().Resolve(project, "uniqueitems", Row("header", "index", "Expansion"), "standard", 80, "enUS", default);
        check(inactive.Tooltip == null, "Header rows have no tooltip");
    }
}

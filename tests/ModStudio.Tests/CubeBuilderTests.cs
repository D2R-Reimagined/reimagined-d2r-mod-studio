using System.Text.Json.Nodes;
using ModStudio.Core;
using static ModStudio.Core.Storage;

/// <summary>Cube builder: editing a cube cell's tokens in place, and the catalog of what a cell can name with its pictures.</summary>
internal static class CubeBuilderTests
{
    public static void Run(string root, Action<bool, string> check, Action<Action, string> throws)
    {
        var cell = CubeTokens.Parse("\"hax,mag,sock=2,qty=3\"");
        check(cell is { Head: "hax", Quantity: 3 } && cell.Has("MAG") && cell.Value("sock") == "2" && cell.ToString() == "hax,mag,sock=2,qty=3", "A cube cell reads as its item and tokens, quotes dropped, order kept");
        cell.Choose(["low", "nor", "mag", "rar"], "rar");
        check(cell.ToString() == "hax,rar,sock=2,qty=3", "Choosing a quality replaces the old one where it was: " + cell);
        cell.Choose(["low", "nor", "mag", "rar"], null); cell.Flag("eth", true); cell.Flag("eth", true); cell.Set("sock", "4"); cell.SetQuantity(1);
        check(cell.ToString() == "hax,sock=4,eth", "Flags are added once at the end, values change in place, quantity 1 is dropped: " + cell);
        cell.Set("sock", ""); cell.Flag("eth", false); cell.Head = "axe";
        check(cell.ToString() == "axe" && CubeTokens.Parse("").ToString() == "" && CubeTokens.Parse("any,").ToString() == "any", "Clearing every token leaves the item alone; an empty cell stays empty");
        check(CubeTokens.Parse("Cow Portal").ToString() == "Cow Portal" && CubeTokens.Parse("r30,qty=2").Quantity == 2 && CubeTokens.Parse("gem4,qty=x").Quantity == 1, "Names with spaces survive and bad quantities count as one");
        var unknown = CubeTokens.Parse("any,weird,pre=5");
        unknown.Choose(["mag"], "mag");
        check(unknown.ToString() == "any,weird,pre=5,mag" && unknown.Chosen(["mag", "rar"]) == "mag", "Tokens the builder does not know are kept");

        var project = new ModProject(Path.Combine(root, "cube-builder"), "test", "Test"); Directory.CreateDirectory(project.Root);
        JsonObject Row(string id, params string[] fields) { var f = new JsonObject(); for (int i = 0; i < fields.Length; i += 2) f[fields[i]] = fields[i + 1]; return new() { ["sourceId"] = id, ["fields"] = f }; }
        void Table(string name, params JsonObject[] rows) => File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(project.Root, "source", "tables")).FullName, name + ".json"),
            new JsonObject { ["schema"] = new JsonObject { ["name"] = name }, ["records"] = new JsonArray(rows.Cast<JsonNode?>().ToArray()) }.ToJsonString(Pretty));
        Table("weapons", Row("a", "code", "hax", "namestr", "hax", "type", "axe", "normcode", "hax", "ubercode", "9ha"));
        Table("misc", Row("r", "code", "r30", "namestr", "r30", "type", "rune"));
        Table("itemtypes", Row("axe", "Code", "axe", "ItemType", "Axe", "Equiv1", "weap"), Row("weap", "Code", "weap", "ItemType", "Weapon"), Row("rune", "Code", "rune", "ItemType", "Rune"), Row("empty", "Code", "ring", "ItemType", "Ring"));
        Table("uniqueitems", Row("u", "index", "The Gnasher", "code", "hax"));
        Table("properties", Row("p", "code", "str", "*Tooltip", "+# to Strength"));
        var strings = Directory.CreateDirectory(Path.Combine(project.Root, "source", "strings")).FullName;
        File.WriteAllText(Path.Combine(strings, "s.json"), new JsonObject { ["schema"] = new JsonObject { ["category"] = "s" }, ["records"] = new JsonArray(
            new[] { ("hax", "Hand Axe"), ("r30", "Ber Rune"), ("The Gnasher", "The Gnasher") }.Select(x => (JsonNode)new JsonObject { ["Key"] = x.Item1, ["translations"] = new JsonObject { ["enUS"] = x.Item2 } }).ToArray()) }.ToJsonString(Pretty));
        var catalog = new CubeBuilderResolver().Catalog(project, "standard", "enUS", [new(0, "a", "2 Ber + axe -> gnasher", "1", "r30,qty=2\nhax,mag", "The Gnasher"), new(1, "b", "", "", "", "")], default);
        check(catalog.Find("hax") is { Kind: "item", Label: "Hand Axe", PictureCode: "hax", BaseTable: "weapons" } && catalog.Find("HAX") != null, "Base items are offered with their picture, case-insensitively");
        check(catalog.Find("weap") is { Kind: "type", Label: "Any Weapon", PictureCode: "hax" } && catalog.Find("ring") is { Kind: "type", PictureCode: "" }, "A type is pictured by a base item of that type (through its ancestors), and by nothing when it has none");
        check(catalog.Find("The Gnasher") is { Kind: "unique", PictureTable: "uniqueitems", PictureIndex: "The Gnasher", PictureCode: "hax" } && catalog.Find("usetype")?.Kind == "special" && catalog.Find("any") != null,
            "Uniques carry their index and base for their picture; special outputs and any are offered");
        check(catalog.Entries[0] is { Summary: "2 × Ber Rune + Hand Axe → The Gnasher (Hand Axe)", Enabled: true, Inactive: false } && catalog.Entries[1].Inactive && catalog.Properties.Single().Code == "str",
            "Recipes are listed as their inputs and outputs by name: " + catalog.Entries[0].Summary);
    }
}

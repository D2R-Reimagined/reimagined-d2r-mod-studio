using System.Text.Json.Nodes;
using ModStudio.Core;
using static ModStudio.Core.Storage;

/// <summary>HD monster appearance: monstats BaseId → unit JSON → variant file, TransLvl → entry, and saving an entry into the project.</summary>
internal static class MonsterAppearanceTests
{
    public static void Run(string root, Action<bool, string> check, Action<Action, string> throws)
    {
        var project = new ModProject(Path.Combine(root, "appearance"), "test", "Test");
        var gameData = Path.Combine(root, "appearance-game", "data");
        void Write(string file, string text) { Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, text, Utf8); }
        static string Transform(string name, double adjustX = 0, double tintW = 0) =>
            $"{{\"type\":\"ColorTransform\",\"name\":\"{name}\",\"colorAdjustment\":{{\"x\":{N(adjustX)},\"y\":0.0,\"z\":0.0,\"w\":1.0}},\"hueAdjustment0\":{{\"x\":0.0,\"y\":0.0,\"z\":0.0,\"w\":0.0}},\"hueAdjustment1\":{{\"x\":0.0,\"y\":0.0,\"z\":0.0,\"w\":0.0}},\"satAdjustment0\":{{\"x\":0.0,\"y\":0.0,\"z\":0.0,\"w\":0.0}},\"satAdjustment1\":{{\"x\":0.0,\"y\":0.0,\"z\":0.0,\"w\":0.0}},\"colorTint\":{{\"x\":0.172549,\"y\":0.0,\"z\":0.0,\"w\":{N(tintW)}}}}}";
        static string N(double v) => v.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture);
        static string Entry(string name, double adjustX = 0) => $"{{\"type\":\"VariantColorTransform\",\"name\":\"{name}\",\"transforms\":[{Transform("Red", adjustX)},{Transform("Green")},{Transform("Blue")}]}}";
        static string Unit(string variant) => $"{{\"type\":\"UnitDefinition\",\"entities\":[{{\"components\":[{{\"type\":\"UnitRootComponent\",\"name\":\"component_root\"}},{{\"type\":\"VariantDefinitionComponent\",\"name\":\"component_variant\",\"filename\":\"{variant}\"}}]}}]}}";
        var variantText = "{\"type\":\"VariantTransformTable\",\"entries\":[" + string.Join(",", new[] { Entry("cold"), Entry("level0"), Entry("level1", 0.8), Entry("level2") })
            + "],\"customColorShiftEntries\":[{\"type\":\"CustomVariantColorShiftMap\",\"name\":\"Bound\",\"colorShiftIndex\":130,\"entry\":" + Entry("Bound_entry", 0.5) + "}]}";
        // The game data is extracted with an upper-case HD folder; lookups ignore case like the game does.
        Write(Path.Combine(gameData, "HD", "character", "enemy", "brute", "brute_variant.json"), variantText);
        Write(Path.Combine(gameData, "HD", "character", "enemy", "brute.json"), Unit("data/hd/character/enemy/brute/brute_variant.json"));
        Write(Path.Combine(gameData, "HD", "character", "enemy", "brutelord.json"), Unit("data/hd/character/enemy/brute/brute_variant.json"));
        Write(Path.Combine(gameData, "HD", "character", "enemy", "loner.json"), Unit("data/hd/character/enemy/loner/loner_variant.json"));
        // The project overrides one unit and points it at the shared variant.
        Write(Path.Combine(project.Root, "data", "hd", "character", "enemy", "hero.json"), Unit("data/hd/character/enemy/brute/brute_variant.json"));
        JsonObject Monster(string id, string baseId, string transLvl) => new() { ["Id"] = id, ["BaseId"] = baseId, ["TransLvl"] = transLvl };
        var monstats = new[] { Monster("brute1", "brute", "0"), Monster("brute2", "brute", "1"), Monster("brute3", "brute", "2"), Monster("lord", "brutelord", "0"), Monster("hero", "hero", "9"), Monster("ghost", "nobody", "0") };

        var noData = HdAppearance.Resolve(project, [], monstats[1], monstats);
        check(noData.Unit == null && noData.Issues.Length == 0 && noData.Notes.Single().Contains("extracted game data"), "Without game data a base-game unit is a note, not a problem");
        var brute2 = HdAppearance.Resolve(project, [gameData], monstats[1], monstats);
        check(brute2.Unit is { InProject: false } && brute2.Variant is { Relative: "data/hd/character/enemy/brute/brute_variant.json", InProject: false } && brute2.DefaultEntry == "level1" && brute2.FamilyRows == 3,
            "BaseId names the unit JSON, the unit names the variant file, TransLvl picks levelN");
        check(brute2.Entries.Select(e => e.Name).SequenceEqual(["cold", "level0", "level1", "level2", "Bound"]) && brute2.Entries[^1] is { Custom: true, ColorShiftIndex: 130 }
            && brute2.Entries[2].Transforms[0].Get(0, 0) == 0.8 && brute2.Entries[2].Transforms[0].Get(5, 0) == 0.172549, "Variant entries, custom colour shifts and transform values are read");
        check(brute2.SharedWith.SequenceEqual(["brutelord (1 monstats row)", "hero (1 monstats row)"]), "Other units loading the same variant file are named with their monstats rows: " + string.Join(", ", brute2.SharedWith));
        var hero = HdAppearance.Resolve(project, [gameData], monstats[4], monstats);
        check(hero.Unit is { InProject: true } && hero.Variant is { InProject: false } && hero.DefaultEntry == null && hero.Issues.Any(i => i.Contains("TransLvl 9") && i.Contains("level9")),
            "A project unit wins over the game data, and a TransLvl without a levelN entry is a problem");
        check(HdAppearance.Resolve(project, [gameData], monstats[5], monstats).Issues.Single().Contains("No HD unit JSON for BaseId nobody"), "A BaseId with no unit JSON is a problem when game data is known");
        var loner = HdAppearance.Resolve(project, [gameData], Monster("loner", "loner", "0"), monstats);
        check(loner.Unit != null && loner.Variant == null && loner.Issues.Single().Contains("loner_variant.json"), "A unit naming a missing variant file is a problem");

        // Saving copies the base file into the project and rewrites only the changed numbers.
        var edited = brute2.Entries[2].Transforms.Select(t => t with { Values = [.. t.Values] }).ToArray();
        edited[1].Values[0] = 0.25; edited[2].Values[5 * 4 + 3] = 1;
        var written = HdAppearance.SaveEntry(project, brute2.Variant!, "level1", false, edited);
        var projectVariant = Path.Combine(project.Root, "data", "hd", "character", "enemy", "brute", "brute_variant.json");
        var expected = variantText.Replace(Entry("level1", 0.8), Entry("level1", 0.8).Replace($"\"name\":\"Green\",\"colorAdjustment\":{{\"x\":0.0", "\"name\":\"Green\",\"colorAdjustment\":{\"x\":0.25")
            .Replace("\"colorTint\":{\"x\":0.172549,\"y\":0.0,\"z\":0.0,\"w\":0.0}}]}", "\"colorTint\":{\"x\":0.172549,\"y\":0.0,\"z\":0.0,\"w\":1.0}}]}"));
        check(written == projectVariant && File.ReadAllText(projectVariant, Utf8) == expected, "Saving a base-game variant copies it into the project with only the edited numbers changed, still minified, 1 written as 1.0");
        check(File.ReadAllText(Path.Combine(gameData, "HD", "character", "enemy", "brute", "brute_variant.json"), Utf8) == variantText, "Saving never touches the game data");
        throws(() => HdAppearance.SaveEntry(project, brute2.Variant!, "level1", false, edited), "A second save from a stale base-game read does not overwrite the project copy");
        var again = HdAppearance.Resolve(project, [gameData], monstats[1], monstats);
        check(again.Variant is { InProject: true } && again.Entries[2].Transforms[1].Get(0, 0) == 0.25, "After saving, the project's variant file is the one resolved");
        File.AppendAllText(projectVariant, " ");
        throws(() => HdAppearance.SaveEntry(project, again.Variant!, "level1", false, edited), "Saving refuses a variant file that changed since it was read");
        var fresh = HdAppearance.Resolve(project, [gameData], monstats[1], monstats);
        var custom = fresh.Entries.Single(e => e.Custom).Transforms.Select(t => t with { Values = [.. t.Values] }).ToArray(); custom[0].Values[0] = -0.5;
        HdAppearance.SaveEntry(project, fresh.Variant!, "Bound", true, custom);
        check(HdAppearance.Resolve(project, [gameData], monstats[1], monstats).Entries.Single(e => e.Custom).Transforms[0].Get(0, 0) == -0.5, "Custom colour shift entries save by their map name");
        throws(() => HdAppearance.SaveEntry(project, fresh.Variant! with { Hash = Hash(File.ReadAllBytes(projectVariant)) }, "level1", false, edited.Take(2).ToArray()), "Saving refuses a transform count that does not match the entry");

        // Indented variant files stay indented with their line endings.
        var indented = Path.Combine(project.Root, "data", "hd", "character", "enemy", "loner", "loner_variant.json");
        Write(indented, JsonNode.Parse(variantText)!.ToJsonString(Pretty).Replace("\n", "\r\n") + "\r\n");
        var lonerVariant = HdAppearance.Resolve(project, [gameData], Monster("loner", "loner", "1"), monstats);
        HdAppearance.SaveEntry(project, lonerVariant.Variant!, "level1", false, edited);
        var indentedText = File.ReadAllText(indented, Utf8);
        check(indentedText.Contains("\r\n    \"entries\"") && indentedText.EndsWith("}\r\n") && !indentedText.Replace("\r\n", "").Contains('\n') && indentedText.Contains("\"x\": 0.25"), "An indented variant file keeps its indentation and CRLF endings");

        // Every vanilla variant file reads, and a save without changes reproduces its bytes.
        if (Environment.GetEnvironmentVariable("MODSTUDIO_BASE_EXCEL") is { Length: > 0 } excel && Path.GetFullPath(Path.Combine(excel, "..", "..")) is var vanilla && Directory.Exists(Path.Combine(vanilla, "hd")))
        {
            var sweep = new ModProject(Path.Combine(root, "appearance-sweep"), "sweep", "Sweep"); int files = 0, entries = 0;
            foreach (var file in Directory.EnumerateFiles(Path.Combine(vanilla, "hd", "character"), "*_variant.json", SearchOption.AllDirectories))
            {
                var relative = "data/" + Relative(vanilla, file);
                var found = HdAppearance.Find(sweep, [vanilla], relative)!;
                var read = HdAppearance.ReadEntries(JsonNode.Parse(File.ReadAllText(file, Utf8), null, Document.SourceJsonOptions)!);
                var first = read.First(e => e.Transforms.Length > 0);
                var copy = HdAppearance.SaveEntry(sweep, found, first.Name, first.Custom, first.Transforms);
                if (!File.ReadAllBytes(copy).SequenceEqual(File.ReadAllBytes(file))) throw new Exception("Unchanged save rewrote " + relative);
                files++; entries += read.Length;
            }
            check(files > 150 && entries > files * 8, $"Every vanilla variant file reads and saves unchanged byte for byte ({files} files, {entries} entries)");
        }
    }
}

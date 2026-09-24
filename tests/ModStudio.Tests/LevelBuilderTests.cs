using System.Text.Json.Nodes;
using ModStudio.Core;
using static ModStudio.Core.Storage;

/// <summary>Level builder: area levels per difficulty, spawn pools and their levels, links both ways, lookups and the issues it flags.</summary>
internal static class LevelBuilderTests
{
    public static void Run(string root, Action<bool, string> check, Action<Action, string> throws)
    {
        var project = new ModProject(Path.Combine(root, "level-builder"), "test", "Test"); Directory.CreateDirectory(project.Root);
        JsonObject Row(string id, params string[] fields) { var f = new JsonObject(); for (int i = 0; i < fields.Length; i += 2) f[fields[i]] = fields[i + 1]; return new() { ["sourceId"] = id, ["fields"] = f }; }
        void Table(string name, params JsonObject[] rows) => File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(project.Root, "source", "tables")).FullName, name + ".json"),
            new JsonObject { ["schema"] = new JsonObject { ["name"] = name }, ["records"] = new JsonArray(rows.Cast<JsonNode?>().ToArray()) }.ToJsonString(Pretty));
        Table("monstats", Row("z", "Id", "zombie1", "NameStr", "Zombie", "Level", "1", "Level(N)", "37", "Level(H)", "68"), Row("f", "Id", "fallen1", "NameStr", "Fallen", "Level", "2"), Row("c", "Id", "chicken", "NameStr", "Chicken"));
        Table("lvlwarp", Row("w0", "Name", "Cave Entrance", "Id", "0"), Row("w1", "Name", "Cave Exit", "Id", "1"));
        Table("lvltypes", Row("t0", "Name", "None", "Id", "0"), Row("t1", "Name", "Act 1 - Wilderness", "Id", "2"));
        Table("objgroup", Row("o0", "GroupName", "Not Used"), Row("o1", "GroupName", "Rogues"));
        Table("soundenviron", Row("s0", "Handle", "ESOUNDENVIRON_NONE"), Row("s1", "Handle", "ESOUNDENVIRON_WILD"));
        var strings = Directory.CreateDirectory(Path.Combine(project.Root, "source", "strings")).FullName;
        File.WriteAllText(Path.Combine(strings, "s.json"), new JsonObject { ["schema"] = new JsonObject { ["category"] = "s" }, ["records"] = new JsonArray(
            new[] { ("Zombie", "Zombie"), ("Fallen", "Fallen"), ("Chicken", "Chicken"), ("Blood Moor", "Blood Moor"), ("Den of Evil", "Den of Evil"), ("EnteringBloodMoor", "Entering Blood Moor"), ("ToBloodMoor", "To The Blood Moor") }
                .Select(x => (JsonNode)new JsonObject { ["Key"] = x.Item1, ["translations"] = new JsonObject { ["enUS"] = x.Item2 } }).ToArray()) }.ToJsonString(Pretty));

        LevelRow[] rows = [
            new(0, "town", "Act 1 - Town", "1", "0", "Rogue Encampment", "1 / 36 / 67", "0", "2,0,0,0,0,0,0,0"),
            new(1, "moor", "Act 1 - Wilderness 1", "2", "0", "Blood Moor", "1 / 36 / 67", "255", "1,8,0,0,0,0,0,0"),
            new(2, "den", "Act 1 - Cave 1", "8", "0", "Den of Evil", "1 / 36 / 67", "255", "0,0,0,0,0,0,0,0"),
            new(3, "cold", "Act 1 - Wilderness 2", "3", "0", "", "", "1", "2,0,0,0,0,0,0,0")];
        var resolver = new LevelBuilderResolver();
        LevelPreview Resolve(params string[] fields) => resolver.Resolve(project, Row("moor", fields), rows, "standard", "enUS", default);

        var moor = Resolve("Name", "Act 1 - Wilderness 1", "Id", "2", "LevelName", "Blood Moor", "LevelEntry", "EnteringBloodMoor", "LevelWarp", "ToBloodMoor",
            "MonLvl", "1", "MonLvlEx", "1", "MonLvl(N)", "30", "MonLvlEx(N)", "36", "MonLvlEx(H)", "67", "MonDen", "600", "MonDen(N)", "600", "MonDen(H)", "600", "MonUMin", "1", "MonUMax", "3", "SizeX", "80", "SizeY", "80",
            "NumMon", "2", "mon1", "zombie1", "mon2", "fallen1", "nmon1", "zombie1", "umon1", "fallen1", "cmon1", "chicken", "cpct1", "30", "camt1", "2",
            "Vis0", "1", "Warp0", "-1", "Vis1", "8", "Warp1", "0", "ObjGrp0", "1", "ObjPrb0", "25", "LevelType", "2", "DrlgType", "3", "SoundEnv", "1", "Waypoint", "255", "Teleport", "1");
        check(moor is { Title: "Blood Moor", Entry: "Entering Blood Moor", WarpText: "To The Blood Moor", Drlg: "Outdoor", LevelType: "Act 1 - Wilderness", Sound: "ESOUNDENVIRON_WILD", Waypoint: false, TownPortal: true },
            "A level shows its localized names, generation, type and sound");
        check(moor.Difficulties.Select(d => (d.AreaLevel, d.AreaLevelColumn)).SequenceEqual([(1, "MonLvlEx"), (36, "MonLvlEx(N)"), (67, "MonLvlEx(H)")]) && moor.Difficulties[0] is { Density: 600, UniqueMin: 1, UniqueMax: 3, SizeX: 80 },
            "Each difficulty's area level comes from MonLvlEx, with its density, unique packs and size");
        check(Resolve("MonLvl(N)", "30").Difficulties[1] is { AreaLevel: 30, AreaLevelColumn: "MonLvl(N)" }, "Without MonLvlEx the area level falls back to MonLvl");
        check(moor.Normal.Select(s => (s.Name, s.Levels[0])).SequenceEqual([("Zombie", 1), ("Fallen", 2)]) && moor.Nightmare.Single().Levels.SequenceEqual([36, 67]) && moor.Unique.Single().Name == "Fallen",
            "Normal monsters spawn at their own level; Nightmare and Hell ones at the area's");
        check(moor.Critters.Single() is { Name: "Chicken", Chance: 30, Amount: 2 } && moor.Objects.Single() is { Name: "Rogues", Chance: 25 }, "Critters and object groups resolve by id and row");
        check(moor.Links.Select(l => (l.Slot, l.Title, l.Back)).SequenceEqual([(0, "Rogue Encampment", true), (1, "Den of Evil", false)]) && moor.Links[1].WarpName == "Cave Entrance" && moor.Links[0].Waypoint,
            "Vis links name their level, their warp, and whether the level links back");
        check(moor.Inbound.Single().Name == "Act 1 - Wilderness 2", "Levels that link here without a link back are listed as inbound");
        check(moor.Issues.Length == 0 && moor.Links[0].Warp == "-1", "A link without a warp is walked across the border, not an issue: " + string.Join(" | ", moor.Issues));
        Table("lvlwarp", Row("w0", "Name", "Cave Entrance", "Id", "0"), Row("w1", "Name", "Cave Exit", "Id", "1"), Row("w2", "Name", "Later Copy", "Id", "0"));
        resolver.Clear();
        check(Resolve("Id", "2", "Vis0", "8", "Warp0", "0").Links.Single().WarpName == "Cave Entrance", "A repeated lvlwarp Id resolves to its first row, as in vanilla");
        check(!Resolve("Id", "0", "Warp0", "0").Issues.Any(i => i.StartsWith("Warp0", StringComparison.Ordinal)), "The Null level's warps are not flagged");

        var broken = Resolve("Id", "2", "mon1", "ghost", "Vis0", "99", "Warp0", "7", "Warp3", "1", "MonUMin", "4", "MonUMax", "2", "NumMon", "14", "MonDen(N)", "100", "ObjGrp0", "9", "Depend", "42", "MonLvl", "x");
        foreach (var expected in new[] { "mon1: no monstats row has Id ghost.", "Vis0: no level has Id 99.", "Warp0: no lvlwarp row has Id 7.", "Warp3 is 1 but Vis3 leads nowhere.", "Normal: MonUMin (4) is above MonUMax (2).",
            "NumMon is 14; the game picks at most 13 kinds of monster.", "MonDen spawns monsters in Nightmare and Hell but no nmon# slot names one.", "ObjGrp0: objgroup has no row 9.", "Depend: no level has Id 42.", "MonLvl is not a whole number: x." })
            check(broken.Issues.Contains(expected), "Level builder flags: " + expected);
        check(resolver.Resolve(project, Row("x", "Name", "Dup", "Id", "8"), [.. rows, new(4, "x", "Dup", "8", "0", "", "", "", "")], "standard", "enUS", default).Issues.Contains("Id 8 is used by more than one level."),
            "A duplicate level Id is flagged");

        var catalog = resolver.Catalog(project, "standard", "enUS", [.. rows, new(4, "blank", "", "", "", "", "", "", "")], default);
        check(catalog.Entries[1] is { Title: "Blood Moor", AreaLevels: "1 / 36 / 67", Waypoint: false } && catalog.Entries[0].Waypoint && catalog.Entries[4].Inactive
            && VisualBuilder.Matches(catalog.Entries[1].SearchText, "blood act 1"), "Levels are listed by localized name, area levels and waypoint");
        check(catalog.Levels.Length == 4 && catalog.Levels.Single(l => l.Id == "2").Vis.SequenceEqual(["1", "8"]) && catalog.Monsters.Length == 3 && catalog.Warps.Length == 2
            && catalog.ObjectGroups[1] is { Code: "1", Name: "Rogues" } && catalog.Sounds[1].Name == "ESOUNDENVIRON_WILD" && catalog.Types.Any(t => t.Code == "2"), "The catalog offers levels, monsters, warps, types, object groups and sounds");
        check(LevelBuilderResolver.AreaLevels(c => c switch { "MonLvl" => "2", "MonLvlEx(N)" => "40", "MonLvl(H)" => "60", _ => "" }) == "2 / 40 / 60", "Area levels read MonLvlEx, else MonLvl");
        check(LevelBuilderResolver.IsWaypoint("0") && !LevelBuilderResolver.IsWaypoint("255") && !LevelBuilderResolver.IsWaypoint(""), "A waypoint is a number below 255");

        // Outdoor borders are engine-generated, so ordinary Vis slots can all be empty.
        var outdoors = TableData.FromTsv(Utf8.GetBytes("Name\tId\tAct\tDrlgType\tLevelName\tVis0\n"
            + "Renamed forest\t5\t0\t3\tDark Wood\t10\nMarsh\t6\t0\t3\tBlack Marsh\t0\nHighland\t7\t0\t3\tTamoe Highland\t0\n"
            + "Passage\t10\t0\t1\tUnderground Passage\t5\nCustom forest\t150\t0\t3\tCustom forest\t6\n"
            + "Jungle\t76\t2\t3\tSpider Forest\t0\nSwamp\t77\t2\t3\tGreat Marsh\t0\nFlayers\t78\t2\t3\tFlayer Jungle\t0\n"
            + "Docks\t75\t2\t2\tKurast Docks\t0\nKurast\t79\t2\t3\tLower Kurast\t0\n"), "levels", "global/excel/levels.txt");
        var outdoorRows = LevelBuilderResolver.Rows(outdoors);
        LevelPreview OutdoorPreview(string id, string act = "0", string drlg = "3", string vis = "0", IReadOnlyList<LevelRow>? snapshot = null) =>
            resolver.Resolve(project, Row("outdoor", "Name", "Renamed area", "Id", id, "Act", act, "DrlgType", drlg, "Vis0", vis, "Warp0", "-1"), snapshot ?? outdoorRows, "standard", "enUS", default);
        var dark = OutdoorPreview("5", vis: "10");
        var marsh = OutdoorPreview("6");
        check(dark.OutdoorLinks.Single() is { Level.Id: "6", Level.Title: "Black Marsh", VariesBySeed: false }
            && marsh.OutdoorLinks.Select(l => l.Level.Id).SequenceEqual(["5", "7"]), "Dark Wood and Black Marsh have reciprocal outdoor navigation alongside Tamoe Highland");
        check(dark.Links.Single() is { Id: "10", Back: true } && marsh.Links.Length == 0 && marsh.Inbound.Single().Id == "150",
            "Generated borders preserve authored cave and inbound links without inventing Vis slots");
        check(OutdoorPreview("5", vis: "6").Links.Single().Back, "The outdoor border supplies a return path to a one-sided Vis link");
        check(OutdoorPreview("5", drlg: "1").OutdoorLinks.Length == 0 && OutdoorPreview("5", act: "1").OutdoorLinks.Length == 0
            && OutdoorPreview("150").OutdoorLinks.Length == 0, "Repurposed and custom level IDs do not inherit unsupported outdoor routes");
        check(OutdoorPreview("5", snapshot: outdoorRows.Where(r => r.Id != "6").ToArray()).OutdoorLinks.Length == 0,
            "Missing outdoor targets are not offered for navigation");
        check(OutdoorPreview("5", snapshot: outdoorRows.Select(r => r.Id == "6" ? r with { DrlgType = "1" } : r).ToArray()).OutdoorLinks.Length == 0,
            "Changing a neighbor's generation removes the inferred border");
        check(OutdoorPreview("5", snapshot: [.. outdoorRows, outdoorRows[1] with { Row = 10 }]).OutdoorLinks.Length == 0,
            "Ambiguous duplicate target IDs do not create inferred borders");
        var jungle = OutdoorPreview("76", act: "2");
        check(jungle.OutdoorLinks.Where(l => l.VariesBySeed).Select(l => l.Level.Id).SequenceEqual(["77", "78"])
            && jungle.OutdoorLinks.Single(l => !l.VariesBySeed).Level.Id == "75"
            && OutdoorPreview("78", act: "2").OutdoorLinks.Single(l => !l.VariesBySeed).Level.Id == "79",
            "Seed-dependent jungle borders are distinguished from the fixed town and Kurast borders");
    }
}

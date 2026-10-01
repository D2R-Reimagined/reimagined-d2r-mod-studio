using System.Text.Json.Nodes;
using ModStudio.Core;
using static ModStudio.Core.Storage;

/// <summary>World map: levels placed by offset, Depend and the game; overlaps; free space; new levels' rows, blank DS1 and minimal preset.</summary>
internal static class LevelWorldTests
{
    public static void Run(Action<bool, string> check, Action<Action, string> throws)
    {
        var table = TableData.FromTsv(Utf8.GetBytes("Name\tId\tAct\tDrlgType\tSizeX\tSizeY\tSizeX(N)\tSizeY(N)\tSizeX(H)\tSizeY(H)\tOffsetX\tOffsetY\tDepend\tVis0\tWarp0\tVis1\tWarp1\tLevelType\tmon1\tLevelName\tWaypoint\t*Comment\n"
            + "Null\t0\t0\t0\t0\t0\t0\t0\t0\t0\t0\t0\t0\t0\t0\t0\t0\t\t\t\t\t\n"
            + "Act 1 - Town\t1\t0\t2\t56\t40\t56\t40\t56\t40\t-1\t-1\t0\t2\t-1\t0\t-1\t1\t\tRogue Encampment\t0\t\n"
            + "Act 1 - Wilderness 2\t3\t0\t3\t80\t80\t80\t80\t80\t80\t1000\t1000\t0\t0\t-1\t0\t-1\t2\t\t\t\t\n"
            + "Act 1 - Wilderness 3\t4\t0\t3\t80\t80\t80\t80\t80\t80\t1000\t1000\t0\t0\t-1\t0\t-1\t2\t\t\t\t\n"
            + "Act 1 - Cave 1\t8\t0\t1\t200\t200\t200\t200\t200\t200\t1500\t1000\t0\t13\t4\t0\t-1\t3\tfallen1\tDen of Evil\t255\tnote\n"
            + "Act 1 - Cave 2 Treasure\t13\t0\t2\t24\t24\t24\t24\t24\t24\t1500\t2500\t0\t8\t5\t0\t-1\t3\t\t\t255\t\n"
            + "Act 1 - Monastery\t26\t0\t2\t64\t18\t64\t18\t64\t18\t3000\t1000\t0\t27\t-1\t0\t-1\t5\t\t\t\t\n"
            + "Act 1 - Courtyard 1\t27\t0\t2\t56\t40\t56\t40\t56\t40\t0\t-40\t26\t0\t-1\t0\t-1\t6\t\t\t\t\n"
            + "Act 1 - Overlap\t40\t0\t2\t10\t10\t10\t10\t30\t30\t1480\t1180\t0\t0\t-1\t0\t-1\t3\t\t\t\t\n"
            + "Act 2 - Town\t41\t1\t2\t56\t56\t56\t56\t56\t56\t1000\t1000\t0\t0\t-1\t0\t-1\t13\t\t\t\t\n"
            + "Expansion\t\t\t\t\t\t\t\t\t\t\t\t\t\t\t\t\t\t\t\t\t\n"), "levels", "global/excel/levels.txt");
        var rows = LevelWorld.Read(table);
        var normal = LevelWorld.Layout(rows, 0, 0);
        check(normal.Find("8") is { X: 1500, Y: 1000, Width: 200, Height: 200, Placement: WorldPlacement.Fixed } && normal.Find("13") is { Y: 2500, Width: 24 },
            "Levels are placed at their offset with their size, in tiles");
        check(normal.Find("27") is { X: 3000, Y: 960, Width: 56, Height: 40, Placement: WorldPlacement.Relative }, "A Depend level's offset is from its parent's corner (Courtyard sits above the Monastery)");
        check(normal.Unplaced.Single(u => u.Source.Id == "1") is { Placement: WorldPlacement.Generated } && normal.Find("1") == null && normal.Find("0") == null && normal.Placed.All(l => l.Source.Act == 0),
            "Offset -1, -1 is placed by the game; the Null level and other acts are left out");
        check(normal.Find("3") is { Placement: WorldPlacement.Outdoor } && !normal.Overlaps.Any(o => o.A is "3" or "4" && o.B is "3" or "4"),
            "Outdoor levels share the generator's strip without being flagged as overlapping each other");
        check(normal.Overlaps.Length == 0 && LevelWorld.Layout(rows, 0, 2).Overlaps.Single() is ("8", "40"), "Overlaps are found per difficulty (Hell's bigger size reaches into the cave)");
        check(normal.Links.Any(l => l is { From: "8", To: "13", Warp: "4", Back: true }) && normal.Links.Any(l => l is { From: "26", To: "27", Back: false }),
            "Links within the act say whether they lead back");
        check(normal.Bounds() == (1000, 960, 2064, 1564), "The act's bounds cover every placed level");
        check(LevelWorld.Collisions(rows, 0, 1690, 1100, 10, 10).Single() == "Act 1 - Cave 1" && LevelWorld.Collisions(rows, 0, 1505, 1185, 5, 5).Length == 2
            && LevelWorld.Collisions(rows, 0, 1470, 1205, 15, 10).Single() == "Act 1 - Overlap" && LevelWorld.Collisions(rows, 0, 1500, 2524, 24, 24).Length == 0,
            "Free space is checked in every difficulty; levels may touch edge to edge");
        var cycle = LevelWorld.Read(TableData.FromTsv(Utf8.GetBytes("Name\tId\tAct\tSizeX\tSizeY\tSizeX(N)\tSizeY(N)\tSizeX(H)\tSizeY(H)\tOffsetX\tOffsetY\tDepend\nA\t1\t0\t5\t5\t5\t5\t5\t5\t0\t0\t2\nB\t2\t0\t5\t5\t5\t5\t5\t5\t0\t0\t1\n"), "levels", "global/excel/levels.txt"));
        check(LevelWorld.Layout(cycle, 0, 0) is { Placed.Length: 0, Unplaced.Length: 2 }, "A Depend loop leaves the levels unplaced instead of hanging");

        check(LevelWorld.NextId(table) == 42, "New levels take the Id after the highest");
        var fields = LevelWorld.LevelFields(table, new("Act 1 - Crypt X", 0, 2, 2000, 3000, 30, 20), 42, templateRow: 4);
        check(fields.S("Name") == "Act 1 - Crypt X" && fields.S("Id") == "42" && fields.S("DrlgType") == "2" && fields.S("SizeX(N)") == "30" && fields.S("SizeY(H)") == "20"
            && fields.S("OffsetX") == "2000" && fields.S("OffsetY") == "3000" && fields.S("Depend") == "0", "A new level is named, numbered, sized in every difficulty and placed from the rectangle");
        check(fields.S("mon1") == "fallen1" && fields.S("LevelType") == "3" && fields.S("LevelName") == "" && fields.S("Vis0") == "0" && fields.S("Warp0") == "-1" && fields.S("Waypoint") == "255" && fields["*Comment"] == null,
            "Copying a template keeps its monsters and tiles but not its names, links, waypoint or comments");
        check(LevelWorld.LevelFields(table, new("Blank", 1, 1, 0, 0, 5, 5), 42).S("mon1") == "" && LevelWorld.DefaultFields(table, 7).S("Warp1") == "-1", "Without a template a new level starts from the defaults");

        var presets = TableData.FromTsv(Utf8.GetBytes("Name\tDef\tLevelId\tPopulate\tLogicals\tOutdoors\tAnimate\tKillEdge\tFillBlanks\tSizeX\tSizeY\tAutoMap\tScan\tPops\tPopPad\tFiles\tFile1\tFile2\tFile3\tFile4\tFile5\tFile6\tDt1Mask\n"
            + "Act 1 - Cave 2 Treasure\t40\t13\t1\t0\t0\t0\t1\t1\t0\t0\t0\t1\t0\t0\t1\tAct1/Caves/CaveRoom2.ds1\t0\t0\t0\t0\t0\t1\n"), "lvlprest", "global/excel/lvlprest.txt");
        var preset = LevelWorld.PresetFields(presets, "Act 1 - Crypt X", 42, "Act1/Caves/Custom/crypt_x.ds1", 0);
        check(preset.S("Def") == "41" && preset.S("LevelId") == "42" && preset.S("File1") == "Act1/Caves/Custom/crypt_x.ds1" && preset.S("File2") == "0" && preset.S("Files") == "1"
            && preset.S("SizeX") == "0" && preset.S("KillEdge") == "1" && preset.S("Dt1Mask") == "1" && preset.S("Scan") == "1", "A preset row names the map, takes the next Def and copies the template's room flags and DT1 mask");

        var ds1 = LevelWorld.BlankDs1(30, 20, 0);
        check(LevelWorld.Ds1Size(ds1) == (30, 20, 0) && ds1.Length == 32 + 31 * 21 * 16 + 8, "A blank DS1 stores the level's size in its header and holds one more cell each way");
        throws(() => LevelWorld.BlankDs1(0, 5, 0), "A blank map needs a size");
        var json = JsonNode.Parse(LevelWorld.MinimalPreset("Crypt X", "data/hd/env/biome/act1_crypt.json"))!;
        check(json.S("type") == "Preset" && json.S("biomeFilename") == "data/hd/env/biome/act1_crypt.json" && json["dependencies"]!["json"]![0].S("path") == "data/hd/env/biome/act1_crypt.json"
            && json["entities"] is JsonArray { Count: 0 } && json["specialTiles"] is JsonObject, "A minimal preset has no entities and depends on its biome");
        check(LevelWorld.Biome(LevelWorld.MinimalPreset("x", "b.json")) == "b.json" && LevelWorld.Biome(Utf8.GetBytes("not json")) == "", "A preset's biome is read, or empty");
        check(LevelWorld.PresetFor("Act1/Caves/CaveRoom2.ds1") == "hd/env/preset/act1/caves/caveroom2.json" && LevelWorld.Slug("Act 1 - Crypt X!") == "act_1_crypt_x",
            "Maps pair with lower-case HD presets; names make file stems");
        check(LevelWorld.MapPath(@"Act1\Custom\a.ds1") == "Act1/Custom/a.ds1", "Map paths use forward slashes");
        throws(() => LevelWorld.MapPath("../a.ds1"), "Map paths cannot leave global/tiles");
        throws(() => LevelWorld.MapPath("Act1/a.json"), "Maps are DS1 files");

        // Vanilla's layout has no overlaps once outdoor generation is accounted for.
        if (Environment.GetEnvironmentVariable("MODSTUDIO_BASE_EXCEL") is { Length: > 0 } excel && File.Exists(Path.Combine(excel, "levels.txt")))
        {
            var vanilla = LevelWorld.Read(TableData.FromTsv(File.ReadAllBytes(Path.Combine(excel, "levels.txt")), "levels", "global/excel/levels.txt"));
            for (int act = 0; act < 5; act++)
                for (int difficulty = 0; difficulty < 3; difficulty++)
                {
                    var layout = LevelWorld.Layout(vanilla, act, difficulty);
                    check(layout.Overlaps.Length == 0 && layout.Placed.Length > 1, $"Vanilla act {act + 1} ({difficulty}) lays out without overlaps: " + string.Join(", ", layout.Overlaps));
                }
            check(LevelWorld.Layout(vanilla, 0, 0).Find("33") is { X: 3996, Y: 966 }, "Vanilla's Cathedral sits relative to Courtyard 2");
        }
    }
}

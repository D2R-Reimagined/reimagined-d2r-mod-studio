using System.Text.Json.Nodes;
using ModStudio.Core;
using static ModStudio.Core.Storage;

/// <summary>Monster builder: COF layouts, DC6 parts, monstats2 looks, assembling a composite from project and game files, and drawing it.</summary>
internal static class MonsterBuilderTests
{
    /// <summary>
    /// A two-layer, one-direction, two-frame COF: the head drawn normally and the torso added as light, the torso on top in
    /// frame 1 and under the head in frame 2; speed 128 (half a frame per game frame); the attack lands on frame 2.
    /// </summary>
    private static byte[] SyntheticCof()
    {
        var bytes = new List<byte> { 2, 2, 1 };
        bytes.AddRange(new byte[21]); bytes.AddRange(BitConverter.GetBytes(128));
        bytes.AddRange([0, 1, 0, 0, 0, (byte)'h', (byte)'t', (byte)'h', 0]);
        bytes.AddRange([1, 1, 0, 1, 4, (byte)'h', (byte)'t', (byte)'h', 0]);
        bytes.AddRange([0, 1]);          // events
        bytes.AddRange([0, 1, 1, 0]);    // draw order: frame 1 head then torso, frame 2 torso then head
        return [.. bytes];
    }

    /// <summary>A one-frame DC6 whose 2 × 2 frame sits one left of and one above its origin: bottom row 5, 6; top row clear, 7.</summary>
    private static byte[] SyntheticDc6()
    {
        var frame = new List<byte>();
        foreach (var value in new[] { 0, 2, 2, -1, 0, 0, 0 }) frame.AddRange(BitConverter.GetBytes(value));
        byte[] pixels = [2, 5, 6, 0x80, 0x81, 1, 7, 0x80];
        frame.AddRange(BitConverter.GetBytes(pixels.Length)); frame.AddRange(pixels);
        var file = new List<byte>();
        foreach (var value in new[] { 6, 1, 0, -1, 1, 1, 28 }) file.AddRange(BitConverter.GetBytes(value));
        file.AddRange(frame);
        return [.. file];
    }

    public static void Run(string root, Action<bool, string> check, Action<Action, string> throws)
    {
        var cof = Cof.Read(SyntheticCof());
        check(cof is { Directions: 1, FramesPerDirection: 2, Speed: 128 } && cof.Layers.Select(l => l.ComponentCode).SequenceEqual(["HD", "TR"]) && cof.Layers[1] is { Transparent: true, Blend: Blend.Add, WeaponClass: "hth" }
            && cof.Order[0][0].SequenceEqual([0, 1]) && cof.Order[0][1].SequenceEqual([1, 0]) && cof.Events.SequenceEqual(new byte[] { 0, 1 }),
            "A COF names its layers, weapon class, blend, speed, events and per-frame draw order");
        throws(() => Cof.Read([2, 2, 1]), "A truncated COF is refused");

        var dc6 = new LegacyAnimation(SyntheticDc6()).Direction(0);
        check(dc6 is { OriginX: 1, OriginY: 1 } && dc6.Frames[0] is { Width: 2, Height: 2 } && dc6.Frames[0].Indices.SequenceEqual(new byte[] { 0, 7, 5, 6 }),
            "DC6 parts are read bottom-up and placed by their offsets like DCC frames: " + string.Join(",", dc6.Frames[0].Indices));
        var dcc = new LegacyAnimation(MissileBuilderTests.SyntheticDcc());
        check(dcc is { DirectionCount: 1, FramesPerDirection: 1 } && dcc.Direction(0).Frames[0].Indices.SequenceEqual(new byte[] { 37, 0, 0, 37 }) && dcc.Retained > 0, "DCC parts decode a direction when it is first drawn");

        var cells = new Dictionary<string, string> { ["HDv"] = "\"lit,med\"", ["Rav"] = "hvy", ["TRv"] = "", ["HD"] = "1", ["TR"] = "1", ["mNU"] = "1", ["mWL"] = "1", ["mA1"] = "0",
            ["BaseW"] = "1hs", ["Shadow"] = "1", ["Light"] = "3", ["light-r"] = "200" };
        var look = MonsterGraphics.Look("TT", 2, c => cells.GetValueOrDefault(c, ""));
        check(look is { Token: "TT", BaseWeapon: "1hs", TransLvl: 2, Shadow: true, Light: 3, Red: 200, Green: 255, LookCount: 2 } && look.Variants["HD"].SequenceEqual(["lit", "med"])
            && look.Variants["RA"].SequenceEqual(["hvy"]) && look.Parts.SetEquals(["HD", "TR"]) && look.Modes.SequenceEqual(["NU", "WL"]),
            "A monstats2 row gives each part's variants (quoted or not, Rav/Lav spelt as they are), the parts, modes, weapon class, shadow and light");

        // Game data: the COF and two parts. The head has no "lit" file, so it falls back to "med". palshift table 4 (TransLvl 2) maps 37 to 55.
        var project = new ModProject(Path.Combine(root, "monster-builder"), "test", "Test"); Directory.CreateDirectory(project.Root);
        var gameData = Path.Combine(root, "monster-builder-game", "data");
        void Write(string relative, byte[] bytes) { var file = Path.Combine(gameData, relative); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllBytes(file, bytes); }
        Write("global/monsters/tt/cof/ttnuhth.cof", SyntheticCof());
        Write("global/monsters/TT/HD/tthdmednuhth.dcc", MissileBuilderTests.SyntheticDcc(37));
        Write("global/monsters/tt/tr/tttrlitnuhth.dcc", MissileBuilderTests.SyntheticDcc(99));
        var shift = Enumerable.Range(0, 8 * 256).Select(i => (byte)(i % 256)).ToArray(); shift[4 * 256 + 37] = 55;
        Write("global/monsters/tt/cof/palshift.dat", shift);
        var none = MonsterGraphics.Composite(project, [], look, "NU", 0);
        check(!none.Drawable && none.Notes.Single().Contains("game data folder"), "Without game data a base-game monster asks for the game data folder");
        var composite = MonsterGraphics.Composite(project, [gameData], look, "NU", 0);
        check(composite is { Drawable: true, CofFile.InProject: false } && composite.Layers.Select(l => l.Variant).SequenceEqual(["med", "lit"]) && composite.Layers.All(l => l.Animation != null) && composite.Shift?[37] == 55,
            "A composite takes the BaseW-less COF, each part's first variant that has a file, and the TransLvl colour shift: " + string.Join(" ", composite.Notes));
        check(MonsterGraphics.Composite(project, [gameData], look, "WL", 0) is { Drawable: false } walk && walk.Notes.Single().Contains("ttwl1hs.cof"), "A mode with no COF names the file looked for");
        check(MonsterGraphics.Shift(project, [gameData], "tt", 0) == null && MonsterGraphics.Shift(project, [gameData], "tt", 9) == null, "TransLvl 0, or past the file's tables, leaves the colours alone");
        check(MonsterGraphics.Tokens(project, [gameData]).SequenceEqual(["TT"]), "Graphics tokens are the monster folders, upper case");
        // The project's own part wins over the game's.
        var own = Path.Combine(project.Root, "data", "global", "monsters", "tt", "hd", "tthdmednuhth.dcc"); Directory.CreateDirectory(Path.GetDirectoryName(own)!); File.WriteAllBytes(own, MissileBuilderTests.SyntheticDcc(12));
        check(MonsterGraphics.Composite(project, [gameData], look, "NU", 0).Layers[0] is { File.InProject: true }, "The project's part wins over the game data");
        File.Delete(own);

        // Drawing: palette 55 is (10, 20, 30) and 99 is (40, 40, 40), as B,G,R.
        var palette = new byte[768]; palette[55 * 3] = 30; palette[55 * 3 + 1] = 20; palette[55 * 3 + 2] = 10; palette[99 * 3] = 40; palette[99 * 3 + 1] = 40; palette[99 * 3 + 2] = 40;
        var scene = new MonsterScene(100, 100);
        var flat = look with { Shadow = false, Light = 0 };
        int Pixel(int channel) { var (x, y) = scene.Anchor; return scene.Pixels[(((int)y - 1) * 100 + (int)x - 1) * 4 + channel]; }
        var first = scene.Render(0, 0, composite, flat, palette, 0, false);
        check(first is { Frame: 0, Frames: 2, Event: 0 } && Pixel(0) == 50 && Pixel(2) == 70, "In frame 1 the torso is added over the recoloured head, as its COF orders and blends it");
        var second = scene.Render(2, 0, composite, flat, palette, 0, false);
        check(second is { Frame: 1, Event: 1 } && Pixel(0) == 10 && Pixel(2) == 30, "In frame 2 the head is drawn over the torso; the attack lands on it");
        check(MonsterScene.FrameAt(4, cof, false) == 0 && MonsterScene.FrameAt(9, cof, true) == 1, "Speed 128 plays half a frame per game frame; a death holds its last frame");
        check(scene.Render(10, 48, composite, flat, palette, 3, false).Distance == 3 * MonsterScene.PixelsPerVelocity * 10, "A moving mode slides the floor at the monster's velocity");

        JsonObject Row(string id, params string[] fields) { var f = new JsonObject(); for (int i = 0; i < fields.Length; i += 2) f[fields[i]] = fields[i + 1]; return new() { ["sourceId"] = id, ["fields"] = f }; }
        void Table(string name, params JsonObject[] rows) => File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(project.Root, "source", "tables")).FullName, name + ".json"),
            new JsonObject { ["schema"] = new JsonObject { ["name"] = name }, ["records"] = new JsonArray(rows.Cast<JsonNode?>().ToArray()) }.ToJsonString(Pretty));
        Table("montype", Row("a", "type", "undead")); Table("treasureclassex", Row("t", "Treasure Class", "Act 1 H2H A")); Table("monstats2", Row("l", "Id", "ttlook"));
        var strings = Path.Combine(project.Root, "source", "strings"); Directory.CreateDirectory(strings);
        File.WriteAllText(Path.Combine(strings, "names.json"), new JsonObject { ["schema"] = new JsonObject { ["category"] = "names" }, ["records"] = new JsonArray(new JsonObject { ["Key"] = "tt", ["translations"] = new JsonObject { ["enUS"] = "Test Terror" } }) }.ToJsonString(Pretty));
        var catalog = new MonsterBuilderResolver().Catalog(project, "standard", "enUS", [gameData], [new(0, "r0", "ttmon", "tt", "TT", "undead", "5/40/70"), new(1, "r1", "", "", "", "", "")], default);
        check(catalog.Entries[0] is { Name: "Test Terror", Inactive: false } && catalog.Entries[1].Inactive && VisualBuilder.Matches(catalog.Entries[0].SearchText, "terror tt")
            && catalog.MonTypes.SequenceEqual(["undead"]) && catalog.TreasureClasses.SequenceEqual(["Act 1 H2H A"]) && catalog.Looks.SequenceEqual(["ttlook"]) && catalog.Tokens.SequenceEqual(["TT"]),
            "The monster catalog localizes names and offers types, treasure classes, looks and graphics tokens");

        // With extracted game data, every vanilla monster COF reads.
        if (Environment.GetEnvironmentVariable("MODSTUDIO_BASE_EXCEL") is { Length: > 0 } excel && Path.GetFullPath(Path.Combine(excel, "..", "..", "global", "monsters")) is var monsters && Directory.Exists(monsters))
        {
            var files = Directory.EnumerateFiles(monsters, "*.cof", SearchOption.AllDirectories).ToArray(); var failures = new List<string>();
            foreach (var file in files) { try { Cof.Read(File.ReadAllBytes(file)); } catch (Exception e) { failures.Add(Path.GetFileName(file) + ": " + e.Message); } }
            check(files.Length > 1000 && failures.Count == 0, $"Every vanilla monster COF reads ({files.Length} files): " + string.Join("; ", failures.Take(5)));
        }
    }
}

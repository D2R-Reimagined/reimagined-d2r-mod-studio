using System.Text.Json.Nodes;
using ModStudio.Core;
using static ModStudio.Core.Storage;

/// <summary>Missile builder: the DCC decoder, facing to direction, flight timing, the scene renderer, and where missile graphics and HD definitions are found.</summary>
internal static class MissileBuilderTests
{
    /// <summary>Writes bits least significant first, as DCC streams are read.</summary>
    private sealed class BitWriter
    {
        private readonly List<bool> bits = [];
        public void Put(long value, int count) { for (int i = 0; i < count; i++) bits.Add(((value >> i) & 1) != 0); }
        public byte[] Bytes() { var bytes = new byte[(bits.Count + 7) / 8]; for (int i = 0; i < bits.Count; i++) if (bits[i]) bytes[i / 8] |= (byte)(1 << (i % 8)); return bytes; }
    }

    /// <summary>
    /// A one-direction, one-frame DCC: a 2 × 2 frame at x -1, y 0 whose one cell draws palette entries 37 and 0 as a
    /// diagonal. No equal-cell, encoding-type or raw streams; the pixel codes stream carries the cell's palette (37, then a
    /// repeat that ends the list) and then one bit per pixel.
    /// </summary>
    public static byte[] SyntheticDcc(byte color = 37)
    {
        var direction = new BitWriter();
        direction.Put(0, 32);                           // coded size
        direction.Put(0, 2);                            // compression: no equal cells, no raw pixels
        direction.Put(0, 4);                            // variable0: 0 bits
        foreach (var _ in Enumerable.Range(0, 4)) direction.Put(3, 4); // width, height, x, y: 4 bits each
        direction.Put(0, 4); direction.Put(0, 4);       // optional data, coded bytes: 0 bits
        direction.Put(2, 4); direction.Put(2, 4);       // frame: width 2, height 2
        direction.Put(-1 & 0xF, 4); direction.Put(0, 4); // x -1, y 0
        direction.Put(0, 1);                            // top-down
        direction.Put(0, 20);                           // pixel mask stream size
        for (int i = 0; i < 256; i++) direction.Put(i == 0 || i == color ? 1 : 0, 1); // palette key: entries 0 and color
        direction.Put(1, 4); direction.Put(0, 4);       // pixel codes: key index 1, then a repeat that ends the list
        foreach (var bit in new[] { 0, 1, 1, 0 }) direction.Put(bit, 1); // pixels: color, clear, clear, color
        var body = direction.Bytes();
        var file = new List<byte> { 0x74, 6, 1 };
        file.AddRange(BitConverter.GetBytes(1)); file.AddRange(BitConverter.GetBytes(1)); file.AddRange(BitConverter.GetBytes(19 + body.Length)); file.AddRange(BitConverter.GetBytes(19));
        file.AddRange(body);
        return [.. file];
    }

    public static void Run(string root, Action<bool, string> check, Action<Action, string> throws)
    {
        var animation = Dcc.Decode(SyntheticDcc());
        var decoded = animation.Directions.Single();
        check(animation.FramesPerDirection == 1 && decoded is { OriginX: 1, OriginY: 1 } && decoded.Frames[0] is { Width: 2, Height: 2 } && decoded.Frames[0].Indices.SequenceEqual(new byte[] { 37, 0, 0, 37 }),
            "A DCC frame is rebuilt from its cell palette and pixel codes, placed by its offset: " + string.Join(",", decoded.Frames[0].Indices));
        throws(() => Dcc.Decode([1, 2, 3]), "Data that is not a DCC is refused");
        var truncated = SyntheticDcc(); throws(() => Dcc.Decode(truncated[..30]), "A truncated DCC bitstream is refused, not read past its end");

        check(Dcc.Direction(0, 8) == 4 && Dcc.Direction(8, 8) == 0 && Dcc.Direction(16, 8) == 5 && Dcc.Direction(32, 8) == 6 && Dcc.Direction(48, 8) == 7 && Dcc.Direction(40, 8) == 2,
            "Facing steps pick the game's 8-direction order (0 SW, 4 S, 5 W, 6 N, 7 E)");
        check(Dcc.Direction(4, 16) == 8 && Dcc.Direction(2, 32) == 16 && Dcc.Direction(10, 1) == 0 && Dcc.Direction(63, 8) == 4 && Dcc.Direction(24, 4) == 1,
            "16- and 32-direction files add the in-between facings; single-direction files always draw direction 0");

        var motion = MissileMotion.Read(c => c switch { "Vel" => "10", "Accel" => "5", "MaxVel" => "20", "Range" => "10", "AnimSpeed" => "8", "LoopAnim" => "1", "InitSteps" => "2", _ => "" });
        check(motion.Distance(1) == 10 && motion.Distance(3) == 10 + 15 + 20 && motion.Distance(4) == 65, "Velocity grows by Accel each frame up to MaxVel");
        check(motion.Frame(1, 4) == -1 && motion.Frame(2, 4) == 0 && motion.Frame(4, 4) == 1 && motion.Frame(12, 4) == 1, "InitSteps hides the missile; AnimSpeed 8 plays half a frame per game frame, looping");
        var once = motion with { Loop = false };
        check(once.Frame(9, 4) == 3 && once.Frame(10, 4) == -1 && once.PlayLength(4) == 8, "A non-looping animation stops drawing once it has played");
        var sub = motion with { SubLoop = true, SubStart = 1, SubStop = 2, FramesPerTick = 1, InitSteps = 0 };
        check(new[] { 0, 1, 2, 3, 4 }.Select(t => sub.Frame(t, 4)).SequenceEqual([0, 1, 2, 1, 2]), "A sub-loop repeats its frames once the animation reaches them");
        var slow = MissileMotion.Read(c => c switch { "Vel" => "10", "Accel" => "-4", "Range" => "10", _ => "" });
        check(slow.Distance(5) == 10 + 6 + 2 && slow.FramesPerTick == 1 && slow.Red == 255, "Deceleration stops at zero; blank AnimSpeed and colours default to 16 and white");

        // Palette entry 37 is (10, 20, 30) as the game stores it: blue, green, red.
        var palette = new byte[768]; palette[37 * 3] = 30; palette[37 * 3 + 1] = 20; palette[37 * 3 + 2] = 10;
        var scene = new MissileScene(200, 100);
        var flight = MissileMotion.Read(c => c switch { "Vel" => "4", "Range" => "5", "Trans" => "0", "LoopAnim" => "1", _ => "" });
        var sprite = new SceneSprite(animation, palette, flight);
        // Three frames in, clear of the caster's marker; the frame's top-left pixel sits one left of and one above the missile.
        var state = scene.Render(3, 48, flight, sprite, null);
        int Pixel(double x, double y, int channel) => scene.Pixels[((int)Math.Round(y) * 200 + (int)Math.Round(x)) * 4 + channel];
        check(state is { Stage: SceneStage.Flight, Frame: 0 } && Pixel(state.X - 1, state.Y - 1, 0) == 10 && Pixel(state.X - 1, state.Y - 1, 2) == 30, "Normal blending draws the palette colour where the frame's origin puts it");
        var additive = flight with { Trans = 1 };
        state = scene.Render(3, 48, additive, sprite with { Motion = additive }, null);
        check(Pixel(state.X - 1, state.Y - 1, 0) > 10 && Pixel(state.X, state.Y - 1, 0) < 60, "Trans 1 adds the colour to the floor instead of covering it");
        var later = scene.Render(4, 48, flight, sprite, null);
        check(Math.Abs(later.X - state.X - 4) < 0.01 && Math.Abs(later.Y - state.Y) < 0.01 && later.Distance == 16, "Facing east moves the missile right by its velocity each frame");
        check(scene.Render(5, 48, flight, sprite, null).Stage == SceneStage.Pause && MissileScene.Period(flight, null) == 5 + MissileScene.Pause, "The flight ends at its range and pauses before looping");
        var boom = new SceneSprite(animation, palette, MissileMotion.Read(c => c == "AnimSpeed" ? "8" : ""));
        check(scene.Render(5, 48, flight, sprite, boom) is { Stage: SceneStage.Explosion, Frame: 0 } && scene.Render(6, 48, flight, sprite, boom).Stage == SceneStage.Explosion && MissileScene.Period(flight, boom) == 5 + 2 + MissileScene.Pause,
            "The explosion missile plays where the flight ends, at its own speed");
        var far = MissileMotion.Read(c => c switch { "Vel" => "40", "Range" => "40", _ => "" });
        var end = scene.Render(39, 48, far, null, null);
        check(end.X is > 0 and < 200 && end.Distance == 39 * 40, "A flight longer than the view is followed by the camera, so its end stays in sight");
        var lit = MissileMotion.Read(c => c switch { "Vel" => "0", "Range" => "5", "Light" => "3", "Red" => "255", "Green" => "0", "Blue" => "0", _ => "" });
        state = scene.Render(0, 48, lit, null, null);
        check(Pixel(state.X, state.Y, 0) > Pixel(state.X, state.Y, 1) + 50 && Pixel(state.X + 90, state.Y, 0) < 50, "The light radius tints the floor in its colour around the missile");
        check(scene.FacingToward(190, 50) == 48 && scene.FacingToward(100, 90) == 0 && scene.FacingToward(10, 50) == 16, "Clicking right, below or left of centre aims east, south or west");

        // Where missile graphics and HD definitions come from.
        var project = new ModProject(Path.Combine(root, "missile-builder"), "test", "Test"); Directory.CreateDirectory(project.Root);
        var gameData = Path.Combine(root, "missile-builder-game", "data");
        void Write(string file, byte[] bytes) { Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllBytes(file, bytes); }
        void WriteText(string file, string text) => Write(file, Utf8.GetBytes(text));
        Write(Path.Combine(gameData, "Global", "Missiles", "Expansion", "SparkBall.dcc"), SyntheticDcc());
        Write(Path.Combine(gameData, "global", "palette", "act2", "pal.dat"), palette);
        var none = MissileGraphics.Art(project, [], "SparkBall");
        check(none.Animation == null && none.Notes.Single().Contains("game data folder"), "Without game data a base-game animation asks for the game data folder");
        var found = MissileGraphics.Art(project, [gameData], "sparkball");
        check(found is { Animation.FramesPerDirection: 1, File.InProject: false } && found.File.Relative.Contains("expansion"), "Animations are found in the expansion folder, ignoring case");
        check(MissileGraphics.Art(project, [gameData], "null") is { Invisible: true, Animation: null } && MissileGraphics.Art(project, [gameData], "Missing").Notes.Single().Contains("Missing.dcc"),
            "An empty CelFile draws nothing; a missing one names the file looked for");
        Write(Path.Combine(project.Root, "data", "global", "missiles", "SparkBall.dcc"), SyntheticDcc(99));
        check(MissileGraphics.Art(project, [gameData], "SparkBall") is { File.InProject: true, Animation: { } own } && own.Directions[0].Frames[0].Indices[0] == 99, "The project's animation wins over the game's");
        check(MissileGraphics.Palette(project, [gameData], 2)?[37 * 3 + 2] == 10 && MissileGraphics.Palette(project, [gameData], 3) == null, "Act palettes are read from the game data");
        check(MissileGraphics.CelFiles(project, [gameData]).SequenceEqual(["SparkBall"], StringComparer.OrdinalIgnoreCase), "Animation choices list every DCC once, project and game data together");
        WriteText(Path.Combine(gameData, "hd", "missiles", "missiles.json"), "{ \"dependencies\": {}, \"spark_ball\": \"spark_ball_hd\" }");
        WriteText(Path.Combine(gameData, "hd", "missiles", "spark_ball_hd.json"), "{ \"dependencies\": { \"particles\": [ { \"path\": \"data/hd/vfx/particles/missiles/spark/spark.particles\" } ], \"models\": [], \"textures\": [ { \"path\": \"data/hd/vfx/textures/glow.texture\" } ] } }");
        var hd = MissileGraphics.Hd(project, [gameData], "sparkball");
        check(hd is { Unit: "spark_ball_hd", File.InProject: false } && hd.Particles.Single().EndsWith("spark.particles") && hd.Textures.Length == 1 && hd.Models.Length == 0, "missiles.json names the HD unit, whose dependencies are listed");
        check(MissileGraphics.Hd(project, [gameData], "other").Notes.Single().Contains("no entry"), "A missile HD does not list is said to draw nothing in HD");

        JsonObject Row(string id, params string[] fields) { var f = new JsonObject(); for (int i = 0; i < fields.Length; i += 2) f[fields[i]] = fields[i + 1]; return new() { ["sourceId"] = id, ["fields"] = f }; }
        void Table(string name, params JsonObject[] rows) => WriteText(Path.Combine(project.Root, "source", "tables", name + ".json"), new JsonObject { ["schema"] = new JsonObject { ["name"] = name }, ["records"] = new JsonArray(rows.Cast<JsonNode?>().ToArray()) }.ToJsonString(Pretty));
        Table("skills", Row("a", "skill", "Spark", "srvmissilea", "sparkball"), Row("b", "skill", "Storm", "cltmissile", "sparkball"));
        Table("sounds", Row("s", "Sound", "spark_hit"));
        var catalog = new MissileBuilderResolver().Catalog(project, "standard", "enUS", [gameData], [new(0, "r0", "sparkball", "SparkBall", "boom"), new(1, "r1", "", "", "")], default);
        check(catalog.Entries[0] is { FiredBy: "Spark, Storm", Inactive: false } && catalog.Entries[1].Inactive && VisualBuilder.Matches(catalog.Entries[0].SearchText, "storm boom")
            && catalog.Sounds.SequenceEqual(["spark_hit"]) && catalog.Skills.SequenceEqual(["Spark", "Storm"]) && catalog.CelFiles.Length == 1,
            "The missile catalog names the skills firing each missile and offers animations, sounds and skills");

        // With extracted game data, every vanilla missile animation decodes.
        if (Environment.GetEnvironmentVariable("MODSTUDIO_BASE_EXCEL") is { Length: > 0 } excel && Path.GetFullPath(Path.Combine(excel, "..", "..", "global", "missiles")) is var missiles && Directory.Exists(missiles))
        {
            var files = Directory.EnumerateFiles(missiles, "*.dcc", SearchOption.AllDirectories).ToArray(); var failures = new List<string>();
            foreach (var file in files) { try { Dcc.Decode(File.ReadAllBytes(file)); } catch (Exception e) { failures.Add(Path.GetFileName(file) + ": " + e.Message); } }
            check(files.Length > 100 && failures.Count == 0, $"Every vanilla missile DCC decodes ({files.Length} files): " + string.Join("; ", failures.Take(5)));
        }
    }
}

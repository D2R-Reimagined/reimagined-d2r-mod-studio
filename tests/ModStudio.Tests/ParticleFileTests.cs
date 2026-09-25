using System.Buffers.Binary;
using System.Diagnostics;
using ModStudio.Core;

internal static class ParticleFileTests
{
    public static void Run(string root, Action<bool, string> check, Action<Action, string> throws)
    {
        var bytes = Fixture();
        var file = ParticleFile.Read(bytes);
        check(file.Chunks.Count == 11 && file.Strings.Count == 17 && file.VersionText.StartsWith("2.9.5", StringComparison.Ordinal), "Baked effect header, chunks and string pool read");
        check(file.Write().SequenceEqual(bytes), "Baked effect writes back byte-identical");
        var info = ParticleEffectInfo.Read(bytes);
        var layer = info.Platforms.Single().Layers.Single(); var renderer = layer.Renderers.Single();
        check(info.Platforms[0].Name == "PC" && layer.Name == "Sparks" && renderer.Kind == "Billboard" && renderer.Material == "vfx/materials/Diablo_Uber.pkma", "Effect resolves platform, layer, renderer kind and material");
        check(renderer.Features.SequenceEqual(["Blend"]) && renderer.Textures.Single().Path == "data/hd/vfx/textures/x.texture" && renderer.Textures.Single().FeatureEnabled, "Renderer lists enabled features and their texture paths");
        check(renderer.Properties.Single(p => p.Name == "General.MipBias").Value == "0.5", "Float renderer setting formats its value");
        var curve = layer.Curves.Single();
        check(curve.Dimension == 4 && curve.Times.SequenceEqual([0f, 1f]) && curve.Value(1, 0) == 0.25f && curve.Name == "__sampler_0", "Colour curve keys split into RGBA values");

        file.Strings[11] = "data/hd/vfx/textures/y.texture";
        var edited = ParticleEffectInfo.Read(file.Write());
        check(edited.Platforms[0].Layers[0].Renderers[0].Textures.Single().Path == "data/hd/vfx/textures/y.texture", "Changing a pooled path is written and read back");

        Edits(bytes, check, throws);

        throws(() => ParticleFile.Read(bytes[..40]), "Truncated baked effect rejected");
        var badMagic = (byte[])bytes.Clone(); badMagic[3] = 0xc9; throws(() => ParticleFile.Read(badMagic), "Non-D2R PopcornFX layout rejected");
        var badClass = (byte[])bytes.Clone(); BinaryPrimitives.WriteUInt32LittleEndian(badClass.AsSpan(28 + 9 * 8 + 5), 999); throws(() => ParticleFile.Read(badClass), "Chunk class outside the string pool rejected");
        throws(() => ParticleObject.Decode([1, 0, 9, 0, 1, 0, 0, 0], ParticleFile.Schema["CParticleEffect"]), "Unknown chunk field rejected");
        throws(() => ParticleObject.Decode([1, 0, 1, 0, 255, 255, 255, 255], ParticleFile.Schema["CParticleEffect"]), "Array longer than its chunk rejected");

        var folder = Path.Combine(root, "particles", "data", "hd", "vfx", "particles"); Directory.CreateDirectory(folder);
        var effectFile = Path.Combine(folder, "fx_test.particles"); File.WriteAllBytes(effectFile, bytes);
        var missiles = Path.Combine(root, "particles", "data", "hd", "missiles"); Directory.CreateDirectory(missiles);
        File.WriteAllText(Path.Combine(missiles, "test.json"), "{\"dependencies\":{\"particles\":[{\"path\":\"data/hd/vfx/particles/fx_test.particles\"}]}}");
        check(ParticleUsage.RelativePath(effectFile) == "data/hd/vfx/particles/fx_test.particles" && ParticleUsage.DataRoot(effectFile) == Path.Combine(root, "particles", "data"), "Particle path resolves to its data-relative name and data root");
        var usage = ParticleUsage.Find([Path.Combine(root, "particles")], [], "data/hd/vfx/particles/FX_Test.particles");
        check(usage.Count == 1 && usage[0].File.EndsWith("test.json", StringComparison.Ordinal), "Usage search finds JSON naming the effect, ignoring case");
        check(File.ReadAllBytes(effectFile).SequenceEqual(bytes), "Inspecting an effect never modifies it");

        Vanilla(check);
    }

    private static void Edits(byte[] bytes, Action<bool, string> check, Action<Action, string> throws)
    {
        var file = ParticleFile.Read(bytes);
        foreach (var chunk in Enumerable.Range(1, file.Chunks.Count))
            if (file.Object((uint)chunk) is { } o && !o.Encode().SequenceEqual(file.Chunks[chunk - 1].Payload)) { check(false, "Chunk re-encodes byte-identical: " + chunk); return; }
        var info = ParticleEffectInfo.Read(bytes); var layer = info.Platforms[0].Layers[0]; var renderer = layer.Renderers[0];
        var texture = renderer.Textures.Single(); var mipBias = renderer.Properties.Single(p => p.Name == "General.MipBias");
        ParticleEdits.SetPath(file, texture.Reference, "data/hd/vfx/textures/other.texture");
        ParticleEdits.SetSetting(file, mipBias.Reference, ParticleEdits.Floats(-1.25f));
        var edited = ParticleEffectInfo.Read(file.Write()).Platforms[0].Layers[0];
        check(edited.Renderers[0].Textures.Single().Path == "data/hd/vfx/textures/other.texture" && file.Strings.Contains("data/hd/vfx/textures/x.texture"), "Texture swap adds the new path and leaves the old pool entry");
        check(edited.Renderers[0].Properties.Single(p => p.Name == "General.MipBias").Value == "-1.25", "Float setting edit is written and read back");
        ParticleEdits.SetSetting(file, mipBias.Reference, ParticleEdits.Floats(0));
        check(!file.Object((uint)mipBias.Reference)!.Has(2), "A setting reset to zero is left out, as the game writes defaults");
        throws(() => ParticleEdits.SetSetting(file, texture.Reference, ParticleEdits.Floats(1)), "Path settings cannot be edited as numbers");
        throws(() => ParticleEdits.SetPath(file, texture.Reference, "../outside.texture"), "Paths leaving the data folder rejected");

        var constant = layer.Colors.Single();
        check(constant.Width == 4 && constant.Value.SequenceEqual([0.8f, 0.3f, 0.1f, 1f]), "Orange float4 constant recognised as a colour");
        int compiled = ParticleEdits.SetColorConstant(file, constant.Layer, constant.Index, [0.1f, 0.4f, 0.9f, 1f]);
        var recoloured = ParticleFile.Read(file.Write()); var blue = ParticleEdits.ColorConstants(recoloured, constant.Layer).Single();
        var blob = recoloured.Chunks[10].Payload; byte[] bluePattern = [.. ParticleEdits.Floats(0.1f, 0.4f, 0.9f, 1f), .. ParticleEdits.Floats(0.1f, 0.4f, 0.9f, 1f)];
        check(compiled == 1 && blue.Value.SequenceEqual([0.1f, 0.4f, 0.9f, 1f]) && blob.AsSpan().IndexOf(bluePattern) >= 0 && recoloured.Object((uint)constant.Layer)!.Elements(1)[0].SequenceEqual(ParticleEdits.Floats(0.1f, 0.4f, 0.9f, 1f)), "Colour constant rewritten in the constant table, value ranges and compiled script");
        throws(() => ParticleEdits.SetColorConstant(file, constant.Layer, constant.Index, [-1f, 0, 0, 1]), "Negative colour rejected");

        var curve = layer.Curves.Single();
        ParticleEdits.Recolor(file, new ColorShift(Brightness: 2), [curve.Reference], [(constant.Layer, constant.Index)]);
        var bright = ParticleEffectInfo.Read(file.Write()).Platforms[0].Layers[0];
        check(Math.Abs(bright.Curves[0].Value(0, 0) - 2f) < 1e-5 && Math.Abs(bright.Curves[0].Value(0, 1) - 1f) < 1e-5 && bright.Curves[0].Value(0, 3) == 1f && Math.Abs(bright.Colors[0].Value[2] - 1.8f) < 1e-5, "Brightness doubles colour channels of curves and constants but not alpha");
        var turned = new ColorShift(120).Apply([0.9f, 0.4f, 0.1f]);
        check(Math.Abs(turned[0] - 0.1f) < 1e-5 && Math.Abs(turned[1] - 0.9f) < 1e-5 && Math.Abs(turned[2] - 0.4f) < 1e-5, "A 120 degree hue turn takes orange to green, keeping saturation and brightness");
        var hdr = new ColorShift(0, 0.5, 2).Apply([4f, 2f, 0f]);
        check(Math.Abs(hdr[0] - 8f) < 1e-4 && Math.Abs(hdr[2] - 4f) < 1e-4, "HDR colours keep their intensity above 1 through a recolour");
        var json = "{\"a\":\"DATA/hd/vfx/x.particles\",\"b\":\"data/hd/vfx/x.particles2\"}";
        check(ParticleEdits.Retarget(json, "data/hd/vfx/x.particles", "data/hd/vfx/y.particles", out var n) == "{\"a\":\"data/hd/vfx/y.particles\",\"b\":\"data/hd/vfx/x.particles2\"}" && n == 1, "Retarget replaces whole quoted references only");
    }

    /// <summary>With MODSTUDIO_GAME_DATA at an extracted data folder, every vanilla effect must round-trip and every schema class must decode.</summary>
    private static void Vanilla(Action<bool, string> check)
    {
        if (Environment.GetEnvironmentVariable("MODSTUDIO_GAME_DATA") is not { Length: > 0 } data || !Directory.Exists(Path.Combine(data, "hd"))) return;
        var clock = Stopwatch.StartNew(); int files = 0, chunks = 0, layers = 0, constants = 0; var failures = new List<string>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(data, "hd"), "*.particles", SearchOption.AllDirectories))
        {
            try
            {
                var bytes = File.ReadAllBytes(path); var file = ParticleFile.Read(bytes);
                if (!file.Write().SequenceEqual(bytes)) { failures.Add(path + ": round trip differs"); continue; }
                for (uint i = 1; i <= file.Chunks.Count; i++)
                    if (ParticleFile.Schema.ContainsKey(file.ClassName((int)i)))
                    {
                        if (!file.Object(i)!.Encode().SequenceEqual(file.Chunks[(int)i - 1].Payload)) { failures.Add($"{path}: chunk {i} re-encodes differently"); break; }
                        chunks++;
                    }
                var info = ParticleEffectInfo.Read(bytes); layers += info.Platforms.Sum(p => p.Layers.Length); files++;
                // Every colour constant must be found in its compiled script: recolour each PC constant of this in-memory copy.
                foreach (var c in info.Platforms.FirstOrDefault(p => p.Name == "PC")?.Layers.SelectMany(l => l.Colors) ?? [])
                {
                    ParticleEdits.SetColorConstant(file, c.Layer, c.Index, [.. c.Value.Select((v, k) => k < 3 ? v * 0.5f + 0.01f : v)]); constants++;
                }
            }
            catch (Exception ex) { failures.Add(path + ": " + ex.Message); }
        }
        check(failures.Count == 0 && files > 1000, $"Vanilla effects read, decode, round-trip and recolour ({files} files, {chunks} chunks re-encoded, {layers} layers, {constants} colour constants rewritten, {clock.Elapsed.TotalSeconds:0.0}s){(failures.Count > 0 ? ": " + string.Join("; ", failures.Take(3)) : "")}");
    }

    /// <summary>A minimal effect in the game's layout: one PC layer with a billboard renderer, three settings, an RGBA curve and an orange constant.</summary>
    private static byte[] Fixture()
    {
        string[] strings = ["CParticleEffect", "CLayerGraphCompileCache", "CLayerGraphCompileCache_LayerSlot", "CLayerCompileCache", "CLayerCompileCacheRenderer", "CLayerCompileCacheRendererProperty",
            "PC", "Sparks", "vfx/materials/Diablo_Uber.pkma", "Blend", "Blend.TextureBase", "data/hd/vfx/textures/x.texture", "General.MipBias"];
        static byte[] U(params uint[] values) { var b = new byte[values.Length * 4]; for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), values[i]); return b; }
        static byte[] F(params float[] values) => U([.. values.Select(BitConverter.SingleToUInt32Bits)]);
        static byte[] A(params uint[] values) => U([(uint)values.Length, .. values]);
        static byte[] Obj(params (int Id, byte[] Value)[] fields)
        {
            var b = new List<byte>(); b.AddRange(BitConverter.GetBytes((ushort)fields.Length));
            foreach (var (id, value) in fields) { b.AddRange(BitConverter.GetBytes((ushort)id)); b.AddRange(value); }
            return [.. b];
        }
        static byte[] A16(params byte[][] entries) => [.. U((uint)entries.Length), .. entries.SelectMany(e => e)];
        var Orange = F(0.8f, 0.3f, 0.1f, 1);
        string[] all = [.. strings, "CLayerCompileCacheSampler", "CParticleNodeSamplerData_Curve", "__sampler_0", "CCompilerBlobCache"];
        (int Class, byte[] Payload)[] chunks =
        [
            (0, Obj((1, A(2)))),
            (1, Obj((0, U(6)), (3, A(3)))),
            (2, Obj((0, U(4)))),
            (3, Obj((0, A16(Orange, Orange)), (1, A16(Orange, U(1, 1, 1, 1))), (7, A(9)), (8, A(5)), (9, A(11)), (20, U(7)))),
            (4, Obj((2, A(6, 7, 8)), (4, U(8)))),
            (5, Obj((0, U(9)), (2, U(uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue)))),
            (5, Obj((0, U(10)), (1, U(12)), (3, U(11)))),
            (5, Obj((0, U(12)), (1, U(7)), (2, F(0.5f, 0, 0, 0)))),
        ];
        var extra = new (int Class, byte[] Payload)[] { (13, Obj((0, U(15)), (1, U(10)))), (14, Obj((17, A(0, BitConverter.SingleToUInt32Bits(1))), (18, A([.. new[] { 1f, 0.5f, 0, 1, 0.25f, 0, 0, 0 }.Select(BitConverter.SingleToUInt32Bits)])))),
            (16, Obj((2, [.. U(10), .. U(7), .. Orange, .. Orange, .. U(9)]))) };
        // The layer's sampler is chunk 9, its curve data chunk 10 and its compiled script chunk 11, which carries the orange constant.
        var ordered = chunks.Concat(extra).ToArray();
        var classes = ordered.Select(c => c.Class).Distinct().ToArray();
        var body = new List<byte>();
        foreach (var (cls, payload) in ordered) { body.AddRange(U((uint)(5 + payload.Length))); body.Add(0x20); body.AddRange(U((uint)cls)); body.AddRange(payload); }
        var header = new List<byte>(U(ParticleFile.Magic, 0x01050902, 8000, (uint)ordered.Length, (uint)classes.Length, (uint)(28 + classes.Length * 8 + body.Count), 0));
        foreach (var c in classes) header.AddRange(U((uint)c, (uint)ordered.Count(o => o.Class == c)));
        var pool = new List<byte>(U((uint)all.Length));
        foreach (var s in all) { pool.Add((byte)s.Length); pool.AddRange(System.Text.Encoding.ASCII.GetBytes(s)); }
        return [.. header, .. body, .. pool];
    }
}

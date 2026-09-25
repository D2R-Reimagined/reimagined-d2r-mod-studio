using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>A renderer setting. <see cref="Path"/> is set for textures, meshes, skeletons, animations and sounds.</summary>
public sealed record ParticleProperty(int Reference, string Name, string Feature, int Type, string TypeName, string Value, string? Path, bool FeatureEnabled)
{
    /// <summary>Numbers and on/off settings; features, paths and sounds are edited other ways.</summary>
    public bool Editable => Path == null && Type is 1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10;
    /// <summary>How many numbers the value holds (1 for bool and scalar settings).</summary>
    public int Width => Type switch { 4 or 8 => 2, 5 or 9 => 3, 6 or 10 => 4, _ => 1 };
    public bool IsFloat => Type is 7 or 8 or 9 or 10;
}
public sealed record ParticleRenderer(int Reference, string Kind, string? Material, string[] Features, ParticleProperty[] Properties)
{
    public IEnumerable<ParticleProperty> Textures => Properties.Where(p => p.Type == 12 && p.Path != null);
    public IEnumerable<ParticleProperty> Resources => Properties.Where(p => p.Type != 12 && p.Path != null);
}
/// <summary>A curve sampled over a particle's life: <see cref="Values"/> holds <see cref="Dimension"/> numbers per key.</summary>
public sealed record ParticleCurve(int Reference, string Name, int Dimension, float[] Times, float[] Values, float[] Tangents)
{
    public float Value(int key, int component) => Values[key * Dimension + component];
}
public sealed record ParticleSampler(int Reference, string Name, string Kind);
public sealed record ParticleLayer(int Reference, string Name, ParticleRenderer[] Renderers, ParticleCurve[] Curves, ParticleSampler[] Samplers, ParticleColorConstant[] Colors);
public sealed record ParticlePlatform(string Name, ParticleLayer[] Layers);
public sealed record ParticleAttribute(string Name, string Type, float[] Default, float[]? Min, float[]? Max, string? Description);

/// <summary>
/// What a baked effect is made of, per platform the game baked it for (PC, Switch, PS4, PS5): its layers, each layer's
/// renderers (material, enabled features, settings, textures and meshes) and the curves its samplers read, plus the
/// effect's attributes. Read-only; the simulation itself is compiled bytecode and is not interpreted.
/// </summary>
public sealed record ParticleEffectInfo(ParticleFile File, string Version, ParticlePlatform[] Platforms, ParticleAttribute[] Attributes, (string Class, int Count)[] Classes)
{
    public static readonly string[] RendererKinds = ["Billboard", "Ribbon", "Mesh", "Triangle", "Decal", "Light", "Sound"];
    public IEnumerable<string> Paths => Platforms.SelectMany(p => p.Layers).SelectMany(l => l.Renderers).SelectMany(r => r.Properties).Select(p => p.Path).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase);
    public IEnumerable<string> Materials => Platforms.SelectMany(p => p.Layers).SelectMany(l => l.Renderers).Select(r => r.Material).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase);

    public static ParticleEffectInfo Load(string path)
    {
        NoLinks(path); var info = new FileInfo(path);
        Require(info.Length <= 64 * 1024 * 1024, "Particle files over 64 MiB are not previewed.");
        return Read(System.IO.File.ReadAllBytes(path));
    }

    public static ParticleEffectInfo Read(byte[] bytes)
    {
        var file = ParticleFile.Read(bytes);
        string Name(uint index, string fallback = "") => file.String(index) ?? fallback;
        var effect = file.Objects("CParticleEffect").FirstOrDefault().Fields ?? throw new InvalidDataException("The effect has no CParticleEffect chunk.");
        var platforms = new List<ParticlePlatform>();
        foreach (var reference in effect.Array(1))
        {
            if (file.Object(reference) is not { } graph || file.ClassName((int)reference) != "CLayerGraphCompileCache") continue;
            var layers = new List<ParticleLayer>();
            foreach (var slot in graph.Array(3))
            {
                if (file.Object(slot) is not { } s || s.U32(0) is not (> 0 and var layerRef) || file.ClassName((int)slot) != "CLayerGraphCompileCache_LayerSlot") continue;
                if (file.Object(layerRef) is not { } layer || file.ClassName((int)layerRef) != "CLayerCompileCache") continue;
                layers.Add(Layer(file, (int)layerRef, layer));
            }
            platforms.Add(new ParticlePlatform(Name(graph.U32(0), "Platform " + (platforms.Count + 1)), [.. layers]));
        }
        var attributes = new List<ParticleAttribute>();
        if (file.Object(effect.U32(3)) is { } list)
            foreach (var reference in list.Array(0))
                if (file.Object(reference) is { } a && file.ClassName((int)reference) == "CParticleAttributeDeclaration")
                    attributes.Add(Attribute(file, a));
        var classes = file.Chunks.GroupBy(c => file.ClassName(c)).Select(g => (g.Key, g.Count())).OrderByDescending(g => g.Item2).ToArray();
        return new ParticleEffectInfo(file, file.VersionText, [.. platforms], [.. attributes], classes);
    }

    private static ParticleLayer Layer(ParticleFile file, int reference, ParticleObject layer)
    {
        var renderers = new List<ParticleRenderer>(); var curves = new List<ParticleCurve>(); var samplers = new List<ParticleSampler>();
        foreach (var r in layer.Array(8))
            if (file.Object(r) is { } renderer && file.ClassName((int)r) == "CLayerCompileCacheRenderer") renderers.Add(Renderer(file, (int)r, renderer));
        foreach (var r in layer.Array(7))
        {
            if (file.Object(r) is not { } sampler || file.ClassName((int)r) != "CLayerCompileCacheSampler") continue;
            var name = file.String(sampler.U32(0)) ?? "sampler"; var data = sampler.U32(1);
            var kind = file.Chunk(data) is { } c ? file.ClassName(c).Replace("CParticleNodeSamplerData_", "") : "Unknown";
            samplers.Add(new ParticleSampler((int)r, name, kind));
            if (kind == "Curve" && file.Object(data) is { } curve)
            {
                float[] times = curve.Floats(17), values = curve.Floats(18);
                if (times.Length == 0 || values.Length % times.Length != 0) continue;
                var label = curve.Localized(0).Select(i => file.String(i)).FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? name;
                curves.Add(new ParticleCurve((int)data, label, values.Length / times.Length, times, values, curve.Floats(19)));
            }
        }
        return new ParticleLayer(reference, file.String(layer.U32(20)) ?? $"Layer {reference}", [.. renderers], [.. curves], [.. samplers], [.. ParticleEdits.ColorConstants(file, reference)]);
    }

    private static ParticleRenderer Renderer(ParticleFile file, int reference, ParticleObject renderer)
    {
        int kind = (int)renderer.U32(0);
        var raw = renderer.Array(2).Select(r => file.Object(r) is { } p && file.ClassName((int)r) == "CLayerCompileCacheRendererProperty" ? p : null).OfType<ParticleObject>().ToList();
        // A feature is a type-0 property; it is on when its value is set (the game writes all bits set).
        var enabled = raw.Where(p => p.U32(1) == 0 && p.Has(2) && p.Ints(2).Any(v => v != 0)).Select(p => file.String(p.U32(0)) ?? "").ToHashSet(StringComparer.Ordinal);
        var properties = new List<ParticleProperty>();
        foreach (var p in raw)
        {
            var name = file.String(p.U32(0)) ?? "?"; int type = (int)p.U32(1);
            var feature = type == 0 ? name : name.Split('.')[0];
            string? path = p.Has(3) ? file.String(p.U32(3)) : null;
            properties.Add(new ParticleProperty(p.Reference, name, feature, type, TypeName(type), Format(type, p, path), path, type == 0 ? enabled.Contains(name) : !name.Contains('.') || feature == "General" || enabled.Contains(feature)));
        }
        return new ParticleRenderer(reference, kind >= 0 && kind < RendererKinds.Length ? RendererKinds[kind] : $"Renderer {kind}", file.String(renderer.U32(4)),
            [.. properties.Where(p => p.Type == 0 && p.FeatureEnabled).Select(p => p.Name)], [.. properties]);
    }

    public static string TypeName(int type) => type switch
    {
        0 => "feature", 1 => "bool", 2 => "int", 3 => "int", 4 => "int2", 5 => "int3", 6 => "int4", 7 => "float", 8 => "float2", 9 => "float3", 10 => "float4",
        12 => "texture", 13 => "atlas", 14 => "resource", 15 => "sound", _ => $"type {type}"
    };

    private static string Format(int type, ParticleObject p, string? path)
    {
        if (path != null || type is 12 or 13 or 14 or 15) return path ?? "(none)";
        var ints = p.Ints(2); var floats = p.Vector(2);
        static string F(float f) => f.ToString("0.####", CultureInfo.InvariantCulture);
        return type switch
        {
            0 => ints.Any(v => v != 0) ? "on" : "off",
            1 => ints[0] != 0 ? "true" : "false",
            2 or 3 => ints[0].ToString(CultureInfo.InvariantCulture),
            4 or 5 or 6 => string.Join(", ", ints.Take(type - 2)),
            7 or 8 or 9 or 10 => string.Join(", ", floats.Take(type - 6).Select(F)),
            _ => string.Join(" ", ints.Select(i => i.ToString("X8", CultureInfo.InvariantCulture)))
        };
    }

    private static ParticleAttribute Attribute(ParticleFile file, ParticleObject a)
    {
        int type = (int)a.U32(1);
        var (name, width, integer) = type switch { 1 => ("bool", 1, true), 25 => ("int", 1, true), 32 => ("float2", 2, false), 33 => ("float3", 3, false), 34 => ("float4", 4, false), 0 => ("float", 1, false), _ => ($"type {type}", 4, false) };
        float[] Take(int id) => integer ? [.. a.Ints(id).Take(width).Select(i => (float)i)] : [.. a.Vector(id).Take(width)];
        var description = a.Localized(2).Select(file.String).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
        // Floats keep default, min and max in fields 14, 32 and 44; integers their default and max in 21 and 50.
        return integer
            ? new ParticleAttribute(file.String(a.U32(0)) ?? "?", name, Take(21), null, a.Has(50) ? Take(50) : null, description)
            : new ParticleAttribute(file.String(a.U32(0)) ?? "?", name, Take(14), a.Has(32) ? Take(32) : null, a.Has(44) ? Take(44) : null, description);
    }
}

/// <summary>
/// Where effects are used: HD JSON files (missiles, overlays, presets, units) naming a .particles path. Game data folders
/// are indexed once per session; project folders are indexed on every search because they change while editing.
/// </summary>
public static partial class ParticleUsage
{
    private static readonly ConcurrentDictionary<string, Dictionary<string, List<string>>> gameIndexes = new(StringComparer.OrdinalIgnoreCase);
    [GeneratedRegex("\"([^\"\\\\]*\\.particles)\"", RegexOptions.IgnoreCase)] private static partial Regex Reference();

    /// <summary>The data-relative path ("data/hd/vfx/…") of a file inside a data folder, or null when it is not under an hd folder.</summary>
    public static string? RelativePath(string file)
    {
        var parts = Path.GetFullPath(file).Replace('\\', '/').Split('/');
        int hd = System.Array.FindLastIndex(parts, p => p.Equals("hd", StringComparison.OrdinalIgnoreCase));
        return hd < 0 ? null : "data/" + string.Join('/', parts[hd..]).ToLowerInvariant();
    }
    /// <summary>The folder holding the hd folder a file sits in: the root its sibling references resolve against.</summary>
    public static string? DataRoot(string file)
    {
        var full = Path.GetFullPath(file); var parts = full.Replace('\\', '/').Split('/');
        int hd = System.Array.FindLastIndex(parts, p => p.Equals("hd", StringComparison.OrdinalIgnoreCase));
        return hd <= 0 ? null : string.Join(Path.DirectorySeparatorChar, parts[..hd]) + (hd == 1 ? Path.DirectorySeparatorChar : "");
    }

    /// <summary>JSON files under each root that name <paramref name="relative"/>, as (root, file) pairs, project roots first.</summary>
    public static List<(string Root, string File)> Find(IEnumerable<string> projectRoots, IEnumerable<string> gameRoots, string relative, CancellationToken token = default)
    {
        relative = Normalize(relative); var found = new List<(string, string)>();
        foreach (var root in projectRoots.Where(Directory.Exists))
            if (Index(root, token).TryGetValue(relative, out var files)) found.AddRange(files.Select(f => (root, f)));
        foreach (var root in gameRoots.Where(Directory.Exists))
            if (gameIndexes.GetOrAdd(Path.GetFullPath(root), r => Index(r, token)).TryGetValue(relative, out var files)) found.AddRange(files.Select(f => (root, f)));
        return found;
    }
    private static string Normalize(string relative)
    {
        relative = relative.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
        return relative.StartsWith("data/", StringComparison.Ordinal) ? relative : "data/" + relative;
    }
    private static Dictionary<string, List<string>> Index(string root, CancellationToken token)
    {
        var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var hd = new[] { Path.Combine(root, "hd"), Path.Combine(root, "data", "hd") }.FirstOrDefault(Directory.Exists);
        if (hd == null) return index;
        foreach (var json in Directory.EnumerateFiles(hd, "*.json", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            string text; try { text = File.ReadAllText(json); } catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
            if (!text.Contains(".particles", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var path in Reference().Matches(text).Select(m => Normalize(m.Groups[1].Value)).Distinct())
                (index.TryGetValue(path, out var files) ? files : index[path] = []).Add(json);
        }
        return index;
    }
}

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>A missile's legacy animation: the DCC its CelFile names, where it was found, and why it is missing when it is.</summary>
public sealed record MissileArt(string CelFile, HdFile? File, Dcc.Animation? Animation, string[] Notes)
{
    public bool Invisible => CelFile.Length == 0 || CelFile.Equals("null", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// What a missile loads in HD: the unit definition hd/missiles/missiles.json maps it to, and the particle systems, models
/// and textures that definition depends on. HD missiles are particle effects; Studio lists them rather than drawing them.
/// </summary>
public sealed record HdMissile(string? Unit, HdFile? File, string[] Particles, string[] Models, string[] Textures, string[] Notes);

/// <summary>One missile as the builder's search lists it: its id, animation, and the skills that fire it.</summary>
public sealed record MissileEntry(int Row, string SourceId, string Id, string CelFile, string FiredBy, string Explosion)
{
    public bool Inactive => Id.Length == 0;
    public string SearchText { get; } = string.Join(' ', Id, CelFile, FiredBy, Explosion).ToLowerInvariant();
}

/// <summary>A missiles.txt row's identity, read on the UI thread for the search list.</summary>
public sealed record MissileRow(int Row, string SourceId, string Id, string CelFile, string Explosion);

public sealed record MissileBuilderCatalog(MissileEntry[] Entries, string[] CelFiles, string[] Sounds, string[] Skills, string[] Issues);

/// <summary>
/// Finds and decodes missile graphics. The legacy animation is data/global/missiles/&lt;CelFile&gt;.dcc (the expansion and
/// extra folders beside it hold more), drawn with an act palette, data/global/palette/&lt;act&gt;/pal.dat. Files are read
/// from the project first, then the user's extracted game data, as the game applies a mod over the base game.
/// </summary>
public static class MissileGraphics
{
    private static readonly string[] Folders = ["data/global/missiles", "data/global/missiles/expansion", "data/global/missiles/extra"];
    private static readonly ConcurrentDictionary<string, Dcc.Animation> animations = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Dictionary<string, string>> hdMaps = new(StringComparer.Ordinal);

    public static MissileArt Art(ModProject project, IReadOnlyList<string> gameData, string celFile, CancellationToken token = default)
    {
        celFile = celFile.Trim();
        if (celFile.Length == 0 || celFile.Equals("null", StringComparison.OrdinalIgnoreCase))
            return new(celFile, null, null, ["No CelFile: the missile draws nothing itself (its function or sub-missiles may)."]);
        HdFile? file = null;
        foreach (var folder in Folders) if ((file = HdAppearance.Find(project, gameData, $"{folder}/{celFile}.dcc")) != null) break;
        if (file == null)
            return new(celFile, null, null, [gameData.Count == 0
                ? $"{celFile}.dcc is not in the project. Choose your extracted game data folder to show the base game's animation."
                : $"{celFile}.dcc is not under data/global/missiles in the project or the game data."]);
        try
        {
            token.ThrowIfCancellationRequested();
            var animation = animations.TryGetValue(file.Hash, out var cached) ? cached : animations[file.Hash] = Dcc.Decode(File.ReadAllBytes(file.Path), token);
            if (animations.Count > 64) animations.Clear();
            return new(celFile, file, animation, []);
        }
        catch (InvalidDataException e) { return new(celFile, file, null, [$"{file.Relative}: {e.Message}"]); }
    }

    /// <summary>An act's 256-colour palette as B,G,R triples; null when neither the project nor the game data has it.</summary>
    public static byte[]? Palette(ModProject project, IReadOnlyList<string> gameData, int act)
    {
        var file = HdAppearance.Find(project, gameData, $"data/global/palette/act{Math.Clamp(act, 1, 5)}/pal.dat");
        if (file == null) return null;
        var bytes = File.ReadAllBytes(file.Path);
        return bytes.Length >= 768 ? bytes[..768] : null;
    }

    /// <summary>The CelFile names available to pick from: every DCC in the missile folders, project and game data together.</summary>
    public static string[] CelFiles(ModProject project, IReadOnlyList<string> gameData)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in gameData.Prepend(project.Root))
            foreach (var folder in Folders)
                if (HdAppearance.LocateFolder(root, folder) is { } directory)
                    foreach (var file in Directory.EnumerateFiles(directory, "*.dcc")) names.Add(Path.GetFileNameWithoutExtension(file));
        return [.. names];
    }

    /// <summary>The HD unit definition a missile loads and what it depends on.</summary>
    public static HdMissile Hd(ModProject project, IReadOnlyList<string> gameData, string missile)
    {
        var notes = new List<string>();
        var mapFile = HdAppearance.Find(project, gameData, "data/hd/missiles/missiles.json");
        if (mapFile == null) return new(null, null, [], [], [], [gameData.Count == 0 ? "No HD missile list in the project; choose your extracted game data folder to see what HD draws." : "No data/hd/missiles/missiles.json found."]);
        Dictionary<string, string> map;
        try
        {
            map = hdMaps.TryGetValue(mapFile.Hash, out var cached) ? cached : hdMaps[mapFile.Hash] = Read(mapFile.Path);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidDataException) { return new(null, null, [], [], [], [$"{mapFile.Relative}: {e.Message}"]); }
        if (!map.TryGetValue(ItemSprites.Key(missile), out var unit)) return new(null, null, [], [], [], [$"{missile} has no entry in missiles.json, so HD draws nothing for it."]);
        var file = HdAppearance.Find(project, gameData, $"data/hd/missiles/{unit}.json");
        if (file == null) return new(unit, null, [], [], [], [$"data/hd/missiles/{unit}.json is not in the project or the game data."]);
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(file.Path, Utf8).TrimStart('﻿'), null, Document.SourceJsonOptions);
            string[] Paths(string kind) => [.. ((root?["dependencies"]?[kind] as JsonArray) ?? []).Select(d => d?["path"]?.GetValue<string>() ?? "").Where(p => p.Length > 0)];
            return new(unit, file, Paths("particles"), Paths("models"), Paths("textures"), [.. notes]);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException) { return new(unit, file, [], [], [], [$"{file.Relative}: {e.Message}"]); }

        static Dictionary<string, string> Read(string path)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (JsonNode.Parse(File.ReadAllText(path, Utf8).TrimStart('﻿'), null, Document.SourceJsonOptions) is JsonObject root)
                foreach (var (key, value) in root)
                    if (value is JsonValue text && text.TryGetValue<string>(out var unit)) map.TryAdd(ItemSprites.Key(key), unit);
            return map;
        }
    }

    public static MissileRow[] Rows(TableData table) =>
        [.. Enumerable.Range(0, table.Records.Count).Select(i => new MissileRow(i, table.Records[i].S("sourceId"), table.Cell(i, "Missile"), table.Cell(i, "CelFile"), table.Cell(i, "ExplosionMissile")))];
}

/// <summary>What the missile builder reads besides the row being edited. Worker-owned; reads authored data only.</summary>
public sealed class MissileBuilderResolver
{
    private static readonly string[] SkillMissileFields = ["srvmissile", "srvmissilea", "srvmissileb", "srvmissilec", "cltmissile", "cltmissilea", "cltmissileb", "cltmissilec", "cltmissiled"];
    private readonly PreviewTables tables = new();
    public void Clear() => tables.Clear();

    public MissileBuilderCatalog Catalog(ModProject project, string profile, string locale, IReadOnlyList<string> gameData, IReadOnlyList<MissileRow> rows, CancellationToken token)
    {
        var issues = new List<string>();
        var data = tables.Open(project, profile, locale, issues, token);
        var skills = data.Rows("skills", false);
        var firedBy = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var skill in skills)
            foreach (var field in SkillMissileFields)
                if (skill.S(field) is { Length: > 0 } missile)
                {
                    if (!firedBy.TryGetValue(missile, out var list)) firedBy[missile] = list = [];
                    if (!list.Contains(skill.S("skill"))) list.Add(skill.S("skill"));
                }
        token.ThrowIfCancellationRequested();
        var entries = rows.Select(r => new MissileEntry(r.Row, r.SourceId, r.Id, r.CelFile, string.Join(", ", firedBy.GetValueOrDefault(r.Id) ?? []), r.Explosion)).ToArray();
        var sounds = data.Rows("sounds", false).Select(s => s.S("Sound")).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        return new(entries, MissileGraphics.CelFiles(project, gameData), sounds, [.. skills.Select(s => s.S("skill")).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal)], [.. issues.Distinct()]);
    }
}

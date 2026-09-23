using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>
/// One ColorTransform of a variant entry: the six xyzw vectors in <see cref="HdAppearance.Fields"/> order, flattened
/// (field × 4 + component). Entries hold three, one per channel of the unit's KTINT mask texture.
/// </summary>
public sealed record VariantTransform(string Name, double[] Values)
{
    public double Get(int field, int component) => Values[field * 4 + component];
    public bool SameValues(VariantTransform other) => Values.SequenceEqual(other.Values);
}

/// <summary>A named colour entry of a variant file. Custom entries are the customColorShiftEntries a state or skill selects by index.</summary>
public sealed record VariantEntry(string Name, bool Custom, int? ColorShiftIndex, VariantTransform[] Transforms)
{
    public string Label => Custom ? $"{Name} (colour shift {ColorShiftIndex})" : Name;
}

/// <summary>An HD file as the game would load it: its data-relative path, where it was found, and the hash that was read.</summary>
public sealed record HdFile(string Relative, string Path, bool InProject, string Hash)
{
    public string Origin => InProject ? "project" : "game data";
}

/// <summary>
/// How a monstats row looks in HD: the unit JSON its BaseId names, the variant (colour) file that unit loads, and the entry
/// its TransLvl picks. <see cref="SharedWith"/> names other units that load the same variant file, so an edit recolours them too.
/// </summary>
public sealed record MonsterAppearance(string BaseId, string TransLvl, HdFile? Unit, HdFile? Variant, VariantEntry[] Entries, string? DefaultEntry,
    string[] SharedWith, int FamilyRows, string[] Notes, string[] Issues);

/// <summary>
/// Reads and writes D2R's HD monster colour variants. A monstats BaseId names data/hd/character/enemy/&lt;BaseId&gt;.json
/// (npc/ for town folk); that unit's VariantDefinitionComponent names the *_variant.json whose "level&lt;TransLvl&gt;" entry
/// tints the model. Files are looked for in the project's data folder first, then in the user's extracted game data, the
/// same order the game applies a mod over the base game. Nothing ships with Studio.
/// </summary>
public static class HdAppearance
{
    /// <summary>The vectors of a ColorTransform, in file order.</summary>
    public static readonly string[] Fields = ["colorAdjustment", "hueAdjustment0", "hueAdjustment1", "satAdjustment0", "satAdjustment1", "colorTint"];
    public static readonly string[] Components = ["x", "y", "z", "w"];
    private static readonly string[] UnitFolders = ["data/hd/character/enemy", "data/hd/character/npc"];
    private static readonly Regex VariantReference = new("\"type\"\\s*:\\s*\"VariantDefinitionComponent\"[^{}]*?\"filename\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly ConcurrentDictionary<string, (long Length, long Written, string? Variant)> references = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A data-relative file ("data/hd/…") under a game data folder. The folder may be the data folder itself (holding hd/)
    /// or its parent (holding data/hd/). Names are matched ignoring case, as the game does.
    /// </summary>
    public static string? Locate(string folder, string relative)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;
        relative = relative.Replace('\\', '/').TrimStart('/');
        foreach (var candidate in new[] { relative, relative.StartsWith("data/", StringComparison.OrdinalIgnoreCase) ? relative[5..] : null })
            if (candidate != null && Existing(folder, candidate) is { } found) return found;
        return null;
    }
    private static string? Existing(string root, string relative, bool directory = false)
    {
        var direct = Path.Combine(root, relative);
        if (directory ? Directory.Exists(direct) : File.Exists(direct)) return direct;
        var current = root;
        var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            bool last = i == parts.Length - 1;
            if (!Directory.Exists(current)) return null;
            var next = (last && !directory ? Directory.EnumerateFiles(current) : Directory.EnumerateDirectories(current))
                .FirstOrDefault(p => Path.GetFileName(p).Equals(parts[i], StringComparison.OrdinalIgnoreCase));
            if (next == null) return null;
            current = next;
        }
        return current;
    }

    /// <summary>The file the game would load for a data-relative path: the project's copy, else the first game data folder that has it.</summary>
    public static HdFile? Find(ModProject project, IReadOnlyList<string> gameData, string relative)
    {
        relative = relative.Replace('\\', '/').TrimStart('/');
        if (relative.StartsWith("data/", StringComparison.OrdinalIgnoreCase) && Existing(project.Root, relative) is { } own) return Open(relative, own, true);
        foreach (var folder in gameData)
            if (Locate(folder, relative) is { } found) return Open(relative, found, false);
        return null;
        static HdFile Open(string relative, string path, bool inProject) => new(relative, path, inProject, Hash(File.ReadAllBytes(path)));
    }

    public static MonsterAppearance Resolve(ModProject project, IReadOnlyList<string> gameData, JsonObject monster, IEnumerable<JsonObject> monstats, CancellationToken token = default)
    {
        var baseId = monster.S("BaseId") is { Length: > 0 } b ? b : monster.S("Id");
        var transLvl = monster.S("TransLvl");
        var notes = new List<string>(); var issues = new List<string>();
        int family = monstats.Count(r => r.S("BaseId").Equals(baseId, StringComparison.OrdinalIgnoreCase));
        HdFile? unit = null;
        foreach (var folder in UnitFolders)
            if ((unit = Find(project, gameData, $"{folder}/{baseId}.json")) != null) break;
        if (unit == null)
        {
            if (gameData.Count == 0) notes.Add($"No HD unit for {baseId} in the project. Choose your extracted game data folder to resolve base-game units.");
            else issues.Add($"No HD unit JSON for BaseId {baseId} under data/hd/character/enemy or npc, in the project or the game data.");
            return new(baseId, transLvl, null, null, [], null, [], family, [.. notes], [.. issues]);
        }
        token.ThrowIfCancellationRequested();
        var variantPath = VariantOf(File.ReadAllText(unit.Path, Utf8));
        if (variantPath == null)
        {
            notes.Add($"{Path.GetFileName(unit.Path)} has no VariantDefinitionComponent, so it has no colour variants.");
            return new(baseId, transLvl, unit, null, [], null, [], family, [.. notes], [.. issues]);
        }
        var variant = Find(project, gameData, variantPath);
        if (variant == null)
        {
            issues.Add($"{variantPath} (named by {Path.GetFileName(unit.Path)}) is not in the project{(gameData.Count == 0 ? "; choose your extracted game data folder to read the base game's copy" : " or the game data")}.");
            return new(baseId, transLvl, unit, null, [], null, [], family, [.. notes], [.. issues]);
        }
        var entries = ReadEntries(ParseJson(File.ReadAllText(variant.Path, Utf8)));
        string? chosen = null;
        if (int.TryParse(transLvl, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level))
        {
            chosen = entries.FirstOrDefault(e => !e.Custom && e.Name.Equals("level" + level, StringComparison.OrdinalIgnoreCase))?.Name;
            if (chosen == null) issues.Add($"TransLvl {level} has no \"level{level}\" entry in {Path.GetFileName(variant.Path)}; entries are {string.Join(", ", entries.Where(e => !e.Custom).Select(e => e.Name))}.");
        }
        token.ThrowIfCancellationRequested();
        var unitName = Path.GetFileNameWithoutExtension(unit.Path);
        var counts = monstats.GroupBy(r => r.S("BaseId"), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var shared = Units(project, gameData, token).Where(u => !u.Name.Equals(unitName, StringComparison.OrdinalIgnoreCase) && Same(u.Variant, variant.Relative))
            .Select(u => $"{u.Name} ({counts.GetValueOrDefault(u.Name)} monstats row{(counts.GetValueOrDefault(u.Name) == 1 ? "" : "s")})").Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return new(baseId, transLvl, unit, variant, entries, chosen, shared, family, [.. notes], [.. issues]);
    }

    /// <summary>The variant file a unit JSON's VariantDefinitionComponent names; null when it has none.</summary>
    public static string? VariantOf(string unitJson) => VariantReference.Match(unitJson) is { Success: true } m ? m.Groups[1].Value.Replace('\\', '/') : null;
    private static bool Same(string? a, string b) => a != null && a.Replace('\\', '/').TrimStart('/').Equals(b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Every unit JSON directly under the enemy and npc folders with the variant file it names; the project's copy wins over the game data's.</summary>
    private static IEnumerable<(string Name, string? Variant)> Units(ModProject project, IReadOnlyList<string> gameData, CancellationToken token)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (root, own) in gameData.Select(g => (g, false)).Prepend((project.Root, true)))
            foreach (var folder in UnitFolders)
            {
                var directory = own ? Existing(root, folder, directory: true) : DirectoryOf(root, folder);
                if (directory == null) continue;
                foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
                {
                    token.ThrowIfCancellationRequested();
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (!seen.Add(name)) continue;
                    yield return (name, CachedVariant(file));
                }
            }
    }
    private static string? DirectoryOf(string root, string relative)
    {
        foreach (var candidate in new[] { relative, relative[5..] })
            if (Existing(root, candidate, directory: true) is { } found) return found;
        return null;
    }
    private static string? CachedVariant(string file)
    {
        var info = new FileInfo(file);
        if (references.TryGetValue(file, out var known) && known.Length == info.Length && known.Written == info.LastWriteTimeUtc.Ticks) return known.Variant;
        string? variant = null;
        try { variant = VariantOf(File.ReadAllText(file, Utf8)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException) { }
        references[file] = (info.Length, info.LastWriteTimeUtc.Ticks, variant);
        return variant;
    }

    private static JsonNode ParseJson(string text) => JsonNode.Parse(text.TrimStart('﻿'), null, Document.SourceJsonOptions) ?? throw new InvalidDataException("Empty variant file.");

    public static VariantEntry[] ReadEntries(JsonNode variant)
    {
        var result = new List<VariantEntry>();
        foreach (var entry in (variant["entries"] as JsonArray ?? []).OfType<JsonObject>()) result.Add(ReadEntry(entry, false, null));
        foreach (var custom in (variant["customColorShiftEntries"] as JsonArray ?? []).OfType<JsonObject>())
            if (custom["entry"] is JsonObject entry)
                result.Add(ReadEntry(entry, true, custom["colorShiftIndex"] is JsonValue index && index.TryGetValue<int>(out var i) ? i : null) with { Name = custom.S("name", entry.S("name")) });
        return [.. result];
    }
    private static VariantEntry ReadEntry(JsonObject entry, bool custom, int? index) =>
        new(entry.S("name"), custom, index, [.. (entry["transforms"] as JsonArray ?? []).OfType<JsonObject>().Select(t =>
            new VariantTransform(t.S("name"), [.. Fields.SelectMany(f => Components.Select(c => Number(t[f]?[c])))]))]);
    private static double Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var d) ? d : 0;

    /// <summary>
    /// Writes one entry's transforms into the project's copy of the variant file. When the variant was read from the game data,
    /// the base file is copied to the same data-relative path in the project first. Only values that changed are rewritten, so
    /// the rest of the file keeps its exact text; a minified file stays minified. Fails when the file changed since it was read.
    /// Returns the project file written.
    /// </summary>
    public static string SaveEntry(ModProject project, HdFile variant, string entryName, bool custom, IReadOnlyList<VariantTransform> transforms)
    {
        Require(variant.Relative.StartsWith("data/hd/", StringComparison.OrdinalIgnoreCase) && variant.Relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase), $"Not an HD variant file: {variant.Relative}");
        var target = variant.InProject ? variant.Path : Inside(project.Root, variant.Relative);
        var bytes = File.ReadAllBytes(variant.Path);
        Require(Hash(bytes) == variant.Hash, $"{variant.Relative} changed since the preview read it. Refresh the preview and apply the edit again.");
        var text = Utf8.GetString(bytes);
        bool bom = text.StartsWith('﻿');
        var root = ParseJson(text);
        JsonObject? entry = custom
            ? (root["customColorShiftEntries"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(c => c.S("name", c["entry"].S("name")) == entryName)?["entry"] as JsonObject
            : (root["entries"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(e => e.S("name") == entryName);
        Require(entry != null, $"{Path.GetFileName(variant.Path)} has no entry named {entryName}.");
        var nodes = (entry!["transforms"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        Require(nodes.Length == transforms.Count, $"{entryName} has {nodes.Length} transforms, not {transforms.Count}.");
        for (int t = 0; t < nodes.Length; t++)
            for (int f = 0; f < Fields.Length; f++)
                for (int c = 0; c < Components.Length; c++)
                {
                    var value = transforms[t].Get(f, c);
                    Require(double.IsFinite(value), $"{transforms[t].Name} {Fields[f]}.{Components[c]} is not a number.");
                    if (nodes[t][Fields[f]] is not JsonObject vector) nodes[t][Fields[f]] = vector = new JsonObject();
                    if (vector[Components[c]] is JsonValue existing && existing.TryGetValue<double>(out var old) && old == value) continue;
                    vector[Components[c]] = Format(value);
                }
        string output;
        if (!text.Contains('\n')) output = root.ToJsonString(Compact);
        else
        {
            output = Json(root);
            if (text.Contains("\r\n")) output = output.Replace("\n", "\r\n");
            if (!text.TrimEnd('﻿').EndsWith('\n')) output = output.TrimEnd('\r', '\n');
        }
        AtomicWrite(target, Utf8.GetBytes((bom ? "﻿" : "") + output), variant.InProject ? variant.Hash : null, requireAbsent: !variant.InProject);
        return target;
    }
    /// <summary>A number written the way the game's files write them: always with a decimal point (1.0, not 1).</summary>
    private static JsonNode Format(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        if (!text.Contains('.') && !text.Contains('E')) text += ".0";
        return JsonNode.Parse(text)!;
    }
}

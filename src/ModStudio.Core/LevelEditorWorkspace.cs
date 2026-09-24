using System.Text.Json;
using System.Text.Json.Nodes;
using Reimagined.Integration;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

public sealed record IntegrationTable(string Source, string[] SourceIds, string Sha256);
public sealed record IntegrationSnapshot(string ProjectId, string Profile, string Root, string Revision, Dictionary<string, IntegrationTable> Tables, Dictionary<string, string>? AssetOverrides = null);
public sealed record LevelSceneChoice(string Label, string Preset, string Map, string LogicalMap, bool NeedsCopy)
{
    public override string ToString() => Label;
}

/// <summary>Read-only table projections and authoritative scene paths. Never deploys or edits generated build output.</summary>
public static class LevelEditorWorkspace
{
    public static string Pointer(ModProject project, string profile) => Inside(project.Cache, "integration/" + profile + "/current.json");
    private static IEnumerable<string> Inputs(ModProject project, string profile) => Files(Path.Combine(project.Root, "source/tables"))
        .Concat(Files(Path.Combine(project.Root, "data/global/excel"))).Concat(Files(Path.Combine(project.Root, "compatibility", profile))).Order(StringComparer.Ordinal);
    public static string ChangeStamp(ModProject project, string profile) => Hash(string.Join("\n", Inputs(project, profile).Select(f => { var info = new FileInfo(f); return f + ":" + info.Length + ":" + info.LastWriteTimeUtc.Ticks; })));
    public static string Fingerprint(ModProject project, string profile) => Hash(string.Join("\n", Inputs(project, profile).Select(f => Relative(project.Root, f) + ":" + Hash(File.ReadAllBytes(f)))));
    public static IntegrationSnapshot Prepare(ModProject project, string profile)
    {
        Require(project.Profiles.Contains(profile), "Unknown runtime profile.");
        var revision = Fingerprint(project, profile);
        string pointer = Pointer(project, profile);
        if (File.Exists(pointer))
        {
            var previous = IntegrationFiles.Read<IntegrationSnapshot>(pointer, 64 * 1024 * 1024);
            if (previous.Revision == revision && previous.Tables.All(t => File.Exists(Inside(previous.Root, t.Key)))) return previous;
        }
        var root = Inside(project.Cache, "integration/" + profile + "/" + revision);
        var settingsFile = Inside(project.Root, "compatibility/" + profile + "/profile.json");
        var settings = File.Exists(settingsFile) ? Read(settingsFile) : new JsonObject();
        var rules = ((JsonArray?)settings["tableOverrides"] ?? []).Select(p => Read(Inside(Path.GetDirectoryName(settingsFile)!, p!.GetValue<string>()))).ToArray();
        var tables = new Dictionary<string, IntegrationTable>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>();
        void Emit(string target, byte[] bytes, string source, string[] ids)
        {
            Require(target.StartsWith("global/excel/", StringComparison.OrdinalIgnoreCase) && target.EndsWith(".txt", StringComparison.OrdinalIgnoreCase), "Invalid table target.");
            Require(tables.TryAdd(target, new(Relative(project.Root, source), ids, Hash(bytes))), "Duplicate table output: " + target);
            AtomicWrite(Inside(root, target), bytes);
        }
        foreach (var file in Files(Path.Combine(project.Root, "source/tables")).Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            var table = TableData.Load(file); var errors = table.Validate(file); if (errors.Count > 0) throw new BuildFailure([.. errors]);
            names.Add(table.Name);
            foreach (var target in (JsonArray)table.Schema["targets"]!)
            {
                var resolved = BuildService.ResolveTable(table, target!.GetValue<string>(), rules);
                Emit(target.GetValue<string>(), resolved.EncodeTsv(), file, table.Records.Select(r => r.S("sourceId")).ToArray());
            }
        }
        foreach (var rule in rules) Require(names.Contains(rule.S("table")), "Unknown profile override table: " + rule.S("table"));
        foreach (var file in Files(Path.Combine(project.Root, "data/global/excel")).Where(f => f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
        {
            var bytes = File.ReadAllBytes(file); var target = Relative(Path.Combine(project.Root, "data"), file);
            var table = TableData.FromTsv(bytes, Path.GetFileNameWithoutExtension(file), target);
            Emit(target, bytes, file, table.Records.Select(r => r.S("sourceId")).ToArray());
        }
        foreach (var asset in (JsonArray?)settings["assetOverrides"] ?? [])
        {
            var target = asset.S("target");
            if (asset.S("scope", "data") != "data" || !target.StartsWith("global/excel/", StringComparison.OrdinalIgnoreCase)) continue;
            Require(asset?["transform"] == null, "Transformed table overrides cannot be used by Level Editor.");
            Require(!tables.TryGetValue(target, out var existing) || existing.Source.StartsWith("data/", StringComparison.Ordinal), "Asset override cannot replace authored table source.");
            Require((tables.GetValueOrDefault(target)?.Sha256) == asset?["expectSha256"]?.GetValue<string>(), "Stale asset override: " + target);
            var file = Inside(Path.GetDirectoryName(settingsFile)!, asset.S("source")); var bytes = File.ReadAllBytes(file);
            var table = TableData.FromTsv(bytes, Path.GetFileNameWithoutExtension(target), target); tables.Remove(target);
            Emit(target, bytes, file, table.Records.Select(r => r.S("sourceId")).ToArray());
        }
        Require(revision == Fingerprint(project, profile), "Tables changed during snapshot creation. Retry.");
        var assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in (JsonArray?)settings["assetOverrides"] ?? [])
        {
            if (asset.S("scope", "data") != "data") continue;
            string target = asset.S("target"); Inside(project.Root, "data/" + target);
            Require(assets.TryAdd(target, asset?["transform"] == null ? Relative(project.Root, Inside(Path.GetDirectoryName(settingsFile)!, asset.S("source"))) : ""), "Competing asset overrides: " + target);
        }
        var result = new IntegrationSnapshot(project.Id, profile, root, revision, tables, assets);
        IntegrationFiles.Write(pointer, result); return result;
    }
    public static TableData? Table(IntegrationSnapshot snapshot, string name, string? baseData)
    {
        string target = "global/excel/" + name + ".txt";
        var file = snapshot.Tables.ContainsKey(target) ? Inside(snapshot.Root, target) : baseData == null ? null : Path.Combine(baseData, target);
        return file != null && File.Exists(file) ? TableData.FromTsv(File.ReadAllBytes(file), name, target) : null;
    }
    public static string Asset(ModProject project, string profile, string logical, string? baseData)
    {
        var file = Inside(project.Root, "data/" + logical);
        var settingsFile = Inside(project.Root, "compatibility/" + profile + "/profile.json");
        var settings = File.Exists(settingsFile) ? Read(settingsFile) : new JsonObject();
        var matches = ((JsonArray?)settings["assetOverrides"] ?? []).Where(a => a.S("scope", "data") == "data" && a.S("target").Equals(logical, StringComparison.OrdinalIgnoreCase)).ToArray();
        Require(matches.Length < 2, "Competing asset overrides: " + logical);
        if (matches.Length == 1)
        {
            var asset = matches[0]!;
            Require(asset["transform"] == null, "This scene uses a generated asset transform. Edit its source in Studio first: " + logical);
            Require((File.Exists(file) ? Hash(File.ReadAllBytes(file)) : null) == asset?["expectSha256"]?.GetValue<string>(), "Stale asset override: " + logical);
            return Inside(Path.GetDirectoryName(settingsFile)!, asset.S("source"));
        }
        return File.Exists(file) || baseData == null ? file : IntegrationFiles.Inside(baseData, logical);
    }
    public static LevelSceneChoice Scene(ModProject project, string profile, string map, string? baseData)
    {
        map = map.Replace('\\', '/');
        if (map.StartsWith("global/tiles/", StringComparison.OrdinalIgnoreCase)) map = map[13..];
        string logicalMap = "global/tiles/" + map;
        string preset = "hd/env/preset/" + Path.ChangeExtension(map, ".json").Replace('\\', '/');
        var json = Asset(project, profile, preset, baseData); var ds1 = Asset(project, profile, logicalMap, baseData);
        Require(File.Exists(json), "No paired HD preset was found for " + map + ". Open its preset from the project explorer or configure its pair in Level Editor.");
        Require(File.Exists(ds1), "Missing map: " + ds1);
        return new(map, json, ds1, logicalMap, !Contains(project.Root, json) || !Contains(project.Root, ds1));
    }
    public static LevelSceneChoice SceneFile(ModProject project, string profile, string file, string? baseData)
    {
        file = IntegrationFiles.Inside(project.Root, file);
        foreach (var suffix in new[] { ".rle-project.json", ".rle-links.json", ".rle-groups.json" })
            if (file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { file = file[..^suffix.Length]; break; }
        if (file.EndsWith(".ds1", StringComparison.OrdinalIgnoreCase))
        {
            var logical = Relative(Path.Combine(project.Root, "data"), file);
            Require(logical.StartsWith("global/tiles/", StringComparison.OrdinalIgnoreCase), "Open the preset paired with this profile-specific DS1.");
            var matched = new List<string>();
            foreach (var sidecar in Files(Path.Combine(project.Root, "data/hd/env/preset")).Where(p => p.EndsWith(".rle-project.json", StringComparison.OrdinalIgnoreCase)))
            {
                var metadata = Read(sidecar);
                if (metadata.S("Map").Replace('\\', '/').Equals("data/" + logical, StringComparison.OrdinalIgnoreCase)) matched.Add(sidecar[..^".rle-project.json".Length]);
            }
            Require(matched.Count < 2, "Several level projects use this map. Open the intended preset instead.");
            return matched.Count == 1 ? SceneFile(project, profile, matched[0], baseData) : Scene(project, profile, logical, baseData);
        }
        var relative = Relative(Path.Combine(project.Root, "data"), file);
        Require(relative.StartsWith("hd/env/preset/", StringComparison.OrdinalIgnoreCase), "Select a preset under the project's data/hd/env/preset folder.");
        var logicalMap = "global/tiles/" + Path.ChangeExtension(relative[14..], ".ds1");
        if (File.Exists(file + ".rle-project.json")) logicalMap = Read(file + ".rle-project.json").S("Map")[5..];
        string mapFile = Asset(project, profile, logicalMap, baseData);
        if (File.Exists(file + ".rle-links.json"))
        {
            var metadata = Read(file + ".rle-links.json");
            mapFile = IntegrationFiles.Inside(project.Root, Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, metadata.S("Ds1Path"))));
            var linkedRelative = Relative(Path.Combine(project.Root, "data"), mapFile);
            if (linkedRelative.StartsWith("global/tiles/", StringComparison.OrdinalIgnoreCase)) logicalMap = linkedRelative;
        }
        Require(File.Exists(file) && File.Exists(mapFile), "The preset's saved scene pair is missing.");
        return new(relative, Asset(project, profile, relative, baseData), mapFile, logicalMap, !Contains(project.Root, mapFile));
    }
    public static LevelSceneChoice CopyBasePair(ModProject project, LevelSceneChoice scene)
    {
        string Copy(string source, string logical)
        {
            if (Contains(project.Root, source)) return source;
            var dest = Inside(project.Root, "data/" + logical); Require(!File.Exists(dest), "A project asset appeared while copying. Retry.");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Copy(source, dest);
            foreach (var suffix in new[] { ".rle-project.json", ".rle-links.json", ".rle-groups.json" })
                if (File.Exists(source + suffix)) File.Copy(source + suffix, dest + suffix, false);
            return dest;
        }
        var json = Copy(scene.Preset, "hd/env/preset/" + Path.ChangeExtension(scene.LogicalMap[13..], ".json"));
        var map = Copy(scene.Map, scene.LogicalMap);
        return scene with { Preset = json, Map = map, NeedsCopy = false };
    }
    public static string TableSource(ModProject project, EditorProject context, string name)
    {
        var pointer = Pointer(project, context.Profile);
        if (File.Exists(pointer) && IntegrationFiles.Read<IntegrationSnapshot>(pointer, 64 * 1024 * 1024).Tables.TryGetValue("global/excel/" + name + ".txt", out var table)) return Inside(project.Root, table.Source);
        var authored = TableData.FileFor(project, "tables", name);
        return File.Exists(authored) ? authored : Inside(project.Root, "data/global/excel/" + name + ".txt");
    }
}

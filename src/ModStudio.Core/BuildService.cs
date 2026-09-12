using System.Text.Json.Nodes;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

public record BuildFile(string Path, string Sha256, long Size);
public record BuildResult(string Id, string Profile, string ProjectId, string ModName, string Output, List<BuildFile> Files, string Snapshot, List<Diagnostic>? Diagnostics = null);
public sealed class BuildFailure(List<Diagnostic> diagnostics) : Exception(string.Join(Environment.NewLine, diagnostics.Select(d => d.ToString()))) { public List<Diagnostic> Diagnostics { get; } = diagnostics; }

public static class BuildService
{
    public static BuildResult Build(ModProject project, string profile, CancellationToken token = default, Action<string>? progress = null)
    {
        Require(project.Profiles.Contains(profile), "Unknown runtime profile.");
        using var buildLock = BuildCache.Lock(project); using var pathChecks = PathChecks(); BuildCache.LoadFingerprints(project);
        var id = Guid.NewGuid().ToString("N"); var folder = Inside(project.Cache, "builds/current");
        var snapshot = Inside(folder, "snapshot"); var output = Inside(folder, "output");
        Directory.CreateDirectory(output); Directory.CreateDirectory(snapshot); var hashes = new Dictionary<string, string>();
        var manifest = Inside(folder, "build.json"); if (File.Exists(manifest)) File.Delete(manifest);
        var cacheFile = Inside(folder, "tables.json");
        Dictionary<string, CachedTable> previous;
        try { previous = File.Exists(cacheFile) ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, CachedTable>>(File.ReadAllText(cacheFile), Pretty) ?? [] : []; }
        catch (System.Text.Json.JsonException) { previous = []; }
        var cache = new Dictionary<string, CachedTable>(); int compiled = 0, reused = 0, written = 0;
        try
        {
            progress?.Invoke("Checking source changes…");
            // One enumeration of the previous snapshot answers "is this copy current?" for every source file without a stat each.
            var captured = FileEntries(snapshot).ToDictionary(f => Relative(snapshot, f.FullName), StringComparer.Ordinal);
            foreach (var file in project.SourceEntries())
            {
                token.ThrowIfCancellationRequested(); hashes[file.FullName] = BuildCache.FileHash(file);
                var relative = Relative(project.Root, file.FullName); var target = Inside(snapshot, relative);
                if (BuildCache.CopyChanged(file.FullName, target, hashes[file.FullName], captured.GetValueOrDefault(relative)))
                    Require(BuildCache.FileHash(target) == hashes[file.FullName], "Source changed while capturing build. Retry.");
                captured.Remove(relative);
            }
            foreach (var old in captured.Values) old.Delete();
            // Remove empty table directories too, so deleted tables cannot be resurrected by the cache.
            foreach (var dir in Directory.GetDirectories(snapshot, "*", SearchOption.AllDirectories).OrderByDescending(p => p.Length))
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            // A second enumeration must find the same files with the same fingerprints, or a save landed mid-capture.
            int recounted = 0;
            foreach (var file in project.SourceEntries()) { recounted++; Require(hashes.TryGetValue(file.FullName, out var hash) && BuildCache.FileHash(file) == hash, "Source changed while capturing build. Retry."); }
            Require(recounted == hashes.Count, "Source changed while capturing build. Retry.");
            progress?.Invoke($"Captured {hashes.Count} source files.");
            var profilePath = Inside(snapshot, $"compatibility/{profile}/profile.json");
            var settings = File.Exists(profilePath) ? Read(profilePath) : new JsonObject { ["schemaVersion"] = 1, ["id"] = profile, ["stringMode"] = profile == "standard" ? "standard" : "full" };
            Require(settings.I("schemaVersion") == 1 && settings.S("id") == profile, "Invalid profile schema or identity.");
            Require(settings.S("stringMode") is "standard" or "full", "Invalid string mode.");
            var modInfoPath = Inside(snapshot, "modinfo.json");
            var modInfo = File.Exists(modInfoPath) ? Read(modInfoPath) : new JsonObject { ["name"] = project.Name, ["version"] = "1.0.0", ["savepath"] = project.Name + "/" };
            var rules = ((JsonArray?)settings["tableOverrides"] ?? []).Select(p => Read(Inside(Path.GetDirectoryName(profilePath)!, p!.GetValue<string>()))).ToArray();
            var contextKey = Hash(typeof(BuildService).Module.ModuleVersionId + project.Name + profile + Json(settings) + string.Join("", rules.Select(Json)));
            var generated = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var diagnostics = new List<Diagnostic>(); var tableNames = new HashSet<string>(); var stringIds = new HashSet<int>();
            void Emit(string relative, byte[] bytes)
            {
                Require(generated.Add(relative), $"Duplicate generated target: {relative}"); var target = Inside(output, relative); if (BuildCache.WriteChanged(target, bytes)) written++;
            }
            var dataPrefix = project.Name + ".mpq/data/";
            foreach (var kind in new[] { "tables", "strings" })
            {
                var sourceRoot = Path.Combine(snapshot, "source", kind); if (!Directory.Exists(sourceRoot)) continue;
                foreach (var dir in Directory.GetDirectories(sourceRoot).Order(StringComparer.Ordinal))
                {
                    token.ThrowIfCancellationRequested(); var file = Path.Combine(dir, "records.json"); var original = Inside(project.Root, Relative(snapshot, file));
                    try
                    {
                        Require(!Directory.Exists(Path.Combine(dir, "records")), "Individual records/ folders must be consolidated before opening this project.");
                        var cacheId = Relative(snapshot, dir);
                        var cacheKey = Hash(contextKey + BuildCache.FileHash(file) + BuildCache.FileHash(Path.Combine(dir, "schema.json")));
                        if (previous.TryGetValue(cacheId, out var saved) && saved.Key == cacheKey && saved.Files.All(f => File.Exists(Inside(output, f.Path)) && BuildCache.FileHash(Inside(output, f.Path)) == f.Sha256))
                        {
                            foreach (var savedFile in saved.Files) Require(generated.Add(savedFile.Path), "Duplicate generated target: " + savedFile.Path);
                            foreach (var stringId in saved.StringIds) Require(stringIds.Add(stringId), $"Duplicate global string ID {stringId}");
                            if (kind == "tables") tableNames.Add(saved.Name);
                            cache[cacheId] = saved; reused++; continue;
                        }
                        var beforeTargets = generated.ToHashSet(StringComparer.OrdinalIgnoreCase);
                        compiled++;
                        progress?.Invoke($"Checking {Path.GetFileName(dir)}…"); var table = TableData.Load(file); var issues = table.Validate(original); diagnostics.AddRange(issues); if (issues.Count > 0) continue;
                        if (kind == "strings")
                        {
                            foreach (var row in table.Records) Require(stringIds.Add(row.I("id")), $"Duplicate global string ID {row.I("id")}");
                            var target = table.Schema.S("target"); Require(target.StartsWith("local/lng/strings/", StringComparison.OrdinalIgnoreCase) && target.EndsWith(".json", StringComparison.OrdinalIgnoreCase), "Invalid string output target.");
                            Emit(dataPrefix + target, table.EncodeCatalog(settings.S("stringMode") == "standard"));
                        }
                        else
                        {
                            tableNames.Add(table.Name);
                            foreach (var targetNode in (JsonArray)table.Schema["targets"]!)
                            {
                                var target = targetNode!.GetValue<string>(); Require(target.StartsWith("global/excel/", StringComparison.OrdinalIgnoreCase) && target.EndsWith(".txt", StringComparison.OrdinalIgnoreCase), "Invalid table output target.");
                                var applicable = rules.Where(r => r.S("table") == table.Name).ToArray();
                                var resolved = applicable.Length == 0 ? table : new TableData(table.Schema, (JsonArray)table.Records.DeepClone());
                                var occupied = new HashSet<string>();
                                foreach (var rule in applicable)
                                {
                                    Require(rule.S("reason").Length > 0, "Override needs a reason.");
                                    if (rule["targets"] is JsonArray targets)
                                    {
                                        Require(targets.Count > 0 && targets.All(t => ((JsonArray)table.Schema["targets"]!).Any(x => x!.GetValue<string>() == t!.GetValue<string>())), "Override references unknown bank.");
                                        if (!targets.Any(t => t!.GetValue<string>() == target)) continue;
                                    }
                                    var index = Enumerable.Range(0, resolved.Records.Count).FirstOrDefault(i => resolved.Records[i].S("sourceId") == rule.S("record"), -1);
                                    Require(index >= 0, $"Unknown override record {rule.S("record")}");
                                    Require(rule["changes"] is JsonObject changes && changes.Count > 0, "Empty override.");
                                    foreach (var change in rule["changes"]!.AsObject())
                                    {
                                        Require(occupied.Add(index + ":" + change.Key), "Competing overrides for the same cell.");
                                        Require(change.Value?["expect"] is JsonValue && change.Value?["value"] is JsonValue, "Override needs string expect/value.");
                                        Require(resolved.Cell(index, change.Key) == change.Value.S("expect"), $"Stale override: {rule.S("record")}/{change.Key}");
                                        resolved.SetCell(index, change.Key, change.Value.S("value"));
                                    }
                                }
                                Emit(dataPrefix + target, resolved.EncodeTsv());
                            }
                        }
                        cache[cacheId] = new(cacheKey, table.Name, kind == "strings" ? table.Records.Select(r => r.I("id")).ToArray() : [],
                            generated.Where(p => !beforeTargets.Contains(p)).Select(p => new BuildFile(p, BuildCache.FileHash(Inside(output, p)), new FileInfo(Inside(output, p)).Length)).ToList());
                    }
                    catch (Exception e) when (e is not OperationCanceledException) { diagnostics.Add(new(original, e.Message)); }
                }
            }
            foreach (var rule in rules) if (!tableNames.Contains(rule.S("table"))) diagnostics.Add(new(Inside(project.Root, Relative(snapshot, profilePath)), "Unknown override table: " + rule.S("table")));
            if (diagnostics.Count > 0) throw new BuildFailure(diagnostics);
            var semanticDiagnostics = CheckedSemantics(project, snapshot, Inside(folder, "semantics.json"), hashes, token);
            if (semanticDiagnostics.Any(d => d.Severity == "Error")) throw new BuildFailure(semanticDiagnostics);
            foreach (var textFile in Files(Path.Combine(snapshot, "source/text")))
            {
                var text = Read(textFile); Require(text.I("schemaVersion") == 1, "Unknown text asset schema.");
                Emit(dataPrefix + text.S("target"), Utf8.GetBytes(text.S("content")));
            }
            var emitted = FileEntries(output).ToDictionary(f => Relative(output, f.FullName), StringComparer.OrdinalIgnoreCase);
            foreach (var file in FileEntries(Path.Combine(snapshot, "data")))
            {
                token.ThrowIfCancellationRequested(); if (new[] { ".bat", ".ps1", ".py", ".mjs", ".bak", ".log" }.Contains(file.Extension.ToLowerInvariant())) continue;
                var relative = dataPrefix + Relative(Path.Combine(snapshot, "data"), file.FullName);
                Require(generated.Add(relative), "Duplicate generated target: " + relative);
                if (BuildCache.CopyChanged(file.FullName, Inside(output, relative), BuildCache.FileHash(file), emitted.GetValueOrDefault(relative))) written++;
            }
            var assetTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in (JsonArray?)settings["assetOverrides"] ?? [])
            {
                token.ThrowIfCancellationRequested(); Require(asset.S("reason").Length > 0, "Asset override needs a reason.");
                var scope = asset.S("scope", "data"); Require(scope is "data" or "mod", "Unknown asset scope."); var target = asset.S("target");
                if (scope == "mod") Require(profile == "d2rl" && target.StartsWith("d2rloader/") && !target.StartsWith("d2rloader/config/") && !target.StartsWith("d2rloader/logs/"), "Invalid loader asset scope.");
                var relative = scope == "data" ? dataPrefix + target : target;
                Require(assetTargets.Add(relative), "Competing asset overrides.");
                // Existing native data assets may be overridden; generated source may not.
                Require(scope != "data" || !generated.Contains(relative) || File.Exists(Inside(snapshot, "data/" + target)), "Asset override cannot replace generated source.");
                var destination = Inside(output, relative); var expected = asset?["expectSha256"]?.GetValue<string>();
                Require((generated.Contains(relative) && File.Exists(destination) ? BuildCache.FileHash(destination) : null) == expected, "Stale asset override: " + target);
                var bytes = File.ReadAllBytes(Inside(Path.GetDirectoryName(profilePath)!, asset.S("source")));
                if (asset?["transform"] != null)
                {
                    Require(asset.S("transform") == "json-template", "Unsupported asset transform.");
                    var template = JsonNode.Parse(Utf8.GetString(bytes).TrimStart('\uFEFF'))!;
                    bytes = Utf8.GetBytes(Json(Template(template, modInfo.S("version"))));
                }
                if (BuildCache.WriteChanged(destination, bytes)) written++; generated.Add(relative);
            }
            Emit(project.Name + ".mpq/modinfo.json", File.Exists(modInfoPath) ? File.ReadAllBytes(modInfoPath) : Utf8.GetBytes(Json(modInfo)));
            token.ThrowIfCancellationRequested();
            progress?.Invoke("Writing build manifest…");
            foreach (var old in Files(output)) if (!generated.Contains(Relative(output, old))) File.Delete(old);
            var entries = FileEntries(output).Select(f => new BuildFile(Relative(output, f.FullName), BuildCache.FileHash(f), f.Length)).ToList();
            var result = new BuildResult(id, profile, project.Id, project.Name, output, entries, snapshot, semanticDiagnostics);
            AtomicWrite(Inside(folder, "build.json"), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result, Pretty));
            AtomicWrite(cacheFile, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(cache, Pretty));
            BuildCache.SaveFingerprints(project);
            BuildCache.RemoveOldBuilds(project, progress);
            progress?.Invoke($"Built {entries.Count} files · {profile} · {id[..8]} · {compiled} tables converted, {reused} reused, {written} output files written"); return result;
        }
        catch { if (File.Exists(manifest)) File.Delete(manifest); throw; }
    }
    /// <summary>
    /// Runs the semantic check against the snapshot, unless the rules and every table they read are unchanged since the
    /// cached result. Keyed on the source hashes already computed for the capture, so editing an unrelated table costs nothing.
    /// </summary>
    private static List<Diagnostic> CheckedSemantics(ModProject project, string snapshot, string cacheFile, Dictionary<string, string> sourceHashes, CancellationToken token)
    {
        var snapshotProject = new ModProject(snapshot, project.Id, project.Name);
        string key;
        try
        {
            var rules = Semantics.Rules(snapshotProject);
            var involved = rules.Select(r => r.Table).Concat(rules.SelectMany(r => r.ReferenceTables ?? [])).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .SelectMany(name => new[] { $"source/tables/{name}/records.json", $"source/tables/{name}/schema.json" }).Append("source/semantics.json");
            key = Hash(typeof(BuildService).Module.ModuleVersionId + string.Join("\n", involved.Select(relative => relative + "=" + sourceHashes.GetValueOrDefault(Inside(project.Root, relative), "missing"))));
        }
        catch (Exception e) when (e is not OperationCanceledException) { key = ""; } // invalid rules: Check reports the problem itself, uncached
        try
        {
            if (key.Length > 0 && File.Exists(cacheFile) && System.Text.Json.JsonSerializer.Deserialize<CachedSemantics>(File.ReadAllText(cacheFile), Pretty) is { } saved && saved.Key == key)
                return saved.Diagnostics.Select(d => d with { File = Inside(project.Root, d.File) }).ToList();
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidDataException) { }
        var semantic = Semantics.Check(snapshotProject, token: token);
        var relativeDiagnostics = semantic.Diagnostics.Select(d => d with { File = Relative(snapshot, d.File) }).ToList();
        if (key.Length > 0) AtomicWrite(cacheFile, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new CachedSemantics(key, relativeDiagnostics, semantic.Rules, semantic.Cells), Pretty));
        return relativeDiagnostics.Select(d => d with { File = Inside(project.Root, d.File) }).ToList();
    }
    private static JsonNode Template(JsonNode node, string version)
    {
        if (node is JsonValue v && v.TryGetValue<string>(out var text) && text.Contains("${")) { Require(text == "${mod.version}", "Unknown template variable."); return JsonValue.Create(version)!; }
        if (node is JsonObject obj) return new JsonObject(obj.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value == null ? null : Template(p.Value, version))));
        if (node is JsonArray array) return new JsonArray(array.Select(n => n == null ? null : Template(n, version)).ToArray());
        return node.DeepClone();
    }
}

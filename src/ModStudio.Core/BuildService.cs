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
        var id = Guid.NewGuid().ToString("N"); var folder = Inside(project.Cache, "builds/" + id);
        var snapshot = Inside(folder, "snapshot"); var output = Inside(folder, "output");
        Directory.CreateDirectory(output); var hashes = new Dictionary<string, string>();
        try
        {
            progress?.Invoke("Capturing consistent source snapshot…");
            foreach (var file in project.SourceFiles())
            {
                token.ThrowIfCancellationRequested(); var bytes = File.ReadAllBytes(file); hashes[file] = Hash(bytes);
                var target = Inside(snapshot, Relative(project.Root, file)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllBytes(target, bytes);
            }
            Require(project.SourceFiles().Count() == hashes.Count && hashes.All(p => File.Exists(p.Key) && Hash(File.ReadAllBytes(p.Key)) == p.Value), "Source changed while capturing build. Retry.");
            var profilePath = Inside(snapshot, $"compatibility/{profile}/profile.json");
            var settings = File.Exists(profilePath) ? Read(profilePath) : new JsonObject { ["schemaVersion"] = 1, ["id"] = profile, ["stringMode"] = profile == "standard" ? "standard" : "full" };
            Require(settings.I("schemaVersion") == 1 && settings.S("id") == profile, "Invalid profile schema or identity.");
            Require(settings.S("stringMode") is "standard" or "full", "Invalid string mode.");
            var modInfoPath = Inside(snapshot, "modinfo.json");
            var modInfo = File.Exists(modInfoPath) ? Read(modInfoPath) : new JsonObject { ["name"] = project.Name, ["version"] = "1.0.0", ["savepath"] = project.Name + "/" };
            var rules = ((JsonArray?)settings["tableOverrides"] ?? []).Select(p => Read(Inside(Path.GetDirectoryName(profilePath)!, p!.GetValue<string>()))).ToArray();
            var generated = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var diagnostics = new List<Diagnostic>(); var tableNames = new HashSet<string>(); var stringIds = new HashSet<int>();
            void Emit(string relative, byte[] bytes)
            {
                Require(generated.Add(relative), $"Duplicate generated target: {relative}"); var target = Inside(output, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllBytes(target, bytes);
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
                    }
                    catch (Exception e) when (e is not OperationCanceledException) { diagnostics.Add(new(original, e.Message)); }
                }
            }
            foreach (var rule in rules) if (!tableNames.Contains(rule.S("table"))) diagnostics.Add(new(Inside(project.Root, Relative(snapshot, profilePath)), "Unknown override table: " + rule.S("table")));
            if (diagnostics.Count > 0) throw new BuildFailure(diagnostics);
            var semantic = Semantics.Check(new ModProject(snapshot, project.Id, project.Name), token: token);
            var semanticDiagnostics = semantic.Diagnostics.Select(d => d with { File = Inside(project.Root, Relative(snapshot, d.File)) }).ToList();
            if (semanticDiagnostics.Any(d => d.Severity == "Error")) throw new BuildFailure(semanticDiagnostics);
            foreach (var textFile in Files(Path.Combine(snapshot, "source/text")))
            {
                var text = Read(textFile); Require(text.I("schemaVersion") == 1, "Unknown text asset schema.");
                Emit(dataPrefix + text.S("target"), Utf8.GetBytes(text.S("content")));
            }
            foreach (var file in Files(Path.Combine(snapshot, "data")))
            {
                token.ThrowIfCancellationRequested(); if (new[] { ".bat", ".ps1", ".py", ".mjs", ".bak", ".log" }.Contains(Path.GetExtension(file).ToLowerInvariant())) continue;
                Emit(dataPrefix + Relative(Path.Combine(snapshot, "data"), file), File.ReadAllBytes(file));
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
                Require((File.Exists(destination) ? Hash(File.ReadAllBytes(destination)) : null) == expected, "Stale asset override: " + target);
                var bytes = File.ReadAllBytes(Inside(Path.GetDirectoryName(profilePath)!, asset.S("source")));
                if (asset?["transform"] != null)
                {
                    Require(asset.S("transform") == "json-template", "Unsupported asset transform.");
                    var template = JsonNode.Parse(Utf8.GetString(bytes).TrimStart('\uFEFF'))!;
                    bytes = Utf8.GetBytes(Json(Template(template, modInfo.S("version"))));
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.WriteAllBytes(destination, bytes); generated.Add(relative);
            }
            Emit(project.Name + ".mpq/modinfo.json", File.Exists(modInfoPath) ? File.ReadAllBytes(modInfoPath) : Utf8.GetBytes(Json(modInfo)));
            token.ThrowIfCancellationRequested(); var entries = Files(output).Select(f => new BuildFile(Relative(output, f), Hash(File.ReadAllBytes(f)), new FileInfo(f).Length)).ToList();
            var result = new BuildResult(id, profile, project.Id, project.Name, output, entries, snapshot, semanticDiagnostics);
            AtomicWrite(Inside(folder, "build.json"), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result, Pretty));
            progress?.Invoke($"Built {entries.Count} files · {profile} · {id[..8]}"); return result;
        }
        catch { if (Directory.Exists(folder)) Directory.Delete(folder, true); throw; }
    }
    private static JsonNode Template(JsonNode node, string version)
    {
        if (node is JsonValue v && v.TryGetValue<string>(out var text) && text.Contains("${")) { Require(text == "${mod.version}", "Unknown template variable."); return JsonValue.Create(version)!; }
        if (node is JsonObject obj) return new JsonObject(obj.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value == null ? null : Template(p.Value, version))));
        if (node is JsonArray array) return new JsonArray(array.Select(n => n == null ? null : Template(n, version)).ToArray());
        return node.DeepClone();
    }
}

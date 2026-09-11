using System.Text.Json.Nodes;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

public record LegacyProject(string Root, string DataRoot, string Name, bool SplitRecords)
{
    public override string ToString() => $"{Name} · {(SplitRecords ? "individual JSON records" : "native data")}\nProject: {Root}\nData: {DataRoot}";
}

public static class LegacyMigration
{
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase) { ".git", ".studio", ".idea", "node_modules", "bin", "obj", "build", "dist" };
    public static List<LegacyProject> Detect(string selected)
    {
        selected = Path.GetFullPath(selected); NoLinks(selected); Require(Directory.Exists(selected), "Choose an existing project folder.");
        bool split = new[] { "tables", "strings" }.Any(kind => Directory.Exists(Path.Combine(selected, "source", kind)) && Directory.GetDirectories(Path.Combine(selected, "source", kind)).Any(dir => Directory.Exists(Path.Combine(dir, "records")) && File.Exists(Path.Combine(dir, "schema.json"))));
        if (split) return [new(selected, Path.Combine(selected, "data"), SuggestedName(selected), true)];
        if (Directory.Exists(Path.Combine(selected, "source/tables")) && Directory.GetDirectories(Path.Combine(selected, "source/tables")).Any(dir => File.Exists(Path.Combine(dir, "records.json")))) return [];
        var result = new List<LegacyProject>();
        void Add(string root, string data)
        {
            if (!Directory.Exists(data) || !new[] { "global", "hd", "local" }.Any(p => Directory.Exists(Path.Combine(data, p))) || result.Any(p => p.DataRoot == data)) return;
            NoLinks(data); result.Add(new(root, data, SuggestedName(Path.GetDirectoryName(data)!), false));
        }
        if (Directory.Exists(Path.Combine(selected, "global")) || Path.GetFileName(selected).Equals("data", StringComparison.OrdinalIgnoreCase)) Add(selected, selected);
        Add(selected, Path.Combine(selected, "data"));
        foreach (var archive in Directory.GetDirectories(selected).Where(p => p.EndsWith(".mpq", StringComparison.OrdinalIgnoreCase))) Add(selected, Path.Combine(archive, "data"));
        if (result.Count == 0 && Directory.Exists(Path.Combine(selected, "mods")))
            foreach (var mod in Directory.GetDirectories(Path.Combine(selected, "mods")))
                foreach (var archive in Directory.GetDirectories(mod).Where(p => p.EndsWith(".mpq", StringComparison.OrdinalIgnoreCase))) Add(mod, Path.Combine(archive, "data"));
        // Discover nested layouts without traversing generated output, dependencies or links.
        void Find(string directory, int depth)
        {
            if (depth > 12 || result.Any(p => Contains(p.DataRoot, directory))) return;
            foreach (var child in Directory.GetDirectories(directory))
            {
                if (Excluded.Contains(Path.GetFileName(child)) || (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                if (new[] { "global", "hd", "local" }.Any(p => Directory.Exists(Path.Combine(child, p)))) Add(selected, child);
                else Find(child, depth + 1);
            }
        }
        Find(selected, 0);
        return result;
    }
    public static ImportReport MigrateInPlace(LegacyProject legacy, string name, string backup, CancellationToken token = default, Action<string>? progress = null)
    {
        var root = Path.GetFullPath(legacy.Root); backup = Path.GetFullPath(backup);
        Require(Path.GetDirectoryName(root) == Path.GetDirectoryName(backup) && !Directory.Exists(backup) && !File.Exists(backup), "Backup must be an unused sibling folder beside the project.");
        NoLinks(root); NoLinks(backup);
        var prepared = root + ".converted-" + Guid.NewGuid().ToString("N");
        progress?.Invoke("Scanning the original project and repository metadata…");
        var originals = MigrationFiles.Fingerprint(MigrationFiles.Enumerate(root, token, skipBuildCache: true), root, "Scanning original project (old .studio/builds stay in backup)", token, progress);
        try
        {
            // Validate once, after supporting files have been preserved and before replacing the root.
            var result = MigrateCore(legacy, prepared, name, token, progress, verifyProfiles: false);
            progress?.Invoke("Preserving repository metadata and supporting project files…");
            MigrationFiles.Run(originals.Keys, root, "Preserving repository metadata and supporting project files", token, progress, (original, reportBytes) =>
            {
                var relative = Relative(root, original);
                token.ThrowIfCancellationRequested();
                NoLinks(original);
                if (legacy.SplitRecords && (relative.StartsWith("source/tables/", StringComparison.Ordinal) || relative.StartsWith("source/strings/", StringComparison.Ordinal))) return;
                bool nativeData = Contains(legacy.DataRoot, original) && (legacy.DataRoot != root || new[] { "global/", "hd/", "local/" }.Any(p => relative.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
                if (nativeData || relative is "modinfo.json" or "mod-project.json" or "migration-report.json") return;
                var target = Inside(prepared, relative);
                if ((relative.StartsWith("source/", StringComparison.Ordinal) || relative.StartsWith("compatibility/", StringComparison.Ordinal)) && File.Exists(target)) return;
                if (relative == ".gitignore" && File.Exists(target)) { File.WriteAllText(target, File.ReadAllText(original) + "\n.studio/\n"); return; }
                MigrationFiles.Copy(original, target, token, reportBytes);
            });
            var migrationReport = Read(Inside(prepared, "migration-report.json"));
            migrationReport["mode"] = "convert-existing"; migrationReport["backupDirectory"] = backup;
            migrationReport["note"] = "Converted at the original project path. Original files retained in backup, including old .studio/builds caches (not copied into the converted project). Git metadata and supporting project files preserved in the converted project.";
            WriteJson(Inside(prepared, "migration-report.json"), migrationReport);
            foreach (var profile in result.Project.Profiles) { token.ThrowIfCancellationRequested(); progress?.Invoke("Verifying preserved project profile: " + profile); BuildService.Build(result.Project, profile, token, message => progress?.Invoke(profile + ": " + message)); }
            var cache = Inside(prepared, ".studio/builds"); if (Directory.Exists(cache)) Directory.Delete(cache, true);
            // A second fingerprint check also covers Git metadata and files excluded from migration input.
            MigrationFiles.Verify(originals, MigrationFiles.Enumerate(root, token, skipBuildCache: true), root, token, progress, "Project changed during conversion. Retry when other tools have finished writing.");
            token.ThrowIfCancellationRequested();
            progress?.Invoke("Keeping original project at " + backup);
            Directory.Move(root, backup);
            try { Directory.Move(prepared, root); }
            catch { Directory.Move(backup, root); throw; }
            return result with { Project = ModProject.Open(root) };
        }
        finally { if (Directory.Exists(prepared)) Directory.Delete(prepared, true); }
    }
    private static string SuggestedName(string root)
    {
        var info = Path.Combine(root, "modinfo.json");
        if (File.Exists(info)) { try { var name = Read(info).S("name"); ModProject.ValidateName(name); return name; } catch (Exception) { } }
        var fallback = Path.GetFileName(root).Replace(".mpq", "", StringComparison.OrdinalIgnoreCase);
        try { ModProject.ValidateName(fallback); return fallback; } catch (Exception) { return "MyMod"; }
    }
    private static IEnumerable<string> Inputs(string root, bool projectRoot = true)
    {
        NoLinks(root);
        foreach (var file in Directory.GetFiles(root).Order(StringComparer.Ordinal)) { NoLinks(file); yield return file; }
        foreach (var dir in Directory.GetDirectories(root).Order(StringComparer.Ordinal))
            if (!(projectRoot && Excluded.Contains(Path.GetFileName(dir))) && !Path.GetFileName(dir).Equals(".git", StringComparison.OrdinalIgnoreCase)) foreach (var file in Inputs(dir, false)) yield return file;
    }
    public static ImportReport Migrate(LegacyProject legacy, string destination, string name, CancellationToken token = default, Action<string>? progress = null)
        => MigrateCore(legacy, destination, name, token, progress, verifyProfiles: true);

    private static ImportReport MigrateCore(LegacyProject legacy, string destination, string name, CancellationToken token, Action<string>? progress, bool verifyProfiles)
    {
        destination = Path.GetFullPath(destination); NoLinks(destination); ModProject.ValidateName(name);
        Require(!Contains(legacy.Root, destination) && !Contains(destination, legacy.Root), "Migration must use a separate destination.");
        Require(!Directory.Exists(destination) || !Directory.EnumerateFileSystemEntries(destination).Any(), "Migration destination must be new or empty.");
        var stage = destination + ".migration-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.GetDirectoryName(stage)!);
        var inputs = MigrationFiles.Fingerprint(Inputs(legacy.Root), legacy.Root, "Scanning migration inputs", token, progress);
        int tables = 0, catalogs = 0, assets = 0, verified = 0, retained = 0;
        try
        {
            if (!legacy.SplitRecords)
            {
                progress?.Invoke("Converting native data into table JSON…");
                var imported = ProjectImporter.Import(legacy.DataRoot, stage, name, token, progress);
                tables = imported.Tables; catalogs = imported.Catalogs; assets = imported.Assets; verified = imported.VerifiedTables;
                progress?.Invoke("Preserving additional project files…");
                MigrationFiles.Run(inputs.Keys.Where(f => !Contains(legacy.DataRoot, f)), legacy.Root, "Preserving additional project files", token, progress, (file, reportBytes) =>
                {
                    token.ThrowIfCancellationRequested(); var target = Inside(stage, "legacy/" + Relative(legacy.Root, file));
                    MigrationFiles.Copy(file, target, token, reportBytes); Interlocked.Increment(ref retained);
                });
            }
            else
            {
                progress?.Invoke("Copying legacy project files…");
                Directory.CreateDirectory(stage);
                MigrationFiles.Run(inputs.Keys, legacy.Root, "Copying legacy project files", token, progress, (file, reportBytes) =>
                {
                    token.ThrowIfCancellationRequested(); var target = Inside(stage, Relative(legacy.Root, file));
                    MigrationFiles.Copy(file, target, token, reportBytes);
                });
                foreach (var kind in new[] { "tables", "strings" })
                {
                    var source = Path.Combine(stage, "source", kind); if (!Directory.Exists(source)) continue;
                    foreach (var dir in Directory.GetDirectories(source))
                    {
                        progress?.Invoke("Consolidating " + kind + ": " + Path.GetFileName(dir));
                        token.ThrowIfCancellationRequested(); var rowsDirectory = Path.Combine(dir, "records"); var recordFile = Path.Combine(dir, "records.json");
                        if (Directory.Exists(rowsDirectory))
                        {
                            Require(!File.Exists(recordFile), "Project contains both consolidated and individual records: " + dir);
                            var records = Files(rowsDirectory).Select(file => Read(file)).OrderBy(r => r.I("order", -1)).ToArray();
                            var model = new TableData((JsonObject)Read(Path.Combine(dir, "schema.json")), new JsonArray(records));
                            Require(model.Validate(recordFile).Count == 0, "Legacy records need review before consolidation: " + dir);
                            WriteJson(recordFile, model.Records); Directory.Delete(rowsDirectory, true);
                        }
                        var table = TableData.Load(recordFile); Require(table.Validate(recordFile).Count == 0, "Invalid legacy table: " + dir);
                        if (kind == "tables")
                        {
                            tables++;
                            foreach (var target in (JsonArray)table.Schema["targets"]!)
                            {
                                var native = Inside(stage, "data/" + target!.GetValue<string>());
                                if (!File.Exists(native)) continue;
                                Require(File.ReadAllBytes(native).SequenceEqual(table.EncodeTsv()), "Native TXT differs from legacy JSON: " + target + ". Reconcile the authoritative version first.");
                                File.Delete(native); verified++;
                            }
                        }
                        else
                        {
                            catalogs++; var native = Inside(stage, "data/" + table.Schema.S("target"));
                            if (File.Exists(native)) { Require(JsonNode.DeepEquals(Read(native), JsonNode.Parse(Utf8.GetString(table.EncodeCatalog(false)).TrimStart('\uFEFF'))), "Native catalog differs from legacy JSON."); File.Delete(native); }
                        }
                    }
                }
                WriteJson(Inside(stage, "mod-project.json"), new JsonObject { ["schemaVersion"] = 1, ["id"] = Guid.NewGuid().ToString(), ["name"] = name });
                var ignore = Inside(stage, ".gitignore"); File.AppendAllText(ignore, "\n.studio/\n");
                foreach (var textFile in Files(Path.Combine(stage, "source/text")))
                {
                    var text = Read(textFile); var native = Inside(stage, "data/" + text.S("target"));
                    if (!File.Exists(native)) continue;
                    Require(File.ReadAllBytes(native).SequenceEqual(Utf8.GetBytes(text.S("content"))), "Native text differs from legacy source: " + text.S("target"));
                    File.Delete(native); verified++;
                }
                assets = Files(Path.Combine(stage, "data")).Count();
            }
            var metadataRoot = legacy.DataRoot == legacy.Root ? legacy.Root : Path.GetDirectoryName(legacy.DataRoot)!;
            progress?.Invoke("Writing project metadata and migration report…");
            var originalInfo = new[] { Path.Combine(metadataRoot, "modinfo.json"), Path.Combine(legacy.Root, "modinfo.json") }.FirstOrDefault(File.Exists);
            var info = originalInfo == null ? new JsonObject { ["version"] = "1.0.0", ["savepath"] = name + "/" } : Read(originalInfo);
            if (info.S("name", name) != name) info["savepath"] = name + "/";
            info["name"] = name; WriteJson(Inside(stage, "modinfo.json"), info);
            foreach (var profile in new[] { "standard", "d2rl" })
            {
                var path = Inside(stage, $"compatibility/{profile}/profile.json");
                if (!File.Exists(path)) WriteJson(path, new JsonObject { ["schemaVersion"] = 1, ["id"] = profile, ["stringMode"] = profile == "standard" ? "standard" : "full", ["tableOverrides"] = new JsonArray(), ["assetOverrides"] = new JsonArray() });
            }
            WriteJson(Inside(stage, "migration-report.json"), new JsonObject { ["schemaVersion"] = 1, ["tables"] = tables, ["catalogs"] = catalogs, ["verifiedTxtFiles"] = verified, ["retainedLegacyFiles"] = retained, ["note"] = "Original project unchanged. Files under legacy/ are preserved for review and are not deployed automatically. Git history and local build/cache folders are excluded.", ["files"] = new JsonArray(inputs.Select(p => (JsonNode?)new JsonObject { ["path"] = Relative(legacy.Root, p.Key), ["sha256"] = p.Value }).ToArray()) });
            var stagedProject = ModProject.Open(stage);
            if (verifyProfiles)
                foreach (var profile in stagedProject.Profiles) { progress?.Invoke("Verifying migrated profile: " + profile); BuildService.Build(stagedProject, profile, token, message => progress?.Invoke(profile + ": " + message)); }
            // These are only the verification builds created above in this new staging folder.
            var validationCache = Inside(stage, ".studio"); if (Directory.Exists(validationCache)) Directory.Delete(validationCache, true);
            token.ThrowIfCancellationRequested();
            progress?.Invoke("Checking original files and finalizing the new project…");
            MigrationFiles.Verify(inputs, Inputs(legacy.Root), legacy.Root, token, progress, "Legacy source changed during migration. Retry from a consistent copy.");
            if (Directory.Exists(destination)) Directory.Delete(destination); Directory.Move(stage, destination);
            return new(ModProject.Open(destination), tables, catalogs, assets, verified);
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }
}

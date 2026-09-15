using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

public sealed record ModProject(string Root, string Id, string Name)
{
    public string Cache => Inside(Root, ".studio");
    public IEnumerable<string> Profiles => Directory.Exists(Path.Combine(Root, "compatibility")) ? Directory.GetDirectories(Path.Combine(Root, "compatibility")).Select(Path.GetFileName).OfType<string>().Order() : ["standard", "d2rl"];
    public string ModFolder => Name;
    /// <summary>Locale codes D2R ships string tables for, in the game's order.</summary>
    public static readonly string[] GameLocales = ["enUS", "zhTW", "deDE", "esES", "frFR", "itIT", "koKR", "plPL", "esMX", "jaJP", "ptBR", "ruRU", "zhCN"];
    /// <summary>Locales in declaration order within catalogs sorted by path; the game's standard list when no catalog declares any.</summary>
    public IReadOnlyList<string> Locales()
    {
        var found = new List<string>();
        var strings = Path.Combine(Root, "source/strings");
        if (Directory.Exists(strings))
            foreach (var catalog in Directory.EnumerateFiles(strings, "*.json").Order(StringComparer.Ordinal))
                try { foreach (var locale in (Read(catalog)["schema"]?["locales"] as JsonArray)?.Select(x => x?.GetValue<string>()).OfType<string>() ?? []) if (!found.Contains(locale)) found.Add(locale); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException) { }
        return found.Count > 0 ? found : GameLocales;
    }
    public static ModProject Open(string root)
    {
        root = Path.GetFullPath(root); NoLinks(root); Require(Directory.Exists(root), "Project folder does not exist.");
        var manifest = Path.Combine(root, "mod-project.json");
        if (File.Exists(manifest))
        {
            var node = Read(manifest); Require(node.I("schemaVersion") is 1 or ProjectLayout.Version, "Unsupported project version."); ValidateName(node.S("name"));
            return new(root, node.S("id"), node.S("name"));
        }
        Require(Directory.Exists(Path.Combine(root, "source/tables")) || Directory.Exists(Path.Combine(root, "data")), "Choose a mod project root, or use Import mod.");
        var info = Path.Combine(root, "modinfo.json"); var name = File.Exists(info) ? Read(info).S("name", "MyMod") : "MyMod"; ValidateName(name);
        return new(root, Hash(root)[..24], name);
    }
    public static void ValidateName(string name) => Require(Regex.IsMatch(name, "^[A-Za-z0-9_-]{1,80}$"), "Mod name must use 1–80 letters, digits, underscores or hyphens.");
    public IEnumerable<string> SourceFiles() => SourceEntries().Select(f => f.FullName);
    /// <summary>Build inputs with their enumerated size and write time, so change detection needs no further syscalls per file.</summary>
    public IEnumerable<FileInfo> SourceEntries()
    {
        foreach (var folder in new[] { "source", "data", "compatibility" }) foreach (var file in FileEntries(Path.Combine(Root, folder))) yield return file;
        foreach (var name in new[] { "modinfo.json", "mod-project.json" }) if (new FileInfo(Path.Combine(Root, name)) is { Exists: true } file) yield return file;
    }
}

public record ImportReport(ModProject Project, int Tables, int Catalogs, int Assets, int VerifiedTables);
public static class ProjectImporter
{
    public static ImportReport Import(string dataFolder, string destination, string name, CancellationToken token = default, Action<string>? progress = null)
    {
        dataFolder = Path.GetFullPath(dataFolder); destination = Path.GetFullPath(destination); ModProject.ValidateName(name); NoLinks(dataFolder); NoLinks(destination);
        Require(Directory.Exists(dataFolder), "Data folder does not exist.");
        Require(!Contains(dataFolder, destination) && !Contains(destination, dataFolder), "Import destination and original data must not overlap.");
        Require(!Directory.Exists(destination) || !Directory.EnumerateFileSystemEntries(destination).Any(), "Import into a new or empty folder.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var stage = destination + ".import-" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(stage);
        int assets = 0, catalogs = 0, verified = 0; var sourceHashes = new Dictionary<string, string>();
        var tables = new Dictionary<string, TableData>(StringComparer.Ordinal); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // A full game extract is ~150,000 files / 40 GB. Assets are copied as streams and never hashed; the consistency check at
        // the end compares the enumeration's size and write time instead of reading everything a second time.
        var seen = new Dictionary<string, (long Length, long Written)>(StringComparer.Ordinal);
        try
        {
            progress?.Invoke("Scanning " + dataFolder + "…");
            var entries = FileEntries(dataFolder).ToList(); int done = 0;
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested(); var file = entry.FullName; var relative = Relative(dataFolder, file); seen[file] = (entry.Length, entry.LastWriteTimeUtc.Ticks);
                if (++done % 500 == 0 || done == entries.Count) progress?.Invoke($"Importing {done:N0} / {entries.Count:N0} files… {relative}");
                if (Regex.IsMatch(relative, @"^global/excel/(?:base/)?[^/]+\.txt$", RegexOptions.IgnoreCase))
                {
                    var bytes = File.ReadAllBytes(file); sourceHashes[file] = Hash(bytes); progress?.Invoke(relative);
                    var stem = Regex.Replace(Path.GetFileNameWithoutExtension(relative).ToLowerInvariant(), "[^a-z0-9-]", "-");
                    var key = stem + ":" + Hash(bytes);
                    if (!tables.TryGetValue(key, out var table))
                    {
                        var tableName = stem + (relative.Contains("/base/", StringComparison.OrdinalIgnoreCase) ? "-base" : "");
                        table = TableData.FromTsv(bytes, tableName, relative); tables.Add(key, table);
                    }
                    else
                    {
                        ((JsonArray)table.Schema["targets"]!).Add(relative);
                        if (!relative.Contains("/base/", StringComparison.OrdinalIgnoreCase)) table.Schema["name"] = stem;
                    }
                    Require(table.EncodeTsv().SequenceEqual(bytes), $"Lossy conversion: {relative}"); verified++;
                    continue;
                }
                if (relative.StartsWith("local/lng/strings/", StringComparison.OrdinalIgnoreCase) && relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    var bytes = File.ReadAllBytes(file); sourceHashes[file] = Hash(bytes); progress?.Invoke(relative);
                    if (TryCatalog(bytes, Path.GetFileNameWithoutExtension(relative), relative) is { } catalog)
                    {
                        Require(names.Add("catalog:" + catalog.Name), $"Duplicate catalog name: {catalog.Name}");
                        TableData.Write(Inside(stage, $"source/strings/{catalog.Name}.json"), catalog); catalogs++;
                        continue;
                    }
                }
                var target = Inside(stage, "data/" + relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target); assets++;
            }
            progress?.Invoke("Writing project files…");
            foreach (var table in tables.Values)
            {
                Require(names.Add(table.Name), $"Duplicate table name: {table.Name}");
                TableData.Write(Inside(stage, $"source/tables/{table.Name}.json"), table);
            }
            WriteJson(Inside(stage, "mod-project.json"), new JsonObject { ["schemaVersion"] = ProjectLayout.Version, ["id"] = Guid.NewGuid().ToString(), ["name"] = name });
            WriteJson(Inside(stage, "modinfo.json"), new JsonObject { ["name"] = name, ["version"] = "1.0.0", ["savepath"] = name + "/" });
            foreach (var profile in new[] { "standard", "d2rl" }) WriteJson(Inside(stage, $"compatibility/{profile}/profile.json"), new JsonObject { ["schemaVersion"] = 1, ["id"] = profile, ["stringMode"] = profile == "standard" ? "standard" : "full", ["tableOverrides"] = new JsonArray(), ["assetOverrides"] = new JsonArray() });
            WriteJson(Inside(stage, "import-report.json"), new JsonObject { ["schemaVersion"] = 1, ["verifiedTables"] = verified, ["assets"] = assets, ["files"] = new JsonArray(sourceHashes.Select(p => (JsonNode?)new JsonObject { ["path"] = Relative(dataFolder, p.Key), ["sha256"] = p.Value }).ToArray()) });
            File.WriteAllText(Inside(stage, ".gitignore"), ".studio/\nbuild/\n*.bak\n");
            File.WriteAllText(Inside(stage, ".gitattributes"), "source/**/*.json text eol=lf\n");
            token.ThrowIfCancellationRequested(); progress?.Invoke("Verifying the original data did not change…");
            int recounted = 0;
            foreach (var entry in FileEntries(dataFolder)) { recounted++; Require(seen.TryGetValue(entry.FullName, out var stamp) && stamp == (entry.Length, entry.LastWriteTimeUtc.Ticks), "Original data changed during import. Retry from a consistent copy."); }
            Require(recounted == seen.Count, "Original data changed during import. Retry from a consistent copy.");
            progress?.Invoke("Moving the project into place…");
            if (Directory.Exists(destination)) Directory.Delete(destination); Directory.Move(stage, destination);
            return new(ModProject.Open(destination), tables.Count, catalogs, assets, verified);
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }
    private static TableData? TryCatalog(byte[] bytes, string name, string target)
    {
        var text = Utf8.GetString(bytes); if (JsonNode.Parse(text.TrimStart('\uFEFF')) is not JsonArray rows || rows.Count == 0) return null;
        if (rows[0] is not JsonObject first || !first.ContainsKey("id") || !first.ContainsKey("Key")) return null;
        var locales = rows.OfType<JsonObject>().SelectMany(row => row.Select(p => p.Key)).Where(k => k is not ("id" or "Key")).Distinct(StringComparer.Ordinal).ToArray();
        var records = new JsonArray();
        foreach (var row in rows)
        {
            Require(row is JsonObject && row["id"] is JsonValue && row["Key"] is JsonValue, $"Inconsistent catalog: {target}");
            var obj = (JsonObject)row!;
            var translations = new JsonObject();
            foreach (var locale in locales)
                if (obj.ContainsKey(locale))
                {
                    Require(obj[locale] is JsonValue value && value.TryGetValue<string>(out _), $"Invalid translation {locale}: {target}");
                    translations[locale] = obj[locale]!.GetValue<string>();
                }
            records.Add(new JsonObject { ["order"] = records.Count, ["id"] = row!["id"]!.GetValue<int>(), ["Key"] = row["Key"]!.GetValue<string>(), ["translations"] = translations });
        }
        var schema = new JsonObject { ["schemaVersion"] = 1, ["category"] = name, ["target"] = target, ["locales"] = new JsonArray(locales.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray()), ["bom"] = text.StartsWith('\uFEFF'), ["newline"] = text.Contains("\r\n") ? "\r\n" : "\n", ["finalNewline"] = text.EndsWith('\n'), ["indent"] = 4 };
        var table = new TableData(schema, records); Require(table.Validate(target).Count == 0, $"Invalid catalog: {target}");
        var encoded = (JsonArray)JsonNode.Parse(Utf8.GetString(table.EncodeCatalog(false)).TrimStart('\uFEFF'))!;
        for (int i = 0; i < rows.Count; i++)
            foreach (var pair in (JsonObject)rows[i]!)
                Require(JsonNode.DeepEquals(pair.Value, encoded[i]?[pair.Key]), $"Catalog conversion changed values: {target}");
        return table;
    }
}

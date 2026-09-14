using System.Text.Json.Nodes;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>
/// Source layout versions. Version 1 kept every table as a folder holding schema.json and records.json; version 2 stores each
/// table or string catalog as one file, source/tables/&lt;name&gt;.json, with the schema and records together.
/// </summary>
public static class ProjectLayout
{
    public const int Version = 2;
    public const string UpgradeAdvice = "This project uses the older folder-per-table layout (schema.json + records.json). Convert it to single-file tables to continue.";
    private static readonly string[] Kinds = ["tables", "strings"];

    /// <summary>Folders that still hold a version 1 table: schema.json beside records.json.</summary>
    public static List<string> LegacyTableFolders(string root)
    {
        var result = new List<string>();
        foreach (var kind in Kinds)
        {
            var parent = Path.Combine(root, "source", kind); if (!Directory.Exists(parent)) continue;
            foreach (var dir in Directory.GetDirectories(parent).Order(StringComparer.Ordinal))
                if (File.Exists(Path.Combine(dir, "schema.json")) && File.Exists(Path.Combine(dir, "records.json"))) result.Add(dir);
        }
        return result;
    }
    public static bool NeedsUpgrade(string root) => LegacyTableFolders(root).Count > 0;

    /// <summary>
    /// Rewrites every version 1 table folder as a single file next to where the folder was, then removes the two old files and
    /// the folder when nothing else is in it. Each table is validated before its folder is touched, and the file is written
    /// before anything is deleted, so an interrupted conversion leaves either form readable.
    /// </summary>
    public static int Upgrade(string root, Action<string>? progress = null, CancellationToken token = default)
    {
        root = Path.GetFullPath(root); NoLinks(root); int converted = 0;
        foreach (var dir in LegacyTableFolders(root))
        {
            token.ThrowIfCancellationRequested(); var name = Path.GetFileName(dir); progress?.Invoke("Converting " + name + "…");
            var file = dir + ".json"; Require(!File.Exists(file), $"Both {name}/ and {name}.json exist; remove one before converting.");
            var table = new TableData((JsonObject)Read(Path.Combine(dir, "schema.json")), (JsonArray)Read(Path.Combine(dir, "records.json")));
            var errors = table.Validate(Path.Combine(dir, "records.json")).Where(d => d.Severity == "Error").ToList();
            if (errors.Count > 0) throw new InvalidDataException($"{name} has errors; fix it in the current layout before converting: {errors[0].Message}");
            TableData.Write(file, table);
            File.Delete(Path.Combine(dir, "schema.json")); File.Delete(Path.Combine(dir, "records.json"));
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            converted++;
        }
        var manifest = Path.Combine(root, "mod-project.json");
        if (File.Exists(manifest)) { var node = Read(manifest); node["schemaVersion"] = Version; WriteJson(manifest, node); }
        return converted;
    }
}

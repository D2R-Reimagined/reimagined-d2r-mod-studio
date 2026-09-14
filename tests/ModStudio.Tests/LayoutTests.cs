using System.Text.Json.Nodes;
using ModStudio.Core;
using static ModStudio.Core.Storage;

internal static class LayoutTests
{
    public static void Run(string root, Action<bool, string> check, Action<Action, string> throws)
    {
        // A version 1 project: one folder per table with schema.json and records.json, plus a stray note in one folder.
        var v1 = Path.Combine(root, "layout-v1"); Directory.CreateDirectory(v1);
        var example = TableData.FromTsv(Utf8.GetBytes("name\tvalue\r\nA\t1\r\nB\t2\r\n"), "example", "global/excel/example.txt");
        var other = TableData.FromTsv(Utf8.GetBytes("code\tx\r\nq\t5\r\n"), "other", "global/excel/other.txt");
        void Folder(string kind, TableData table, JsonObject? schema = null)
        {
            var dir = Path.Combine(v1, "source", kind, table.Name); Directory.CreateDirectory(dir);
            WriteJson(Path.Combine(dir, "schema.json"), schema ?? table.Schema); WriteJson(Path.Combine(dir, "records.json"), table.Records);
        }
        Folder("tables", example); Folder("tables", other); File.WriteAllText(Path.Combine(v1, "source/tables/other/notes.txt"), "keep me");
        var catalog = new TableData(new JsonObject { ["schemaVersion"] = 1, ["category"] = "ui", ["locales"] = new JsonArray("enUS"), ["target"] = "local/lng/strings/ui.json" },
            new JsonArray(new JsonObject { ["order"] = 0, ["id"] = 1, ["Key"] = "k", ["translations"] = new JsonObject { ["enUS"] = "v" } }));
        var catalogDir = Path.Combine(v1, "source/strings/ui"); Directory.CreateDirectory(catalogDir);
        WriteJson(Path.Combine(catalogDir, "schema.json"), catalog.Schema); WriteJson(Path.Combine(catalogDir, "records.json"), catalog.Records);
        WriteJson(Path.Combine(v1, "mod-project.json"), new JsonObject { ["schemaVersion"] = 1, ["id"] = "layout", ["name"] = "Layout" });
        WriteJson(Path.Combine(v1, "modinfo.json"), new JsonObject { ["name"] = "Layout", ["version"] = "1.0.0", ["savepath"] = "Layout/" });
        foreach (var profile in new[] { "standard", "d2rl" }) WriteJson(Path.Combine(v1, $"compatibility/{profile}/profile.json"), new JsonObject { ["schemaVersion"] = 1, ["id"] = profile, ["stringMode"] = "standard", ["tableOverrides"] = new JsonArray() });

        var project = ModProject.Open(v1);
        check(ProjectLayout.NeedsUpgrade(v1) && ProjectLayout.LegacyTableFolders(v1).Count == 3 && LegacyMigration.Detect(v1).Count == 0, "Version 1 projects open, are detected as needing conversion and are not offered for migration again");
        throws(() => BuildService.Build(project, "standard"), "Building a version 1 layout is refused until it is converted");
        var messages = new List<string>(); int converted = ProjectLayout.Upgrade(v1, messages.Add);
        check(converted == 3 && messages.Count == 3, "Upgrade converts every table and catalog folder");
        check(File.Exists(Path.Combine(v1, "source/tables/example.json")) && !Directory.Exists(Path.Combine(v1, "source/tables/example")), "Converted tables become one file and lose their empty folder");
        check(File.Exists(Path.Combine(v1, "source/tables/other.json")) && File.ReadAllText(Path.Combine(v1, "source/tables/other/notes.txt")) == "keep me" && !File.Exists(Path.Combine(v1, "source/tables/other/schema.json")), "Folders holding other files are kept without the old table files");
        check(File.Exists(Path.Combine(v1, "source/strings/ui.json")) && !Directory.Exists(catalogDir), "String catalogs convert the same way");
        check(!ProjectLayout.NeedsUpgrade(v1) && Read(Path.Combine(v1, "mod-project.json")).I("schemaVersion") == ProjectLayout.Version && ModProject.Open(v1).Name == "Layout", "Converted projects record the layout version and still open");
        var reloaded = TableData.Load(Path.Combine(v1, "source/tables/example.json"));
        check(reloaded.Columns.SequenceEqual(["name", "value"]) && reloaded.Cell(1, "name") == "B" && reloaded.Validate("x").Count == 0, "Converted tables keep their schema and rows");
        var doc = new Document(Path.Combine(v1, "source/tables/example.json"));
        check(doc.Table != null && !doc.PendingSource && doc.Text.Contains("\"schema\"") && doc.Text.Contains("\"records\""), "Documents read single-file tables and serialize schema and records together");
        doc.SetCells([(0, "value", "9")]); doc.Save();
        check(TableData.Load(doc.FilePath).Cell(0, "value") == "9" && TableData.Load(doc.FilePath).Schema.S("name") == "example", "Saving a single-file table keeps the schema beside the edited records");
        var build = BuildService.Build(project, "standard");
        check(File.Exists(Path.Combine(build.Output, "Layout.mpq/data/global/excel/example.txt")) && File.ReadAllText(Path.Combine(build.Output, "Layout.mpq/data/global/excel/example.txt")).Contains("A\t9") && File.Exists(Path.Combine(build.Output, "Layout.mpq/data/local/lng/strings/ui.json")), "Converted projects build tables and catalogs");
        check(project.Locales().SequenceEqual(["enUS"]), "Catalog locales are read from single-file catalogs");
        check(ProjectLayout.Upgrade(v1) == 0, "Upgrading a converted project is a no-op");
        throws(() => { Directory.CreateDirectory(Path.Combine(v1, "source/tables/example")); WriteJson(Path.Combine(v1, "source/tables/example/schema.json"), example.Schema); WriteJson(Path.Combine(v1, "source/tables/example/records.json"), example.Records); ProjectLayout.Upgrade(v1); }, "Upgrade refuses to overwrite an existing single-file table");
        Directory.Delete(Path.Combine(v1, "source/tables/example"), true);
        var plain = new Document(Path.Combine(v1, "modinfo.json"));
        check(plain.Table == null && !plain.PendingSource, "Ordinary JSON files are not mistaken for tables");
    }
}

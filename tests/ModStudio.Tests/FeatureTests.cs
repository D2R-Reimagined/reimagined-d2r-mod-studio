using System.Text.Json.Nodes;
using ModStudio.Core;
using static ModStudio.Core.Storage;

internal static class FeatureTests
{
    public static void Run(string root, Action<bool, string> check, Action<Action, string> throws, Action<string, string> write)
    {
        var original = Path.Combine(root, "legacy-mod"); var data = Path.Combine(original, "data");
        write(Path.Combine(data, "global/excel/uniqueitems.txt"), "index\tcode\tlvl\nTest\taxe1\t10\n");
        write(Path.Combine(data, "global/excel/weapons.txt"), "code\ttype\naxe1\tweap\n");
        write(Path.Combine(data, "global/excel/armor.txt"), "code\ttype\narm1\tweap\n");
        write(Path.Combine(data, "global/excel/misc.txt"), "code\ttype\npot1\tweap\n");
        write(Path.Combine(data, "global/excel/itemtypes.txt"), "Code\nweap\n");
        write(Path.Combine(data, "hd/bin/asset.bin"), "native asset");
        write(Path.Combine(original, "README.md"), "legacy notes");
        WriteJson(Path.Combine(original, "modinfo.json"), new JsonObject { ["name"] = "OriginalMod", ["version"] = "2.1", ["savepath"] = "OriginalMod/", ["custom"] = "keep" });
        var candidate = LegacyMigration.Detect(original).Single();
        check(candidate.Name == "OriginalMod" && !candidate.SplitRecords, "Migration detects legacy native project and metadata");
        var target = Path.Combine(root, "migrated-mod"); var report = LegacyMigration.Migrate(candidate, target, "AdoptedMod"); var project = report.Project;
        check(LegacyMigration.Detect(target).Count == 0, "Current JSON projects cannot be mistakenly migrated as native assets only");
        check(report.Tables == 5 && report.VerifiedTables == 5 && File.Exists(Path.Combine(target, "legacy/README.md")), "Migration converts all TXT tables and retains other source files");
        var metadata = Read(Path.Combine(target, "modinfo.json"));
        check(metadata.S("custom") == "keep" && metadata.S("version") == "2.1" && metadata.S("savepath") == "AdoptedMod/", "Migration keeps metadata and separates renamed mod saves");
        check(File.Exists(Path.Combine(data, "global/excel/uniqueitems.txt")) && File.ReadAllText(Path.Combine(original, "README.md")) == "legacy notes", "Migration preserves original native project");
        throws(() => LegacyMigration.Migrate(candidate, target, "AdoptedMod"), "Migration rejects occupied output");
        throws(() => LegacyMigration.Migrate(candidate, Path.Combine(original, "nested"), "AdoptedMod"), "Migration rejects source overlap");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel(); var destination = Path.Combine(root, "canceled-migration");
            throws(() => LegacyMigration.Migrate(candidate, destination, "AdoptedMod", canceled.Token), "Migration respects cancellation");
            check(!Directory.Exists(destination), "Canceled migration publishes no partial project");
        }
        var changingDestination = Path.Combine(root, "changed-migration");
        throws(() => LegacyMigration.Migrate(candidate, changingDestination, "AdoptedMod", progress: step => { if (step.StartsWith("Converting native", StringComparison.Ordinal)) write(Path.Combine(original, "README.md"), "changed"); }), "Migration detects concurrent changes outside data");
        check(!Directory.Exists(changingDestination), "Changed-input migration leaves no destination");
        var nestedRoot = Path.Combine(root, "nested-project");
        write(Path.Combine(nestedRoot, "content/mods/Nested/Nested.mpq/data/global/excel/example.txt"), "name\tvalue\nExample\t1\n");
        write(Path.Combine(nestedRoot, "scripts/build.js"), "original script"); write(Path.Combine(nestedRoot, ".git/config"), "original git metadata");
        var slashCandidate = LegacyMigration.Detect(nestedRoot + Path.DirectorySeparatorChar).Single();
        check(slashCandidate.Root == nestedRoot && !slashCandidate.Root.EndsWith(Path.DirectorySeparatorChar), "Detect normalizes a trailing separator so in-place staging and backups stay beside the project");
        var nestedCandidate = LegacyMigration.Detect(nestedRoot).Single();
        check(nestedCandidate.Root == nestedRoot && nestedCandidate.DataRoot.EndsWith("Nested.mpq" + Path.DirectorySeparatorChar + "data"), "Migration finds deeply nested data and preserves selected project root");
        var backupRoot = nestedRoot + ".backup";
        write(Path.Combine(nestedRoot, ".studio/builds/old/data/cache.bin"), "old generated build");
        write(Path.Combine(nestedRoot, ".studio/recovery/unsaved.json"), "keep recovery");
        for (int i = 0; i < 24; i++) write(Path.Combine(nestedRoot, $"scripts/extra/{i}.txt"), new string((char)('a' + i), 300000));
        var stages = new List<string>();
        var inPlace = LegacyMigration.MigrateInPlace(new LegacyProject(nestedRoot + Path.DirectorySeparatorChar, nestedCandidate.DataRoot, nestedCandidate.Name, false), "Nested", backupRoot, progress: stages.Add);
        check(!Directory.Exists(Path.Combine(nestedRoot, ".studio/builds")) && File.Exists(Path.Combine(backupRoot, ".studio/builds/old/data/cache.bin")), "In-place migration leaves generated build caches in backup only");
        check(File.ReadAllText(Path.Combine(nestedRoot, ".studio/recovery/unsaved.json")) == "keep recovery", "Cache exclusion preserves unsaved recovery files");
        check(Enumerable.Range(0, 24).All(i => File.ReadAllText(Path.Combine(nestedRoot, $"scripts/extra/{i}.txt")) == new string((char)('a' + i), 300000)), "Parallel preservation copies every file without mixing buffers");
        check(stages.Any(s => s.StartsWith("Preserving repository") && s.Contains("MiB") && s.Contains("workers")), "Preservation reports file counts bytes and bounded workers");
        check(stages.Count(s => s.StartsWith("Verifying preserved project profile:")) == 2 && !stages.Any(s => s.StartsWith("Verifying migrated profile:")), "In-place migration verifies each profile once after preservation");
        check(inPlace.Project.Root == nestedRoot && File.Exists(Path.Combine(nestedRoot, "source/tables/example.json")), "In-place migration publishes converted source at the original root");
        check(File.ReadAllText(Path.Combine(nestedRoot, "scripts/build.js")) == "original script" && File.ReadAllText(Path.Combine(nestedRoot, ".git/config")) == "original git metadata", "In-place migration preserves scripts and Git metadata");
        check(File.Exists(Path.Combine(backupRoot, "content/mods/Nested/Nested.mpq/data/global/excel/example.txt")), "In-place migration retains the original nested files in backup");
        throws(() => LegacyMigration.MigrateInPlace(nestedCandidate, "Nested", backupRoot), "In-place migration refuses to overwrite a backup");
        var directRoot = Path.Combine(root, "direct-native"); write(Path.Combine(directRoot, "global/excel/example.txt"), "name\nExample\n");
        write(Path.Combine(directRoot, ".git/config"), "direct metadata");
        var directCandidate = LegacyMigration.Detect(directRoot).Single();
        using (var midway = new CancellationTokenSource())
        {
            throws(() => LegacyMigration.MigrateInPlace(directCandidate, "Direct", directRoot + ".backup", midway.Token,
                step => { if (step.StartsWith("Preserving repository metadata")) midway.Cancel(); }), "Cancellation during preservation aborts in-place conversion");
            check(File.Exists(Path.Combine(directRoot, "global/excel/example.txt")) && !Directory.Exists(directRoot + ".backup") && !Directory.GetDirectories(root, "direct-native.converted-*").Any(), "Canceled preservation cleans staging and leaves original intact");
        }
        bool changedGit = false;
        throws(() => LegacyMigration.MigrateInPlace(directCandidate, "Direct", directRoot + ".backup", progress: step =>
        {
            if (!changedGit && step.StartsWith("Verifying preserved project profile")) { changedGit = true; write(Path.Combine(directRoot, ".git/config"), "externally changed metadata"); }
        }), "Parallel fingerprint verification rejects changed Git metadata");
        check(!Directory.Exists(directRoot + ".backup"), "Changed metadata prevents the final folder swap");
        using (var stop = new CancellationTokenSource())
        {
            stop.Cancel(); throws(() => LegacyMigration.MigrateInPlace(directCandidate, "Direct", directRoot + ".backup", stop.Token), "Canceled in-place conversion does not swap the project");
            check(File.Exists(Path.Combine(directRoot, "global/excel/example.txt")) && !Directory.Exists(directRoot + ".backup"), "Canceled conversion leaves original root intact");
        }
        if (OperatingSystem.IsWindows())
        {
            LegacyMigration.FinalizationException? blocked = null;
            using (var held = new FileStream(Path.Combine(directRoot, ".git/config"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { LegacyMigration.MigrateInPlace(directCandidate, "Direct", directRoot + ".backup"); }
                catch (LegacyMigration.FinalizationException ex) { blocked = ex; }
                check(blocked != null && File.Exists(Path.Combine(directRoot, "global/excel/example.txt")) && Directory.GetDirectories(root, "direct-native.converted-*").Length == 1, "Blocked rename retains prepared conversion and original files");
            }
            using (var stop = new CancellationTokenSource()) { stop.Cancel(); throws(() => blocked!.Retry(stop.Token), "Finalization retry honors cancellation"); }
            var unchanged = File.ReadAllText(Path.Combine(directRoot, ".git/config"));
            write(Path.Combine(directRoot, ".git/config"), "changed after rename failed");
            throws(() => blocked!.Retry(default), "Finalization retry rejects original files changed after conversion");
            write(Path.Combine(directRoot, ".git/config"), unchanged);
            var retainedFile = Path.Combine(Directory.GetDirectories(root, "direct-native.converted-*").Single(), ".git/config");
            var retainedText = File.ReadAllText(retainedFile);
            write(retainedFile, "changed prepared conversion");
            throws(() => blocked!.Retry(default), "Finalization retry rejects changed prepared files");
            write(retainedFile, retainedText);
            blocked!.Retry(default);
            check(!Directory.GetDirectories(root, "direct-native.converted-*").Any() && File.Exists(Path.Combine(directRoot + ".backup", "global/excel/example.txt")), "Finalization retry publishes without converting again and retains backup");
        }
        else LegacyMigration.MigrateInPlace(directCandidate, "Direct", directRoot + ".backup");
        check(File.Exists(Path.Combine(directRoot, ".git/config")), "Direct data-root conversion retains repository metadata");
        check(new RunSettings(project.Root).PathIssues(project).Any(p => p.Contains("overlap")), "Run settings detects source/deployment overlap");
        check(new RunSettings(Path.Combine(root, "mods"), Path.Combine(root, "missing.exe")).PathIssues(project).Count >= 2, "Run settings explains wrong deployment folder and missing executable");
        var packed = Path.Combine(root, "install/mods/Other/Other.mpq/data/global/excel/example.txt"); write(packed, "name\nExample\n");
        check(LegacyMigration.Detect(Path.Combine(root, "install")).Single().Root.EndsWith("Other"), "Migration detects an unpacked mod within an installation");

        var file = Semantics.TableFile(project, "uniqueitems"); var document = new Document(file); var table = document.Table!;
        document.LockedRows.Add(0);
        throws(() => document.SetCells([(0, "lvl", "11")]), "Row locks block cell edits");
        throws(() => document.SetRaw("[]"), "Edit locks protect raw-source bypass"); document.LockedRows.Clear();
        document.LockedColumns.Add("lvl");
        throws(() => document.SetCells([(0, "index", "Other"), (0, "lvl", "11")]), "Column locks reject entire paste transaction");
        check(table.Cell(0, "index") == "Test", "Rejected locked paste leaves other cells unchanged"); document.LockedColumns.Clear();
        document.SetRaw(document.Text.Replace("\"10\"", "\"11\"")); document.ApplySource(); document.LockedRows.Add(0); document.Undo(); document.Redo();
        check(document.Table!.Cell(0, "lvl") == "11", "Existing undo/redo remains usable with edit locks"); document.Undo(); document.LockedRows.Clear();
        table = document.Table!;
        var rule = Semantics.Rules(project).Single(r => r.Table == "uniqueitems" && r.Column == "code");
        check(Semantics.References(project, rule, "axe1").Single().Column == "code", "Reference navigation locates the base item");
        var weaponFile = Semantics.TableFile(project, "weapons"); var buffer = TableData.Load(weaponFile); buffer.SetCell(0, "code", "axe2");
        check(Semantics.References(project, rule, "axe1", new Dictionary<string, TableData> { [weaponFile] = buffer }).Count == 0, "Unsaved reference targets replace disk values");
        check(Semantics.Check(project).Diagnostics.Count == 0, "Built-in reference checks accept coherent source");
        // Item names are string keys: a key that no catalog defines is reported once a catalog exists to look it up in.
        var catalogFile = TableData.FileFor(project, "strings", "item-names");
        TableData.Write(catalogFile, new TableData(new JsonObject { ["schemaVersion"] = 1, ["category"] = "item-names", ["locales"] = new JsonArray("enUS"), ["target"] = "local/lng/strings/item-names.json" },
            new JsonArray(new JsonObject { ["id"] = 1, ["Key"] = "Test", ["translations"] = new JsonObject { ["enUS"] = "Test Unique" } })));
        var catalogCheck = Semantics.Check(project).Diagnostics;
        check(catalogCheck.Count == 0, "Item name keys present in a string catalog pass the string-key check: " + string.Join("; ", catalogCheck));
        var nameRule = Semantics.Rules(project).Single(r => r.Table == "uniqueitems" && r.Column == "index");
        check(Semantics.References(project, nameRule, "Test").Single().File == catalogFile, "Reference navigation locates the item name in its string catalog");
        var renamed = new Document(file); renamed.SetCells([(0, "index", "Missing")]); renamed.Save();
        var nameIssues = Semantics.Check(project).Diagnostics;
        check(nameIssues.Count == 1 && nameIssues[0].Field == "index" && nameIssues[0].Severity == "Warning" && nameIssues[0].Message.Contains("Missing"), "An item name without a string catalog entry is reported as a warning");
        renamed.SetCells([(0, "index", "Test")]); renamed.Save(); File.Delete(catalogFile);
        var validationFile = Path.Combine(target, "source/semantics.json");
        WriteJson(validationFile, new JsonObject { ["schemaVersion"] = 1, ["rules"] = new JsonArray(new JsonObject { ["table"] = "uniqueitems", ["column"] = "lvl", ["type"] = "integer", ["min"] = 0, ["max"] = 99, ["severity"] = "Error" }) });
        document.SetCells([(0, "lvl", "100")]);
        check(Semantics.Check(project, new Dictionary<string, TableData> { [file] = document.Table! }).Diagnostics.Any(d => d.ColumnOrField() == "lvl" && d.Severity == "Error"), "Semantic ranges check unsaved source buffers");
        var diff = TableDiff.Compare(TableData.Load(file), document.Table!);
        check(diff.Count == 1 && diff[0].Before == "10" && diff[0].After == "100", "Cell diff identifies only the changed field");
        document.Save(); throws(() => BuildService.Build(project, "standard"), "Explicit semantic errors block build"); document.Undo(); document.Save(); table = document.Table!;
        var reviewed = ProfileEditing.ReadCell(project, table, 0, "lvl", "d2rl");
        var overrideFile = ProfileEditing.WriteCell(project, table, 0, "lvl", "d2rl", "12", "Fixture profile", reviewed);
        check(ProfileEditing.ReadCell(project, table, 0, "lvl", "d2rl").Effective == "12" && table.Cell(0, "lvl") == "10", "Profile editing keeps shared source separate");
        var build = BuildService.Build(project, "d2rl");
        check(File.ReadAllText(Path.Combine(build.Output, "AdoptedMod.mpq/data/global/excel/uniqueitems.txt")).Contains("\t12"), "Edited profile compiles to expected runtime value");
        reviewed = ProfileEditing.ReadCell(project, table, 0, "lvl", "d2rl");
        var changed = Read(overrideFile); changed["reason"] = "External change"; WriteJson(overrideFile, changed);
        throws(() => ProfileEditing.WriteCell(project, table, 0, "lvl", "d2rl", "13", "Stale review", reviewed), "Profile editor rejects stale file fingerprints");
        changed["targets"] = new JsonArray("global/excel/uniqueitems.txt"); WriteJson(overrideFile, changed);
        throws(() => ProfileEditing.ReadCell(project, table, 0, "lvl", "d2rl"), "Bank-specific profile rules require explicit source editing");

        var splitRoot = Path.Combine(root, "split-project"); Directory.CreateDirectory(splitRoot);
        foreach (var source in project.SourceFiles()) { var dest = Inside(splitRoot, Relative(target, source)); Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Copy(source, dest); }
        // Oldest layout: a folder per table holding schema.json and one file per record.
        var splitFile = Path.Combine(splitRoot, "source/tables/uniqueitems.json"); var splitTable = TableData.Load(splitFile); var splitRecords = splitTable.Records;
        WriteJson(Path.Combine(splitRoot, "source/tables/uniqueitems/schema.json"), splitTable.Schema);
        foreach (var row in splitRecords) WriteJson(Path.Combine(splitRoot, "source/tables/uniqueitems/records", row.S("sourceId") + ".json"), row!);
        File.Delete(splitFile);
        var splitCandidate = LegacyMigration.Detect(splitRoot).Single(); check(splitCandidate.SplitRecords, "Migration detects individual JSON records");
        var consolidated = LegacyMigration.Migrate(splitCandidate, Path.Combine(root, "consolidated"), "AdoptedMod");
        check(JsonNode.DeepEquals(TableData.Load(Path.Combine(consolidated.Project.Root, "source/tables/uniqueitems.json")).Records, splitRecords) && !Directory.Exists(Path.Combine(consolidated.Project.Root, "source/tables/uniqueitems")), "Split-record migration preserves row content and order in a single-file table");
        check(File.ReadAllText(Path.Combine(consolidated.Project.Root, "data/hd/bin/asset.bin")) == "native asset", "Migration preserves native asset folders that share cache-like names");
        check(Directory.Exists(Path.Combine(splitRoot, "source/tables/uniqueitems/records")), "Split-record migration leaves original records in place");
        write(Path.Combine(splitRoot, "data/global/excel/uniqueitems.txt"), "index\tcode\tlvl\nConflict\taxe1\t10\n");
        throws(() => LegacyMigration.Migrate(splitCandidate, Path.Combine(root, "split-conflict"), "AdoptedMod"), "Legacy JSON/TXT disagreement blocks migration");
        File.Delete(Path.Combine(splitRoot, "data/global/excel/uniqueitems.txt"));
        LegacyMigration.MigrateInPlace(splitCandidate, "AdoptedMod", splitRoot + ".backup");
        check(!Directory.Exists(Path.Combine(splitRoot, "source/tables/uniqueitems/records")) && File.Exists(splitFile), "In-place split conversion does not restore obsolete individual records");
    }
    private static string ColumnOrField(this Diagnostic diagnostic) => diagnostic.Field;
}

using System.Diagnostics;
using System.Text.Json;
using ModStudio.Core;
using static ModStudio.Core.Storage;

internal static class BuildTests
{
    public static void Run(string root, Action<bool, string> check, Action<Action, string> throws, Action<string, string> write)
    {
        var native = Path.Combine(root, "incremental-native");
        write(Path.Combine(native, "global/excel/first.txt"), "name\tvalue\nOne\t1\n");
        write(Path.Combine(native, "global/excel/second.txt"), "name\tvalue\nTwo\t2\n");
        write(Path.Combine(native, "hd/asset.bin"), new string('x', 1024 * 1024));
        var project = ProjectImporter.Import(native, Path.Combine(root, "incremental-project"), "Incremental").Project;
        var log = new List<string>(); var timer = Stopwatch.StartNew();
        var first = BuildService.Build(project, "standard", progress: log.Add); var cold = timer.Elapsed.TotalMilliseconds;
        var output = Inside(first.Output, "Incremental.mpq/data/global/excel/second.txt");
        var marker = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc); File.SetLastWriteTimeUtc(output, marker);
        log.Clear(); timer.Restart(); var next = BuildService.Build(project, "standard", progress: log.Add);
        Console.WriteLine($"Incremental fixture: cold {cold:F1} ms, unchanged {timer.Elapsed.TotalMilliseconds:F1} ms");
        check(next.Output == first.Output && Directory.GetDirectories(Inside(project.Cache, "builds")).Length == 1, "Build reuses a single current output directory");
        check(log.Last().Contains("0 tables converted, 2 reused, 0 output files written") && File.GetLastWriteTimeUtc(output) == marker, "Unchanged build reuses conversions and does not rewrite output");
        // Hashes are remembered against size and write time, so an unchanged build reads nothing but directory listings.
        var fingerprints = Inside(project.Cache, "hashes.json"); var assetOutput = Inside(first.Output, "Incremental.mpq/data/hd/asset.bin");
        check(File.Exists(fingerprints) && File.ReadAllText(fingerprints).Contains(Inside(project.Root, "data/hd/asset.bin").Replace("\\", "\\\\")), "Build persists file fingerprints for later builds");
        write(Inside(project.Root, "data/hd/asset.bin"), new string('y', 1024 * 1024)); // same size, new content
        log.Clear(); next = BuildService.Build(project, "standard", progress: log.Add);
        check(File.ReadAllText(assetOutput)[0] == 'y' && log.Last().Contains("1 output files written"), "Same-size asset edits are detected and copied");
        write(Inside(project.Root, "data/hd/asset.bin"), new string('z', 1024 * 1024)); File.SetLastWriteTimeUtc(Inside(project.Root, "data/hd/asset.bin"), DateTime.UtcNow.AddMinutes(-5));
        next = BuildService.Build(project, "standard");
        check(File.ReadAllText(assetOutput)[0] == 'z', "A write time older than the cache guard still hashes a file whose stamp changed");
        var records = TableData.FileFor(project, "tables", "first"); var doc = new Document(records); doc.SetCells([(0, "value", "3")]); doc.Save();
        log.Clear(); next = BuildService.Build(project, "standard", progress: log.Add);
        check(log.Last().Contains("1 tables converted, 1 reused, 1 output files written") && File.GetLastWriteTimeUtc(output) == marker, "Single-table edit rebuilds only its changed output");
        File.WriteAllText(output, "tampered"); log.Clear(); next = BuildService.Build(project, "standard", progress: log.Add);
        check(File.ReadAllText(output).Contains("Two") && log.Last().Contains("1 tables converted, 1 reused"), "Damaged cached output is regenerated");
        var sourceAsset = Inside(project.Root, "data/hd/asset.bin"); File.Delete(sourceAsset);
        next = BuildService.Build(project, "standard");
        check(!File.Exists(Inside(next.Output, "Incremental.mpq/data/hd/asset.bin")), "Incremental build removes deleted native assets");
        File.Delete(TableData.FileFor(project, "tables", "second"));
        next = BuildService.Build(project, "standard"); check(!File.Exists(output), "Incremental build removes deleted table outputs");
        var old = Inside(project.Cache, "builds/" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(old);
        File.WriteAllText(Inside(old, "build.json"), JsonSerializer.Serialize(next, Pretty));
        next = BuildService.Build(project, "d2rl"); check(!Directory.Exists(old) && next.Output == first.Output, "Successful build retires recognized historical caches and shares output across profiles");
        var profileFile = Inside(project.Root, "compatibility/d2rl/profile.json");
        var profile = Read(profileFile);
        write(Inside(project.Root, "compatibility/d2rl/loader-asset.txt"), "loader asset");
        profile["assetOverrides"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["scope"] = "mod", ["target"] = "d2rloader/assets/example.txt", ["source"] = "loader-asset.txt", ["reason"] = "fixture" });
        WriteJson(profileFile, profile);
        next = BuildService.Build(project, "d2rl"); next = BuildService.Build(project, "d2rl");
        check(File.ReadAllText(Inside(next.Output, "d2rloader/assets/example.txt")) == "loader asset", "Incremental asset overrides compare against current source rather than previous output");
        next = BuildService.Build(project, "standard");
        check(!File.Exists(Inside(next.Output, "d2rloader/assets/example.txt")), "Switching profiles removes obsolete loader output");
        var deploy = Path.Combine(root, "incremental-game/mods/Incremental"); DeploymentService.Deploy(project, next, deploy);
        var deployedFile = Inside(deploy, "Incremental.mpq/data/global/excel/first.txt"); File.SetLastWriteTimeUtc(deployedFile, marker);
        next = BuildService.Build(project, "d2rl"); var deployed = new List<string>(); DeploymentService.Deploy(project, next, deploy, progress: deployed.Add);
        check(File.GetLastWriteTimeUtc(deployedFile) == marker && deployed.All(s => !s.Contains("first.txt")), "Deployment skips unchanged files while updating ownership");
        using (var canceled = new CancellationTokenSource()) { canceled.Cancel(); throws(() => BuildService.Build(project, "d2rl", canceled.Token), "Canceled incremental build is rejected"); }
        throws(() => DeploymentService.Deploy(project, next, deploy), "Canceled build cannot publish partially refreshed output");
        next = BuildService.Build(project, "d2rl"); check(File.Exists(Path.Combine(next.Output, "Incremental.mpq/data/global/excel/first.txt")), "Build resumes safely after cancellation");
        using (var held = new FileStream(Inside(project.Cache, "build.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            throws(() => BuildService.Build(project, "standard"), "Concurrent build cannot mutate shared output");
        var game = Path.Combine(root, "detected-game"); write(Path.Combine(game, "D2R.exe"), "fixture");
        check(RunSettings.DetectExecutables(game).SequenceEqual(new[] { "D2R.exe" }), "Game folder detects vanilla without a loader");
        write(Path.Combine(game, "D2RLoader.exe"), "fixture");
        check(RunSettings.DetectExecutables(game).Length == 2, "Game folder offers both detected launch targets");
        var settings = new RunSettings(GameDirectory: game, LaunchTarget: "D2RLoader.exe");
        check(settings.ResolvedExecutable == Path.Combine(game, "D2RLoader.exe"), "Loader selection resolves within the game folder");
        (new RunSettings(Executable: Path.Combine(game, "D2RLoader.exe"))).Save(project, "d2rl");
        var migrated = RunSettings.Load(project, "d2rl");
        check(migrated.GameDirectory == game && migrated.LaunchTarget == "D2RLoader.exe", "Existing executable settings migrate to folder and retain loader choice");
        var launchSettings = settings with { DeploymentDirectory = Path.Combine(game, "mods", project.Name), Runner = Environment.ProcessPath!, RunnerArguments = ["runner argument"] };
        var launch = RunController.CreateStartInfo(project, launchSettings);
        check(launch.WorkingDirectory == game && launch.ArgumentList.Take(2).SequenceEqual(new[] { "runner argument", Path.Combine(game, "D2RLoader.exe") }), "Folder launch preserves runner arguments and selected loader path");
        File.Delete(Path.Combine(game, "D2RLoader.exe"));
        check(settings.PathIssues(project).Any(s => s.Contains("not found")), "Missing selected loader is reported without silently switching to vanilla");
        throws(() => RunController.CreateStartInfo(project, settings), "Play refuses a missing selected loader");
        RunCatalogNewlinesAndProfileModInfo(root, check, throws, write);
        RunProfileRuleSets(root, check, throws, write);
        RunAtomicWriteOverOpenReader(root, check, throws);
    }

    // Windows refuses to replace a file that another handle has open (a preview reading it, an indexer or virus scanner
    // opening a file just written): a save must wait that out instead of failing with "Access to the path is denied".
    private static void RunAtomicWriteOverOpenReader(string root, Action<bool, string> check, Action<Action, string> throws)
    {
        var folder = Path.Combine(root, "atomic-write-reader"); Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "list.json"); File.WriteAllText(file, "old");
        var reader = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = Task.Run(async () => { await Task.Delay(300); reader.Dispose(); });
        AtomicWrite(file, Utf8.GetBytes("new"));
        release.Wait();
        check(File.ReadAllText(file) == "new", "A save replaces a file a reader held open for a moment");

        if (OperatingSystem.IsWindows())
        {
            using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                throws(() => AtomicWrite(file, Utf8.GetBytes("newer")), "A save over a file held open for good still fails");
            check(File.ReadAllText(file) == "new" && Directory.GetFiles(folder).Length == 1, "A failed save leaves the file and no temporary behind");
        }
        else
        {
            // Elsewhere the rename replaces the file under an open reader, which keeps reading the old contents.
            using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                AtomicWrite(file, Utf8.GetBytes("new"));
            check(File.ReadAllText(file) == "new" && Directory.GetFiles(folder).Length == 1, "A save replaces a file held open, leaving no temporary behind");
        }
        throws(() => AtomicWrite(file, Utf8.GetBytes("x"), requireAbsent: true), "A save that requires a new file still refuses an existing one");
    }

    private static void RunCatalogNewlinesAndProfileModInfo(string root, Action<bool, string> check, Action<Action, string> throws, Action<string, string> write)
    {
        var native = Path.Combine(root, "profile-modinfo-native");
        // The exact shape the game ships (and Studio writes): four-space indent, CRLF lines, final newline.
        var crlfCatalog = "[\r\n    {\r\n        \"id\": 1,\r\n        \"Key\": \"Alpha\",\r\n        \"enUS\": \"Alpha\"\r\n    },\r\n    {\r\n        \"id\": 2,\r\n        \"Key\": \"Beta\",\r\n        \"enUS\": \"Beta %d\"\r\n    }\r\n]\r\n";
        var lfCatalog = crlfCatalog.Replace("\r\n", "\n");
        write(Path.Combine(native, "local/lng/strings/crlf.json"), crlfCatalog);
        write(Path.Combine(native, "local/lng/strings/lf.json"), lfCatalog);
        write(Path.Combine(native, "global/excel/first.txt"), "name\tvalue\r\nOne\t1\r\n");
        var project = ProjectImporter.Import(native, Path.Combine(root, "profile-modinfo-project"), "ProfileInfo").Project;
        var originalInfo = Inside(project.Root, "modinfo.json");
        File.WriteAllText(originalInfo, "{\r\n    \"name\": \"ProfileInfo\",\r\n    \"version\": \"2.0.0\",\r\n    \"savepath\": \"ProfileInfo/\"\r\n}\r\n", Utf8);
        var standard = BuildService.Build(project, "standard");
        check(File.ReadAllText(Inside(standard.Output, "ProfileInfo.mpq/data/local/lng/strings/crlf.json"), Utf8) == crlfCatalog, "CRLF string catalogs build byte-identical, without doubled carriage returns");
        check(File.ReadAllText(Inside(standard.Output, "ProfileInfo.mpq/data/local/lng/strings/lf.json"), Utf8) == lfCatalog, "LF string catalogs build byte-identical");
        check(File.ReadAllBytes(Inside(standard.Output, "ProfileInfo.mpq/modinfo.json")).SequenceEqual(File.ReadAllBytes(originalInfo)), "Without a profile modInfo the project's modinfo.json ships unchanged");

        var standardProfile = Inside(project.Root, "compatibility/standard/profile.json");
        var settings = Read(standardProfile); settings["modInfo"] = new System.Text.Json.Nodes.JsonObject { ["savepath"] = "ProfileInfoSoft/" }; WriteJson(standardProfile, settings);
        standard = BuildService.Build(project, "standard", modName: "ProfileInfoSoft");
        var built = Read(Inside(standard.Output, "ProfileInfoSoft.mpq/modinfo.json"));
        check(built.S("savepath") == "ProfileInfoSoft/" && built.S("name") == "ProfileInfo" && built.S("version") == "2.0.0", "A profile modInfo overrides the savepath and keeps the other fields");
        var loader = BuildService.Build(project, "d2rl");
        check(File.ReadAllBytes(Inside(loader.Output, "ProfileInfo.mpq/modinfo.json")).SequenceEqual(File.ReadAllBytes(originalInfo)), "Other profiles keep the project's modinfo.json");

        foreach (var (bad, name) in new (System.Text.Json.Nodes.JsonObject, string)[]
        {
            (new() { ["savepath"] = "../Escape/" }, "a savepath that leaves the save folder"),
            (new() { ["savepath"] = "NoSlash" }, "a savepath without a trailing slash"),
            (new() { ["version"] = "9.9.9" }, "a modInfo field other than name and savepath"),
            (new(), "an empty modInfo"),
        })
        {
            settings["modInfo"] = bad; WriteJson(standardProfile, settings);
            throws(() => BuildService.Build(project, "standard"), "Profile modInfo rejects " + name);
        }
    }

    private static void RunProfileRuleSets(string root, Action<bool, string> check, Action<Action, string> throws, Action<string, string> write)
    {
        var native = Path.Combine(root, "rule-set-native");
        write(Path.Combine(native, "global/excel/rows.txt"), "name\tvalue\tenabled\r\nOne\t1\t1\r\nTwo\t2\t1\r\nThree\t3\t1\r\n");
        var project = ProjectImporter.Import(native, Path.Combine(root, "rule-set-project"), "RuleSets").Project;
        var dir = Inside(project.Root, "compatibility/standard"); var profilePath = Path.Combine(dir, "profile.json");
        System.Text.Json.Nodes.JsonObject Rule(string record, string column, string expect, string value) => new()
        {
            ["table"] = "rows", ["record"] = record, ["reason"] = "fixture",
            ["changes"] = new System.Text.Json.Nodes.JsonObject { [column] = new System.Text.Json.Nodes.JsonObject { ["expect"] = expect, ["value"] = value } },
        };
        System.Text.Json.Nodes.JsonObject Set(params System.Text.Json.Nodes.JsonObject[] rules) => new() { ["generator"] = "scripts/fixture.py", ["rules"] = new System.Text.Json.Nodes.JsonArray(rules) };
        void Profile(params string[] files) { var settings = Read(profilePath); settings["tableOverrides"] = new System.Text.Json.Nodes.JsonArray(files.Select(f => (System.Text.Json.Nodes.JsonNode)f).ToArray()); WriteJson(profilePath, settings); }
        string Built(string profile = "standard") => File.ReadAllText(Inside(BuildService.Build(project, profile).Output, "RuleSets.mpq/data/global/excel/rows.txt"));

        WriteJson(Path.Combine(dir, "set.json"), Set(Rule("row-00000", "enabled", "1", "0"), Rule("row-00002", "value", "3", "30")));
        WriteJson(Path.Combine(dir, "single.json"), Rule("row-00001", "value", "2", "20"));
        Profile("set.json", "single.json");
        check(Built() == "name\tvalue\tenabled\r\nOne\t1\t0\r\nTwo\t20\t1\r\nThree\t30\t1\r\n", "A rule set applies every rule in it, beside one-rule files");
        check(Built("d2rl") == "name\tvalue\tenabled\r\nOne\t1\t1\r\nTwo\t2\t1\r\nThree\t3\t1\r\n", "A rule set only changes its own profile");

        var table = TableData.Load(Inside(project.Root, "source/tables/rows.json"));
        throws(() => ProfileEditing.ReadCell(project, table, 0, "enabled", "standard"), "Studio does not edit a cell owned by a generated rule set");
        var single = ProfileEditing.ReadCell(project, table, 1, "value", "standard");
        check(single.Effective == "20" && single.RuleFile!.EndsWith("single.json"), "One-rule files stay editable beside a rule set");
        check(ProfileEditing.ReadCell(project, table, 0, "value", "standard").Effective == "1", "Cells a rule set does not change stay plain");

        foreach (var (bad, name) in new (System.Text.Json.Nodes.JsonObject, string)[]
        {
            (Set(Rule("row-00000", "enabled", "1", "0"), Rule("row-00000", "enabled", "1", "1")), "two rules for the same cell"),
            (Set(Rule("row-00000", "enabled", "9", "0")), "a stale rule"),
            (new() { ["rules"] = new System.Text.Json.Nodes.JsonArray() }, "an empty set"),
            (new() { ["rules"] = new System.Text.Json.Nodes.JsonArray(Rule("row-00000", "enabled", "1", "0")), ["reason"] = "x" }, "a set with other keys"),
            (new() { ["rules"] = new System.Text.Json.Nodes.JsonArray(Set(Rule("row-00000", "enabled", "1", "0"))) }, "a nested set"),
        })
        {
            WriteJson(Path.Combine(dir, "set.json"), bad);
            throws(() => Built(), "A rule set rejects " + name);
        }
        WriteJson(Path.Combine(dir, "set.json"), Set(Rule("row-00001", "value", "2", "25")));
        throws(() => Built(), "A rule set and a one-rule file competing for a cell block the build");

        // Remove rules leave a table's last rows out of one profile's build.
        System.Text.Json.Nodes.JsonObject Remove(string record) => new() { ["table"] = "rows", ["record"] = record, ["reason"] = "fixture", ["remove"] = true };
        var full = "name\tvalue\tenabled\r\nOne\t1\t1\r\nTwo\t2\t1\r\nThree\t3\t1\r\n";
        WriteJson(Path.Combine(dir, "set.json"), Set(Remove("row-00002"), Rule("row-00000", "enabled", "1", "0")));
        check(Built() == "name\tvalue\tenabled\r\nOne\t1\t0\r\nTwo\t20\t1\r\n", "A remove rule leaves the last row out of its profile's build, beside cell rules");
        check(Built("d2rl") == full, "Other profiles keep a row one profile removes");
        throws(() => ProfileEditing.ReadCell(project, table, 2, "value", "standard"), "Studio shows a removed row as left out of the build instead of editing it");
        WriteJson(Path.Combine(dir, "set.json"), Set(Remove("row-00001"), Remove("row-00002")));
        Profile("set.json");
        check(Built() == "name\tvalue\tenabled\r\nOne\t1\t1\r\n", "A profile can remove several rows at the end of a table");
        foreach (var (bad, name) in new (System.Text.Json.Nodes.JsonObject, string)[]
        {
            (Set(Remove("row-00001")), "a row that kept rows come after"),
            (Set(Remove("row-00002"), Rule("row-00002", "value", "3", "4")), "removing and changing the same row"),
            (Set(Remove("row-00002"), Remove("row-00002")), "removing the same row twice"),
            (Set(new System.Text.Json.Nodes.JsonObject { ["table"] = "rows", ["record"] = "row-00002", ["reason"] = "fixture", ["remove"] = true, ["changes"] = new System.Text.Json.Nodes.JsonObject { ["value"] = new System.Text.Json.Nodes.JsonObject { ["expect"] = "3", ["value"] = "4" } } }), "a remove rule with changes"),
            (Set(new System.Text.Json.Nodes.JsonObject { ["table"] = "rows", ["record"] = "row-00002", ["reason"] = "fixture", ["remove"] = false }), "remove set to false"),
            (Set(new System.Text.Json.Nodes.JsonObject { ["table"] = "rows", ["record"] = "row-00002", ["reason"] = "", ["remove"] = true }), "a remove rule without a reason"),
        })
        {
            WriteJson(Path.Combine(dir, "set.json"), bad);
            throws(() => Built(), "A profile rejects " + name);
        }
    }
}

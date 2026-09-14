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
    }
}

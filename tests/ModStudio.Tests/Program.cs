using System.Text.Json.Nodes;
using ModStudio.Core;
using static ModStudio.Core.Storage;

if (args.Contains("--studio-child")) { Console.WriteLine("Mod Studio launch fixture"); await Task.Delay(TimeSpan.FromSeconds(45)); return; }
if (args.Length == 2 && args[0] == "--create-ui-fixture") { UiFixture.Create(args[1]); return; }
var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "mod-studio-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
var count = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); count++; Console.WriteLine("PASS " + name); }
void Throws(Action action, string name) { try { action(); } catch { count++; Console.WriteLine("PASS " + name); return; } throw new Exception("Did not reject: " + name); }
void Write(string file, string text) { Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, text, Utf8); }
try
{
    PreviewTests.Run(root, Check, Throws);
    BuildTests.Run(root, Check, Throws, Write);
    var native = Path.Combine(root, "original"); var target = Path.Combine(root, "project");
    var encodedFile = Path.Combine(root, "utf16.bat");
    var encodedBytes = System.Text.Encoding.Unicode.GetPreamble().Concat(System.Text.Encoding.Unicode.GetBytes("@echo off\r\necho hello\r\n")).ToArray();
    File.WriteAllBytes(encodedFile, encodedBytes); var encodedDoc = new Document(encodedFile); encodedDoc.Save();
    Check(TextFileEncoding.LooksLikeText(encodedBytes) && File.ReadAllBytes(encodedFile).SequenceEqual(encodedBytes), "UTF16 script detection and save preserve encoding and BOM");
    File.WriteAllBytes(encodedFile, new byte[] { 0x63, 0x61, 0x66, 0xe9 }); new Document(encodedFile).Save();
    Check(File.ReadAllBytes(encodedFile).SequenceEqual(new byte[] { 0x63, 0x61, 0x66, 0xe9 }), "Single-byte text fallback preserves existing bytes");
    File.WriteAllBytes(encodedFile, new byte[] { 0xff, 0xfe, 0, 3, 0 }); new Document(encodedFile, forceRaw: true).Save();
    Check(File.ReadAllBytes(encodedFile).SequenceEqual(new byte[] { 0xff, 0xfe, 0, 3, 0 }), "Explicit binary raw mode preserves arbitrary bytes");
    var preferencesFile = Path.Combine(root, "user-settings/preferences.json");
    var preferences = StudioPreferences.Load(preferencesFile);
    Check(preferences.LastProject == null && !preferences.HasIntroduced(target), "First launch has no remembered project or settings introduction");
    preferences.RowEditorSearch = "damage"; preferences.Save(preferencesFile);
    Check(StudioPreferences.Load(preferencesFile).RowEditorSearch == "damage", "Row editor search persists across launches");
    preferences.Remember(target, preferencesFile);
    preferences = StudioPreferences.Load(preferencesFile);
    Check(preferences.LastProject == Path.GetFullPath(target), "Last project survives reloading preferences");
    preferences.MarkIntroduced(target, preferencesFile);
    preferences = StudioPreferences.Load(preferencesFile);
    Check(preferences.HasIntroduced(target + Path.DirectorySeparatorChar) && !File.Exists(Path.Combine(target, ".studio", "run-settings.json")), "Settings introduction persists without saving run settings");
    preferences.Remember(native, preferencesFile);
    preferences = StudioPreferences.Load(preferencesFile);
    Check(preferences.LastProject == native && preferences.HasIntroduced(target) && !preferences.HasIntroduced(native), "Settings introduction is tracked separately for each project");
    var tsv = "\uFEFFname\t\tname\tvalue\r\nExample\t0\t\t160\r\n\r\nExpansion\r\n";
    Write(Path.Combine(native, "global/excel/example.txt"), tsv); Write(Path.Combine(native, "global/excel/base/example.txt"), tsv);
    Write(Path.Combine(native, "global/excel/different.txt"), "name\nMain"); Write(Path.Combine(native, "global/excel/base/different.txt"), "name\nBase");
    Write(Path.Combine(native, "global/dataversionbuild.txt"), "93847"); Write(Path.Combine(native, "hd/native.bin"), "unchanged");
    Write(Path.Combine(native, "local/lng/strings/example.json"), "[{\"id\":100,\"Key\":\"Example\",\"enUS\":\"Hello %d\",\"frFR\":\"Salut %d\"}]");
    var report = ProjectImporter.Import(native, target, "TestMod"); var project = report.Project;
    Check(report.Tables == 3 && report.Catalogs == 1 && report.VerifiedTables == 4, "Import shares only identical banks and recognizes catalogs");
    Check(File.ReadAllBytes(Path.Combine(native, "global/excel/example.txt")).SequenceEqual(Utf8.GetBytes(tsv)), "Original source never changed");
    Throws(() => ProjectImporter.Import(native, target, "TestMod"), "Import rejects occupied destinations");
    Throws(() => ProjectImporter.Import(native, Path.Combine(native, "nested"), "TestMod"), "Import rejects overlapping paths");
    var records = Path.Combine(target, "source/tables/example/records.json"); var doc = new Document(records);
    Check(doc.Table!.EncodeTsv().SequenceEqual(Utf8.GetBytes(tsv)), "BOM, CRLF, trailing blanks, short rows and duplicate headers round trip");
    Check(doc.Table.Cell(0, "column-2") == "0" && doc.Table.Cell(0, "name#3") == "", "Zero and empty remain distinct");
    var before = doc.Text; doc.SetCells([(0, "value", "180"), (2, "name", "Expansion edited")]); Check(doc.IsDirty && doc.Table.Cell(0, "value") == "180", "Bulk cell editing");
    doc.Undo(); Check(doc.Table.Cell(0, "value") == "160" && doc.Table.Cell(2, "name") == "Expansion", "Bulk undo restores row widths and fields");
    Check(!doc.IsDirty, "Undo to saved state clears dirty marker");
    doc.Redo(); Check(doc.Table.Cell(0, "value") == "180", "Bulk redo"); doc.Save(); Check(!doc.IsDirty, "Save clears dirty state");
    doc.SetCells([(0, "value", "181")]); doc.SetCells([(0, "value", "182")]); doc.Undo(); doc.Undo();
    Check(!doc.IsDirty && doc.Table.Cell(0, "value") == "180", "Multiple undo steps return to saved state");
    doc.Redo(); doc.Redo(); Check(doc.IsDirty && doc.Table.Cell(0, "value") == "182", "Multiple redo steps restore transactions"); doc.Undo(); doc.Undo();
    Throws(() => doc.SetCells([(0, "value", "190"), (2, "name", "bad\tcell")]), "Invalid bulk paste rejected as a transaction"); Check(doc.Table.Cell(0, "value") == "180", "Invalid paste preserves earlier cells");
    doc.SetRaw("[ broken"); Check(doc.PendingSource && doc.IsDirty, "Invalid raw text remains dirty"); doc.ApplySource(); Check(doc.Diagnostics.Count > 0, "Invalid raw text has diagnostics");
    Throws(doc.Save, "Invalid raw source cannot replace disk");
    var recovery = Path.Combine(root, "recovery.json"); doc.Recover(recovery); var recovered = new Document(records); recovered.RestoreRecovery(recovery); Check(recovered.Text == "[ broken", "Recovery retains invalid text");
    doc.Undo(); Check(doc.Table != null && !doc.PendingSource, "Undo invalid source restores table");
    doc.SetCells([(0, "value", "200")]); Write(records, "[]"); Throws(doc.Save, "External changes block overwrite"); Throws(() => new Document(records).RestoreRecovery(recovery), "Stale recovery cannot overwrite a newer disk revision");
    Write(records, before); doc = new Document(records);
    var build = BuildService.Build(project, "standard");
    Check(File.ReadAllBytes(Path.Combine(build.Output, "TestMod.mpq/data/global/excel/example.txt")).SequenceEqual(Utf8.GetBytes(tsv)), "Portable compiler preserves original table bytes");
    Check(File.Exists(Path.Combine(build.Output, "TestMod.mpq/data/hd/native.bin")), "Native assets included");
    var d2rl = BuildService.Build(project, "d2rl"); Check(build.Files.Select(f => f.Sha256).SequenceEqual(d2rl.Files.Select(f => f.Sha256)), "Profiles match without overrides");
    var catalogFile = Path.Combine(target, "source/strings/example/records.json"); var catalog = TableData.Load(catalogFile);
    var catalogRow = catalog.Records[0]!;
    catalogRow["standardTranslations"] = new JsonObject { ["enUS"] = "Short %d" };
    catalogRow["standardReviewedAgainst"] = new JsonObject { ["enUS"] = Hash("Hello %d") };
    Check(catalog.Validate(catalogFile).Count == 0, "Reviewed compact translations retain placeholders");
    catalogRow["standardTranslations"]!["enUS"] = "Short %s";
    Check(catalog.Validate(catalogFile).Count > 0, "Compact placeholder changes are diagnosed");
    catalogRow["standardTranslations"]!["enUS"] = "Short %d"; catalog.SetCell(0, "enUS", "Changed %d");
    Check(catalog.Validate(catalogFile).Count > 0, "Changing full translation requires compact review");
    var profilePath = Path.Combine(target, "compatibility/d2rl/profile.json"); var profile = Read(profilePath);
    WriteJson(Path.Combine(target, "compatibility/d2rl/change.json"), new JsonObject { ["table"] = "example", ["record"] = "row-00000", ["reason"] = "Fixture", ["changes"] = new JsonObject { ["value"] = new JsonObject { ["expect"] = "160", ["value"] = "210" } } });
    profile["tableOverrides"] = new JsonArray("change.json"); WriteJson(profilePath, profile);
    d2rl = BuildService.Build(project, "d2rl"); Check(File.ReadAllText(Path.Combine(d2rl.Output, "TestMod.mpq/data/global/excel/example.txt")).Contains("210"), "Runtime override applies to selected build");
    profile["tableOverrides"] = new JsonArray("change.json", "change.json"); WriteJson(profilePath, profile);
    Throws(() => BuildService.Build(project, "d2rl"), "Competing cell overrides block build");
    profile["tableOverrides"] = new JsonArray("change.json"); WriteJson(profilePath, profile);
    doc.SetCells([(0, "value", "170")]); doc.Save(); Throws(() => BuildService.Build(project, "d2rl"), "Stale override blocks build"); Write(records, before);
    var deployment = Path.Combine(root, "game/mods/TestMod");
    Throws(() => DeploymentService.Deploy(project, build, deployment), "Replaced or failed build cannot deploy stale output");
    build = BuildService.Build(project, "standard");
    DeploymentService.Deploy(project, build, deployment); Check(File.Exists(Path.Combine(deployment, "TestMod.mpq/data/hd/native.bin")), "Deploy writes complete output");
    Write(Path.Combine(deployment, "personal.cfg"), "keep"); DeploymentService.Deploy(project, build, deployment); Check(File.ReadAllText(Path.Combine(deployment, "personal.cfg")) == "keep", "Deployment retains unowned files");
    Write(Path.Combine(deployment, "TestMod.mpq/data/hd/native.bin"), "external"); Throws(() => DeploymentService.Deploy(project, build, deployment), "Locally modified deployed asset protected");
    Write(Path.Combine(deployment, "TestMod.mpq/data/hd/native.bin"), "unchanged");
    File.Delete(Path.Combine(target, "data/hd/native.bin")); var removed = BuildService.Build(project, "standard"); DeploymentService.Deploy(project, removed, deployment);
    Check(!File.Exists(Path.Combine(deployment, "TestMod.mpq/data/hd/native.bin")), "Removed owned output does not linger");
    var ownerPath = Path.Combine(deployment, ".studio-owner.json"); var previousOwner = File.ReadAllBytes(ownerPath);
    var pending = Path.Combine(deployment, ".studio-transaction"); Directory.CreateDirectory(Path.Combine(pending, "backup"));
    File.WriteAllBytes(Path.Combine(pending, "backup/.studio-owner.json"), previousOwner);
    var interruptedOwner = Utf8.GetBytes("partial ownership"); File.WriteAllBytes(ownerPath, interruptedOwner);
    Write(Path.Combine(pending, "journal.json"), System.Text.Json.JsonSerializer.Serialize(new DeploymentJournal(project.Id, [new(".studio-owner.json", Hash(previousOwner), Hash(interruptedOwner))]), Pretty));
    DeploymentService.Deploy(project, removed, deployment);
    Check(File.ReadAllBytes(ownerPath).SequenceEqual(previousOwner) && !Directory.Exists(pending), "Next deployment recovers a previous process's journal");
    using (var canceled = new CancellationTokenSource())
    {
        canceled.Cancel(); Throws(() => DeploymentService.Deploy(project, build, deployment, canceled.Token), "Canceled deployment does not write");
        Check(!File.Exists(Path.Combine(deployment, "TestMod.mpq/data/hd/native.bin")), "Canceled deployment leaves last successful output");
    }
    Write(Path.Combine(target, "data/hd/native.bin"), "unchanged"); build = BuildService.Build(project, "standard");
    using (var interrupted = new CancellationTokenSource())
    {
        Throws(() => DeploymentService.Deploy(project, build, deployment, interrupted.Token, _ => interrupted.Cancel()), "Cancellation during deployment rolls back");
        Check(!File.Exists(Path.Combine(deployment, "TestMod.mpq/data/hd/native.bin")), "Rollback restores prior ownership and missing files");
    }
    Throws(() => DeploymentService.Deploy(project, build, target), "Source/deployment overlap rejected");
    Throws(() => Inside(root, "../outside"), "Path traversal rejected");
    Throws(() => Inside(root, "safe\\outside"), "Cross-platform backslash traversal rejected");
    Throws(() => ModProject.ValidateName("../wrong"), "Invalid mod names rejected");
    var corruptNative = Path.Combine(root, "corrupt"); Directory.CreateDirectory(Path.Combine(corruptNative, "global/excel")); File.WriteAllBytes(Path.Combine(corruptNative, "global/excel/bad.txt"), [255]);
    var corruptTarget = Path.Combine(root, "corrupt-project"); Throws(() => ProjectImporter.Import(corruptNative, corruptTarget, "TestMod"), "Lossy UTF-8 conversion rejected"); Check(!Directory.Exists(corruptTarget), "Failed import leaves no partial project");
    Throws(() => TableData.FromTsv(Utf8.GetBytes("a\r\nb\nc"), "bad", "bad.txt"), "Mixed TSV newlines rejected");
    var game = Path.Combine(root, OperatingSystem.IsWindows() ? "game/D2R.exe" : "game/game-fixture"); Write(game, "test fixture only");
    var start = RunController.CreateStartInfo(project, new(deployment, game)); Check(start.ArgumentList.SequenceEqual(new[] { "-mod", "TestMod", "-txt" }) && !start.UseShellExecute, "Launch uses separate arguments without shell interpolation");
    var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Environment.ProcessPath!;
    var runnerArgs = Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? new[] { typeof(Document).Assembly.Location.Replace("ModStudio.Core.dll", "ModStudio.Tests.dll"), "--studio-child" } : new[] { "--studio-child" };
    using (var run = new RunController())
    {
        bool launchFailed = false;
        var vanishedRunner = Path.Combine(root, "vanished-runner"); Write(vanishedRunner, "removed before launch");
        try { await run.ExecuteAsync(project, "standard", new RunSettings(deployment, game, vanishedRunner), true, true, CancellationToken.None, _ => { if (File.Exists(vanishedRunner)) File.Delete(vanishedRunner); }); }
        catch (System.ComponentModel.Win32Exception) { launchFailed = true; }
        Check(launchFailed && !run.Running, "Failed process startup leaves controller available for retry");
        var runSettings = new RunSettings(deployment, game, host, runnerArgs);
        await run.ExecuteAsync(project, "standard", runSettings, true, true, CancellationToken.None);
        Check(run.Running, "Play starts a real harmless child process");
        bool rejected = false; try { await run.ExecuteAsync(project, "standard", runSettings, true, true, CancellationToken.None); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Play refuses a second deployment while its child runs"); run.Stop();
        for (int i = 0; i < 50 && run.Running; i++) await Task.Delay(20);
        Check(!run.Running, "Stop terminates only the owned child process");
    }
    FeatureTests.Run(root, Check, Throws, Write);
    Console.WriteLine($"PASS {count} checks");
}
finally { Directory.Delete(root, true); }

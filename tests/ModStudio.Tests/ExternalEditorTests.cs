using System.Text.Json;
using System.Text.Json.Nodes;
using ModStudio.Core;
using static ModStudio.Core.Storage;

internal static class ExternalEditorTests
{
    public static void Run(string root, Action<bool, string> check, Action<Action, string> throws)
    {
        var nativeRoot = Path.Combine(root, "native-external-project");
        var nativeWorkspace = Inside(nativeRoot, "data/global/excel");
        var nativeFile = Inside(nativeWorkspace, "example.txt");
        var nativeBank = Inside(nativeWorkspace, "base/example.txt");
        AtomicWrite(nativeFile, Utf8.GetBytes("code\tvalue\na\t1\n")); AtomicWrite(nativeBank, File.ReadAllBytes(nativeFile));
        WriteJson(Inside(nativeRoot, "modinfo.json"), new JsonObject { ["name"] = "NativeExternal" });
        var nativeProject = ModProject.Open(nativeRoot);
        var foreignTarget = Path.Combine(root, "native-external-game/mods/NativeExternal");
        var foreignOwner = Inside(foreignTarget, ".studio-owner.json");
        AtomicWrite(foreignOwner, JsonSerializer.SerializeToUtf8Bytes(new DeploymentManifest("previous-migrated-project", "old-build", "standard", []), Pretty));
        new RunSettings(DeploymentDirectory: foreignTarget).Save(nativeProject, "standard");
        var ownerBefore = File.ReadAllBytes(foreignOwner);
        var native = NativeExternalEditorTarget.Resolve(nativeProject);
        check(native?.Workspace == nativeWorkspace && native.File == "", "Native workspace resolves to source even when deployment belongs to a previous migrated project");
        check(NativeExternalEditorTarget.Resolve(nativeProject, nativeBank)?.File == nativeBank, "Native single-file launch supports nested TXT banks");
        check(NativeExternalEditorTarget.Resolve(nativeProject, foreignOwner) == null, "External file resolution excludes files outside native Excel source");
        var nativeLaunch = new ExternalEditorSettings(Environment.ProcessPath!, WorkspaceArguments: ["{files}"]).StartInfo(native!.File, native.Workspace, true);
        check(nativeLaunch.ArgumentList.Contains(nativeFile) && nativeLaunch.ArgumentList.Contains(nativeBank) && nativeLaunch.ArgumentList.All(p => Contains(nativeWorkspace, p)) &&
            nativeLaunch.ArgumentList.Count == nativeLaunch.ArgumentList.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Count(),
            "Native workspace launch passes each source TXT path once instead of deployment files");
        check(File.ReadAllBytes(foreignOwner).SequenceEqual(ownerBefore) && !Directory.Exists(Inside(nativeProject.Cache, "builds")) && ExternalEditorSync.Load(nativeProject) == null, "Native external opening needs no build, deployment ownership change, or synchronization session");
        var generated = TableData.FromTsv(File.ReadAllBytes(nativeFile), "generated", "global/excel/generated.txt");
        var generatedFile = TableData.FileFor(nativeProject, "tables", "generated"); TableData.Write(generatedFile, generated);
        check(NativeExternalEditorTarget.Resolve(nativeProject) == null && NativeExternalEditorTarget.Resolve(nativeProject, generatedFile) == null, "JSON-source workspaces retain the generated deployment and synchronization route");
        check(NativeExternalEditorTarget.Resolve(nativeProject, nativeFile)?.File == nativeFile, "Native assets can still open directly in a mixed source project");
        var folder = Path.Combine(root, "external-source"); Directory.CreateDirectory(folder);
        var project = new ModProject(folder, "external-fixture", "External");
        var source = TableData.FileFor(project, "tables", "example");
        var table = TableData.FromTsv(Utf8.GetBytes("code\tvalue\tnote\r\na\t10\tfirst\r\nb\t20\tsecond\r\n"), "example", "global/excel/example.txt");
        table.Schema["identityColumns"] = new JsonArray("code"); TableData.Write(source, table);
        var target = Path.Combine(root, "external-game/mods/External");
        var output = Inside(target, "External.mpq/data/global/excel/example.txt");
        var build = BuildService.Build(project, "standard"); DeploymentService.Deploy(project, build, target);
        ExternalEditorSync.Begin(project, build, target);
        void External(int row, string field, string value)
        {
            var txt = TableData.FromTsv(File.ReadAllBytes(output), "example", "global/excel/example.txt"); txt.SetCell(row, field, value); AtomicWrite(output, txt.EncodeTsv());
        }
        void Shared(int row, string field, string value) { var t = TableData.Load(source); t.SetCell(row, field, value); TableData.Write(source, t); }
        string Txt(int row, string field) => TableData.FromTsv(File.ReadAllBytes(output), "example", "global/excel/example.txt").Cell(row, field);
        External(0, "value", "11");
        throws(() => BuildService.Build(project, "standard"), "Build protects pending external edits");
        throws(() => DeploymentService.Deploy(project, build, target, overwriteDestination: true), "Overwrite destination cannot discard pending external edits");
        throws(() => ExternalEditorSync.End(project), "Ending a session protects pending external edits");
        ExternalEditorSync.Synchronize(project);
        check(TableData.Load(source).Cell(0, "value") == "11", "External TXT save updates source JSON");
        check(ExternalEditorSync.Synchronize(project).Count == 0, "Own sync writes converge without feedback loops");
        var extended = Read(source); extended["customMetadata"] = "preserve";
        AtomicWrite(source, Utf8.GetBytes("\uFEFF// authored metadata\n" + Json(extended)));
        External(0, "note", "external note"); ExternalEditorSync.Synchronize(project);
        check(Utf8.GetString(File.ReadAllBytes(source)).StartsWith('\uFEFF') && Read(source).S("customMetadata") == "preserve", "External sync accepts commented BOM source and retains metadata and encoding");
        Shared(0, "note", "Studio note"); External(1, "value", "21");
        ExternalEditorSync.Synchronize(project);
        check(Txt(0, "note") == "Studio note" && TableData.Load(source).Cell(1, "value") == "21", "Concurrent edits to different cells merge in both directions");
        Shared(0, "value", "12"); External(0, "value", "13");
        string? stamp = null;
        try { ExternalEditorSync.Synchronize(project); } catch (ExternalSyncConflict e) { stamp = e.Stamp; }
        check(stamp != null && TableData.Load(source).Cell(0, "value") == "12" && Txt(0, "value") == "13", "Same-cell conflict preserves both versions for review");
        Shared(1, "note", "changed after review");
        throws(() => ExternalEditorSync.Synchronize(project, choice: ExternalConflictChoice.External, reviewedStamp: stamp), "Stale conflict review cannot overwrite newer edits");
        try { ExternalEditorSync.Synchronize(project); } catch (ExternalSyncConflict e) { stamp = e.Stamp; }
        ExternalEditorSync.Synchronize(project, choice: ExternalConflictChoice.External, reviewedStamp: stamp);
        check(TableData.Load(source).Cell(0, "value") == "13" && Txt(1, "note") == "changed after review", "External conflict choice retains independent Studio edits");
        Shared(0, "value", "14"); External(0, "value", "15");
        try { ExternalEditorSync.Synchronize(project); } catch (ExternalSyncConflict e) { stamp = e.Stamp; }
        ExternalEditorSync.Synchronize(project, choice: ExternalConflictChoice.Studio, reviewedStamp: stamp);
        check(Txt(0, "value") == "14", "Studio conflict choice updates external TXT");
        External(0, "value", "16");
        throws(() => ExternalEditorSync.Synchronize(project, new HashSet<string> { source }), "Unsaved Studio document blocks external import");
        using (var canceled = new CancellationTokenSource()) { canceled.Cancel(); throws(() => ExternalEditorSync.Synchronize(project, token: canceled.Token), "Cancellation before commit preserves both sides"); }
        check(TableData.Load(source).Cell(0, "value") == "14", "Canceled sync did not modify source");
        ExternalEditorSync.Synchronize(project);
        var clean = File.ReadAllBytes(output); var savedSource = File.ReadAllBytes(source);
        AtomicWrite(output, Utf8.GetBytes("code\tvalue\tnote\na\t99\tx\textra\n"));
        throws(() => ExternalEditorSync.Synchronize(project), "Malformed/partial TXT save is rejected");
        check(File.ReadAllBytes(source).SequenceEqual(savedSource), "Malformed save leaves JSON intact");
        AtomicWrite(output, Utf8.GetBytes("renamed\tvalue\tnote\na\t99\tx\nb\t20\ty\n"));
        throws(() => ExternalEditorSync.Synchronize(project), "Changed TXT headers are rejected");
        AtomicWrite(output, Utf8.GetBytes("code\tvalue\tnote\nb\t20\ty\na\t99\tx\n"));
        throws(() => ExternalEditorSync.Synchronize(project), "Reordered protected identities are rejected");
        File.Delete(output); throws(() => ExternalEditorSync.Synchronize(project), "Deleted TXT is preserved as a pending problem"); AtomicWrite(output, clean);
        var replacement = output + ".replacement"; AtomicWrite(replacement, Utf8.GetBytes(Utf8.GetString(clean) + "c\t30\tappended\r\n")); File.Move(replacement, output, true);
        ExternalEditorSync.Synchronize(project);
        var appended = TableData.Load(source);
        check(appended.Records.Count == 3 && appended.Cell(2, "code") == "c" && appended.Records[0].S("sourceId") == "row-00000", "Atomic editor replacement imports appended rows and preserves existing IDs");
        check(appended.Records[2].S("sourceId") != "row-00002", "External append receives a new Studio identity");
        // A fresh project object demonstrates that the baseline is on disk, independent of the app process.
        External(2, "note", "offline edit");
        ExternalEditorSync.Synchronize(new ModProject(folder, project.Id, project.Name));
        check(TableData.Load(source).Cell(2, "note") == "offline edit", "Persistent session imports edits made while Studio was closed");
        build = BuildService.Build(project, "standard"); DeploymentService.Deploy(project, build, target, overwriteDestination: false);
        check(ExternalEditorSync.Synchronize(project).Count == 0, "Redeployment rebases the session and keeps ownership coherent without overwrite");
        var otherBuild = BuildService.Build(project, "d2rl");
        throws(() => DeploymentService.Deploy(project, otherBuild, target, overwriteDestination: true), "Active session blocks deployment profile replacement");
        throws(() => DeploymentService.Deploy(project, otherBuild, Path.Combine(root, "different/mods/External"), overwriteDestination: true), "Active session blocks deployment target replacement");

        // Exercise interrupted multi-file commit recovery without requiring a process crash.
        var beforeSource = File.ReadAllBytes(source); var afterTable = TableData.Load(source); afterTable.SetCell(0, "note", "recovered"); var afterSource = Utf8.GetBytes(Json(afterTable.ToFile()));
        var journalFile = Inside(project.Cache, "external-editor/pending.json");
        AtomicWrite(journalFile, JsonSerializer.SerializeToUtf8Bytes(new ExternalSyncJournal(project.Id, target, [new(source, beforeSource, afterSource)]), Pretty));
        throws(() => BuildService.Build(project, "standard"), "Interrupted synchronization blocks a build");
        ExternalEditorSync.Synchronize(project);
        check(Txt(0, "note") == "recovered" && !File.Exists(journalFile), "Interrupted synchronization recovers before merging");
        beforeSource = File.ReadAllBytes(source);
        AtomicWrite(journalFile, JsonSerializer.SerializeToUtf8Bytes(new ExternalSyncJournal(project.Id, target, [new(source, beforeSource, afterSource)]), Pretty));
        Shared(0, "note", "newer external source edit");
        throws(() => ExternalEditorSync.Synchronize(project), "Recovery refuses to overwrite newer disk changes");
        check(File.Exists(journalFile), "Conflicting recovery retains its journal");
        AtomicWrite(source, beforeSource); ExternalEditorSync.Synchronize(project);
        ExternalEditorSync.End(project);

        // A single source table can emit multiple banks with a target-specific override.
        table = TableData.Load(source); ((JsonArray)table.Schema["targets"]!).Add("global/excel/bank/example.txt"); TableData.Write(source, table);
        var profilePath = Inside(project.Root, "compatibility/standard/profile.json"); var rulePath = Inside(project.Root, "compatibility/standard/bank.json");
        WriteJson(profilePath, new JsonObject { ["schemaVersion"] = 1, ["id"] = "standard", ["stringMode"] = "standard", ["tableOverrides"] = new JsonArray("bank.json") });
        WriteJson(rulePath, new JsonObject { ["table"] = "example", ["record"] = "row-00000", ["reason"] = "bank fixture", ["targets"] = new JsonArray("global/excel/bank/example.txt"), ["changes"] = new JsonObject { ["value"] = new JsonObject { ["expect"] = table.Cell(0, "value"), ["value"] = "100" } } });
        build = BuildService.Build(project, "standard"); DeploymentService.Deploy(project, build, target); ExternalEditorSync.Begin(project, build, target);
        var bankFile = Inside(target, "External.mpq/data/global/excel/bank/example.txt");
        var bank = TableData.FromTsv(File.ReadAllBytes(bankFile), "example", "global/excel/bank/example.txt"); bank.SetCell(0, "value", "101"); AtomicWrite(bankFile, bank.EncodeTsv());
        ExternalEditorSync.Synchronize(project);
        check(TableData.Load(source).Cell(0, "value") == "16" && Read(rulePath)["changes"]!["value"].S("value") == "101" && Read(rulePath)["targets"]![0]!.GetValue<string>().Contains("bank/"), "External override edits preserve shared source and bank scope");
        External(1, "note", "shared across banks"); ExternalEditorSync.Synchronize(project);
        check(TableData.FromTsv(File.ReadAllBytes(bankFile), "example", "global/excel/bank/example.txt").Cell(1, "note") == "shared across banks", "Shared edits propagate to sibling banks");
        External(1, "note", "first proposal"); bank = TableData.FromTsv(File.ReadAllBytes(bankFile), "example", "global/excel/bank/example.txt"); bank.SetCell(1, "note", "different proposal"); AtomicWrite(bankFile, bank.EncodeTsv());
        throws(() => ExternalEditorSync.Synchronize(project), "Divergent edits in shared TXT banks do not silently choose a winner");

        var exe = Environment.ProcessPath!; var workspace = Path.GetDirectoryName(output)!;
        var start = new ExternalEditorSettings(exe).StartInfo(output, workspace, false);
        check(start.ArgumentList.SequenceEqual(new[] { output }) && !start.UseShellExecute, "Single-file editor launch passes an intact path without shell quoting");
        start = new ExternalEditorSettings(exe, WorkspaceArguments: ["--folder", "{workspace}"]).StartInfo("", workspace, true);
        check(start.ArgumentList.SequenceEqual(new[] { "--folder", workspace }), "Workspace launch supports folder-specific arguments");
        start = new ExternalEditorSettings(exe, WorkspaceArguments: ["{files}"]).StartInfo("", workspace, true);
        check(start.ArgumentList.Contains(output) && start.ArgumentList.Contains(bankFile) &&
            start.ArgumentList.Count == start.ArgumentList.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Count(),
            "TXTeditor workspace mode passes each TXT path exactly once");
        var repeated = new ExternalEditorSettings(exe, WorkspaceArguments: ["{files}", "{files}"]).StartInfo("", workspace, true);
        check(repeated.ArgumentList.SequenceEqual(start.ArgumentList), "Repeated workspace file placeholders do not reopen every TXT");
        // Sibling banks and foreign TXT copies beside the tables must not open as duplicate tabs; the workspace launch gets one output per table.
        var foreign = Inside(workspace, "base/example.txt"); AtomicWrite(foreign, File.ReadAllBytes(output));
        var session = ExternalEditorSync.Load(project)!; var tracked = session.Tables.Select(t => Inside(session.Target, t.Outputs.Keys.First())).ToList();
        var scoped = new ExternalEditorSettings(exe, WorkspaceArguments: ["{files}"]).StartInfo("", workspace, true, tracked);
        check(scoped.ArgumentList.Contains(output) && !scoped.ArgumentList.Contains(bankFile) && !scoped.ArgumentList.Contains(foreign), "Workspace launch opens one TXT per table, skipping sibling banks and foreign copies");
        File.Delete(foreign); Directory.Delete(Path.GetDirectoryName(foreign)!);
        check(new ExternalEditorSettings().StartInfo(output, workspace, false).UseShellExecute, "Default file association remains supported");
        var doc = new Document(source); Shared(1, "value", "90"); doc.ReloadClean(); check(doc.Table!.Cell(1, "value") == "90" && !doc.ExternalChange && !doc.IsDirty, "Clean Studio document reload follows synchronized disk state");
        doc.SetCells([(1, "value", "91")]); throws(doc.ReloadClean, "Automatic reload cannot discard unsaved Studio edits");
    }
}

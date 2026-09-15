using ModStudio.Core;
using static ModStudio.Core.Storage;

internal static class DeploymentOverwriteTests
{
    public static async Task RunAsync(string root, Action<bool, string> check, Action<Action, string> throws, Action<string, string> write)
    {
        var native = Path.Combine(root, "overwrite-native");
        write(Path.Combine(native, "global/excel/base/charstats.txt"), "name\tvalue\nTest\t1\n");
        var project = ProjectImporter.Import(native, Path.Combine(root, "overwrite-project"), "Overwrite").Project;
        var destination = Path.Combine(root, "overwrite-game/mods/Overwrite");
        var relative = "Overwrite.mpq/data/global/excel/base/charstats.txt";
        var file = Inside(destination, relative);
        write(file, "existing mod file");
        write(Inside(destination, "personal.cfg"), "keep");
        check(RunSettings.Load(project, "standard").OverwriteDestination, "New run settings default to overwriting destination");
        write(RunSettings.SettingsFile(project, "standard"), "{\"deploymentDirectory\":\"\"}");
        check(RunSettings.Load(project, "standard").OverwriteDestination, "Legacy run settings default to overwriting destination");
        var settings = new RunSettings(destination, OverwriteDestination: false);
        settings.Save(project, "standard");
        check(!RunSettings.Load(project, "standard").OverwriteDestination && RunSettings.Load(project, "d2rl").OverwriteDestination, "Overwrite opt-out persists separately per profile");
        using var run = new RunController();
        var rejected = false;
        try { await run.ExecuteAsync(project, "standard", settings, true, false, CancellationToken.None); }
        catch (InvalidDataException ex) { rejected = ex.Message.Contains("Overwrite destination"); }
        check(rejected && File.ReadAllText(file) == "existing mod file", "Deploy with overwrite disabled protects unowned destination");
        var build = BuildService.Build(project, "standard");
        using (var canceled = new CancellationTokenSource())
        {
            throws(() => DeploymentService.Deploy(project, build, destination, canceled.Token, _ => canceled.Cancel(), overwriteDestination: true), "Interrupted overwrite deployment is canceled");
            check(File.ReadAllText(file) == "existing mod file" && !File.Exists(Inside(destination, ".studio-owner.json")), "Overwrite rollback restores pre-existing unowned file and ownership");
        }
        settings = settings with { OverwriteDestination = true }; settings.Save(project, "standard");
        build = await run.ExecuteAsync(project, "standard", RunSettings.Load(project, "standard"), true, false, CancellationToken.None);
        check(File.ReadAllBytes(file).SequenceEqual(File.ReadAllBytes(Inside(build.Output, relative))) && File.ReadAllText(Inside(destination, "personal.cfg")) == "keep", "Deploy overwrites existing mod files and retains unrelated files");
        write(file, "external edit");
        throws(() => DeploymentService.Deploy(project, build, destination, overwriteDestination: false), "Disabled overwrite protects externally edited owned file");
        DeploymentService.Deploy(project, build, destination, overwriteDestination: true);
        check(File.ReadAllBytes(file).SequenceEqual(File.ReadAllBytes(Inside(build.Output, relative))), "Enabled overwrite replaces externally edited owned file");
        var owner = Inside(destination, ".studio-owner.json");
        var manifest = Read(owner); manifest["projectId"] = "another-project"; WriteJson(owner, manifest);
        throws(() => DeploymentService.Deploy(project, build, destination, overwriteDestination: true), "Overwrite retains other-project ownership guard");
        throws(() => DeploymentService.Deploy(project, build, project.Root, overwriteDestination: true), "Overwrite retains source overlap guard");

        write(file, "external edit to preserve");
        write(Inside(destination, "former-project-only.cfg"), "keep former file");
        var former = new DeploymentManifest("previous-conversion", "previous-build", "standard", [
            new(relative, Hash("old deployed bytes"), 18), new("former-project-only.cfg", Hash("keep former file"), 16)]);
        var formerBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(former, Pretty); AtomicWrite(owner, formerBytes);
        try { await run.ExecuteAsync(project, "standard", settings, true, false, CancellationToken.None, reviewOwnership: _ => Task.FromResult(false)); throw new Exception("Expected ownership cancellation"); }
        catch (OperationCanceledException) { check(File.ReadAllBytes(owner).SequenceEqual(formerBytes) && File.ReadAllText(file) == "external edit to preserve", "Declining deployment reuse preserves owner and deployed edits"); }
        bool staleRejected = false;
        try
        {
            await run.ExecuteAsync(project, "standard", settings, true, false, CancellationToken.None, reviewOwnership: _ =>
            {
                AtomicWrite(owner, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(former with { ProjectId = "changed-during-review" }, Pretty));
                return Task.FromResult(true);
            });
        }
        catch (InvalidDataException ex) { staleRejected = ex.Message.Contains("since review"); }
        check(staleRejected && File.ReadAllText(file) == "external edit to preserve", "Ownership changes during review invalidate reuse approval");
        AtomicWrite(owner, formerBytes);
        using (var cancellation = new CancellationTokenSource())
        {
            try
            {
                await run.ExecuteAsync(project, "standard", settings, true, false, cancellation.Token,
                    message => { if (message.StartsWith("Deployed ")) cancellation.Cancel(); }, _ => Task.FromResult(true));
                throw new Exception("Expected canceled transfer");
            }
            catch (OperationCanceledException) { check(File.ReadAllBytes(owner).SequenceEqual(formerBytes) && File.ReadAllText(file) == "external edit to preserve", "Canceled ownership transfer rolls back files and former owner"); }
        }
        build = await run.ExecuteAsync(project, "standard", settings, true, false, CancellationToken.None, reviewOwnership: _ => Task.FromResult(true));
        var nextOwner = System.Text.Json.JsonSerializer.Deserialize<DeploymentManifest>(File.ReadAllBytes(owner), Pretty)!;
        check(nextOwner.ProjectId == project.Id && File.ReadAllText(Inside(destination, "former-project-only.cfg")) == "keep former file" && File.ReadAllText(Inside(destination, "personal.cfg")) == "keep", "Approved reuse transfers ownership while preserving files outside the build");
        var backups = Directory.GetDirectories(Inside(project.Cache, "deployment-backups"));
        check(backups.Any(folder => File.ReadAllBytes(Inside(folder, "files/.studio-owner.json")).SequenceEqual(formerBytes) && File.ReadAllText(Inside(folder, "files/" + relative)) == "external edit to preserve" && File.Exists(Inside(folder, "backup.json"))), "Ownership transfer keeps a durable backup of replaced edits and the original ownership record");
        ExternalEditorSync.Begin(project, build, destination);
        var deployedTable = TableData.FromTsv(File.ReadAllBytes(file), "charstats", "global/excel/base/charstats.txt");
        deployedTable.SetCell(0, "value", "2"); AtomicWrite(file, deployedTable.EncodeTsv()); ExternalEditorSync.Synchronize(project);
        var session = ExternalEditorSync.Load(project)!;
        check(session.ProjectId == project.Id && TableData.Load(Inside(project.Root, session.Tables.Single().Source)).Cell(0, "value") == "2", "Converted project can start external synchronization after approved deployment reuse");
        ExternalEditorSync.End(project);
    }
}

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
    }
}

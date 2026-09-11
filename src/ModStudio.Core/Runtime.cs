using System.Diagnostics;
using System.Text.Json;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

public record RunSettings(string DeploymentDirectory = "", string Executable = "", string Runner = "", string[]? RunnerArguments = null, string[]? Arguments = null, bool SaveBeforePlay = true)
{
    public IReadOnlyList<string> PathIssues(ModProject project)
    {
        var issues = new List<string>();
        try
        {
            if (!string.IsNullOrWhiteSpace(Executable) && !File.Exists(Executable)) issues.Add("Game executable must be an existing file, not a folder.");
            if (!string.IsNullOrWhiteSpace(Runner) && !File.Exists(Runner)) issues.Add("Runner must be an existing executable file.");
            if (!string.IsNullOrWhiteSpace(DeploymentDirectory))
            {
                var target = Path.GetFullPath(DeploymentDirectory);
                if (File.Exists(target)) issues.Add("Deployment must be a folder, not a file.");
                if (Contains(project.Root, target) || Contains(target, project.Root)) issues.Add("Deployment must be outside the source project, without overlapping it.");
                if (!Path.GetFileName(Path.TrimEndingDirectorySeparator(target)).Equals(project.Name, StringComparison.Ordinal)) issues.Add($"Select the final mod folder named {project.Name}, not the game, mods, .mpq or data folder.");
                if (!string.IsNullOrWhiteSpace(Executable) && !string.Equals(Path.TrimEndingDirectorySeparator(target), Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Executable)!, "mods", project.Name)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) issues.Add($"For Play, deployment must be beside the selected executable under mods/{project.Name}.");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { issues.Add("Invalid path: " + ex.Message); }
        return issues;
    }
    public static string SettingsFile(ModProject project, string profile) => Inside(project.Cache, $"settings/{profile}.json");
    public static RunSettings Load(ModProject project, string profile) => File.Exists(SettingsFile(project, profile)) ? JsonSerializer.Deserialize<RunSettings>(File.ReadAllText(SettingsFile(project, profile)), Pretty)! : new();
    public void Save(ModProject project, string profile) => AtomicWrite(SettingsFile(project, profile), JsonSerializer.SerializeToUtf8Bytes(this, Pretty));
}
public record DeploymentManifest(string ProjectId, string BuildId, string Profile, List<BuildFile> Files);
public record JournalEntry(string Path, string? Before, string? After);
public record DeploymentJournal(string ProjectId, List<JournalEntry> Entries);

public static class DeploymentService
{
    private const string Owner = ".studio-owner.json";
    private const string Transaction = ".studio-transaction";
    public static void Deploy(ModProject project, BuildResult build, string target, CancellationToken token = default, Action<string>? progress = null)
    {
        Require(build.ProjectId == project.Id && build.ModName == project.Name, "Build belongs to a different project.");
        Require(!string.IsNullOrWhiteSpace(target), "Choose a deployment mod folder in Run settings."); target = Path.GetFullPath(target); NoLinks(target);
        Require(!Contains(project.Root, target) && !Contains(target, project.Root), "Source and deployment folders must not overlap.");
        Require(Path.GetFileName(target).Equals(project.Name, StringComparison.Ordinal), $"Deployment must be the mod folder named {project.Name}, for example game/mods/{project.Name}.");
        Directory.CreateDirectory(target);
        using var fileLock = new FileStream(Inside(target, ".studio-deploy.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var transaction = Inside(target, Transaction); if (Directory.Exists(transaction)) Recover(project, target, progress);
        var ownerFile = Inside(target, Owner); DeploymentManifest? previous = null;
        if (File.Exists(ownerFile))
        {
            previous = JsonSerializer.Deserialize<DeploymentManifest>(File.ReadAllText(ownerFile), Pretty)!;
            Require(previous.ProjectId == project.Id, "Another project owns this deployment. Choose a separate mod folder.");
        }
        var next = build.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        Require(next.Count == build.Files.Count && build.Files.All(f => !f.Path.StartsWith(".studio-", StringComparison.OrdinalIgnoreCase)), "Invalid build ownership paths.");
        foreach (var file in build.Files)
        {
            token.ThrowIfCancellationRequested(); var source = Inside(build.Output, file.Path); Require(File.Exists(source) && Hash(File.ReadAllBytes(source)) == file.Sha256, "Build output changed; rebuild before deploying.");
            var dest = Inside(target, file.Path);
            if (File.Exists(dest) && previous?.Files.All(f => !f.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase)) != false)
                Require(Hash(File.ReadAllBytes(dest)) == file.Sha256, $"Unowned destination file would be overwritten: {file.Path}. Use a separate test mod folder.");
        }
        foreach (var owned in previous?.Files ?? [])
        {
            var dest = Inside(target, owned.Path); if (File.Exists(dest)) Require(Hash(File.ReadAllBytes(dest)) == owned.Sha256, $"Deployed file was edited outside Studio: {owned.Path}. Preserve/import it before deploying.");
        }
        var ownership = JsonSerializer.SerializeToUtf8Bytes(new DeploymentManifest(project.Id, build.Id, build.Profile, build.Files), Pretty);
        var all = build.Files.Select(f => f.Path).Concat(previous?.Files.Select(f => f.Path) ?? []).Append(Owner).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var entries = all.Select(relative => new JournalEntry(relative, File.Exists(Inside(target, relative)) ? Hash(File.ReadAllBytes(Inside(target, relative))) : null, relative == Owner ? Hash(ownership) : next.GetValueOrDefault(relative)?.Sha256)).ToList();
        Directory.CreateDirectory(transaction);
        try
        {
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                if (entry.Before != null) { var backup = Inside(transaction, "backup/" + entry.Path); Directory.CreateDirectory(Path.GetDirectoryName(backup)!); File.Copy(Inside(target, entry.Path), backup); }
                if (entry.After != null)
                {
                    var staged = Inside(transaction, "next/" + entry.Path); Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                    File.WriteAllBytes(staged, entry.Path == Owner ? ownership : File.ReadAllBytes(Inside(build.Output, entry.Path)));
                    Require(Hash(File.ReadAllBytes(staged)) == entry.After, "Staged deployment hash mismatch.");
                }
            }
            AtomicWrite(Inside(transaction, "journal.json"), JsonSerializer.SerializeToUtf8Bytes(new DeploymentJournal(project.Id, entries), Pretty));
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested(); var dest = Inside(target, entry.Path);
                Require((File.Exists(dest) ? Hash(File.ReadAllBytes(dest)) : null) == entry.Before, "Destination changed during deployment.");
                if (entry.After == null) { if (File.Exists(dest)) File.Delete(dest); }
                else { Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Move(Inside(transaction, "next/" + entry.Path), dest, true); }
                progress?.Invoke("Deployed " + entry.Path);
            }
            // The owner manifest is the last committed file. Recovery can always roll back a partial commit.
            Directory.Delete(transaction, true);
        }
        catch
        {
            if (File.Exists(Path.Combine(transaction, "journal.json"))) Recover(project, target, progress);
            else if (Directory.Exists(transaction)) Directory.Delete(transaction, true);
            throw;
        }
    }
    public static void Recover(ModProject project, string target, Action<string>? progress = null)
    {
        var transaction = Inside(target, Transaction); var journalFile = Inside(transaction, "journal.json");
        Require(File.Exists(journalFile), "Incomplete deployment staging without a journal. Preserve it and remove it manually before retrying.");
        var journal = JsonSerializer.Deserialize<DeploymentJournal>(File.ReadAllText(journalFile), Pretty)!;
        Require(journal.ProjectId == project.Id, "Recovery journal belongs to a different project.");
        foreach (var entry in journal.Entries)
        {
            var current = Inside(target, entry.Path); var hash = File.Exists(current) ? Hash(File.ReadAllBytes(current)) : null;
            Require(hash == entry.Before || hash == entry.After, $"Recovery found an external edit: {entry.Path}. Journal retained.");
            if (entry.Before != null) Require(Hash(File.ReadAllBytes(Inside(transaction, "backup/" + entry.Path))) == entry.Before, "Recovery backup is damaged.");
        }
        foreach (var entry in journal.Entries.AsEnumerable().Reverse())
        {
            var dest = Inside(target, entry.Path);
            if (entry.Before == null) { if (File.Exists(dest)) File.Delete(dest); }
            else AtomicWrite(dest, File.ReadAllBytes(Inside(transaction, "backup/" + entry.Path)));
        }
        Directory.Delete(transaction, true); progress?.Invoke("Previous deployment restored.");
    }
}

public sealed class RunController : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private Process? child;
    public bool Running => child is { HasExited: false };
    public static ProcessStartInfo CreateStartInfo(ModProject project, RunSettings settings)
    {
        Require(File.Exists(settings.Executable), "Select an existing game/loader executable in Run settings.");
        Require(Path.GetFullPath(settings.DeploymentDirectory) == Path.GetFullPath(Path.Combine(Path.GetDirectoryName(settings.Executable)!, "mods", project.Name)), "Play requires deployment to the selected game's mods/<mod-name> folder.");
        if (!OperatingSystem.IsWindows() && settings.Executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) Require(File.Exists(settings.Runner), "Configure Wine/Proton or another supported runner for a Windows executable on this platform.");
        if (!string.IsNullOrEmpty(settings.Runner)) Require(File.Exists(settings.Runner), "Runner executable does not exist.");
        var start = new ProcessStartInfo { FileName = string.IsNullOrEmpty(settings.Runner) ? settings.Executable : settings.Runner, WorkingDirectory = Path.GetDirectoryName(settings.Executable)!, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        if (!string.IsNullOrEmpty(settings.Runner)) { foreach (var arg in settings.RunnerArguments ?? []) start.ArgumentList.Add(arg); start.ArgumentList.Add(settings.Executable); }
        foreach (var arg in new[] { "-mod", project.Name, "-txt" }.Concat(settings.Arguments ?? [])) start.ArgumentList.Add(arg);
        return start;
    }
    public async Task<BuildResult> ExecuteAsync(ModProject project, string profile, RunSettings settings, bool deploy, bool play, CancellationToken token, Action<string>? progress = null)
    {
        Require(await gate.WaitAsync(0, token), "Another build/deployment is already running.");
        try
        {
            Require(!Running || !deploy, "Stop this editor's running game before deploying again.");
            var start = play ? CreateStartInfo(project, settings) : null;
            var build = await Task.Run(() => BuildService.Build(project, profile, token, progress), token);
            if (deploy) await Task.Run(() => DeploymentService.Deploy(project, build, settings.DeploymentDirectory, token, progress), token);
            token.ThrowIfCancellationRequested();
            if (start != null)
            {
                child?.Dispose(); child = new Process { StartInfo = start, EnableRaisingEvents = true };
                child.OutputDataReceived += (_, e) => { if (e.Data != null) progress?.Invoke(e.Data); }; child.ErrorDataReceived += (_, e) => { if (e.Data != null) progress?.Invoke(e.Data); };
                child.Exited += (_, _) => progress?.Invoke("Launched process exited.");
                try { Require(child.Start(), "Process did not start."); }
                catch { child.Dispose(); child = null; throw; }
                child.BeginOutputReadLine(); child.BeginErrorReadLine();
                progress?.Invoke($"Process started · PID {child.Id} · build {build.Id[..8]}. Verify mod loading in game.");
            }
            return build;
        }
        finally { gate.Release(); }
    }
    public void Stop() { try { if (Running) child!.Kill(true); } catch (InvalidOperationException) { /* The owned process exited between checking and stopping. */ } }
    public void Dispose() { child?.Dispose(); gate.Dispose(); }
}

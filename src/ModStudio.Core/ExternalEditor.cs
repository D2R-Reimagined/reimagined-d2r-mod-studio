using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

public record NativeExternalEditorTarget(string File, string Workspace)
{
    /// <summary>Native TXT already is editable source. It needs neither deployment nor JSON synchronization.</summary>
    public static NativeExternalEditorTarget? Resolve(ModProject project, string? source = null)
    {
        var workspace = Inside(project.Root, "data/global/excel");
        if (source != null)
        {
            var file = Path.GetFullPath(source);
            if (!Contains(workspace, file) || !file.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return null;
            NoLinks(file); Require(System.IO.File.Exists(file), "Native TXT file is unavailable.");
            return new(file, workspace);
        }
        var tables = Inside(project.Root, "source/tables");
        if (Directory.Exists(tables) && Directory.EnumerateFiles(tables, "*.json").Any() || ProjectLayout.NeedsUpgrade(project.Root)) return null;
        return Files(workspace).Any(p => p.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) ? new("", workspace) : null;
    }
}

public sealed record ExternalEditorSettings(string Executable = "", string[]? FileArguments = null, string[]? WorkspaceArguments = null, bool Configured = false)
{
    /// <summary>Builds the launch. <paramref name="files"/> lists the TXT tables {files} expands to; when omitted every TXT below the workspace is used.
    /// Sessions pass one tracked output per table so sibling banks and foreign copies beside the tables are not opened as duplicates.</summary>
    public ProcessStartInfo StartInfo(string file, string workspace, bool openWorkspace, IEnumerable<string>? files = null)
    {
        var target = openWorkspace ? workspace : file;
        Require(openWorkspace ? Directory.Exists(target) : File.Exists(target), "External editor target is unavailable.");
        if (string.IsNullOrWhiteSpace(Executable)) return new(target) { UseShellExecute = true };
        Require(File.Exists(Executable), "Choose an existing external editor executable.");
        var start = new ProcessStartInfo(Executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(Executable))! };
        var passedFiles = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var arg in (openWorkspace ? WorkspaceArguments : FileArguments) ?? [openWorkspace ? "{workspace}" : "{file}"])
        {
            if (arg == "{files}") foreach (var txt in (files ?? Files(workspace)).Where(p => p.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
            {
                if (passedFiles.Add(txt)) start.ArgumentList.Add(txt);
            }
            else if (arg == "{file}" && !openWorkspace)
            {
                if (passedFiles.Add(file)) start.ArgumentList.Add(file);
            }
            else start.ArgumentList.Add(arg.Replace("{file}", file).Replace("{workspace}", workspace));
        }
        Require(!OperatingSystem.IsWindows() || start.ArgumentList.Sum(a => a.Length + 3) + Executable.Length < 30000, "Workspace arguments exceed the Windows command-line limit. Use {workspace} if supported, or open the folder from inside your editor.");
        return start;
    }
}

public record ExternalTable(string Source, string BaselineSource, Dictionary<string, byte[]> Outputs);
public record ExternalSession(string ProjectId, string Profile, string Target, List<ExternalTable> Tables, Dictionary<string, string>? Inputs = null);
public enum ExternalConflictChoice { Review, Studio, External }
public sealed class ExternalSyncConflict(string message, string stamp) : IOException(message)
{
    public string Stamp { get; } = stamp;
}
public record ExternalWrite(string Path, byte[] Before, byte[] After);
public record ExternalSyncJournal(string ProjectId, string Target, List<ExternalWrite> Writes);

/// <summary>One persistent deployment/profile session per project. Only generated TXT tables are tracked.
/// All proposed changes are validated before a recoverable transaction writes sources, outputs and finally the baseline.</summary>
public static class ExternalEditorSync
{
    private static bool SamePath(string a, string b) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static string StateFile(ModProject project) => Inside(project.Cache, "external-editor/session.json");
    private static string JournalFile(ModProject project) => Inside(project.Cache, "external-editor/pending.json");
    public static ExternalSession? Load(ModProject project)
    {
        if (!File.Exists(StateFile(project))) return null;
        var session = JsonSerializer.Deserialize<ExternalSession>(File.ReadAllBytes(StateFile(project)), Pretty)!;
        Require(session.ProjectId == project.Id, "External editor session belongs to another project.");
        NoLinks(session.Target);
        Require(!Contains(project.Root, session.Target) && !Contains(session.Target, project.Root), "External editor deployment overlaps source.");
        foreach (var table in session.Tables)
        {
            Require(table.Source.StartsWith("source/tables/", StringComparison.Ordinal) && table.Source.EndsWith(".json", StringComparison.Ordinal), "Invalid external source mapping.");
            _ = Inside(project.Root, table.Source);
            foreach (var path in table.Outputs.Keys)
            {
                Require(path.StartsWith(project.Name + ".mpq/data/global/excel/", StringComparison.Ordinal) && path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase), "Invalid external output mapping.");
                _ = Inside(session.Target, path);
            }
        }
        return session;
    }
    private static void CheckOwner(ModProject project, ExternalSession session)
    {
        Require(!Directory.Exists(Inside(session.Target, ".studio-transaction")), "Finish deployment recovery before synchronizing external edits.");
        var owner = JsonSerializer.Deserialize<DeploymentManifest>(File.ReadAllBytes(Inside(session.Target, ".studio-owner.json")), Pretty);
        Require(owner?.ProjectId == project.Id && owner.Profile == session.Profile, "External editor deployment owner or profile changed.");
    }
    public static void RequireClean(ModProject project, string? profile = null, string? target = null)
    {
        Require(!File.Exists(JournalFile(project)), "External synchronization was interrupted. Synchronize now to recover before building.");
        var session = Load(project); if (session == null) return;
        CheckOwner(project, session);
        Require(profile == null || profile == session.Profile, "End external editing before deploying a different profile.");
        Require(target == null || SamePath(target, session.Target), "End external editing before changing deployment folders.");
        foreach (var table in session.Tables)
            foreach (var output in table.Outputs)
                Require(File.Exists(Inside(session.Target, output.Key)) && Hash(File.ReadAllBytes(Inside(session.Target, output.Key))) == Hash(output.Value), "External TXT edits are pending. Synchronize before building or deploying: " + output.Key);
    }
    public static ExternalSession Begin(ModProject project, BuildResult build, string target)
    {
        using var gate = BuildCache.Lock(project);
        RequireClean(project, build.Profile, target);
        return CaptureBuild(project, build, target);
    }
    internal static void Deployed(ModProject project, BuildResult build, string target)
    {
        if (Load(project) != null) CaptureBuild(project, build, target);
    }
    private static ExternalSession CaptureBuild(ModProject project, BuildResult build, string target)
    {
        target = Path.GetFullPath(target); NoLinks(target);
        Require(!Contains(project.Root, target) && !Contains(target, project.Root), "External editor deployment must not overlap source.");
        Require(Path.GetFileName(target) == project.Name, "External editor deployment must be the project's mod folder.");
        Require(build.ProjectId == project.Id && build.ModName == project.Name, "Build belongs to another project.");
        var owner = JsonSerializer.Deserialize<DeploymentManifest>(File.ReadAllBytes(Inside(target, ".studio-owner.json")), Pretty)!;
        Require(owner.ProjectId == project.Id && owner.BuildId == build.Id && owner.Profile == build.Profile, "Deploy this build before opening the external editor.");
        var tables = new List<ExternalTable>();
        foreach (var source in Directory.GetFiles(Inside(build.Snapshot, "source/tables"), "*.json"))
        {
            var table = TableData.Load(source); var outputs = new Dictionary<string, byte[]>();
            foreach (var bank in (JsonArray)table.Schema["targets"]!)
            {
                var path = project.Name + ".mpq/data/" + bank!.GetValue<string>();
                var bytes = File.ReadAllBytes(Inside(target, path));
                Require(build.Files.Any(f => f.Path == path && f.Sha256 == Hash(bytes)), "Deployed TXT changed while starting external editing: " + path);
                outputs.Add(path, bytes);
            }
            tables.Add(new(Relative(build.Snapshot, source), File.ReadAllText(source), outputs));
        }
        var inputs = tables.ToDictionary(t => t.Source, t => Hash(t.BaselineSource));
        var profileRelative = $"compatibility/{build.Profile}/profile.json";
        var capturedProfile = Inside(build.Snapshot, profileRelative);
        inputs[profileRelative] = File.Exists(capturedProfile) ? Hash(File.ReadAllBytes(capturedProfile)) : "missing";
        if (File.Exists(capturedProfile)) foreach (var reference in (JsonArray?)Read(capturedProfile)["tableOverrides"] ?? [])
        {
            var rule = Inside(Path.GetDirectoryName(capturedProfile)!, reference!.GetValue<string>());
            inputs[Relative(build.Snapshot, rule)] = Hash(File.ReadAllBytes(rule));
        }
        var session = new ExternalSession(project.Id, build.Profile, Path.GetFullPath(target), tables, inputs);
        Require(tables.Count > 0, "No generated TXT tables are available.");
        AtomicWrite(StateFile(project), JsonSerializer.SerializeToUtf8Bytes(session, Pretty));
        return session;
    }
    public static void End(ModProject project)
    {
        using var gate = BuildCache.Lock(project); RequireClean(project); File.Delete(StateFile(project));
    }
    private static JsonNode SourceNode(byte[] bytes) => JsonNode.Parse(Utf8.GetString(bytes).TrimStart('\uFEFF'), documentOptions: Document.SourceJsonOptions)!;
    private static TableData ParseSource(string text) => TableData.FromFile(JsonNode.Parse(text.TrimStart('\uFEFF'), documentOptions: Document.SourceJsonOptions)!, "external sync", out _);
    private static void Compatible(TableData baseline, TableData current, bool source)
    {
        Require(JsonNode.DeepEquals(baseline.Schema["columns"], current.Schema["columns"]), "Column/header changes require manual reconciliation.");
        Require(current.Records.Count >= baseline.Records.Count, "Deleted rows require manual reconciliation.");
        var originalRows = source ? null : Enumerable.Range(0, baseline.Records.Count).Select(row => string.Join('\t', baseline.Columns.Select(c => baseline.Cell(row, c)))).ToHashSet(StringComparer.Ordinal);
        for (int row = 0; row < baseline.Records.Count; row++)
        {
            if (source) Require(baseline.Records[row].S("sourceId") == current.Records[row].S("sourceId"), "Source rows moved; reconcile row identities before syncing.");
            foreach (var column in baseline.Columns.Where(baseline.IsIdentityColumn))
                Require(baseline.Cell(row, column) == current.Cell(row, column), "Changed row identities require manual reconciliation.");
            if (!source)
            {
                var old = string.Join('\t', baseline.Columns.Select(c => baseline.Cell(row, c)));
                var next = string.Join('\t', current.Columns.Select(c => current.Cell(row, c)));
                Require(old == next || !originalRows!.Contains(next), "A row now matches another original row. Reordering or ambiguous row replacement requires manual reconciliation.");
            }
        }
    }
    public static IReadOnlyList<string> Synchronize(ModProject project, IReadOnlySet<string>? dirtyFiles = null,
        ExternalConflictChoice choice = ExternalConflictChoice.Review, string? reviewedStamp = null, CancellationToken token = default)
    {
        using var gate = BuildCache.Lock(project); using var checks = PathChecks();
        Recover(project);
        var session = Load(project); if (session == null) return [];
        CheckOwner(project, session);
        using var deploymentLock = new FileStream(Inside(session.Target, ".studio-deploy.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        token.ThrowIfCancellationRequested();
        // Most watcher events are our own completed writes. Avoid decoding every table on an unchanged scan.
        if (session.Inputs != null && session.Inputs.All(p =>
            (File.Exists(Inside(project.Root, p.Key)) ? Hash(File.ReadAllBytes(Inside(project.Root, p.Key))) : "missing") == p.Value) &&
            session.Tables.All(t => t.Outputs.All(o => File.Exists(Inside(session.Target, o.Key)) && Hash(File.ReadAllBytes(Inside(session.Target, o.Key))) == Hash(o.Value)))) return [];
        var before = new Dictionary<string, byte[]>(); var after = new Dictionary<string, byte[]>();
        byte[] Capture(string path) { if (!before.TryGetValue(path, out var value)) before[path] = value = File.ReadAllBytes(path); return value; }
        var statePath = StateFile(project); Capture(statePath);
        var profilePath = Inside(project.Root, $"compatibility/{session.Profile}/profile.json");
        var rules = new Dictionary<string, JsonNode>();
        if (File.Exists(profilePath))
        {
            var settings = JsonNode.Parse(Capture(profilePath))!;
            foreach (var reference in (JsonArray?)settings["tableOverrides"] ?? [])
            {
                var rulePath = Inside(Path.GetDirectoryName(profilePath)!, reference!.GetValue<string>());
                Require(!rules.ContainsKey(rulePath), "Duplicate profile override reference.");
                rules.Add(rulePath, JsonNode.Parse(Capture(rulePath))!);
            }
        }
        var currentTables = new Dictionary<string, TableData>();
        var profileUnchanged = session.Inputs != null && before.Where(p => Contains(Inside(project.Root, "compatibility/" + session.Profile), p.Key))
            .All(p => session.Inputs.GetValueOrDefault(Relative(project.Root, p.Key)) == Hash(p.Value)) &&
            session.Inputs.GetValueOrDefault(Relative(project.Root, profilePath)) == (File.Exists(profilePath) ? Hash(Capture(profilePath)) : "missing");
        foreach (var tracked in session.Tables)
        {
            var path = Inside(project.Root, tracked.Source);
            var bytes = Capture(path);
            foreach (var output in tracked.Outputs) Capture(Inside(session.Target, output.Key));
            if (profileUnchanged && session.Inputs?.GetValueOrDefault(tracked.Source) == Hash(bytes) &&
                tracked.Outputs.All(o => Hash(before[Inside(session.Target, o.Key)]) == Hash(o.Value))) continue;
            Require(dirtyFiles?.Contains(path) != true, "Save the open document before external synchronization: " + tracked.Source);
            currentTables.Add(tracked.Source, ParseSource(Utf8.GetString(bytes)));
        }
        foreach (var path in rules.Keys) Require(dirtyFiles?.Contains(path) != true, "Save the open profile override before synchronizing.");
        Require(dirtyFiles?.Contains(profilePath) != true, "Save the open profile before synchronizing.");
        var stamp = Hash(string.Join("\n", before.OrderBy(p => p.Key).Select(p => p.Key + "=" + Hash(p.Value))));
        Require(choice == ExternalConflictChoice.Review || reviewedStamp == stamp, "Files changed since conflict review. Synchronize again to review the current versions.");
        var conflicts = new List<string>(); var proposals = new Dictionary<string, string>();
        JsonNode? Rule(TableData table, int row, string column, string output)
        {
            var bank = output[(project.Name + ".mpq/data/").Length..];
            var matches = rules.Values.Where(r => r.S("table") == table.Name && r.S("record") == table.Records[row].S("sourceId") && r["changes"]?[column] != null &&
                (r["targets"] is not JsonArray targets || targets.Any(t => t!.GetValue<string>() == bank))).ToArray();
            Require(matches.Length <= 1, "Competing profile overrides.");
            var rule = matches.FirstOrDefault();
            if (rule != null) Require(rule["changes"]![column].S("expect") == table.Cell(row, column), "Profile override is stale; reconcile its expected value before syncing.");
            return rule;
        }
        TableData Effective(TableData table, string output)
        {
            var resolved = ParseSource(Json(table.ToFile()));
            var bank = output[(project.Name + ".mpq/data/").Length..];
            var indices = table.Records.Select((r, i) => (Id: r.S("sourceId"), Index: i)).ToDictionary(r => r.Id, r => r.Index);
            var occupied = new HashSet<string>();
            foreach (var rule in rules.Values.Where(r => r.S("table") == table.Name && (r["targets"] is not JsonArray targets || targets.Any(t => t!.GetValue<string>() == bank))))
            {
                Require(indices.TryGetValue(rule.S("record"), out var row), "Unknown profile override row.");
                foreach (var change in rule["changes"]!.AsObject())
                {
                    Require(occupied.Add(row + ":" + change.Key), "Competing profile overrides.");
                    Require(change.Value.S("expect") == table.Cell(row, change.Key), "Profile override is stale; reconcile its expected value before syncing.");
                    resolved.SetCell(row, change.Key, change.Value.S("value"));
                }
            }
            return resolved;
        }
        void Propose(string key, string value, Action apply)
        {
            Require(!proposals.TryGetValue(key, out var previous) || previous == value, "Different TXT banks changed the same shared cell differently. Reconcile the banks before syncing.");
            proposals[key] = value; apply();
        }
        // Read every bank against the same current source, then apply collected changes together.
        var actions = new List<Action>();
        foreach (var tracked in session.Tables)
        {
            token.ThrowIfCancellationRequested();
            if (!currentTables.TryGetValue(tracked.Source, out var current)) continue;
            var baselineSource = ParseSource(tracked.BaselineSource);
            Compatible(baselineSource, current, true);
            foreach (var output in tracked.Outputs)
            {
                var baseline = TableData.FromTsv(output.Value, current.Name, output.Key);
                baseline.Schema["identityColumns"] = baselineSource.Schema["identityColumns"]?.DeepClone();
                var external = TableData.FromTsv(Capture(Inside(session.Target, output.Key)), current.Name, output.Key);
                Compatible(baseline, external, false);
                var effective = Effective(current, output.Key);
                for (int row = 0; row < baseline.Records.Count; row++) foreach (var column in baseline.Columns)
                {
                    var old = baseline.Cell(row, column); var value = external.Cell(row, column); var local = effective.Cell(row, column);
                    if (value == old || value == local) continue;
                    if (local != old)
                    {
                        conflicts.Add($"{tracked.Source} · row {row + 1} · {column}: Studio '{local}', external '{value}'");
                        if (choice != ExternalConflictChoice.External) continue;
                    }
                    int r = row; var c = column;
                    var rule = Rule(current, row, column, output.Key);
                    var key = rule == null ? tracked.Source + ":" + row + ":" + column : rules.First(p => ReferenceEquals(p.Value, rule)).Key + ":" + column;
                    Propose(key, value, () => actions.Add(() => { if (rule == null) current.SetCell(r, c, value); else rule["changes"]![c]!["value"] = value; }));
                }
                if (external.Records.Count > baseline.Records.Count)
                {
                    Require(current.Records.Count == baseline.Records.Count, "Rows were appended on both sides. Reconcile them before syncing.");
                    var appendKey = tracked.Source + ":append";
                    var appended = external.Records.Skip(baseline.Records.Count).Select(r => r!["fields"]!.DeepClone()).ToArray();
                    var signature = string.Join("\n", appended.Select(Json));
                    bool first = !proposals.ContainsKey(appendKey);
                    Propose(appendKey, signature, () => { if (first) actions.Add(() => { foreach (var fields in appended) { var record = current.NewRecord((JsonObject)fields); record["order"] = current.Records.Count; current.Records.Add(record); } }); });
                }
            }
        }
        if (conflicts.Count > 0 && choice == ExternalConflictChoice.Review) throw new ExternalSyncConflict(string.Join("\n", conflicts.Take(20)), stamp);
        foreach (var action in actions) action();
        foreach (var rule in rules) after[rule.Key] = JsonNode.DeepEquals(JsonNode.Parse(before[rule.Key]), rule.Value) ? before[rule.Key] : Utf8.GetBytes(Json(rule.Value));
        var nextTables = new List<ExternalTable>();
        foreach (var tracked in session.Tables)
        {
            if (!currentTables.TryGetValue(tracked.Source, out var table)) { nextTables.Add(tracked); continue; }
            var path = Inside(project.Root, tracked.Source);
            var issues = table.Validate(path); Require(issues.Count == 0, string.Join("\n", issues));
            var original = SourceNode(before[path]);
            original["records"] = table.Records.DeepClone();
            after[path] = JsonNode.DeepEquals(original, SourceNode(before[path])) ? before[path] : Utf8.GetBytes((Utf8.GetString(before[path]).StartsWith('\uFEFF') ? "\uFEFF" : "") + Json(original));
            var outputs = new Dictionary<string, byte[]>();
            foreach (var output in tracked.Outputs)
            {
                var resolved = Effective(table, output.Key); var bytes = resolved.EncodeTsv();
                // Formatting-only editor saves are accepted without rewriting the user's TXT.
                var disk = TableData.FromTsv(before[Inside(session.Target, output.Key)], table.Name, output.Key);
                if (TableDiff.Compare(resolved, disk).Count == 0) bytes = before[Inside(session.Target, output.Key)];
                outputs.Add(output.Key, bytes); after[Inside(session.Target, output.Key)] = bytes;
            }
            nextTables.Add(new(tracked.Source, Utf8.GetString(after[path]), outputs));
        }
        var inputs = nextTables.ToDictionary(t => t.Source, t => Hash(after.GetValueOrDefault(Inside(project.Root, t.Source)) ?? before[Inside(project.Root, t.Source)]));
        inputs[Relative(project.Root, profilePath)] = before.TryGetValue(profilePath, out var profileBytes) ? Hash(profileBytes) : "missing";
        foreach (var rule in rules) inputs[Relative(project.Root, rule.Key)] = Hash(after[rule.Key]);
        after[statePath] = JsonSerializer.SerializeToUtf8Bytes(session with { Tables = nextTables, Inputs = inputs }, Pretty);
        var ownerPath = Inside(session.Target, ".studio-owner.json");
        var owner = JsonSerializer.Deserialize<DeploymentManifest>(Capture(ownerPath), Pretty)!;
        var files = owner.Files.Select(f => after.TryGetValue(Inside(session.Target, f.Path), out var bytes) ? f with { Sha256 = Hash(bytes), Size = bytes.Length } : f).ToList();
        if (!files.SequenceEqual(owner.Files)) after[ownerPath] = JsonSerializer.SerializeToUtf8Bytes(owner with { Files = files }, Pretty);
        var writes = after.Where(p => !p.Value.SequenceEqual(before[p.Key])).OrderBy(p => p.Key == statePath ? 1 : 0).Select(p => new ExternalWrite(p.Key, before[p.Key], p.Value)).ToList();
        token.ThrowIfCancellationRequested();
        foreach (var input in before) Require(Hash(File.ReadAllBytes(input.Key)) == Hash(input.Value), "Files changed during synchronization. Retry after the editor finishes saving.");
        if (writes.Count == 0) return [];
        AtomicWrite(JournalFile(project), JsonSerializer.SerializeToUtf8Bytes(new ExternalSyncJournal(project.Id, session.Target, writes), Pretty));
        // Once journaled, complete or leave recovery data. Cancellation never interrupts the commit.
        Commit(project, new(project.Id, session.Target, writes));
        return writes.Where(w => w.Path != statePath).Select(w => w.Path).ToArray();
    }
    private static void Recover(ModProject project)
    {
        if (!File.Exists(JournalFile(project))) return;
        var journal = JsonSerializer.Deserialize<ExternalSyncJournal>(File.ReadAllBytes(JournalFile(project)), Pretty)!;
        var session = Load(project); Require(session != null && SamePath(journal.Target, session.Target), "Invalid external recovery target.");
        using var deploymentLock = new FileStream(Inside(session!.Target, ".studio-deploy.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Commit(project, journal);
    }
    private static void Commit(ModProject project, ExternalSyncJournal journal)
    {
        var session = Load(project); Require(session != null && journal.ProjectId == project.Id && SamePath(journal.Target, session.Target), "Invalid external synchronization recovery ownership.");
        CheckOwner(project, session!);
        foreach (var write in journal.Writes)
        {
            var allowedSource = session!.Tables.Any(t => SamePath(Inside(project.Root, t.Source), write.Path));
            var allowedOutput = session.Tables.Any(t => t.Outputs.Keys.Any(p => SamePath(Inside(session.Target, p), write.Path)));
            var profileRoot = Inside(project.Root, "compatibility/" + session.Profile);
            Require(allowedSource || allowedOutput || SamePath(StateFile(project), write.Path) || SamePath(Inside(session.Target, ".studio-owner.json"), write.Path) ||
                Contains(profileRoot, write.Path) && write.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase), "Recovery path is outside the synchronization scope."); NoLinks(write.Path);
            var hash = Hash(File.ReadAllBytes(write.Path));
            Require(hash == Hash(write.Before) || hash == Hash(write.After), "File changed during external sync recovery. Recovery data retained: " + write.Path);
        }
        foreach (var write in journal.Writes)
            if (Hash(File.ReadAllBytes(write.Path)) != Hash(write.After)) AtomicWrite(write.Path, write.After, Hash(write.Before));
        File.Delete(JournalFile(project));
    }
}

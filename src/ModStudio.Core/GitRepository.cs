using System.Diagnostics;
using System.Text;

namespace ModStudio.Core;

public enum GitChangeKind { Modified, Added, Deleted, Renamed, TypeChanged, Untracked, Conflicted }

/// <summary>One path from <c>git status</c>. Staged/Unstaged mirror the index and work-tree columns; both may be set.</summary>
public sealed record GitChange(string Path, GitChangeKind Kind, bool Staged, bool Unstaged, string? OriginalPath = null)
{
    public string Name => Path.Contains('/') ? Path[(Path.LastIndexOf('/') + 1)..] : Path;
    public string Folder => Path.Contains('/') ? Path[..Path.LastIndexOf('/')] : "";
    public char Badge => Kind switch { GitChangeKind.Modified => 'M', GitChangeKind.Added => 'A', GitChangeKind.Deleted => 'D', GitChangeKind.Renamed => 'R', GitChangeKind.TypeChanged => 'T', GitChangeKind.Untracked => '?', _ => '!' };
    /// <summary>Every pathspec that must accompany this change on the command line (renames carry their old name).</summary>
    public IEnumerable<string> Pathspecs => OriginalPath == null ? [Path] : [Path, OriginalPath];
}

public sealed record GitStatus(string Branch, bool Detached, string? Upstream, int Ahead, int Behind, IReadOnlyList<GitChange> Changes, bool HasCommits)
{
    public static readonly GitStatus Empty = new("", false, null, 0, 0, [], false);
}

public sealed record GitCommit(string Hash, string ShortHash, string Author, string Date, string Subject);

public sealed record GitResult(string Command, int ExitCode, string Output, string Error)
{
    public bool Ok => ExitCode == 0;
    public string Text => (Output + (Output.Length > 0 && Error.Length > 0 ? Environment.NewLine : "") + Error).Trim();
    public GitResult Throw() => Ok ? this : throw new InvalidOperationException(Text.Length > 0 ? Text : $"git {Command} failed ({ExitCode}).");
}

/// <summary>
/// The Studio's git integration drives the user's own <c>git</c> executable rather than an embedded library, so SSH agents,
/// credential managers, hooks and global config behave exactly as they do from a terminal.
/// </summary>
public sealed class GitRepository
{
    private static readonly Lazy<string?> executable = new(Locate);
    public static string? Executable => executable.Value;
    public static bool Available => Executable != null;
    /// <summary>Raised after every command so the UI can echo a console; the callback runs on the calling thread.</summary>
    public event Action<GitResult>? Ran;
    public string WorkTree { get; }
    private GitRepository(string workTree) => WorkTree = workTree;

    private static string? Locate()
    {
        var name = OperatingSystem.IsWindows() ? "git.exe" : "git";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            try { var candidate = System.IO.Path.Combine(dir.Trim('"'), name); if (File.Exists(candidate)) return candidate; } catch (ArgumentException) { }
        if (OperatingSystem.IsWindows())
            foreach (var folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "/Programs" })
            {
                var candidate = System.IO.Path.Combine(folder, "Git", "cmd", "git.exe"); if (File.Exists(candidate)) return candidate;
            }
        else foreach (var candidate in new[] { "/usr/bin/git", "/usr/local/bin/git", "/opt/homebrew/bin/git" }) if (File.Exists(candidate)) return candidate;
        return null;
    }

    /// <summary>The repository containing <paramref name="folder"/>, or null when it is not under version control (or git is missing).</summary>
    public static async Task<GitRepository?> OpenAsync(string folder, CancellationToken token = default)
    {
        if (!Available || !Directory.Exists(folder)) return null;
        var result = await ExecuteAsync(folder, ["rev-parse", "--show-toplevel"], token);
        if (!result.Ok) return null;
        var top = result.Output.Trim(); if (top.Length == 0) return null;
        return new GitRepository(System.IO.Path.GetFullPath(top));
    }

    public static async Task<GitRepository> InitAsync(string folder, CancellationToken token = default)
    {
        Storage.Require(Available, "git was not found. Install Git (https://git-scm.com) and restart Studio.");
        (await ExecuteAsync(folder, ["init", "-q"], token)).Throw();
        return await OpenAsync(folder, token) ?? throw new InvalidOperationException("git init succeeded but the repository could not be opened.");
    }

    public string Relative(string file) => Storage.Relative(WorkTree, System.IO.Path.GetFullPath(file));
    public string Absolute(string relative) => System.IO.Path.GetFullPath(System.IO.Path.Combine(WorkTree, relative));

    public async Task<GitResult> RunAsync(IReadOnlyList<string> args, CancellationToken token = default)
    {
        var result = await ExecuteAsync(WorkTree, args, token);
        Ran?.Invoke(result);
        return result;
    }

    private static async Task<GitResult> ExecuteAsync(string workingDirectory, IReadOnlyList<string> args, CancellationToken token)
    {
        var start = new ProcessStartInfo { FileName = Executable ?? "git", WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, StandardOutputEncoding = Storage.Utf8, StandardErrorEncoding = Storage.Utf8 };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        // Never block on a hidden console prompt; credential helpers and SSH agents still work because they do not use the terminal.
        start.Environment["GIT_TERMINAL_PROMPT"] = "0"; start.Environment["LC_ALL"] = "C.UTF-8"; start.Environment["GIT_PAGER"] = "cat"; start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        using var process = new Process { StartInfo = start };
        process.Start(); process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(token); var error = process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException) { try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } throw; }
        return new GitResult(string.Join(' ', args), process.ExitCode, await output, await error);
    }

    // Status ------------------------------------------------------------------------------------------------------------

    public async Task<GitStatus> StatusAsync(CancellationToken token = default)
    {
        var result = (await RunAsync(["status", "--porcelain=v2", "--branch", "--untracked-files=all", "-z"], token)).Throw();
        var status = ParseStatus(result.Output);
        if (status.Branch.Length == 0 || !status.HasCommits)
        {
            // porcelain v2 reports no oid on an unborn branch; fall back to the symbolic ref for the name.
            var head = await RunAsync(["symbolic-ref", "--short", "-q", "HEAD"], token);
            if (head.Ok && head.Output.Trim().Length > 0) status = status with { Branch = head.Output.Trim() };
        }
        return status;
    }

    /// <summary>Parses <c>git status --porcelain=v2 --branch -z</c>.</summary>
    public static GitStatus ParseStatus(string text)
    {
        string branch = "", upstream = ""; bool detached = false, hasCommits = false; int ahead = 0, behind = 0;
        var changes = new List<GitChange>();
        var fields = text.Split('\0');
        for (int i = 0; i < fields.Length; i++)
        {
            var line = fields[i]; if (line.Length == 0) continue;
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                var parts = line.Split(' ', 3);
                if (parts.Length < 3) continue;
                switch (parts[1])
                {
                    case "branch.oid": hasCommits = parts[2] != "(initial)"; break;
                    case "branch.head": detached = parts[2] == "(detached)"; branch = detached ? "" : parts[2]; break;
                    case "branch.upstream": upstream = parts[2]; break;
                    case "branch.ab":
                        foreach (var token in parts[2].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                            if (token.Length > 1 && int.TryParse(token.AsSpan(1), out var n)) { if (token[0] == '+') ahead = n; else if (token[0] == '-') behind = n; }
                        break;
                }
                continue;
            }
            switch (line[0])
            {
                case '1':
                {
                    var parts = line.Split(' ', 9); if (parts.Length < 9) break;
                    changes.Add(Change(parts[1], parts[8], null)); break;
                }
                case '2':
                {
                    var parts = line.Split(' ', 10); if (parts.Length < 10) break;
                    var original = i + 1 < fields.Length ? fields[++i] : null;
                    changes.Add(Change(parts[1], parts[9], original)); break;
                }
                case 'u':
                {
                    var parts = line.Split(' ', 11); if (parts.Length < 11) break;
                    changes.Add(new GitChange(parts[10], GitChangeKind.Conflicted, true, true)); break;
                }
                case '?': changes.Add(new GitChange(line[2..], GitChangeKind.Untracked, false, true)); break;
            }
        }
        if (detached) branch = "HEAD (detached)";
        return new GitStatus(branch, detached, upstream.Length == 0 ? null : upstream, ahead, behind, changes.OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToList(), hasCommits);
    }

    private static GitChange Change(string xy, string path, string? original)
    {
        char index = xy[0], work = xy.Length > 1 ? xy[1] : '.';
        var kind = work == 'D' || index == 'D' ? GitChangeKind.Deleted
            : index is 'A' or 'C' || work == 'A' ? GitChangeKind.Added
            : index == 'R' || work == 'R' ? GitChangeKind.Renamed
            : index == 'T' || work == 'T' ? GitChangeKind.TypeChanged
            : index == 'U' || work == 'U' ? GitChangeKind.Conflicted
            : GitChangeKind.Modified;
        return new GitChange(path, kind, index != '.', work != '.', original);
    }

    // Working tree ------------------------------------------------------------------------------------------------------

    /// <summary>Runs a command with the paths supplied through a NUL-separated file, so thousands of files never exceed the command-line limit.</summary>
    private async Task<GitResult> WithPathsAsync(IEnumerable<string> before, IEnumerable<string> paths, CancellationToken token)
    {
        var list = paths.Distinct(StringComparer.Ordinal).ToList();
        if (list.Count == 0) return new GitResult(string.Join(' ', before), 0, "", "");
        var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "modstudio-pathspec-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllBytesAsync(file, Storage.Utf8.GetBytes(string.Join('\0', list)), token);
        var args = before.ToList(); bool separator = args.Count > 0 && args[^1] == "--"; if (separator) args.RemoveAt(args.Count - 1);
        args.Add("--pathspec-from-file=" + file); args.Add("--pathspec-file-nul"); if (separator) args.Add("--");
        try { return await RunAsync(args, token); }
        finally { try { File.Delete(file); } catch (IOException) { } }
    }

    public async Task StageAsync(IEnumerable<GitChange> changes, CancellationToken token = default) =>
        (await WithPathsAsync(["add", "-A", "--"], changes.SelectMany(c => c.Pathspecs), token)).Throw();

    public async Task UnstageAsync(IEnumerable<GitChange> changes, CancellationToken token = default) =>
        (await WithPathsAsync(["restore", "--staged", "--"], changes.SelectMany(c => c.Pathspecs), token)).Throw();

    /// <summary>Rollback: tracked paths return to HEAD; files git has never seen are deleted from disk.</summary>
    public async Task DiscardAsync(IEnumerable<GitChange> changes, CancellationToken token = default)
    {
        var list = changes.ToList();
        var tracked = list.Where(c => c.Kind is not (GitChangeKind.Untracked or GitChangeKind.Added)).ToList();
        if (tracked.Count > 0) (await WithPathsAsync(["restore", "--staged", "--worktree", "--"], tracked.SelectMany(c => c.Pathspecs), token)).Throw();
        var added = list.Where(c => c.Kind == GitChangeKind.Added).ToList();
        if (added.Count > 0) (await WithPathsAsync(["restore", "--staged", "--"], added.Select(c => c.Path), token)).Throw();
        foreach (var change in list.Where(c => c.Kind is GitChangeKind.Untracked or GitChangeKind.Added))
        {
            var file = Absolute(change.Path);
            Storage.Require(Storage.Contains(WorkTree, file), "Refusing to delete a path outside the repository: " + change.Path);
            if (File.Exists(file)) File.Delete(file);
        }
    }

    /// <summary>Commits exactly the given changes, leaving anything else staged or modified untouched.</summary>
    public async Task<GitResult> CommitAsync(string message, IReadOnlyList<GitChange> changes, bool amend = false, CancellationToken token = default)
    {
        Storage.Require(!string.IsNullOrWhiteSpace(message), "Enter a commit message.");
        Storage.Require(changes.Count > 0 || amend, "Select at least one change to commit.");
        Storage.Require(!changes.Any(c => c.Kind == GitChangeKind.Conflicted), "Resolve merge conflicts before committing.");
        await StageAsync(changes, token);
        var paths = changes.SelectMany(c => c.Pathspecs).ToList();
        var args = new List<string> { "commit", "-q", "-m", message.Trim() };
        if (amend) args.Add("--amend");
        if (paths.Count > 0) { args.Add("--only"); args.Add("--"); return (await WithPathsAsync(args, paths, token)).Throw(); }
        return (await RunAsync(args, token)).Throw();
    }

    // Remotes -------------------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<string>> RemotesAsync(CancellationToken token = default) =>
        (await RunAsync(["remote"], token)).Throw().Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public Task<GitResult> FetchAsync(CancellationToken token = default) => RunAsync(["fetch", "--prune"], token);

    public Task<GitResult> PullAsync(CancellationToken token = default) => RunAsync(["pull", "--no-rebase"], token);

    /// <summary>Pushes the current branch; a branch without an upstream is published to <paramref name="remote"/> (default origin) and tracked.</summary>
    public async Task<GitResult> PushAsync(GitStatus status, string? remote = null, CancellationToken token = default)
    {
        Storage.Require(!status.Detached && status.Branch.Length > 0, "Check out a branch before pushing.");
        if (status.Upstream != null) return await RunAsync(["push"], token);
        var remotes = await RemotesAsync(token);
        Storage.Require(remotes.Count > 0, "This repository has no remote. Add one first (git remote add origin <url>).");
        remote ??= remotes.Contains("origin") ? "origin" : remotes[0];
        return await RunAsync(["push", "--set-upstream", remote, status.Branch], token);
    }

    // Branches and history ------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<string>> BranchesAsync(CancellationToken token = default) =>
        (await RunAsync(["for-each-ref", "--format=%(refname:short)", "refs/heads/"], token)).Throw().Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public async Task CheckoutAsync(string branch, bool create, CancellationToken token = default)
    {
        Storage.Require(branch.Trim().Length > 0 && !branch.Any(char.IsWhiteSpace), "Branch names cannot contain spaces.");
        (await RunAsync(["check-ref-format", "--branch", branch])).Throw();
        (await RunAsync(create ? ["switch", "-c", branch] : ["switch", branch], token)).Throw();
    }

    public async Task<IReadOnlyList<GitCommit>> LogAsync(int max = 200, CancellationToken token = default)
    {
        var result = await RunAsync(["log", "-n", max.ToString(), "--date=short", "--format=%H%x1f%h%x1f%an%x1f%ad%x1f%s%x1e"], token);
        if (!result.Ok) return []; // unborn branch
        var commits = new List<GitCommit>();
        foreach (var record in result.Output.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = record.Trim('\n', '\r').Split('\x1f'); if (f.Length < 5) continue;
            commits.Add(new GitCommit(f[0].Trim(), f[1], f[2], f[3], f[4]));
        }
        return commits;
    }

    /// <summary>The commit's message, file summary and a bounded patch.</summary>
    public async Task<string> ShowAsync(string hash, CancellationToken token = default)
    {
        var result = (await RunAsync(["show", "--stat", "--patch", "--format=commit %H%nAuthor: %an <%ae>%nDate:   %ad%n%n    %s%n%n%b", "--date=iso", hash, "--"], token)).Throw();
        return Truncate(result.Output);
    }

    /// <summary>
    /// A unified diff of the change against HEAD (or, for files git does not know yet, against nothing).
    /// <paramref name="fullContext"/> includes every unchanged line too, which is what a side-by-side view needs.
    /// </summary>
    public async Task<string> DiffAsync(GitChange change, bool fullContext = false, CancellationToken token = default)
    {
        string[] options = fullContext ? ["--no-color", "--no-ext-diff", "--diff-algorithm=histogram", "-U999999"] : ["--no-color", "--no-ext-diff"];
        if (change.Kind == GitChangeKind.Untracked || change.Kind == GitChangeKind.Added && !change.Unstaged)
        {
            var file = Absolute(change.Path);
            if (change.Kind == GitChangeKind.Added) return Truncate((await RunAsync(["diff", .. options, "--cached", "--", change.Path], token)).Output);
            if (!File.Exists(file)) return "";
            var bytes = await File.ReadAllBytesAsync(file, token);
            if (!TextFileEncoding.LooksLikeText(bytes)) return $"Binary file {change.Path} ({bytes.Length:N0} bytes)";
            var text = Storage.Utf8.GetString(bytes); var lines = text.Split('\n');
            var builder = new StringBuilder().Append("--- /dev/null\n+++ b/").Append(change.Path).Append("\n@@ -0,0 +1,").Append(lines.Length).Append(" @@\n");
            foreach (var line in lines) builder.Append('+').Append(line.TrimEnd('\r')).Append('\n');
            return Truncate(builder.ToString());
        }
        var args = new List<string> { "diff" }; args.AddRange(options); args.Add("HEAD"); args.Add("--"); args.AddRange(change.Pathspecs);
        var result = await RunAsync(args, token);
        if (!result.Ok) result = await RunAsync(["diff", .. options, "--cached", "--", change.Path], token); // no HEAD yet
        return Truncate(result.Output);
    }

    private static string Truncate(string text) => text.Length > 400_000 ? text[..400_000] + "\n… diff truncated …\n" : text;
}

using ModStudio.Core;

internal static class GitTests
{
    public static async Task RunAsync(string root, Action<bool, string> check, Action<Action, string> throws, Action<string, string> write)
    {
        // Git marks its object files read-only, which would defeat the harness's recursive cleanup on Windows.
        try { await RunCoreAsync(root, check, throws, write); }
        finally { foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal); }
    }

    private static async Task RunCoreAsync(string root, Action<bool, string> check, Action<Action, string> throws, Action<string, string> write)
    {
        // Parsing needs no git at all.
        var parsed = GitRepository.ParseStatus("# branch.oid abc\0# branch.head main\0# branch.upstream origin/main\0# branch.ab +2 -1\0" +
            "1 .M N... 100644 100644 100644 h h source/tables/misc.json\0" + "1 A. N... 000000 100644 100644 0 h new.txt\0" + "1 MM N... 100644 100644 100644 h h both.txt\0" +
            "2 R. N... 100644 100644 100644 h h R100 renamed.txt\0old.txt\0" + "1 .D N... 100644 100644 000000 h h gone.txt\0" + "u UU N... 100644 100644 100644 100644 h h h conflict.txt\0" + "? untracked.bin\0");
        check(parsed.Branch == "main" && parsed.Upstream == "origin/main" && parsed.Ahead == 2 && parsed.Behind == 1 && parsed.HasCommits, "Porcelain v2 branch header is parsed");
        var byPath = parsed.Changes.ToDictionary(c => c.Path);
        check(byPath["source/tables/misc.json"] is { Kind: GitChangeKind.Modified, Staged: false, Unstaged: true } && byPath["new.txt"] is { Kind: GitChangeKind.Added, Staged: true, Unstaged: false }
            && byPath["both.txt"] is { Kind: GitChangeKind.Modified, Staged: true, Unstaged: true } && byPath["renamed.txt"] is { Kind: GitChangeKind.Renamed, OriginalPath: "old.txt" }
            && byPath["gone.txt"].Kind == GitChangeKind.Deleted && byPath["conflict.txt"].Kind == GitChangeKind.Conflicted && byPath["untracked.bin"] is { Kind: GitChangeKind.Untracked, Badge: '?' }, "Porcelain v2 entries map to change kinds");
        check(parsed.Changes.Select(c => c.Path).SequenceEqual(parsed.Changes.Select(c => c.Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) && byPath["renamed.txt"].Pathspecs.SequenceEqual(["renamed.txt", "old.txt"]), "Changes are sorted and renames carry both pathspecs");
        check(GitRepository.ParseStatus("# branch.oid (initial)\0# branch.head main\0? a.txt\0") is { HasCommits: false, Branch: "main" }, "An unborn branch reports no commits");

        if (!GitRepository.Available) { Console.WriteLine("SKIP git integration: git executable not found"); return; }
        var folder = Path.Combine(root, "git-project"); Directory.CreateDirectory(folder);
        check((await GitRepository.OpenAsync(folder))?.WorkTree != Path.GetFullPath(folder), "A plain folder is not reported as its own repository");
        var repo = await GitRepository.InitAsync(folder);
        check(repo.WorkTree == Path.GetFullPath(folder), "Initializing makes the folder a repository root");
        var console = new List<GitResult>(); repo.Ran += console.Add;
        (await repo.RunAsync(["config", "user.email", "studio@example.com"])).Throw(); (await repo.RunAsync(["config", "user.name", "Studio Tests"])).Throw();
        (await repo.RunAsync(["config", "commit.gpgsign", "false"])).Throw(); (await repo.RunAsync(["config", "core.autocrlf", "false"])).Throw();
        check(await GitRepository.OpenAsync(Path.Combine(folder, "source")) == null, "Opening a missing subfolder fails cleanly");
        write(Path.Combine(folder, "source/tables/a.json"), "{\"a\":1}\n"); write(Path.Combine(folder, "source/tables/b.json"), "{\"b\":1}\n"); write(Path.Combine(folder, "notes.md"), "# notes\n");
        var inside = await GitRepository.OpenAsync(Path.Combine(folder, "source"));
        check(inside != null && inside.WorkTree == repo.WorkTree && inside.Relative(Path.Combine(folder, "source", "tables", "a.json")) == "source/tables/a.json", "Subfolders resolve to the repository root");
        var status = await repo.StatusAsync();
        check(!status.HasCommits && status.Branch.Length > 0 && status.Changes.Count == 3 && status.Changes.All(c => c.Kind == GitChangeKind.Untracked), "A fresh repository lists new files as untracked with a branch name");
        check((await repo.LogAsync()).Count == 0, "History is empty before the first commit");

        // Commit only the selected files; the third stays untracked.
        var selected = status.Changes.Where(c => c.Path.StartsWith("source/")).ToList();
        (await repo.CommitAsync("Add tables", selected)).Throw();
        status = await repo.StatusAsync();
        check(status.HasCommits && status.Changes.Select(c => c.Path).SequenceEqual(["notes.md"]) && status.Upstream == null && status.Ahead == 0, "Committing a selection leaves unselected files untracked");
        var log = await repo.LogAsync();
        check(log.Count == 1 && log[0].Subject == "Add tables" && log[0].Author == "Studio Tests" && log[0].ShortHash.Length >= 7 && log[0].Hash.StartsWith(log[0].ShortHash), "Log reports the commit");
        check((await repo.ShowAsync(log[0].Hash)).Contains("source/tables/a.json"), "Show lists the files in a commit");

        // Modify, delete, rename and add; stage one file by hand to prove the selection commit ignores unrelated staged work.
        write(Path.Combine(folder, "source/tables/a.json"), "{\"a\":2}\n"); File.Delete(Path.Combine(folder, "source/tables/b.json"));
        write(Path.Combine(folder, "source/tables/c.json"), "{\"c\":1}\n");
        await repo.StageAsync([new GitChange("notes.md", GitChangeKind.Untracked, false, true)]);
        status = await repo.StatusAsync(); byPath = status.Changes.ToDictionary(c => c.Path);
        check(byPath["source/tables/a.json"].Kind == GitChangeKind.Modified && byPath["source/tables/b.json"].Kind == GitChangeKind.Deleted && byPath["source/tables/c.json"].Kind == GitChangeKind.Untracked && byPath["notes.md"] is { Kind: GitChangeKind.Added, Staged: true }, "Status reports modified, deleted, untracked and staged files");
        var diff = await repo.DiffAsync(byPath["source/tables/a.json"]);
        check(diff.Contains("-{\"a\":1}") && diff.Contains("+{\"a\":2}"), "Diff shows the working tree change against HEAD");
        check((await repo.DiffAsync(byPath["source/tables/c.json"])).Contains("+{\"c\":1}") && (await repo.DiffAsync(byPath["notes.md"])).Contains("+# notes"), "Untracked and newly added files diff against nothing");
        (await repo.CommitAsync("Edit tables", [byPath["source/tables/a.json"], byPath["source/tables/b.json"], byPath["source/tables/c.json"]])).Throw();
        status = await repo.StatusAsync();
        check(status.Changes.Select(c => c.Path).SequenceEqual(["notes.md"]) && status.Changes[0].Staged, "Committing a selection keeps other staged files staged");
        check((await repo.LogAsync())[0].Subject == "Edit tables" && !File.Exists(Path.Combine(folder, "source/tables/b.json")), "Deletions and additions are committed together");
        await repo.UnstageAsync(status.Changes); status = await repo.StatusAsync();
        check(status.Changes.Single().Kind == GitChangeKind.Untracked, "Unstaging a new file returns it to untracked");

        // Rollback: tracked files return to HEAD, untracked files are removed.
        write(Path.Combine(folder, "source/tables/a.json"), "{\"a\":3}\n"); status = await repo.StatusAsync();
        await repo.DiscardAsync(status.Changes); status = await repo.StatusAsync();
        check(status.Changes.Count == 0 && File.ReadAllText(Path.Combine(folder, "source/tables/a.json")) == "{\"a\":2}\n" && !File.Exists(Path.Combine(folder, "notes.md")), "Rollback restores tracked files and deletes untracked ones");
        throws(() => repo.DiscardAsync([new GitChange("../outside.txt", GitChangeKind.Untracked, false, true)]).GetAwaiter().GetResult(), "Rollback refuses paths outside the repository");
        throws(() => repo.CommitAsync(" ", status.Changes).GetAwaiter().GetResult(), "Committing without a message is rejected");
        throws(() => repo.CommitAsync("x", []).GetAwaiter().GetResult(), "Committing nothing is rejected");

        // Branches and pushing without a remote.
        var initialBranch = status.Branch;
        await repo.CheckoutAsync("feature/test", create: true); status = await repo.StatusAsync();
        check(status.Branch == "feature/test" && (await repo.BranchesAsync()).Contains("feature/test"), "Creating and switching branches works");
        await repo.CheckoutAsync(initialBranch, create: false);
        check((await repo.StatusAsync()).Branch == initialBranch, "Switching back to the original branch works");
        throws(() => repo.CheckoutAsync("bad name", create: true).GetAwaiter().GetResult(), "Branch names with spaces are rejected");
        throws(() => repo.PushAsync(status).GetAwaiter().GetResult(), "Pushing without a remote explains the problem");

        // A bare remote: push publishes the branch and sets the upstream; a second push is a plain push.
        var bare = Path.Combine(root, "git-remote.git"); (await repo.RunAsync(["init", "--bare", "-q", bare])).Throw();
        (await repo.RunAsync(["remote", "add", "origin", bare])).Throw();
        status = await repo.StatusAsync(); check(status.Upstream == null, "A branch without upstream reports none");
        (await repo.PushAsync(status)).Throw(); status = await repo.StatusAsync();
        check(status.Upstream == "origin/" + status.Branch && status.Ahead == 0 && status.Behind == 0, "The first push sets the upstream");
        write(Path.Combine(folder, "source/tables/a.json"), "{\"a\":4}\n"); status = await repo.StatusAsync();
        (await repo.CommitAsync("Bump", status.Changes)).Throw(); status = await repo.StatusAsync();
        check(status.Ahead == 1, "Local commits count as ahead of the upstream");
        (await repo.PushAsync(status)).Throw(); (await repo.FetchAsync()).Throw(); (await repo.PullAsync()).Throw();
        check((await repo.StatusAsync()).Ahead == 0 && console.Any(r => r.Command == "push") && console.Any(r => r.Command == "fetch --prune"), "Push, fetch and pull run and are echoed to the console");
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using AvaloniaEdit;
using ModStudio.Core;
using static ModStudio.Core.Storage;

namespace ModStudio.App;

public partial class MainWindow
{
    /// <summary>Turns the fixture into a repository, then commits, diffs, rolls back and browses history through the panel.</summary>
    private async Task SmokeGitAsync(string root, string output)
    {
        if (!GitRepository.Available) { Console.WriteLine("SKIP git smoke: git executable not found"); return; }
        var fullRoot = System.IO.Path.GetFullPath(root);
        async Task Settle(Func<bool>? until = null)
        {
            bool Ready() => !git.Busy && git.Repository?.WorkTree == fullRoot && (until?.Invoke() ?? true);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            do
            {
                UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                if (Ready()) return;
                await Task.Delay(50);
            } while (DateTime.UtcNow < deadline);
            Require(Ready(), $"Git panel did not settle: branch '{git.Status.Branch}', busy {git.Busy}, repository '{git.Repository?.WorkTree}', changes {git.Items.Count}, output '{Output.Text}'.");
        }
        Require(git.Repository?.WorkTree != fullRoot, "Fixture must not already be its own repository.");
        LeftTabs.SelectedItem = GitTab; await Task.Delay(100);
        var init = git.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Create Git repository");
        Require(init.IsVisible, "A folder without a repository did not offer to create one.");
        init.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle(() => git.Items.Count > 0);
        var repo = git.Repository!;
        Require(repo.WorkTree == fullRoot && File.ReadAllText(System.IO.Path.Combine(root, ".gitignore")).Contains(".studio/"), "Creating a repository did not initialize the fixture or ignore the Studio cache.");
        (await repo.RunAsync(["config", "user.email", "smoke@example.com"])).Throw(); (await repo.RunAsync(["config", "user.name", "Smoke"])).Throw(); (await repo.RunAsync(["config", "commit.gpgsign", "false"])).Throw();
        Require(git.Items.Count > 0 && git.Items.All(i => i.Change.Kind == GitChangeKind.Untracked && i.IsChecked), "A fresh repository did not list every file as an untracked, checked change.");
        var all = git.Items.Count;
        // Commit everything except one file; it stays untracked.
        var keep = git.Items.First(i => i.Change.Path == "modinfo.json"); keep.IsChecked = false;
        git.CommitMessage = "Initial import"; await git.CommitAsync(push: false); await Settle(() => git.Items.Count == 1);
        Require(git.Items.Select(i => i.Change.Path).SequenceEqual(["modinfo.json"]) && git.CommitMessage.Length == 0 && git.Status.HasCommits, $"Committing the checked files left: {string.Join(", ", git.Items.Select(i => i.Change.Path))}");
        Require((await repo.LogAsync()).Single().Subject == "Initial import", "The panel commit did not reach the log.");
        // History and console in the bottom Git tab.
        git.GetVisualDescendants().OfType<Button>().Single(b => ToolTip.GetTip(b) as string == "Show commit history").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (int i = 0; i < 300 && (gitLog.ItemsSource == null || !gitDetails.Document.Text.Contains("Initial import")); i++) await Task.Delay(50);
        await Task.Delay(200); UpdateLayout();
        Require(BottomPanel.IsVisible && BottomTabs.SelectedItem == gitBottomTab && gitLog.ItemsSource is IReadOnlyList<GitCommit> { Count: 1 } && gitDetails.Document.Text.Contains("Initial import"), $"History did not show the commit and its details: panel {BottomPanel.IsVisible}, tab {BottomTabs.SelectedItem == gitBottomTab}, commits {(gitLog.ItemsSource as IReadOnlyList<GitCommit>)?.Count}, details {gitDetails.Document.Text.Length} chars, selected {gitLog.SelectedItem}, shown {gitLogShown}/{gitLogRevision}, busy {git.Busy}, output '{Output.Text}'.");
        Require((gitConsole.Text ?? "").Contains("> git commit") && !(gitConsole.Text ?? "").Contains("> git status"), "The console did not echo the commit, or echoed status polling.");
        // Edit a tracked file, delete another, and diff the edit.
        var modified = System.IO.Path.Combine(root, "mod-project.json"); var before = File.ReadAllText(modified); File.WriteAllText(modified, before.Replace("\"name\"", "\"name\" "));
        File.Delete(System.IO.Path.Combine(root, "import-report.json"));
        await git.RefreshAsync(); await Settle();
        var items = git.Items.ToDictionary(i => i.Change.Path);
        Require(items["mod-project.json"].Change.Kind == GitChangeKind.Modified && items["import-report.json"].Change.Kind == GitChangeKind.Deleted && items["modinfo.json"] is { Change.Kind: GitChangeKind.Untracked, IsChecked: false }, "Status did not track the edit, deletion and the unchecked file across refreshes.");
        // Single click previews the change side by side; another click reuses the preview tab; double-click keeps it.
        async Task<DiffPane> OpenDiffAsync(string path, bool preview)
        {
            await ShowGitDiffAsync(items[path].Change, preview);
            for (int i = 0; i < 100 && ((Documents.SelectedItem as TabItem)?.Content as DiffPane)?.Change.Path != path; i++) await Task.Delay(50);
            await Task.Delay(150); UpdateLayout();
            return (DiffPane)((TabItem)Documents.SelectedItem!).Content!;
        }
        // A real click on the row (not the checkbox) must open the preview without any extra step.
        var row = git.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "mod-project.json" && t.GetVisualAncestors().OfType<ListBoxItem>().Any());
        var point = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), this)!.Value;
        this.MouseDown(point, MouseButton.Left, RawInputModifiers.None); this.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        for (int i = 0; i < 100 && tabs.All(t => t.Tag is not GitDiffTag); i++) await Task.Delay(50);
        Require(tabs.Any(t => t.Tag is GitDiffTag && t == previewTab) && items["mod-project.json"].IsChecked, "Clicking a change row did not open a preview diff (or it toggled the checkbox).");
        GitChangeItem Item(string path) => git.Items.First(i => i.Change.Path == path);
        var box = git.GetVisualDescendants().OfType<ListBoxItem>().First(l => (l.DataContext as GitChangeItem)?.Change.Path == "modinfo.json").GetVisualDescendants().OfType<CheckBox>().First();
        var boxPoint = box.TranslatePoint(new Point(box.Bounds.Width / 2, box.Bounds.Height / 2), this)!.Value;
        this.MouseDown(boxPoint, MouseButton.Left, RawInputModifiers.None); this.MouseUp(boxPoint, MouseButton.Left, RawInputModifiers.None);
        for (int i = 0; i < 60 && !Item("modinfo.json").IsChecked; i++) await Task.Delay(50);
        Require(Item("modinfo.json").IsChecked, $"Clicking the checkbox did not toggle it (box {box.IsChecked}, attached {box.IsAttachedToVisualTree()}, at {boxPoint}, output '{Output.Text}').");
        // Arrow keys move the preview with the selection, as in JetBrains.
        git.GetVisualDescendants().OfType<ListBox>().First().SelectedItem = Item("mod-project.json");
        for (int i = 0; i < 100 && ((Documents.SelectedItem as TabItem)?.Content as DiffPane)?.Change.Path != "mod-project.json"; i++) await Task.Delay(50);
        Require(((TabItem)Documents.SelectedItem!).Content is DiffPane { Change.Path: "mod-project.json" } && tabs.Count(t => t.Tag is GitDiffTag) == 1, "Selecting a row did not move the preview.");
        Item("modinfo.json").IsChecked = false; items = git.Items.ToDictionary(i => i.Change.Path);
        var pane = await OpenDiffAsync("mod-project.json", preview: true);
        var diffTab = tabs.Single(t => t.Tag is GitDiffTag);
        Require(previewTab == diffTab && pane.Rows.Count > 3 && pane.Rows.Count(r => r.Kind == DiffRowKind.Modified) == 1 && pane.Rows.All(r => r.Kind == DiffRowKind.Modified || r.Kind == DiffRowKind.Equal) && pane.Summary.StartsWith("1 change"), $"Side-by-side rows are wrong: {pane.Rows.Count} rows, {pane.Summary}.");
        Require(pane.LeftText.Contains("\"name\":") && pane.RightText.Contains("\"name\" :") && pane.LeftText.Split('\n').Length == pane.RightText.Split('\n').Length, "The two sides are not aligned line for line.");
        Require(((diffTab.Header as StackPanel)?.Children.OfType<TextBlock>().First().Text ?? "").Contains("mod-project.json (diff) · preview"), "The diff did not open as a preview tab.");
        using (var image = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96))) { image.Render(this); image.Save(System.IO.Path.Combine(output, "git-diff.png"), PngBitmapEncoderOptions.Default); }
        var deleted = await OpenDiffAsync("import-report.json", preview: true);
        Require(tabs.Count(t => t.Tag is GitDiffTag) == 1 && deleted.Rows.Count > 0 && deleted.Rows.All(r => r.Kind == DiffRowKind.Removed && r.Right == null) && deleted.RightText.Trim().Length == 0, "A deleted file did not show every line as removed with an empty right side.");
        var untracked = await OpenDiffAsync("modinfo.json", preview: false);
        Require(tabs.Count(t => t.Tag is GitDiffTag) == 2 && previewTab?.Content == deleted && untracked.Rows.Count > 0 && untracked.Rows.All(r => r.Kind == DiffRowKind.Added && r.Left == null), "Double-click did not open a kept tab beside the preview, or an untracked file did not show as all additions.");
        await ShowGitDiffAsync(items["modinfo.json"].Change, preview: false); await Task.Delay(100);
        Require(tabs.Count(t => t.Tag is GitDiffTag) == 2 && ((TabItem)Documents.SelectedItem!).Content == untracked, "Reopening a change did not reuse its tab.");
        DiffPane.Unified = true; untracked.ToggleLayoutForTests(); Require(untracked.UnifiedText.Contains("+++ b/modinfo.json") && untracked.UnifiedText.Contains("+{"), "The unified toggle did not show the patch."); DiffPane.Unified = false; untracked.ToggleLayoutForTests();
        foreach (var open in tabs.Where(t => t.Tag is GitDiffTag).ToList()) await CloseTabAsync(open);
        var live = await OpenDiffAsync("mod-project.json", preview: false);
        // Rollback the deletion and the edit, leaving the untracked file alone (git may normalize line endings on restore).
        git.Choose = (_, _, options) => Task.FromResult<string?>(options[0]);
        await repo.DiscardAsync([items["mod-project.json"].Change, items["import-report.json"].Change]); await git.RefreshAsync(); await Settle();
        Require(File.Exists(System.IO.Path.Combine(root, "import-report.json")) && File.ReadAllText(modified).ReplaceLineEndings("\n") == before.ReplaceLineEndings("\n") && git.Items.Select(i => i.Change.Path).SequenceEqual(["modinfo.json"]), "Rollback did not restore the tracked files.");
        for (int i = 0; i < 100 && live.Summary != "No differences"; i++) await Task.Delay(50);
        Require(live.Summary == "No differences" && live.Rows.Count == 0, $"The open diff did not follow the rollback: {live.Summary}.");
        await CloseTabAsync(tabs.Single(t => t.Tag is GitDiffTag));
        // Branch creation through the repository the panel uses.
        await repo.CheckoutAsync("smoke/branch", true);
        // Overlap refresh requests as file-watcher callbacks do. A queued RefreshAsync returns before status is applied;
        // Busy tracks Git operations, not status refreshes, so wait for both the branch and its rendered header.
        var branchRefresh = git.RefreshAsync();
        await git.RefreshAsync();
        await Settle(() => git.Status.Branch == "smoke/branch" && git.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "smoke/branch"));
        await branchRefresh;
        Require(git.Status.Branch == "smoke/branch" && git.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "smoke/branch"), "The header did not follow the branch switch.");
        gitTabs.SelectedIndex = 0; await Task.Delay(300); UpdateLayout();
        using (var image = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96))) { image.Render(this); image.Save(System.IO.Path.Combine(output, "git-panel.png"), PngBitmapEncoderOptions.Default); }
        Require(all - 1 == (await repo.RunAsync(["ls-files"])).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length, "The commit did not contain exactly the checked files.");
        LeftTabs.SelectedIndex = 0; SetPanelVisible("bottom", true); BottomTabs.SelectedIndex = 0;
    }
}

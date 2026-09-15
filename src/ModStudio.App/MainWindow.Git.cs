using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using ModStudio.Core;

namespace ModStudio.App;

/// <summary>Version control: the Git tab beside the project tree commits and syncs; the bottom Git tab shows history and the command console.</summary>
public partial class MainWindow
{
    private readonly GitPanel git = new();
    private readonly ListBox gitLog = new() { Background = Brushes.Transparent, BorderThickness = new(0) };
    private readonly TextEditor gitDetails = DiffEditor();
    private readonly TextBox gitConsole = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new("Consolas, Menlo, monospace"), FontSize = 12 };
    private readonly TextBlock gitLogStatus = new() { Classes = { "muted" }, Margin = new(10, 6) };
    private readonly TabControl gitTabs = new();
    private TabItem? gitBottomTab;
    private string? gitLogRepository;
    private int gitLogRevision;
    private sealed record GitDiffTag(string Path) { public override string ToString() => Path.Contains('/') ? Path[(Path.LastIndexOf('/') + 1)..] + " (diff)" : Path + " (diff)"; }

    private void InitializeGit()
    {
        GitTab.Content = git;
        git.Choose = (title, text, options) => ChooseAsync(title, text, options);
        git.Failed += ex => ShowError(ex);
        git.StatusMessage += text => Status.Text = text;
        git.Output += text => { gitConsole.Text = ((gitConsole.Text ?? "") + text + Environment.NewLine); if (gitConsole.Text.Length > 120000) gitConsole.Text = gitConsole.Text[^100000..]; gitConsole.CaretIndex = gitConsole.Text.Length; };
        git.OpenFileRequested += async file => { try { await OpenDocumentAsync(file); } catch (Exception ex) { ShowError(ex); } };
        git.DiffRequested += async (change, preview) => { try { await ShowGitDiffAsync(change, preview); } catch (Exception ex) { ShowError(ex); } };
        git.HistoryRequested += () => { ShowBottomTab(BottomTabs.Items.IndexOf(gitBottomTab)); gitTabs.SelectedIndex = 0; _ = RefreshGitLogAsync(); };
        git.Changed += () => { gitLogRevision++; if (BottomPanel.IsVisible && BottomTabs.SelectedItem == gitBottomTab && gitTabs.SelectedIndex == 0) _ = RefreshGitLogAsync(); _ = ReloadOpenDiffsAsync(); };
        LeftTabs.SelectionChanged += (_, _) => { if (LeftTabs.SelectedItem == GitTab) _ = git.RefreshAsync(); };
        // Bottom tool window: Log and Console.
        var log = new Grid { ColumnDefinitions = new("2*,5,3*") };
        var logPane = new DockPanel(); DockPanel.SetDock(gitLogStatus, Dock.Top); logPane.Children.Add(gitLogStatus); logPane.Children.Add(gitLog); log.Children.Add(logPane);
        var splitter = new GridSplitter { Width = 5, ResizeDirection = GridResizeDirection.Columns }; Grid.SetColumn(splitter, 1); log.Children.Add(splitter);
        Grid.SetColumn(gitDetails, 2); log.Children.Add(gitDetails);
        gitLog.ItemTemplate = new FuncDataTemplate<GitCommit>((commit, _) =>
        {
            if (commit == null) return new TextBlock();
            var row = new DockPanel { Margin = new(2, 1) };
            var hash = new TextBlock { Text = commit.ShortHash, FontFamily = new("Consolas, Menlo, monospace"), FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#D8BC86")), VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 10, 0) };
            var meta = new TextBlock { Text = commit.Author + " · " + commit.Date, Classes = { "muted" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new(10, 0, 0, 0) };
            DockPanel.SetDock(hash, Dock.Left); DockPanel.SetDock(meta, Dock.Right);
            row.Children.Add(hash); row.Children.Add(meta); row.Children.Add(new TextBlock { Text = commit.Subject, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            ToolTip.SetTip(row, commit.Hash); return row;
        }, supportsRecycling: false);
        // Replacing the list raises a deferred empty selection; only real selections drive the details pane.
        gitLog.SelectionChanged += async (_, _) => { if (!gitLogUpdating && gitLog.SelectedItem is GitCommit commit) await ShowGitCommitAsync(commit); };
        var console = new DockPanel(); var consoleTools = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(consoleTools, Dock.Top);
        var clear = new Button { Content = "Clear" }; clear.Click += (_, _) => gitConsole.Text = ""; consoleTools.Children.Add(clear);
        consoleTools.Children.Add(new TextBlock { Text = "Every git command Studio runs, with its output", Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0) });
        console.Children.Add(consoleTools); console.Children.Add(gitConsole);
        gitTabs.Items.Add(new TabItem { Header = new TextBlock { Text = "Log", FontSize = 12 }, Content = log });
        gitTabs.Items.Add(new TabItem { Header = new TextBlock { Text = "Console", FontSize = 12 }, Content = console });
        gitTabs.Styles.Add(new Style(x => x.OfType<TabItem>()) { Setters = { new Setter(TabItem.PaddingProperty, new Thickness(8, 4)), new Setter(TabItem.MinHeightProperty, 28.0) } });
        gitTabs.SelectionChanged += (_, _) => { if (gitTabs.SelectedIndex == 0) _ = RefreshGitLogAsync(); };
        gitBottomTab = new TabItem { Header = new TextBlock { Text = "Git", FontSize = 13 }, Content = gitTabs };
        BottomTabs.Items.Add(gitBottomTab);
        BottomTabs.SelectionChanged += (_, _) => { if (BottomTabs.SelectedItem == gitBottomTab && gitTabs.SelectedIndex == 0) _ = RefreshGitLogAsync(); };
    }

    private static TextEditor DiffEditor()
    {
        var editor = new TextEditor { IsReadOnly = true, ShowLineNumbers = false, FontFamily = new("Consolas, Menlo, monospace"), FontSize = 12, Background = new SolidColorBrush(Color.Parse("#202122")), WordWrap = false, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        editor.TextArea.TextView.LineTransformers.Add(new DiffPane.DiffColorizer());
        editor.Options.EnableHyperlinks = false; editor.Options.EnableEmailHyperlinks = false;
        return editor;
    }

    private int gitLogShown = -1;
    private bool gitLogUpdating;
    private async Task RefreshGitLogAsync()
    {
        var repo = git.Repository;
        if (repo == null) { gitLog.ItemsSource = null; gitDetails.Document = new TextDocument(); gitLogStatus.Text = GitRepository.Available ? "No Git repository. Use the Git tab beside the project tree to create one." : "Git is not installed."; gitLogRepository = null; return; }
        if (gitLogRepository == repo.WorkTree && gitLogShown == gitLogRevision) return;
        gitLogRepository = repo.WorkTree; gitLogShown = gitLogRevision;
        try
        {
            var commits = await repo.LogAsync();
            if (git.Repository != repo) return;
            var selected = (gitLog.SelectedItem as GitCommit)?.Hash;
            gitLog.ItemsSource = commits; gitLogStatus.Text = commits.Count == 0 ? "No commits yet." : $"{git.Status.Branch} · {commits.Count} commit{(commits.Count == 1 ? "" : "s")}{(commits.Count >= 200 ? " shown" : "")}";
            var show = commits.FirstOrDefault(c => c.Hash == selected) ?? commits.FirstOrDefault();
            gitLogUpdating = true;
            try { gitLog.SelectedItem = show; } finally { gitLogUpdating = false; }
            await ShowGitCommitAsync(show); // selection events do not fire while the tab has not been realized
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task ShowGitCommitAsync(GitCommit? commit)
    {
        var repo = git.Repository; if (commit == null || repo == null) { gitDetails.Document = new TextDocument(); return; }
        try { var text = await repo.ShowAsync(commit.Hash); if (Equals(gitLog.SelectedItem, commit) && gitDetails.Document.Text != text) gitDetails.Document = new TextDocument(text); }
        catch (Exception ex) { ShowError(ex); }
    }

    /// <summary>Opens (or refreshes) a diff tab for a change. Preview tabs are shared, like single-click file previews; none are part of the saved session.</summary>
    private async Task ShowGitDiffAsync(GitChange change, bool preview)
    {
        var repo = git.Repository; if (repo == null) return;
        var existing = tabs.FirstOrDefault(t => t.Content is DiffPane pane && pane.Change.Path == change.Path);
        if (existing != null) { if (!preview) KeepTab(existing); Documents.SelectedItem = existing; await ReloadDiffAsync(existing, change); return; }
        var tab = new TabItem { Tag = new GitDiffTag(change.Path), Content = new DiffPane(change) };
        AddDocumentTab(tab, preview);
        await ReloadDiffAsync(tab, change);
    }

    private async Task ReloadDiffAsync(TabItem tab, GitChange change)
    {
        var repo = git.Repository; if (repo == null || tab.Content is not DiffPane pane) return;
        try
        {
            var unified = await repo.DiffAsync(change); var full = await repo.DiffAsync(change, fullContext: true);
            if (!tabs.Contains(tab) || git.Repository != repo) return;
            bool binary = UnifiedDiff.IsBinary(unified) || UnifiedDiff.IsBinary(full);
            pane.Show(change, unified.Length > 0 ? unified : "No differences." + Environment.NewLine, binary ? [] : UnifiedDiff.Rows(full), binary);
            ToolTip.SetTip(tab, change.Kind + " · " + change.Path);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    /// <summary>After a status refresh, open diff tabs follow the file: edits update them, and a rolled-back or committed file reads as unchanged.</summary>
    private async Task ReloadOpenDiffsAsync()
    {
        foreach (var tab in tabs.Where(t => t.Content is DiffPane).ToList())
        {
            var pane = (DiffPane)tab.Content!;
            var change = git.Status.Changes.FirstOrDefault(c => c.Path == pane.Change.Path) ?? pane.Change with { Kind = GitChangeKind.Modified, Staged = false, Unstaged = false };
            await ReloadDiffAsync(tab, change);
        }
    }
}

using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Styling;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ModStudio.Core;

namespace ModStudio.App;

/// <summary>A change row with the commit checkbox; check state survives refreshes so a long list is not reset under the user.</summary>
public sealed class GitChangeItem(GitChange change, bool isChecked) : INotifyPropertyChanged
{
    private bool isChecked = isChecked;
    public GitChange Change { get; } = change;
    public bool IsChecked { get => isChecked; set { if (isChecked == value) return; isChecked = value; PropertyChanged?.Invoke(this, new(nameof(IsChecked))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>The Commit tool window: a checkable change list, message box and branch / remote actions, in the spirit of JetBrains IDEs.</summary>
public sealed class GitPanel : Grid
{
    public static readonly IBrush Modified = new SolidColorBrush(Color.Parse("#6E9BD1")), Added = new SolidColorBrush(Color.Parse("#7FBF6A")), Deleted = new SolidColorBrush(Color.Parse("#8E8578")),
        Untracked = new SolidColorBrush(Color.Parse("#D98C5F")), Conflicted = new SolidColorBrush(Color.Parse("#E06C6C"));
    public static IBrush BrushFor(GitChangeKind kind) => kind switch { GitChangeKind.Added => Added, GitChangeKind.Deleted => Deleted, GitChangeKind.Untracked => Untracked, GitChangeKind.Conflicted => Conflicted, _ => Modified };

    private readonly Button branchButton = new() { Padding = new(8, 4), MinHeight = 0, HorizontalContentAlignment = HorizontalAlignment.Left, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock branchLabel = new() { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock syncLabel = new() { Classes = { "muted" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new(6, 0, 0, 0) };
    private readonly Button refreshButton = new() { Classes = { "explorerTool" } }, fetchButton = new() { Classes = { "explorerTool" } }, pullButton = new() { Classes = { "explorerTool" } }, pushButton = new() { Classes = { "explorerTool" } }, historyButton = new() { Classes = { "explorerTool" } };
    private readonly StackPanel missing = new() { Margin = new(14), Spacing = 10, IsVisible = false };
    private readonly TextBlock missingText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button initButton = new() { Content = "Create Git repository" };
    private readonly Grid repository = new() { RowDefinitions = new("Auto,*,Auto,Auto"), IsVisible = false };
    private readonly CheckBox selectAll = new() { MinHeight = 0, Padding = new(6, 0, 0, 0), IsThreeState = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock changesLabel = new() { Text = "Changes", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#8E8578")), VerticalAlignment = VerticalAlignment.Center };
    private readonly ListBox list = new() { Background = Brushes.Transparent, BorderThickness = new(0), Padding = new(0), SelectionMode = SelectionMode.Multiple };
    private readonly TextBlock empty = new() { Text = "No changes", Classes = { "muted" }, Margin = new(14, 10), IsVisible = false };
    private readonly TextBox message = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 72, MaxHeight = 160, PlaceholderText = "Commit message", Margin = new(10, 6, 10, 6), FontSize = 12 };
    private readonly Button commitButton = new() { Content = "Commit", Classes = { "accent" } }, commitPushButton = new() { Content = "Commit and Push" };
    private readonly CheckBox amend = new() { Content = "Amend", MinHeight = 0, FontSize = 12, Margin = new(6, 0, 0, 0) };
    private readonly ProgressBar busy = new() { IsIndeterminate = true, Height = 3, IsVisible = false, Margin = new(0), MinHeight = 0 };
    private readonly TextBlock operationLabel = new() { Classes = { "muted" }, FontSize = 11, Margin = new(12, 2, 12, 0), IsVisible = false, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly Dictionary<string, bool> checkedByPath = new(StringComparer.Ordinal);
    private CancellationTokenSource? operation;
    private string? root;
    private bool refreshQueued, refreshing, updatingSelectAll, showing, pressOnCheckBox;
    private int generation;

    public GitRepository? Repository { get; private set; }
    public GitStatus Status { get; private set; } = GitStatus.Empty;
    public IReadOnlyList<GitChangeItem> Items { get; private set; } = [];
    public IReadOnlyList<GitChange> CheckedChanges => Items.Where(i => i.IsChecked).Select(i => i.Change).ToList();
    public string CommitMessage { get => message.Text ?? ""; set => message.Text = value; }
    public bool Busy => operation != null;
    public void FocusMessage() => message.Focus();

    /// <summary>Console echo of every git command (command line, then output).</summary>
    public event Action<string>? Output;
    public event Action<string>? StatusMessage;
    public event Action<Exception>? Failed;
    public event Action<string>? OpenFileRequested;
    /// <summary>Open the change in a viewer; preview follows the explorer convention (single click reuses one temporary tab).</summary>
    public event Action<GitChange, bool>? DiffRequested;
    public event Action? HistoryRequested;
    /// <summary>Raised whenever the repository or its status changed, so the history view can follow.</summary>
    public event Action? Changed;
    /// <summary>Modal chooser supplied by the window: title, message, options → chosen option.</summary>
    public Func<string, string, string[], Task<string?>> Choose { get; set; } = (_, _, options) => Task.FromResult<string?>(options.Last());

    public GitPanel()
    {
        RowDefinitions = new("Auto,Auto,Auto,*");
        // Header: branch, sync state and tools.
        var header = new Grid { RowDefinitions = new("Auto,Auto"), Margin = new(10, 8, 6, 4) };
        var branchRow = new DockPanel { VerticalAlignment = VerticalAlignment.Center, LastChildFill = false };
        var branchContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        branchContent.Children.Add(ExplorerIcons.Tool("M5,3 a1.5,1.5 0 1,0 0.01,0 M5,13 a1.5,1.5 0 1,0 0.01,0 M11,4 a1.5,1.5 0 1,0 0.01,0 M5,4.5 V11.5 M11,5.5 C11,9 5,7 5,11"));
        branchContent.Children.Add(branchLabel); branchButton.Content = branchContent; ToolTip.SetTip(branchButton, "Switch or create a branch");
        branchRow.Children.Add(branchButton); branchRow.Children.Add(syncLabel); header.Children.Add(branchRow);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 4, 0, 0) }; SetRow(tools, 1);
        refreshButton.Content = ExplorerIcons.Tool("M13,8 A5,5 0 1,1 11.5,4.5 M11,2 L11.8,4.8 L9,5.5"); ToolTip.SetTip(refreshButton, "Refresh changes");
        fetchButton.Content = ExplorerIcons.Tool("M8,2 V9 M5,6 L8,9 L11,6 M3,12 H13"); ToolTip.SetTip(fetchButton, "Fetch: download remote commits without changing your files");
        pullButton.Content = ExplorerIcons.Tool("M8,2 V13 M4,9 L8,13 L12,9"); ToolTip.SetTip(pullButton, "Pull: fetch and merge the upstream branch");
        pushButton.Content = ExplorerIcons.Tool("M8,14 V3 M4,7 L8,3 L12,7"); ToolTip.SetTip(pushButton, "Push commits to the remote");
        historyButton.Content = ExplorerIcons.Tool("M8,8 m-6,0 a6,6 0 1,0 12,0 a6,6 0 1,0 -12,0 M8,4.5 V8 L10.5,9.5"); ToolTip.SetTip(historyButton, "Show commit history");
        foreach (var button in new[] { refreshButton, fetchButton, pullButton, pushButton, historyButton }) tools.Children.Add(button);
        header.Children.Add(tools); Children.Add(header);
        SetRow(busy, 1); Children.Add(busy); SetRow(operationLabel, 2); Children.Add(operationLabel);
        // Folder without a repository (or no git at all).
        missing.Children.Add(missingText); missing.Children.Add(initButton); SetRow(missing, 3); Children.Add(missing);
        // Changes, message and commit actions.
        var changesHeader = new DockPanel { Margin = new(10, 4, 10, 2) };
        changesHeader.Children.Add(selectAll); changesHeader.Children.Add(changesLabel); repository.Children.Add(changesHeader);
        var listHost = new Grid(); listHost.Children.Add(list); listHost.Children.Add(empty); SetRow(listHost, 1); repository.Children.Add(listHost);
        SetRow(message, 2); repository.Children.Add(message);
        var actions = new WrapPanel { Margin = new(8, 0, 8, 8), Orientation = Orientation.Horizontal };
        actions.Children.Add(commitButton); actions.Children.Add(commitPushButton); actions.Children.Add(amend);
        SetRow(actions, 3); repository.Children.Add(actions);
        SetRow(repository, 3); Children.Add(repository);
        ToolTip.SetTip(selectAll, "Include every change in the commit"); ToolTip.SetTip(amend, "Replace the previous commit instead of creating a new one"); ToolTip.SetTip(commitButton, "Commit the checked changes (Ctrl+Enter)");
        list.ItemTemplate = new FuncDataTemplate<GitChangeItem>((item, _) => ChangeRow(item), supportsRecycling: false);
        list.Styles.Add(new Style(x => x.OfType<ListBoxItem>()) { Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(6, 0)), new Setter(MinHeightProperty, 24.0) } });
        list.ContextMenu = new ContextMenu(); list.ContextMenu.Opening += (_, e) => { list.ContextMenu.ItemsSource = SelectedItems.Count == 0 ? null : ChangeMenu(); if (SelectedItems.Count == 0) e.Cancel = true; };
        // Selection drives the preview (mouse or arrow keys), as in the JetBrains commit window; a tap gesture alone can be
        // missed by a real mouse click that moves a pixel or two between press and release.
        list.AddHandler(PointerPressedEvent, (_, e) => pressOnCheckBox = (e.Source as Visual)?.FindAncestorOfType<CheckBox>(true) != null, RoutingStrategies.Tunnel);
        list.AddHandler(PointerReleasedEvent, (_, _) => Dispatcher.UIThread.Post(() => pressOnCheckBox = false, DispatcherPriority.Background), RoutingStrategies.Tunnel);
        list.SelectionChanged += (_, _) =>
        {
            // Ticking a checkbox selects its row too; that must not open a preview under the click.
            if (showing || pressOnCheckBox || list.SelectedItems?.Count != 1 || SelectedItems[0] is not { } item) return;
            // After the input cycle, so a press that lands on a checkbox still completes as a click.
            Dispatcher.UIThread.Post(() => { if (list.SelectedItems?.Count == 1 && ReferenceEquals(SelectedItems[0], item)) ShowDiff(item.Change, preview: true); }, DispatcherPriority.Background);
        };
        list.Tapped += (_, e) => { if ((e.Source as Visual)?.FindAncestorOfType<CheckBox>(true) == null && SelectedItems.FirstOrDefault() is { } item) ShowDiff(item.Change, preview: true); };
        list.DoubleTapped += (_, e) => { if ((e.Source as Visual)?.FindAncestorOfType<CheckBox>(true) == null && SelectedItems.FirstOrDefault() is { } item) ShowDiff(item.Change, preview: false); };
        list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Space && SelectedItems.Count > 0) { bool target = !SelectedItems.All(i => i.IsChecked); foreach (var item in SelectedItems) item.IsChecked = target; e.Handled = true; }
            else if (e.Key == Key.Enter && SelectedItems.FirstOrDefault() is { } item) { ShowDiff(item.Change, preview: false); e.Handled = true; }
        };
        selectAll.IsCheckedChanged += (_, _) => { if (updatingSelectAll) return; bool target = selectAll.IsChecked == true; foreach (var item in Items) item.IsChecked = target; };
        message.KeyDown += async (_, e) => { if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { e.Handled = true; await CommitAsync(push: false); } };
        refreshButton.Click += async (_, _) => await RefreshAsync();
        fetchButton.Click += async (_, _) => await RunOperationAsync("Fetching…", async (repo, token) => Report(await repo.FetchAsync(token), "Fetched."));
        pullButton.Click += async (_, _) => await RunOperationAsync("Pulling…", async (repo, token) => Report(await repo.PullAsync(token), "Pulled."));
        pushButton.Click += async (_, _) => await PushAsync();
        historyButton.Click += (_, _) => HistoryRequested?.Invoke();
        commitButton.Click += async (_, _) => await CommitAsync(push: false);
        commitPushButton.Click += async (_, _) => await CommitAsync(push: true);
        branchButton.Click += async (_, _) => await ShowBranchMenuAsync();
        initButton.Click += async (_, _) => await InitializeAsync();
        refreshTimer.Tick += async (_, _) => { refreshTimer.Stop(); await RefreshAsync(); };
        UpdateButtons();
    }

    private List<GitChangeItem> SelectedItems => list.SelectedItems?.OfType<GitChangeItem>().ToList() ?? [];

    private Control ChangeRow(GitChangeItem? item)
    {
        if (item == null) return new TextBlock();
        var row = new DockPanel { Background = Brushes.Transparent, Margin = new(0, 0, 4, 0) };
        var check = new CheckBox { MinHeight = 0, Padding = new(0), Margin = new(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        check.Bind(ToggleButtonIsCheckedProperty, new Binding(nameof(GitChangeItem.IsChecked)) { Mode = BindingMode.TwoWay, Source = item });
        check.IsCheckedChanged += (_, _) => UpdateSelectAll();
        var badge = new TextBlock { Text = item.Change.Badge.ToString(), Width = 14, FontSize = 11, FontWeight = FontWeight.Bold, Foreground = BrushFor(item.Change.Kind), VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 4, 0) };
        var name = new TextBlock { Text = item.Change.Name, Foreground = BrushFor(item.Change.Kind), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var folder = new TextBlock { Text = item.Change.Folder, Classes = { "muted" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new(6, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        DockPanel.SetDock(check, Dock.Left); DockPanel.SetDock(badge, Dock.Left); DockPanel.SetDock(name, Dock.Left);
        row.Children.Add(check); row.Children.Add(badge); row.Children.Add(name); row.Children.Add(folder);
        ToolTip.SetTip(row, item.Change.Kind + (item.Change.OriginalPath == null ? "" : " from " + item.Change.OriginalPath) + " · " + item.Change.Path + "\nClick to preview the diff; double-click to keep it open");
        return row;
    }
    private static readonly AvaloniaProperty<bool?> ToggleButtonIsCheckedProperty = Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty;

    private List<object> ChangeMenu()
    {
        var selected = SelectedItems.Select(i => i.Change).ToList(); var first = selected[0];
        var menu = new List<object>();
        menu.Add(Item("Show Diff", () => { ShowDiff(first, preview: false); return Task.CompletedTask; }));
        if (first.Kind != GitChangeKind.Deleted) menu.Add(Item("Open File", () => { OpenFileRequested?.Invoke(Repository!.Absolute(first.Path)); return Task.CompletedTask; }));
        menu.Add(new Separator());
        menu.Add(Item(selected.Count == 1 ? "Rollback…" : $"Rollback {selected.Count} files…", () => RollbackAsync(selected)));
        if (selected.Any(c => c.Staged)) menu.Add(Item("Unstage", () => RunOperationAsync("Unstaging…", async (repo, token) => await repo.UnstageAsync(selected.Where(c => c.Staged), token))));
        menu.Add(new Separator());
        menu.Add(Item("Copy Path", async () => { var clipboard = TopLevel.GetTopLevel(this)?.Clipboard; if (clipboard != null) await clipboard.SetTextAsync(string.Join(Environment.NewLine, selected.Select(c => c.Path))); }));
        return menu;
    }
    private MenuItem Item(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) => { try { await action(); } catch (Exception ex) { Failed?.Invoke(ex); } };
        return item;
    }

    // Project binding -------------------------------------------------------------------------------------------------------

    public void SetProject(string? projectRoot)
    {
        root = projectRoot; Repository = null; Status = GitStatus.Empty; checkedByPath.Clear(); message.Text = ""; amend.IsChecked = false;
        generation++; operation?.Cancel();
        Show([]); UpdateButtons();
        _ = RefreshAsync();
    }

    /// <summary>Debounced refresh for file watcher bursts; changes inside .git are the repository's own bookkeeping.</summary>
    public void QueueRefresh(string? changedPath = null)
    {
        if (Repository == null) return;
        if (changedPath != null && changedPath.Replace('\\', '/').Contains("/.git/", StringComparison.OrdinalIgnoreCase)) return;
        refreshTimer.Stop(); refreshTimer.Start();
    }

    public async Task RefreshAsync()
    {
        if (root == null) { Show([]); return; }
        if (refreshing) { refreshQueued = true; return; }
        refreshing = true; int current = generation;
        try
        {
            if (!GitRepository.Available) { Repository = null; Show([]); return; }
            var repo = Repository;
            // Re-resolve when the project gained its own repository inside an enclosing one (Create Git repository).
            if (repo == null || !Storage.Contains(repo.WorkTree, root) || repo.WorkTree != System.IO.Path.GetFullPath(root) && Directory.Exists(System.IO.Path.Combine(root, ".git")))
            {
                repo = await GitRepository.OpenAsync(root);
                if (current != generation) return;
                if (repo != null) repo.Ran += result => Dispatcher.UIThread.Post(() => Echo(result));
                Repository = repo;
            }
            if (repo == null) { Show([]); return; }
            var status = await repo.StatusAsync();
            if (current != generation) return;
            Status = status; Show(status.Changes);
        }
        catch (Exception ex) { if (current == generation) Failed?.Invoke(ex); }
        finally
        {
            refreshing = false; UpdateButtons(); Changed?.Invoke();
            if (refreshQueued) { refreshQueued = false; _ = RefreshAsync(); }
        }
    }

    private void Echo(GitResult result)
    {
        // Status polling would drown the console; only report commands the user can act on.
        if (result.Command.StartsWith("status ") || result.Command.StartsWith("symbolic-ref") || result.Command.StartsWith("for-each-ref") || result.Command.StartsWith("log ") || result.Command.StartsWith("show ") || result.Command.StartsWith("diff ") || result.Command == "remote") return;
        Output?.Invoke("> git " + result.Command + (result.Text.Length > 0 ? Environment.NewLine + result.Text : "") + (result.Ok ? "" : $"{Environment.NewLine}(exit code {result.ExitCode})"));
    }

    private void Show(IReadOnlyList<GitChange> changes)
    {
        // Background refreshes usually find nothing new; rebuilding the rows anyway would drop selection and swallow a click in progress.
        if (Items.Count > 0 && changes.SequenceEqual(Items.Select(i => i.Change))) { ShowHeader(); return; }
        foreach (var item in Items) checkedByPath[item.Change.Path] = item.IsChecked;
        Items = changes.Select(c => new GitChangeItem(c, !checkedByPath.TryGetValue(c.Path, out var was) || was)).ToList();
        foreach (var item in Items) item.PropertyChanged += (_, _) => UpdateSelectAll();
        var selectedPaths = SelectedItems.Select(i => i.Change.Path).ToHashSet(StringComparer.Ordinal);
        showing = true;
        try { list.ItemsSource = Items; foreach (var item in Items.Where(i => selectedPaths.Contains(i.Change.Path))) list.SelectedItems?.Add(item); }
        finally { showing = false; }
        empty.IsVisible = Items.Count == 0 && Repository != null;
        changesLabel.Text = Items.Count == 0 ? "Changes" : $"Changes · {Items.Count}";
        UpdateSelectAll();
        ShowHeader();
    }

    private void ShowHeader()
    {
        bool hasRepo = Repository != null;
        repository.IsVisible = hasRepo; missing.IsVisible = !hasRepo;
        if (!GitRepository.Available) { missingText.Text = "Git was not found on this computer. Install Git from https://git-scm.com and restart Studio to commit, push and pull from here."; initButton.IsVisible = false; }
        else if (root == null) { missingText.Text = "Open a project to use version control."; initButton.IsVisible = false; }
        else { missingText.Text = "This project is not in a Git repository yet. Create one to track changes, commit and share your mod."; initButton.IsVisible = true; }
        if (hasRepo)
        {
            branchLabel.Text = Status.Branch.Length > 0 ? Status.Branch : "no branch";
            var sync = new List<string>();
            if (Status.Ahead > 0) sync.Add($"↑{Status.Ahead}"); if (Status.Behind > 0) sync.Add($"↓{Status.Behind}");
            syncLabel.Text = string.Join(" ", sync); syncLabel.IsVisible = sync.Count > 0;
            ToolTip.SetTip(syncLabel, Status.Upstream == null ? null : $"{Status.Ahead} to push, {Status.Behind} to pull · tracking {Status.Upstream}");
            ToolTip.SetTip(branchButton, (Status.Upstream == null ? "No upstream branch" : "Tracking " + Status.Upstream) + "\nClick to switch or create a branch");
        }
        else { branchLabel.Text = "Git"; syncLabel.IsVisible = false; }
    }

    private void UpdateSelectAll()
    {
        updatingSelectAll = true;
        try { selectAll.IsChecked = Items.Count > 0 && Items.All(i => i.IsChecked) ? true : Items.Any(i => i.IsChecked) ? null : false; selectAll.IsEnabled = Items.Count > 0; }
        finally { updatingSelectAll = false; }
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool idle = operation == null, hasRepo = Repository != null;
        busy.IsVisible = operationLabel.IsVisible = !idle;
        foreach (var button in new[] { fetchButton, pullButton, pushButton, historyButton, branchButton }) button.IsEnabled = idle && hasRepo;
        refreshButton.IsEnabled = idle && root != null; initButton.IsEnabled = idle;
        bool canCommit = idle && hasRepo && (Items.Any(i => i.IsChecked) || amend.IsChecked == true);
        commitButton.IsEnabled = canCommit; commitPushButton.IsEnabled = canCommit; message.IsEnabled = idle; list.IsEnabled = idle;
        pullButton.IsEnabled = pushButton.IsEnabled = idle && hasRepo && !Status.Detached;
    }

    // Operations -----------------------------------------------------------------------------------------------------------

    private void Report(GitResult result, string success) { result.Throw(); StatusMessage?.Invoke(result.Text.Length > 0 ? result.Text.Split('\n')[0].Trim() : success); }

    private async Task RunOperationAsync(string label, Func<GitRepository, CancellationToken, Task> action)
    {
        var repo = Repository; if (repo == null || operation != null) return;
        using var cancellation = new CancellationTokenSource(); operation = cancellation; operationLabel.Text = label; UpdateButtons();
        try { await action(repo, cancellation.Token); }
        catch (OperationCanceledException) { StatusMessage?.Invoke("Git operation canceled."); }
        catch (Exception ex) { Failed?.Invoke(ex); }
        finally { operation = null; UpdateButtons(); await RefreshAsync(); }
    }

    public void Cancel() => operation?.Cancel();

    public Task CommitAsync(bool push) => RunOperationAsync(push ? "Committing and pushing…" : "Committing…", async (repo, token) =>
    {
        var changes = CheckedChanges; var text = CommitMessage;
        var result = await repo.CommitAsync(text, changes, amend.IsChecked == true, token);
        message.Text = ""; amend.IsChecked = false;
        StatusMessage?.Invoke($"Committed {changes.Count} file{(changes.Count == 1 ? "" : "s")}: {text.Split('\n')[0].Trim()}");
        if (push) Report(await repo.PushAsync(await repo.StatusAsync(token), token: token), "Pushed.");
    });

    private Task PushAsync() => RunOperationAsync("Pushing…", async (repo, token) => Report(await repo.PushAsync(await repo.StatusAsync(token), token: token), "Pushed."));

    private async Task InitializeAsync()
    {
        if (root == null) return;
        try
        {
            var repo = await GitRepository.InitAsync(root);
            // Studio's cache and recovery files never belong in history.
            var ignore = System.IO.Path.Combine(root, ".gitignore");
            if (!File.Exists(ignore)) await File.WriteAllTextAsync(ignore, ".studio/\n", Storage.Utf8);
            else if (!(await File.ReadAllTextAsync(ignore)).Split('\n').Any(l => l.Trim() is ".studio" or ".studio/" or "/.studio" or "/.studio/")) await File.AppendAllTextAsync(ignore, "\n.studio/\n");
            Output?.Invoke("> git init" + Environment.NewLine + "Initialized repository in " + repo.WorkTree);
            StatusMessage?.Invoke("Git repository created. Check the files to include and write a commit message.");
        }
        catch (Exception ex) { Failed?.Invoke(ex); }
        await RefreshAsync();
    }

    public void ShowDiff(GitChange change, bool preview) { if (Repository != null) DiffRequested?.Invoke(change, preview); }

    private async Task RollbackAsync(List<GitChange> selected)
    {
        var deleted = selected.Count(c => c.Kind is GitChangeKind.Untracked or GitChangeKind.Added);
        var summary = selected.Count == 1 ? selected[0].Path : $"{selected.Count} files";
        var detail = deleted > 0 ? $"\n{deleted} new file{(deleted == 1 ? "" : "s")} will be deleted from disk." : "";
        if (await Choose("Rollback changes", $"Discard local changes to {summary}? This cannot be undone.{detail}", ["Rollback", "Cancel"]) != "Rollback") return;
        await RunOperationAsync("Rolling back…", async (repo, token) => { await repo.DiscardAsync(selected, token); StatusMessage?.Invoke($"Rolled back {summary}."); });
    }

    private async Task ShowBranchMenuAsync()
    {
        var repo = Repository; if (repo == null) return;
        IReadOnlyList<string> branches;
        try { branches = await repo.BranchesAsync(); } catch (Exception ex) { Failed?.Invoke(ex); return; }
        var items = new List<object>();
        var create = new MenuItem { Header = "New Branch…" }; create.Click += async (_, _) => await NewBranchAsync(); items.Add(create); items.Add(new Separator());
        foreach (var branch in branches)
        {
            var item = new MenuItem { Header = branch == Status.Branch ? "✓ " + branch : "    " + branch, IsEnabled = branch != Status.Branch };
            item.Click += async (_, _) => await RunOperationAsync("Switching branch…", async (r, token) => { await r.CheckoutAsync(branch, false, token); StatusMessage?.Invoke("Switched to " + branch); });
            items.Add(item);
        }
        var menu = new ContextMenu { ItemsSource = items, Placement = PlacementMode.BottomEdgeAlignedLeft };
        menu.Open(branchButton);
    }

    private async Task NewBranchAsync()
    {
        var owner = TopLevel.GetTopLevel(this) as Window; if (owner == null) return;
        var dialog = new Window { Title = "New branch", Width = 420, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        var stack = new StackPanel { Margin = new(20), Spacing = 12 };
        stack.Children.Add(new TextBlock { Text = "Branch name (created from " + (Status.Branch.Length > 0 ? Status.Branch : "the current commit") + ")", TextWrapping = TextWrapping.Wrap });
        var name = new TextBox { PlaceholderText = "feature/my-change" }; stack.Children.Add(name);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        string? result = null;
        var ok = new Button { Content = "Create", Classes = { "accent" }, IsDefault = true }; ok.Click += (_, _) => { result = name.Text?.Trim(); dialog.Close(); };
        var cancel = new Button { Content = "Cancel", IsCancel = true }; cancel.Click += (_, _) => dialog.Close();
        buttons.Children.Add(ok); buttons.Children.Add(cancel); stack.Children.Add(buttons); dialog.Content = stack;
        dialog.Opened += (_, _) => name.Focus();
        await dialog.ShowDialog(owner);
        if (string.IsNullOrWhiteSpace(result)) return;
        await RunOperationAsync("Creating branch…", async (r, token) => { await r.CheckoutAsync(result, true, token); StatusMessage?.Invoke("Created and switched to " + result); });
    }
}

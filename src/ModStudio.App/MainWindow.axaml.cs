using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using ModStudio.Core;
using static ModStudio.Core.Storage;

namespace ModStudio.App;

public partial class MainWindow : Window
{
    private ModProject? project;
    private readonly ProjectTerminal terminal = new();
    private readonly ObservableCollection<TabItem> tabs = [];
    private TabItem? previewTab;
    private readonly SemaphoreSlim documentOpening = new(1, 1);
    private readonly ObservableCollection<Diagnostic> diagnostics = [];
    private readonly List<Diagnostic> catalogIdWarnings = [];
    private readonly DispatcherTimer catalogWarningTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private int catalogWarningRevision;
    private readonly RunController controller = new();
    private CancellationTokenSource? operation;
    private FileSystemWatcher? watcher;
    private readonly DispatcherTimer recoveryTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer runStateTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly Dictionary<Document, int> recoveredRevision = [];
    private bool closingApproved, inspectorUpdating;
    private string? runningBuild;
    private int workspaceRevision;
    private readonly List<Diagnostic> buildDiagnostics = [];
    private EditorPane? Active => (Documents?.SelectedItem as TabItem)?.Content as EditorPane;
    private string Profile => ProfilePicker.SelectedItem is string s ? s : (ProfilePicker.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "standard";
    public MainWindow()
    {
        InitializeComponent(); InitializeLog(); InitializeItemPreview(); InitializeSkillPreview(); InitializeMissilePreview(); InitializeStatPreview(); InitializeDropPreview(); InitializeMonsterPreview(); InitializePreviewLinks(); InitializeAffixPreview(); InitializeRecipePreview(); InitializeRowEditor(); InitializeExplorerSearch(); InitializeLaunchTargets(); InitializeFindInFiles(); BottomTabs.Items.Add(new TabItem { Header = new TextBlock { Text = "Terminal", FontSize = 13 }, Content = terminal }); InitializeGit(); InitializeColumnGuide(); InitializeLayout(); InitializeViewPreferences(); InitializeTabDragging(); Problems.ItemsSource = diagnostics;
        EditorTextInfo.Attach(CellValue, () => CellValueInfo.Text = string.IsNullOrEmpty(CellValue.Text) ? "" : EditorTextInfo.Describe(CellValue.Text));
        catalogWarningTimer.Tick += async (_, _) => { catalogWarningTimer.Stop(); if (project is { } current) await RefreshCatalogIdWarningsAsync(current); };
        // Reserve space for the overlay scrollbar only when the one-row toolbar overflows.
        ToolbarActions.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.ExtentProperty || e.Property == ScrollViewer.ViewportProperty)
                ToolbarActionButtons.Margin = new(0, 0, 0, ToolbarActions.Extent.Width > ToolbarActions.Viewport.Width + 1 ? 22 : 0);
        };
        if (!Program.Arguments.Contains("--smoke")) WindowState = WindowState.Maximized;
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://ModStudio.App/Assets/ReimaginedModStudio.ico")));
        var welcome = (TabItem)Documents.Items[0]!; Documents.Items.Clear(); tabs.Add(welcome); Documents.ItemsSource = tabs;
        ProfilePicker.Items.Clear(); ProfilePicker.ItemsSource = new[] { "standard", "d2rl" }; ProfilePicker.SelectedIndex = 0;
        ProfilePicker.SelectionChanged += (_, _) => { _ = RefreshSemanticInspectorAsync(); RefreshItemPreview(); RefreshSkillPreview(); RefreshMissilePreview(); RefreshStatPreview(); RefreshDropPreview(); RefreshMonsterPreview(); RefreshAffixPreview(); RefreshRecipePreview(); RefreshLaunchTargets(); RefreshVisualBuilders(); };
        recoveryTimer.Tick += (_, _) => SaveRecovery(idleOnly: true); recoveryTimer.Start();
        runStateTimer.Tick += (_, _) => RefreshRunControls(); runStateTimer.Start(); InitializeExternalEditor(); InitializeCompanion();
        KeyDown += async (_, e) =>
        {
            bool command = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
            if (command && e.Key == Key.S) { await SaveAllAsync(); e.Handled = true; }
            else if (command && e.Key == Key.K && project != null) { SetPanelVisible("explorer", true); LeftTabs.SelectedItem = GitTab; git.FocusMessage(); e.Handled = true; }
        };
        Closing += async (_, e) =>
        {
            if (closingApproved) return; e.Cancel = true;
            if (externalBusy) { Status.Text = "External synchronization is finishing. Close again when it has finished."; return; }
            if (operation != null) { operation.Cancel(); ShowError(new Exception("Canceling the current operation. Close again when it has finished.")); return; }
            if (!await MayLeaveAsync()) return;
            if (controller.Running && await ChooseAsync("A game process is still running", "Closing Studio leaves that process running.", "Leave running and close", "Cancel") != "Leave running and close") return;
            try { SaveOpenFiles(); } catch (Exception ex) { ShowError(ex); return; }
            closingApproved = true; terminal.Dispose(); git.Cancel(); watcher?.Dispose(); recoveryTimer.Stop(); runStateTimer.Stop(); controller.Dispose(); Close();
        };
        Opened += async (_, _) =>
        {
            try
            {
                if (Program.Arguments.Contains("--smoke")) { await SmokeAsync(); return; }                _ = CheckStudioUpdateAsync(false);
                if (Program.Integration?.Initial != null)
                {
                    await Program.Integration.StartAsync(async r => await Dispatcher.UIThread.InvokeAsync(() => ReceiveCompanionAsync(r)));
                    return;
                }
                var arg = Program.Arguments.FirstOrDefault(a => !a.StartsWith('-'));
                var previous = arg ?? StudioPreferences.Load(StudioPreferences.DefaultFile).LastProject;
                if (previous != null)
                {
                    if (Directory.Exists(previous)) await LoadProjectAsync(previous);
                    else Status.Text = "Last project is unavailable. Use Open project to choose its new location.";
                }
                if (Program.Integration != null) await Program.Integration.StartAsync(async r => await Dispatcher.UIThread.InvokeAsync(() => ReceiveCompanionAsync(r)));
            }
            catch (Exception e) { ShowError(e); }
            finally { if (!Program.Arguments.Contains("--smoke") && Program.Integration != null) await Program.Integration.StartAsync(async r => await Dispatcher.UIThread.InvokeAsync(() => ReceiveCompanionAsync(r))); }
        };
    }
    private void RefreshRunControls()
    {
        StopButton.IsVisible = operation != null || controller.Running;
        StopButton.Content = operation != null ? "■ Cancel" : "■ Stop";
        if (runningBuild != null && !controller.Running && operation == null) RunState.Text = "Last game: " + runningBuild + " · exited";
    }
    private bool logging;
    private void Log(string text) => Dispatcher.UIThread.Post(() => { AppendLog(text); logging = true; try { Status.Text = text; } finally { logging = false; } });
    private void AppendLog(string text)
    {
        Output.Text = (Output.Text ?? "") + DateTime.Now.ToString("HH:mm:ss") + "  " + text + Environment.NewLine;
        if (Output.Text.Length > 60000) Output.Text = Output.Text[^50000..];
    }
    /// <summary>Every status-bar message is also kept in the Log tab, so a message that was only glimpsed can be read back later; clicking the status bar opens that tab.</summary>
    private void InitializeLog()
    {
        Status.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty && !logging && Status.Text is { Length: > 0 } text) AppendLog(text); };
        Status.PointerPressed += (_, _) => ShowBottomTab(1);
    }
    private void ShowError(Exception e) { if (Program.Arguments.Contains("--smoke")) Console.Error.WriteLine(e); logging = true; try { Status.Text = e.Message; } finally { logging = false; } AppendLog("Error: " + e.Message); ShowBottomTab(1); }
    private async Task<string?> PickFolderAsync(string title)
    {
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false }); return result.FirstOrDefault()?.TryGetLocalPath();
    }
    private async Task<string?> PickFileAsync(string title)
    {
        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false }); return result.FirstOrDefault()?.TryGetLocalPath();
    }
    private async Task<string?> ChooseAsync(string title, string message, params string[] options)
    {
        var dialog = new Window { Title = title, Width = 560, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        var stack = new StackPanel { Margin = new(20), Spacing = 16 }; stack.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        var buttons = new WrapPanel(); foreach (var option in options) { var button = new Button { Content = option }; button.Click += (_, _) => dialog.Close(option); buttons.Children.Add(button); }
        stack.Children.Add(buttons); dialog.Content = stack; return await dialog.ShowDialog<string?>(this);
    }
    /// <summary>
    /// Projects written before single-file tables keep a folder per table. They are converted in place after the user agrees;
    /// declining leaves the project untouched and unopened, since the editor, build and checks only read the current layout.
    /// </summary>
    private async Task<bool> UpgradeLayoutIfNeededAsync(ModProject candidate)
    {
        var folders = await Task.Run(() => ProjectLayout.LegacyTableFolders(candidate.Root));
        if (folders.Count == 0) return true;
        var choice = Program.Arguments.Contains("--smoke") ? "Convert" : await ChooseAsync("Convert project layout",
            $"{candidate.Name} stores each table as a folder with schema.json and records.json ({folders.Count} tables). Studio now keeps each table in a single file, source/tables/<name>.json, with the schema inside it.\n\n" +
            "Convert now? Each folder is rewritten as one file and the old files are removed. Anything else inside a table folder is kept. Commit or back up first if you want to be able to compare.",
            "Convert", "Not now");
        if (choice != "Convert") { Status.Text = "Project not opened: convert its tables to the single-file layout first."; return false; }
        int converted = await Task.Run(() => ProjectLayout.Upgrade(candidate.Root, message => Dispatcher.UIThread.Post(() => Status.Text = message)));
        Log($"Converted {converted} tables to single-file layout in {candidate.Root}");
        return true;
    }
    private async Task<bool> MayLeaveAsync()
    {
        if (!tabs.Select(t => t.Content).OfType<EditorPane>().Any(p => p.Document.IsDirty)) return true;
        var choice = await ChooseAsync("Unsaved documents", "Save changes before leaving this project? Invalid source remains available in local recovery.", "Save all", "Keep recovery and leave", "Cancel");
        if (choice == "Save all") return await SaveAllAsync(); if (choice == "Keep recovery and leave") { SaveRecovery(); return true; } return false;
    }
    private async void OpenClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!await MayLeaveAsync()) return; var root = await PickFolderAsync("Open mod project root"); if (root == null) return;
            var legacy = await Task.Run(() => LegacyMigration.Detect(root));
            if (legacy.Any(p => p.SplitRecords) || !Directory.Exists(System.IO.Path.Combine(root, "source/tables")) && legacy.Count > 0)
            {
                var choice = await ChooseAsync("Legacy project detected", "This folder holds native mod data rather than a Studio project. Import it to edit as JSON source with recovery and build support?", "Import mod…", "Open as-is", "Cancel");
                if (choice == "Import mod…") { await MigrateAsync(root); return; }
                if (choice != "Open as-is") return;
            }
            await LoadProjectAsync(root);
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private async void MigrateClicked(object? sender, RoutedEventArgs e)
    {
        try { Require(operation == null, "Wait for the current operation."); if (!await MayLeaveAsync()) return; await MigrateAsync(null); }
        catch (Exception ex) { ShowError(ex); }
    }
    /// <summary>Import mod: one window collects source, name, mode, project folder and deployment target; then the progress window runs the migration.</summary>
    private async Task MigrateAsync(string? root)
    {
        Require(operation == null, "Wait for the current operation.");
        var preferences = StudioPreferences.Load(StudioPreferences.DefaultFile);
        var plan = await new ImportModWindow(preferences.ResolvedProjectsFolder, root).ShowDialog<ImportPlan?>(this); if (plan == null) return;
        operation = new(); RefreshRunControls(); ImportReport? report;
        recoveryTimer.Stop(); if (watcher != null) watcher.EnableRaisingEvents = false;
        if (plan.Backup != null) terminal.Stop();
        try { report = await new MigrationProgressWindow(plan.Source, plan.Destination, plan.Name, operation, Log, plan.Backup).ShowDialog<ImportReport?>(this); }
        finally { operation.Dispose(); operation = null; recoveryTimer.Start(); if (watcher != null) watcher.EnableRaisingEvents = true; RefreshRunControls(); }
        if (report == null) return;
        if (plan.DeploymentDirectory.Length > 0 || plan.GameDirectory.Length > 0)
        {
            // The window already showed and confirmed the game paths, so skip the first-open Run settings introduction.
            foreach (var profile in report.Project.Profiles)
            {
                var settings = RunSettings.Load(report.Project, profile);
                if (plan.GameDirectory.Length > 0)
                {
                    var executables = RunSettings.DetectExecutables(plan.GameDirectory);
                    settings = settings with { GameDirectory = plan.GameDirectory, Executable = "", LaunchTarget = executables.Contains(settings.LaunchTarget, StringComparer.OrdinalIgnoreCase) || executables.Length == 0 ? settings.LaunchTarget : executables[0] };
                }
                if (plan.DeploymentDirectory.Length > 0) settings = settings with { DeploymentDirectory = plan.DeploymentDirectory };
                settings.Save(report.Project, profile);
            }
            if (plan.GameDirectory.Length > 0) preferences.MarkIntroduced(report.Project.Root, StudioPreferences.DefaultFile);
        }
        await LoadProjectAsync(report.Project.Root); Log($"Imported {report.Tables} tables and {report.Catalogs} catalogs. See migration-report.json for verification and preserved files.");
        await new QuickStartWindow().ShowDialog(this);
    }
    private async void TutorialClicked(object? sender, RoutedEventArgs e) => await new QuickStartWindow().ShowDialog(this);
    public async Task LoadProjectAsync(string root)
    {
        Require(operation == null && !externalBusy, "Wait for the current operation before switching projects.");
        var nextProject = await Task.Run(() => ModProject.Open(root));
        Require(!externalBusy, "External synchronization started. Wait for it to finish before switching projects.");
        if (!await UpgradeLayoutIfNeededAsync(nextProject)) return;
        Require(!externalBusy, "Wait for external synchronization before switching projects.");
        Program.Integration?.ClaimProject(nextProject.Root); companionProfiles.Clear(); companionStamps.Clear();
        if (!Program.Arguments.Contains("--smoke")) SaveOpenFiles();
        watcher?.Dispose(); externalWatcher?.Dispose(); externalActive = false; findInFiles?.Close();
        project = nextProject; terminal.SetProject(root); git.SetProject(root); previewTab = null; tabs.Clear(); recoveredRevision.Clear(); lastEdit.Clear(); Documents.ItemsSource = tabs; buildDiagnostics.Clear(); catalogIdWarnings.Clear(); catalogWarningTimer.Stop(); catalogWarningRevision++;
        Title = $"{project.Name} | Reimagined D2R Mod Studio"; ProjectLabel.Text = project.Name; ToolTip.SetTip(ProjectLabel, project.Root);
        var entries = await Task.Run(() => ProjectEntry.Read(project.Root));
        await RefreshCatalogIdWarningsAsync(nextProject);
        explorerSearchTimer.Stop(); ExplorerSearch.Text = ""; SetExplorerEntries(entries); ProfilePicker.ItemsSource = project.Profiles.ToArray(); ProfilePicker.SelectedItem = project.Profiles.Contains("standard") ? "standard" : project.Profiles.FirstOrDefault();
        watcher = new(project.Root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName, EnableRaisingEvents = true };
        watcher.Changed += OnExternalChange; watcher.Created += OnExternalChange; watcher.Deleted += OnExternalChange; watcher.Renamed += OnExternalChange;
        RefreshLaunchTargets(); RefreshStatus(); Status.Text = "Project ready. Single-click to preview; double-click to keep a file open.";
        if (!Program.Arguments.Contains("--smoke"))
        {
            try
            {
                var preferences = StudioPreferences.Load(StudioPreferences.DefaultFile);
                preferences.Remember(project.Root, StudioPreferences.DefaultFile);
                await RestoreOpenFilesAsync();
                await ApplyDetectedGameDefaultsAsync();
                if (!preferences.HasIntroduced(project.Root)) await ShowSettingsAsync();
            }
            catch (Exception ex) { ShowError(ex); }
        }
        WatchExternalSession();
    }
    private void OnExternalChange(object? sender, FileSystemEventArgs e)
    {
        if (project == null || !Contains(project.Root, e.FullPath) || Contains(project.Cache, e.FullPath) || Contains(System.IO.Path.Combine(project.Root, ".git"), e.FullPath)) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (externalActive) { externalPending = true; externalChanged = DateTime.UtcNow; }
            if (e.ChangeType != WatcherChangeTypes.Changed) QueueExplorerRefresh();
            git.QueueRefresh();
            workspaceRevision++; SemanticStatus.Text = "Files changed; run source checks again";
            if (project is { } current &&
                (Contains(Path.Combine(current.Root, "source/strings"), e.FullPath) && e.FullPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                 e is RenamedEventArgs renamedCatalog && Contains(Path.Combine(current.Root, "source/strings"), renamedCatalog.OldFullPath) && renamedCatalog.OldFullPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            { catalogWarningTimer.Stop(); catalogWarningTimer.Start(); }
            RefreshItemPreview(); RefreshSkillPreview(); RefreshMissilePreview(); RefreshStatPreview(); RefreshDropPreview(); RefreshMonsterPreview(); RefreshAffixPreview(); RefreshRecipePreview(); RefreshVisualBuilders();
            _ = RefreshSemanticInspectorAsync();
            if (runningBuild != null && controller.Running) RunState.Text = $"Game: {runningBuild} · source changed";
            foreach (var tab in tabs)
            {
                if (tab.Content is not EditorPane pane) continue;
                if (pane.Document.FilePath == e.FullPath || e is RenamedEventArgs renamed && pane.Document.FilePath == renamed.OldFullPath)
                {
                    try { if (!pane.Document.ExternalChange) continue; } catch (IOException) { }
                    Status.Text = "File changed on disk. Use Reload from the document tab before saving: " + System.IO.Path.GetFileName(pane.Document.FilePath);
                    RefreshStatus();
                }
            }
        });
    }
    private void UpdateTabHeader(TabItem tab)
    {
        bool preview = tab == previewTab;
        var label = tab.Content is EditorPane pane ? Label(pane.Document) : tab.Tag is string path ? System.IO.Path.GetFileName(path) : tab.Tag?.ToString();
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        header.Children.Add(new TextBlock { Text = label + (preview ? " · preview" : ""), FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            FontStyle = preview ? FontStyle.Italic : FontStyle.Normal,
            Foreground = preview ? new SolidColorBrush(Color.Parse("#D8BC86")) : Brushes.White });
        var close = new Button { Content = "×", Width = 24, Height = 24, MinWidth = 24, MinHeight = 24, Padding = new(0), Margin = new(0), FontSize = 17 };
        close.Classes.Add("tabClose");
        ToolTip.SetTip(close, "Close file");
        Avalonia.Automation.AutomationProperties.SetName(close, "Close " + label);
        close.Click += async (_, e) => { e.Handled = true; await CloseTabAsync(tab); };
        close.DoubleTapped += (_, e) => e.Handled = true;
        header.Children.Add(close); tab.Header = header;
        var filePath = (tab.Content as EditorPane)?.Document.FilePath ?? tab.Tag as string;
        if (filePath != null) header.ContextMenu = new ContextMenu { ItemsSource = IsExternalTable(filePath) ? new[] { CreateOpenLocationItem(filePath), CreateExternalEditorItem(filePath) } : new[] { CreateOpenLocationItem(filePath) } };
        ToolTip.SetTip(tab, preview ? "Temporary preview · Double-click this tab to keep it open" : tab.Content is EditorPane p ? p.Document.FilePath : tab.Tag);
    }
    private MenuItem CreateOpenLocationItem(string path, bool directory = false)
    {
        var item = new MenuItem { Header = "Open File Location" };
        item.Click += (_, _) =>
        {
            try
            {
                var folder = directory ? System.IO.Path.GetFullPath(path) : System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!;
                if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("File location is unavailable: " + folder);
                Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
            }
            catch (Exception e) { ShowError(e); }
        };
        return item;
    }
    private readonly HashSet<TabItem> closingTabs = [];
    private async Task CloseTabAsync(TabItem tab)
    {
        if (!tabs.Contains(tab) || !closingTabs.Add(tab)) return;
        try
        {
            if (tab.Content is EditorPane pane && pane.Document.IsDirty)
            {
                var choice = await ChooseAsync("Close document", "How would you like to close this document with unsaved changes?", "Keep recovery and close", "Discard Changes", "Cancel");
                if (choice == "Keep recovery and close")
                {
                    // Do not close if writing recovery fails.
                    pane.Document.Recover(RecoveryFile(pane.Document));
                    recoveredRevision[pane.Document] = pane.Document.Revision;
                }
                else if (choice == "Discard Changes")
                {
                    // The recovery timer may have written this document while the dialog was open.
                    var recovery = RecoveryFile(pane.Document);
                    if (File.Exists(recovery)) File.Delete(recovery);
                    recoveredRevision.Remove(pane.Document);
                }
                else return;
            }
            if (previewTab == tab) previewTab = null;
            tabs.Remove(tab); RefreshStatus();
        }
        catch (Exception e) { ShowError(e); }
        finally { closingTabs.Remove(tab); }
    }
    private void KeepTab(TabItem tab)
    {
        if (previewTab == tab) previewTab = null;
        UpdateTabHeader(tab);
    }
    private void AddDocumentTab(TabItem tab, bool preview, bool activate = true)
    {
        if (preview && previewTab != null)
        {
            if (previewTab.Content is EditorPane old && old.Document.IsDirty) KeepTab(previewTab);
            else { tabs.Remove(previewTab); previewTab = null; }
        }
        if (preview) previewTab = tab;
        tab.AddHandler(PointerPressedEvent, async (_, e) =>
        {
            if (!e.GetCurrentPoint(tab).Properties.IsMiddleButtonPressed || !new Rect(tab.Bounds.Size).Contains(e.GetPosition(tab))) return;
            e.Handled = true;
            await CloseTabAsync(tab);
        }, RoutingStrategies.Tunnel);
        tab.DoubleTapped += (_, _) => KeepTab(tab);
        tabs.Add(tab); UpdateTabHeader(tab); if (activate) Documents.SelectedItem = tab; RefreshStatus();
    }
    private readonly Dictionary<TabItem, Task<EditorPane?>> loadingDocuments = [];
    /// <summary>Opens (or focuses) a document tab. <paramref name="activate"/> false adds the tab without selecting it, for edits made from Find in files.</summary>
    public Task<EditorPane?> OpenDocumentAsync(string file, bool preview = false, bool activate = true)
    {
        file = System.IO.Path.GetFullPath(file);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var existing = tabs.FirstOrDefault(t => string.Equals((t.Content as EditorPane)?.Document.FilePath ?? t.Tag as string, file, comparison));
        if (existing != null)
        {
            if (!preview) KeepTab(existing);
            if (activate) Documents.SelectedItem = existing;
            return loadingDocuments.TryGetValue(existing, out var pending) ? pending : Task.FromResult(existing.Content as EditorPane);
        }
        var openingProject = project;
        var placeholder = new StackPanel { Margin = new(24), Spacing = 14 };
        placeholder.Children.Add(new TextBlock { Text = "Loading " + System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(file)) + "/" + System.IO.Path.GetFileName(file) + "…" });
        placeholder.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 4 });
        placeholder.Children.Add(new TextBlock { Text = "Reading and validating the file. You can switch tabs or close this tab.", TextWrapping = TextWrapping.Wrap });
        var tab = new TabItem { Tag = file, Content = placeholder };
        AddDocumentTab(tab, preview, activate);
        async Task<EditorPane?> LoadAsync()
        {
            // Yield before starting work so the loading tab can be laid out and painted.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await documentOpening.WaitAsync();
            try { return openingProject == project && tabs.Contains(tab) ? await OpenDocumentCoreAsync(file, preview, openingProject, tab, activate) : null; }
            catch (Exception ex)
            {
                if (tabs.Contains(tab)) tab.Content = new TextBlock { Margin = new(24), TextWrapping = TextWrapping.Wrap, Text = "Unable to open file: " + ex.Message + "\nClose this tab and reopen the file to retry." };
                return null;
            }
            finally { documentOpening.Release(); loadingDocuments.Remove(tab); }
        }
        var task = LoadAsync(); loadingDocuments[tab] = task; return task;
    }
    private async Task<EditorPane?> OpenDocumentCoreAsync(string file, bool preview, ModProject? openingProject, TabItem? loadingTab = null, bool activate = true)
    {
        file = System.IO.Path.GetFullPath(file);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var existing = tabs.FirstOrDefault(t => t != loadingTab && string.Equals((t.Content as EditorPane)?.Document.FilePath ?? t.Tag as string, file, comparison)); if (existing != null) { if (!preview) KeepTab(existing); if (activate) Documents.SelectedItem = existing; return existing.Content as EditorPane; }
        var extension = System.IO.Path.GetExtension(file).ToLowerInvariant();
        if (SpecialistPreviewPane.Supports(file))
        {
            if (openingProject != project || loadingTab != null && !tabs.Contains(loadingTab)) return null;
            var specialTab = loadingTab ?? new TabItem { Tag = file };
            specialTab.Content = new SpecialistPreviewPane(file);
            if (loadingTab == null) AddDocumentTab(specialTab, preview, activate); else UpdateTabHeader(specialTab);
            return null;
        }
        var isText = await Task.Run(() => { NoLinks(file); using var input = File.OpenRead(file); var prefix = new byte[(int)Math.Min(input.Length, 8192)]; input.ReadExactly(prefix); return TextFileEncoding.LooksLikeText(prefix, isPrefix: input.Position < input.Length); });
        if (!isText)
        {
            NoLinks(file); using var stream = File.OpenRead(file); var bytes = new byte[Math.Min(stream.Length, 1024)]; await stream.ReadExactlyAsync(bytes);
            var binaryView = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Text = $"{System.IO.Path.GetFileName(file)}\n{stream.Length:N0} bytes\nRead-only binary preview (first 1,024 bytes)\n\n" + Convert.ToHexString(bytes) };
            if (openingProject != project || loadingTab != null && !tabs.Contains(loadingTab)) return null;
            var binary = loadingTab ?? new TabItem { Tag = file };
            var binaryPanel = new DockPanel();
            var rawButton = new Button { Content = "Open as raw text", HorizontalAlignment = HorizontalAlignment.Left };
            ToolTip.SetTip(rawButton, "Edit the entire file as text. Binary structure is not validated; use this only when intentional.");
            rawButton.Click += async (_, _) =>
            {
                try
                {
                    var rawDocument = await Task.Run(() => new Document(file, forceRaw: true));
                    if (!tabs.Contains(binary)) return;
                    var rawPane = new EditorPane(rawDocument, ShowError, UpdateInspector, save: SavePane);
                    binary.Content = rawPane;
                    rawDocument.Changed += () => { if (rawDocument.IsDirty) KeepTab(binary); UpdateTabHeader(binary); RefreshStatus(); };
                    UpdateTabHeader(binary); UpdateInspector(rawPane);
                }
                catch (Exception ex) { ShowError(ex); }
            };
            DockPanel.SetDock(rawButton, Dock.Top); binaryPanel.Children.Add(rawButton); binaryPanel.Children.Add(binaryView); binary.Content = binaryPanel;
            var closeBinary = new MenuItem { Header = "Close preview" }; closeBinary.Click += async (_, _) => await CloseTabAsync(binary); binary.ContextMenu = new ContextMenu { ItemsSource = new[] { CreateOpenLocationItem(file), closeBinary } };
            if (openingProject != project) return null;
            if (loadingTab == null) AddDocumentTab(binary, preview, activate); return null;
        }
        Status.Text = "Loading " + System.IO.Path.GetFileName(file) + "…";
        var document = await Task.Run(() => new Document(file));
        if (openingProject != project || loadingTab != null && !tabs.Contains(loadingTab)) return null;
        var pane = new EditorPane(document, ShowError, UpdateInspector, SavePane, FindOpenDocument);
        AttachVisualBuilder(pane);
        RememberEditorView(pane, file);
        pane.ReferenceRequested += async (sender, row, column, anchor) => await NavigateCellReferenceAsync(sender, row, column, anchor);
        pane.ColumnGuideRequested += OpenColumnGuide;
        pane.ItemHovered += (sender, row, anchor) => { if (row >= 0) { if (EditorPane.ItemHoverCards) RequestItemPreview(sender, row, anchor, sender.HoverPosition); } else if (itemRequestPane == sender && itemHoverRequest) ScheduleItemTooltipClose(); };
        var tab = loadingTab ?? new TabItem(); tab.Content = pane;
        if (loadingTab == null) AddDocumentTab(tab, preview, activate); else { UpdateTabHeader(tab); if (Documents.SelectedItem == tab) UpdateInspector(pane); }
        var menu = new ContextMenu(); var reload = new MenuItem { Header = "Reload from disk…" }; var close = new MenuItem { Header = "Close document…" }; menu.ItemsSource = new[] { CreateOpenLocationItem(file), reload, close }; tab.ContextMenu = menu;
        if (IsExternalTable(file)) menu.ItemsSource = new[] { CreateOpenLocationItem(file), CreateExternalEditorItem(file), reload, close };
        reload.Click += async (_, _) => { if (document.IsDirty && await ChooseAsync("Reload document", "Current edits will remain in recovery. Reload the disk version?", "Reload", "Cancel") != "Reload") return; SaveRecovery(); tabs.Remove(tab); await OpenDocumentAsync(file); };
        close.Click += async (_, _) => await CloseTabAsync(tab);
        document.Changed += () => { lastEdit[document] = DateTime.UtcNow; if (document.IsDirty) KeepTab(tab); if (runningBuild != null && controller.Running && document.IsDirty) RunState.Text = $"Game: {runningBuild} · newer unsaved edits"; buildDiagnostics.Clear(); SemanticStatus.Text = "Source changed; run checks again"; UpdateTabHeader(tab); RefreshStatus(); _ = RefreshSemanticInspectorAsync(); RefreshItemPreview(); RefreshSkillPreview(); RefreshMissilePreview(); RefreshStatPreview(); RefreshDropPreview(); RefreshMonsterPreview(); RefreshAffixPreview(); RefreshRecipePreview(); RefreshColumnGuide(); RefreshVisualBuilders(document); };
        var recoveryFile = RecoveryFile(document);
        if (File.Exists(recoveryFile) && !Program.Arguments.Contains("--smoke"))
        {
            var choice = await ChooseAsync("Recovered editing session", "Local recovery exists for this document. Restore it, open its text for comparison, or discard it?", "Restore", "Open recovery text", "Discard", "Later");
            if (choice == "Restore") { try { document.RestoreRecovery(recoveryFile); pane.Refresh(); } catch (Exception e) { ShowError(e); } }
            else if (choice == "Open recovery text") await OpenDocumentCoreAsync(recoveryFile, false, openingProject);
            else if (choice == "Discard") File.Delete(recoveryFile);
        }
        RefreshStatus(); Status.Text = $"Opened {Label(document)} · {document.Table?.Records.Count ?? 0:N0} rows"; return pane;
    }
    /// <summary>Tab title: table files show their table name, everything else its file name.</summary>
    private static string Label(Document doc) => (doc.IsDirty ? "● " : "") + (doc.Table != null && doc.FilePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? System.IO.Path.GetFileNameWithoutExtension(doc.FilePath) : System.IO.Path.GetFileName(doc.FilePath));
    private Document? FindOpenDocument(string file) => tabs.Select(t => t.Content).OfType<EditorPane>()
        .Select(p => p.Document).FirstOrDefault(d => string.Equals(d.FilePath, file, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
    private string RecoveryFile(Document doc) => Inside(project!.Cache, "recovery/" + Hash(doc.FilePath) + ".json");
    private readonly Dictionary<Document, DateTime> lastEdit = [];
    private static readonly TimeSpan RecoveryIdle = TimeSpan.FromSeconds(2);
    /// <summary>
    /// Writes recovery for dirty documents. Recovery serializes the whole table on the UI thread, which is a visible pause on
    /// large tables, so the periodic timer only takes documents that have been idle for a moment; closing takes everything.
    /// </summary>
    private void SaveRecovery(bool idleOnly = false)
    {
        if (project == null) return;
        foreach (var pane in tabs.Select(t => t.Content).OfType<EditorPane>())
        {
            var doc = pane.Document;
            if (!doc.IsDirty && recoveredRevision.Remove(doc)) { try { File.Delete(RecoveryFile(doc)); } catch (Exception e) { ShowError(e); } }
            if (!doc.IsDirty || recoveredRevision.GetValueOrDefault(doc, -1) == doc.Revision) continue;
            if (idleOnly && lastEdit.TryGetValue(doc, out var edited) && DateTime.UtcNow - edited < RecoveryIdle) continue;
            try { doc.Recover(RecoveryFile(doc)); recoveredRevision[doc] = doc.Revision; } catch (Exception e) { ShowError(e); }
        }
    }
    private void RefreshStatus()
    {
        diagnostics.Clear(); var listed = new HashSet<Diagnostic>();
        static string ProblemPath(string file) => Path.GetFullPath(file).Replace('/', Path.DirectorySeparatorChar);
        void Add(Diagnostic diagnostic) { var normalized = diagnostic with { File = ProblemPath(diagnostic.File) }; if (listed.Add(normalized)) diagnostics.Add(normalized); }
        foreach (var diagnostic in buildDiagnostics) Add(diagnostic);
        var changes = new List<string>();
        var openFiles = tabs.Select(t => t.Content).OfType<EditorPane>().Select(p => ProblemPath(p.Document.FilePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var diagnostic in catalogIdWarnings) if (!openFiles.Contains(ProblemPath(diagnostic.File))) Add(diagnostic);
        foreach (var pane in tabs.Select(t => t.Content).OfType<EditorPane>())
        {
            foreach (var diagnostic in pane.Document.Diagnostics) Add(diagnostic);
            if (pane.Document.IsDirty) changes.Add(pane.Document.FilePath + " — unsaved revision " + pane.Document.Revision);
        }
        Changes.ItemsSource = changes;
    }
    private static List<Diagnostic> ScanCatalogIdWarnings(ModProject current)
    {
        var warnings = new List<Diagnostic>(); var folder = Path.Combine(current.Root, "source/strings");
        if (!Directory.Exists(folder)) return warnings;
        foreach (var file in Directory.GetFiles(folder, "*.json"))
            try { warnings.AddRange(TableData.Load(file).DuplicateIdWarnings(file)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Add(new(file, ex.Message)); }
        return warnings;
    }
    private async Task RefreshCatalogIdWarningsAsync(ModProject current)
    {
        var revision = ++catalogWarningRevision;
        var warnings = await Task.Run(() => ScanCatalogIdWarnings(current));
        if (project != current || revision != catalogWarningRevision) return;
        catalogIdWarnings.Clear(); catalogIdWarnings.AddRange(warnings); RefreshStatus();
    }
    private void DocumentSelected(object? sender, SelectionChangedEventArgs e) { if (Active != null) UpdateInspector(Active); else { RefreshRowEditor(null); RefreshItemPreview(); RefreshSkillPreview(); RefreshMissilePreview(); RefreshStatPreview(); RefreshDropPreview(); RefreshMonsterPreview(); RefreshAffixPreview(); RefreshRecipePreview(); RefreshColumnGuide(); } }
    private void UpdateInspector(EditorPane pane)
    {
        if (Active != pane) return; inspectorUpdating = true;
        var table = pane.Document.Table; SelectionLabel.Text = table == null ? pane.Document.FilePath : $"{table.Name} · row {pane.SelectedRow}\n{table.Records.Count:N0} records · {table.Columns.Length} columns";
        FieldPicker.ItemsSource = table?.Columns; FieldPicker.SelectedItem = table?.Columns.Contains(pane.SelectedColumn) == true ? pane.SelectedColumn : table?.Columns.FirstOrDefault(); inspectorUpdating = false; FieldSelected(null, null!);
        inspectorInfoText = table == null ? "Raw document. Unknown fields are preserved." : table.IsCatalog ? "Locale values are editable; imported IDs and keys remain stable, entries added in Studio can set theirs. Compact translation review is validated on build." : "Cell values remain strings. Empty and zero are distinct. Runtime identity columns are protected where defined by this table's schema.";
        UpdateFieldInsight();
        RefreshRowEditor(pane); RefreshItemPreview(); RefreshSkillPreview(); RefreshMissilePreview(); RefreshStatPreview(); RefreshDropPreview(); RefreshMonsterPreview(); RefreshAffixPreview(); RefreshRecipePreview(); RefreshColumnGuide();
    }
    private void FieldSelected(object? sender, SelectionChangedEventArgs e) { if (!inspectorUpdating) { CellValue.Text = Active?.Document.Table is { } table && Active.SelectedRow < table.Records.Count && FieldPicker.SelectedItem is string field ? table.Cell(Active.SelectedRow, field) : ""; UpdateFieldInsight(); _ = RefreshSemanticInspectorAsync(); } }
    private string inspectorInfoText = "";
    /// <summary>
    /// What the selected field means in this row: the function its value selects, the functions that read it, or the codes
    /// of its formula. Falls back to the table's general note when there is nothing specific to say.
    /// </summary>
    private void UpdateFieldInsight()
    {
        IReadOnlyList<string> insight = [];
        if (Active is { } pane && pane.Document.Table is { IsCatalog: false } table && !pane.Document.PendingSource && pane.SelectedRow >= 0 && pane.SelectedRow < table.Records.Count && FieldPicker.SelectedItem is string column)
        {
            int row = pane.SelectedRow;
            try { insight = FieldInsight.Describe(table.Name, table.Columns, c => table.ColumnIndex(c) >= 0 ? table.Cell(row, c) : "", column); }
            catch (Exception ex) when (ex is InvalidDataException or FormatException) { insight = [ex.Message]; }
        }
        InspectorInfo.Text = insight.Count > 0 ? string.Join("\n", insight) : inspectorInfoText;
        InspectorInfo.Classes.Set("muted", insight.Count == 0);
    }
    private void ApplyCellClicked(object? sender, RoutedEventArgs e) { try { if (Active is { } pane && FieldPicker.SelectedItem is string field) { pane.Document.SetCells([(pane.SelectedRow, field, CellValue.Text ?? "")]); pane.Refresh(); } } catch (Exception ex) { ShowError(ex); } }
    private async void ProblemDoubleTapped(object? sender, TappedEventArgs e) { try { if (Problems.SelectedItem is Diagnostic d && File.Exists(d.File)) { var pane = await OpenDocumentAsync(d.File); if (d.Row >= 0) pane?.Jump(d.Row, d.Field); } } catch (Exception ex) { ShowError(ex); } }
    private async void SaveAllClicked(object? sender, RoutedEventArgs e) => await SaveAllAsync();
    private void SavePane(EditorPane pane)
    {
        // Save applies pending raw source itself; re-parsing an already applied table would only replace the model the grid is showing.
        pane.Document.Save();
        var recovery = RecoveryFile(pane.Document); if (File.Exists(recovery)) File.Delete(recovery);
        pane.Refresh();
    }
    private async Task<bool> SaveAllAsync()
    {
        try
        {
            foreach (var pane in tabs.Select(t => t.Content).OfType<EditorPane>())
            {
                if (!pane.Document.IsDirty) continue; pane.Document.Save(); var recovery = RecoveryFile(pane.Document); if (File.Exists(recovery)) File.Delete(recovery); pane.Refresh();
            }
            Status.Text = "All documents saved."; await Task.CompletedTask; return true;
        }
        catch (Exception e) { SaveRecovery(); ShowError(e); return false; }
    }
    private async void SettingsClicked(object? sender, RoutedEventArgs e)
        => await ShowSettingsAsync();
    private async Task ShowSettingsAsync()
    {
        try
        {
            Require(project != null, "Open a project first."); var current = RunSettings.Load(project!, Profile);
            var dialog = new Window { Title = "Run settings · " + Profile, Width = 720, Height = 650, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var panel = new StackPanel { Margin = new(20), Spacing = 10 };
            panel.Children.Add(new TextBlock { Text = $"Source project: {project!.Root}\nThis is the folder you edit. Studio discovers native data within a selected project during migration. Deployment is a separate game-ready output folder.", TextWrapping = TextWrapping.Wrap });
            TextBox Field(string label, string value) { panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }); var box = new TextBox { Text = value }; panel.Children.Add(box); return box; }
            var deployment = Field($"Deployment mod folder (…/mods/{project!.Name})", current.DeploymentDirectory);
            panel.Children.Add(new TextBlock { Text = $"Example: C:/Games/Diablo II Resurrected/mods/{project.Name}\nSelect the final mod folder. Studio creates its .mpq/data contents. The destination may be new; do not select your source data folder.\nThe folder's name is the name the mod is built and launched under, so a second folder such as mods/{project.Name}-test deploys the same project as a separate mod.", TextWrapping = TextWrapping.Wrap });
            var pickDeploy = new Button { Content = "Browse deployment folder" }; pickDeploy.Click += async (_, _) => { var value = await PickFolderAsync("Deployment mod folder"); if (value != null) deployment.Text = value; }; panel.Children.Add(pickDeploy);
            var overwriteDestination = new CheckBox { Content = "Overwrite destination", IsChecked = current.OverwriteDestination }; panel.Children.Add(overwriteDestination);
            panel.Children.Add(new TextBlock { Text = "Replace existing files included in the build during Deploy / Play, including files edited outside Studio. Unrelated files are kept. Enabled by default; saved per profile.", TextWrapping = TextWrapping.Wrap });
            var gameDirectory = Field("Game installation folder (contains D2R.exe / D2RLoader.exe)", current.InstallationDirectory);
            var launchTarget = new ComboBox { MinWidth = 200 };
            panel.Children.Add(new TextBlock { Text = "Launch using" }); panel.Children.Add(launchTarget);
            var detected = new TextBlock { TextWrapping = TextWrapping.Wrap }; panel.Children.Add(detected);
            void DetectLaunchers()
            {
                var selected = launchTarget.SelectedItem as string ?? current.LaunchTarget;
                var choices = RunSettings.DetectExecutables(gameDirectory.Text ?? ""); launchTarget.ItemsSource = choices;
                launchTarget.SelectedItem = choices.Contains(selected) ? selected : choices.FirstOrDefault();
                detected.Text = choices.Length == 0 ? "No D2R.exe or D2RLoader.exe found. Choose the game installation folder." : "Detected: " + string.Join(", ", choices) + ". The launch target is independent of the build profile.";
            }
            DetectLaunchers();
            var message = new TextBlock { TextWrapping = TextWrapping.Wrap };
            void UseGameFolder(string value) { gameDirectory.Text = value; if (string.IsNullOrWhiteSpace(deployment.Text)) deployment.Text = System.IO.Path.Combine(value, "mods", project.Name); }
            var gameButtons = new WrapPanel();
            var pickGame = new Button { Content = "Browse game folder" }; pickGame.Click += async (_, _) => { var value = await PickFolderAsync("Game installation folder containing D2R.exe"); if (value != null) UseGameFolder(value); }; gameButtons.Children.Add(pickGame);
            var detectGame = new Button { Content = "Detect installation", Margin = new(8, 0, 0, 0) }; gameButtons.Children.Add(detectGame);
            var detectedInstalls = new ComboBox { MinWidth = 320, Margin = new(8, 0, 0, 0), IsVisible = false, PlaceholderText = "Choose a detected installation" }; gameButtons.Children.Add(detectedInstalls);
            panel.Children.Add(gameButtons);
            var installations = Array.Empty<GameInstallation>();
            detectedInstalls.SelectionChanged += (_, _) => { if (detectedInstalls.SelectedIndex >= 0 && detectedInstalls.SelectedIndex < installations.Length) UseGameFolder(installations[detectedInstalls.SelectedIndex].Directory); };
            async Task DetectInstallationsAsync(bool fillEmpty)
            {
                detectGame.IsEnabled = false; detectGame.Content = "Detecting…";
                try
                {
                    installations = (await Task.Run(() => GameInstallDetector.Detect())).ToArray();
                    detectedInstalls.ItemsSource = installations.Select(i => $"{i.Directory}  ·  {i.Source} ({string.Join(", ", i.Executables)})").ToArray();
                    detectedInstalls.IsVisible = installations.Length > 0;
                    if (installations.Length == 0) detected.Text = "No Diablo II: Resurrected installation was found in the Battle.net, Steam or registry locations. Browse to the game folder manually.";
                    else if (fillEmpty && string.IsNullOrWhiteSpace(gameDirectory.Text)) detectedInstalls.SelectedIndex = 0;
                    else if (!fillEmpty && installations.Length == 1) detectedInstalls.SelectedIndex = 0;
                }
                catch (Exception ex) { message.Text = ex.Message; }
                finally { detectGame.IsEnabled = true; detectGame.Content = "Detect installation"; }
            }
            detectGame.Click += async (_, _) => await DetectInstallationsAsync(false);
            var runner = Field("Optional runner executable (Wine/Proton on Linux or macOS)", current.Runner);
            var runnerArgs = Field("Runner arguments (JSON array, before the game executable)", JsonSerializer.Serialize(current.RunnerArguments ?? []));
            var args = Field("Additional game arguments (JSON array)", JsonSerializer.Serialize(current.Arguments ?? []));
            var saveBefore = new CheckBox { Content = "Save all documents before Build / Deploy / Play", IsChecked = current.SaveBeforePlay }; panel.Children.Add(saveBefore);
            panel.Children.Add(message);
            void CheckPaths()
            {
                var issues = new RunSettings(deployment.Text ?? "", Runner: runner.Text ?? "", GameDirectory: gameDirectory.Text ?? "", LaunchTarget: launchTarget.SelectedItem as string ?? "D2R.exe").PathIssues(project);
                var modName = string.IsNullOrWhiteSpace(deployment.Text) ? project.Name : DeploymentService.ModName(deployment.Text);
                var renamed = issues.Count == 0 && modName != project.Name ? $"\nDeploys and plays as mod ‘{modName}’ ({modName}.mpq, -mod {modName}) rather than ‘{project.Name}’." : "";
                message.Text = (issues.Count > 0 ? string.Join("\n", issues) : string.IsNullOrWhiteSpace(deployment.Text) || string.IsNullOrWhiteSpace(gameDirectory.Text) ? "Setup incomplete: Build works without game paths. Configure deployment and the game folder before Play." : "Paths look consistent. Deploy also checks folder ownership and existing files before copying.") + renamed;
            }
            deployment.TextChanged += (_, _) => CheckPaths(); gameDirectory.TextChanged += (_, _) => { DetectLaunchers(); CheckPaths(); }; launchTarget.SelectionChanged += (_, _) => CheckPaths(); runner.TextChanged += (_, _) => CheckPaths(); CheckPaths();
            var save = new Button { Content = "Save local settings" }; save.Click += (_, _) => { try { new RunSettings(deployment.Text ?? "", "", runner.Text ?? "", JsonSerializer.Deserialize<string[]>(runnerArgs.Text ?? "[]"), JsonSerializer.Deserialize<string[]>(args.Text ?? "[]"), saveBefore.IsChecked == true, gameDirectory.Text ?? "", launchTarget.SelectedItem as string ?? current.LaunchTarget, overwriteDestination.IsChecked == true).Save(project, Profile); RefreshLaunchTargets(); dialog.Close(); } catch (Exception ex) { message.Text = ex.Message; } }; panel.Children.Add(save);
            dialog.Content = new ScrollViewer { Content = panel };
            if (string.IsNullOrWhiteSpace(gameDirectory.Text) && !Program.Arguments.Contains("--smoke")) dialog.Opened += async (_, _) => await DetectInstallationsAsync(true);
            if (!Program.Arguments.Contains("--smoke"))
            {
                var preferences = StudioPreferences.Load(StudioPreferences.DefaultFile);
                preferences.MarkIntroduced(project.Root, StudioPreferences.DefaultFile);
            }
            await dialog.ShowDialog(this);
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private async void BuildClicked(object? sender, RoutedEventArgs e) => await RunAsync(false, false);
    private async void DeployClicked(object? sender, RoutedEventArgs e) => await RunAsync(true, false);
    private async void PlayClicked(object? sender, RoutedEventArgs e) => await RunAsync(true, true);
    private void StopClicked(object? sender, RoutedEventArgs e) { operation?.Cancel(); controller.Stop(); RunState.Text = "Stop requested"; Log("Cancellation requested; active deployment recovery finishes before returning."); }
    private async Task RunAsync(bool deploy, bool play)
    {
        try
        {
            Require(project != null && operation == null && !externalBusy, "Open a project and wait for the current operation.");
            var settings = RunSettings.Load(project!, Profile);
            if (tabs.Select(t => t.Content).OfType<EditorPane>().Any(p => p.Document.IsDirty))
            {
                if (!settings.SaveBeforePlay && await ChooseAsync("Unsaved documents", "Save all documents before this build?", "Save and continue", "Cancel") != "Save and continue") return;
                if (!await SaveAllAsync()) return;
            }
            if (!await SyncExternalAsync(true)) return;
            buildDiagnostics.Clear(); RefreshStatus(); ShowBottomTab(1); operation = new();
            try
            {
                var revisions = tabs.Select(t => t.Content).OfType<EditorPane>().ToDictionary(p => p.Document, p => p.Document.Revision);
                var build = await controller.ExecuteAsync(project!, Profile, settings, deploy, play, operation.Token, Log, ReviewDeploymentOwnershipAsync);
                buildDiagnostics.AddRange(build.Diagnostics ?? []); RefreshStatus();
                if (play) { runningBuild = build.Id[..8]; RunState.Text = "Game: " + runningBuild + (revisions.Any(p => p.Key.Revision != p.Value) ? " · newer edits" : " · " + build.Profile); }
                var warnings = build.Diagnostics ?? [];
                foreach (var warning in warnings.Take(25)) AppendLog($"  {warning.Severity}: {Path.GetFileName(warning.File)}{(warning.Row >= 0 ? $" row {warning.Row}" : "")}{(warning.Field.Length > 0 ? $" {warning.Field}" : "")}: {warning.Message}");
                if (warnings.Count > 25) AppendLog($"  … {warnings.Count - 25} more in the Problems tab");
                Log($"Build {build.Id[..8]} complete{(warnings.Count > 0 ? $" with {warnings.Count} warning(s) — see the Problems tab" : "")}. Output: {build.Output}");
            }
            finally { operation.Dispose(); operation = null; }
        }
        catch (BuildFailure e) { buildDiagnostics.AddRange(e.Diagnostics); RefreshStatus(); ShowBottomTab(0); Status.Text = "Build blocked. Select a problem to locate its source."; }
        catch (OperationCanceledException) { Status.Text = "Build/deployment canceled."; }
        catch (Exception e) { ShowError(e); }
    }
    private async Task SmokeAsync()
    {
        async Task SettleRowEditorAsync()
        {
            // A timer continuation may run before Background-priority selection/layout work on CI.
            // Drain that work before taking a baseline; wait for the actual selected document state.
            var timeout = Stopwatch.StartNew();
            int settled = 0;
            while (timeout.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Dispatcher.UIThread.InvokeAsync(() => UpdateLayout(), DispatcherPriority.Background);
                var pane = Active;
                bool matches = !rowEditorRefreshQueued && pane != null && rowEditorPane == pane &&
                    rowEditorRow == pane.SelectedRow && rowEditorRevision == pane.Document.Revision &&
                    ReferenceEquals(rowEditorTable, pane.Document.Table);
                if (matches && ++settled >= 2) return;
                if (!matches) settled = 0;
                await Task.Delay(10);
            }
            throw new TimeoutException($"Row Editor did not settle: selected row {Active?.SelectedRow}, editor row {rowEditorRow}, revision {Active?.Document.Revision}/{rowEditorRevision}, queued {rowEditorRefreshQueued}.");
        }
        async Task SettleExplorerSearchAsync(Func<bool> ready, string expectation)
        {
            var timeout = Stopwatch.StartNew();
            int settled = 0;
            while (timeout.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Dispatcher.UIThread.InvokeAsync(() => UpdateLayout(), DispatcherPriority.Background);
                bool matches = !explorerSearchTimer.IsEnabled && ready();
                if (matches && ++settled >= 2) return;
                if (!matches) settled = 0;
                await Task.Delay(10);
            }
            throw new TimeoutException($"Explorer search did not settle for {expectation}: query '{ExplorerSearch.Text}', timer {explorerSearchTimer.IsEnabled}, entries [{string.Join(", ", Flatten((IEnumerable<ProjectEntry>)ProjectTree.ItemsSource!).Select(entry => entry.Name))}].");
        }
        int exit = 0;
        try
        {
            recoveryTimer.Stop();
            await CheckStudioUpdateAsync(false);
            Require(!updateBusy && UpdateButton.IsEnabled && studioUpdate == null,
                "Portable update check must finish without offering an install.");
            int index = Array.IndexOf(Program.Arguments, "--smoke"); var root = Program.Arguments[index + 1]; var output = Program.Arguments[index + 2]; Directory.CreateDirectory(output);
            await SmokeColumnGuideAsync(output);
            await LoadProjectAsync(root); var results = new List<object>();
            if (Program.Arguments.Contains("--companion-only"))
            {
                await SmokeCompanionAsync(output);
                if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lifetime) lifetime.Shutdown(0);
                return;
            }
            if (Program.Arguments.Contains("--cell-tip-only"))
            {
                var pane = await OpenDocumentAsync(Path.Combine(root, "source/strings/cell-tip.json"));
                Require(pane != null, "Cell tooltip smoke needs a catalog.");
                pane!.GridColumnOf(pane.TableGrid, 2)!.Width = new DataGridLength(100);
                DataGridCell? Cell(int rowIndex)
                {
                    var row = pane.TableGrid.GetVisualDescendants().OfType<DataGridRow>().FirstOrDefault(r => r.IsVisible && r.DataContext is RowView view && view.Row == rowIndex);
                    return row == null ? null : pane.GridColumnOf(pane.TableGrid, 2)!.GetCellContent(row)?.GetVisualAncestors().OfType<DataGridCell>().FirstOrDefault();
                }
                var timeout = Stopwatch.StartNew();
                while (timeout.Elapsed < TimeSpan.FromSeconds(10) && (Cell(0)?.Bounds.Width ?? 0) == 0)
                { await Dispatcher.UIThread.InvokeAsync(() => UpdateLayout(), DispatcherPriority.Background); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); await Task.Delay(10); }
                var longCell = Cell(0); var shortCell = Cell(1);
                Require(longCell != null && shortCell != null, "Cell tooltip smoke did not realize both catalog rows.");
                var tip = pane.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "TruncatedCellTooltip");
                var longPoint = longCell!.TranslatePoint(new Point(22, longCell!.Bounds.Height / 2), this)!.Value;
                this.MouseMove(longPoint, RawInputModifiers.None);
                Require(tip.IsVisible && tip.GetVisualDescendants().OfType<TextBlock>().Single().Text == "of the Mammoth", "Clipped text did not show its complete value immediately.");
                var firstX = Canvas.GetLeft(tip);
                this.MouseMove(new Point(longPoint.X + 10, longPoint.Y), RawInputModifiers.None);
                Require(tip.IsVisible && Canvas.GetLeft(tip) > firstX + 5, "Cell tooltip did not follow the pointer.");
                UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                using (var image = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96)))
                { image.Render(this); image.Save(Path.Combine(output, "cell-tip.png"), PngBitmapEncoderOptions.Default); }
                var shortPoint = shortCell!.TranslatePoint(new Point(22, shortCell!.Bounds.Height / 2), this)!.Value;
                this.MouseMove(shortPoint, RawInputModifiers.None);
                Require(!tip.IsVisible, "A fully visible cell showed the tooltip.");
                pane.GridColumnOf(pane.TableGrid, 2)!.Width = new DataGridLength(220); UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                this.MouseMove(longPoint, RawInputModifiers.None);
                Require(!tip.IsVisible, "Widening the column did not clear the truncated-cell tooltip.");
                pane.GridColumnOf(pane.TableGrid, 2)!.Width = new DataGridLength(100);
                pane.Jump(0, "enUS"); pane.ToggleFrozenRows(); UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                var frozenRow = pane.FrozenGrid.GetVisualDescendants().OfType<DataGridRow>().FirstOrDefault(r => r.IsVisible && r.DataContext is RowView view && view.Row == 0);
                var frozenCell = frozenRow == null ? null : pane.GridColumnOf(pane.FrozenGrid, 2)!.GetCellContent(frozenRow)?.GetVisualAncestors().OfType<DataGridCell>().FirstOrDefault();
                Require(frozenCell != null, "Frozen catalog row was not realized for the tooltip check.");
                var frozenPoint = frozenCell!.TranslatePoint(new Point(22, frozenCell!.Bounds.Height / 2), this)!.Value;
                this.MouseMove(frozenPoint, RawInputModifiers.None);
                Require(tip.IsVisible, "A clipped frozen-row cell did not show the tooltip.");
                File.WriteAllText(Path.Combine(output, "cell-tip-passed.json"), "{\"passed\":true}");
                closingApproved = true; (Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)!.Shutdown(0); return;
            }
            if (Program.Arguments.Contains("--catalog-ids-only"))
            {
                var file = Path.Combine(root, "source/strings/duplicate-ids.json");
                Require(diagnostics.Count(d => d.Severity == "Warning" && d.Field == "id" && d.File == Path.GetFullPath(file).Replace('/', Path.DirectorySeparatorChar)) == 2, "Problems did not list both duplicate IDs when the project opened.");
                var pane = await OpenDocumentAsync(file);
                Require(pane != null && diagnostics.Count(d => d.Severity == "Warning" && d.Field == "id" && d.File == Path.GetFullPath(file).Replace('/', Path.DirectorySeparatorChar)) == 2, "Opening the catalog duplicated or removed its ID problems.");
                var corrected = (JsonObject)JsonNode.Parse(pane!.Document.Text)!;
                corrected["records"]![1]!["id"] = 2;
                pane.Document.SetRaw(corrected.ToJsonString()); pane.Document.ApplySource(); RefreshStatus();
                Require(diagnostics.All(d => d.Field != "id"), "Correcting an ID in Source view did not clear its problems.");
                pane.Document.Save(); await RefreshCatalogIdWarningsAsync(project!);
                Require(catalogIdWarnings.All(d => d.Field != "id") && diagnostics.All(d => d.Field != "id"), "Saving the corrected ID did not clear project-wide problems.");
                File.WriteAllText(Path.Combine(output, "catalog-ids-passed.json"), "{\"passed\":true}");
                closingApproved = true; (Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)!.Shutdown(0); return;
            }
            if (Program.Arguments.Contains("--external-editor-only"))
            {
                await SmokeExternalEditorAsync(output);
                closingApproved = true; (Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)!.Shutdown(0); return;
            }
            if (Program.Arguments.Contains("--cell-references-only"))
            {
                await SmokeCellReferencesAsync(output);
                File.WriteAllText(System.IO.Path.Combine(output, "cell-references-passed.json"), "{\"passed\":true}");
                closingApproved = true; (Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)!.Shutdown(0); return;
            }
            if (Program.Arguments.Contains("--skilldesc-classes-only"))
            {
                await SmokeSkillClassFilterAsync();
                File.WriteAllText(System.IO.Path.Combine(output, "skilldesc-classes-passed.json"), "{\"passed\":true}");
                closingApproved = true; (Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)!.Shutdown(0); return;
            }
            if (Program.Arguments.Contains("--row-copy-only"))
            {
                var pane = await OpenDocumentAsync(System.IO.Path.Combine(root, "source/tables/sounds.json"));
                Require(pane != null && Clipboard != null, "Row-copy smoke needs a table and clipboard.");
                await SmokeRowCopyAsync(pane!, Clipboard!);
                File.WriteAllText(System.IO.Path.Combine(output, "row-copy-passed.json"), "{\"passed\":true}");
                closingApproved = true; (Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)!.Shutdown(0); return;
            }
            if (Program.Arguments.Contains("--full-row-paste-only"))
            {
                var pane = await OpenDocumentAsync(System.IO.Path.Combine(root, "source/tables/hireling.json"));
                Require(pane != null && Clipboard != null, "Hireling paste smoke needs a table and clipboard.");
                await SmokeFullRowIntoAddedCellAsync(pane!);
                File.WriteAllText(System.IO.Path.Combine(output, "full-row-paste-passed.json"), "{\"passed\":true}");
                closingApproved = true; (Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)!.Shutdown(0); return;
            }
            if (Program.Arguments.Contains("--level-builder-only"))
            {
                await SmokeLevelBuilderAsync(output);
                File.WriteAllText(System.IO.Path.Combine(output, "level-builder-passed.json"), "{\"passed\":true}");
                closingApproved = true; (Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)!.Shutdown(0); return;
            }
            if (Program.Arguments.Contains("--visual-builder-only"))
            {
                await SmokeVisualBuilderAsync(output);
            await SmokeMissileBuilderAsync(output);
            await SmokeMonsterBuilderAsync(output);
            await SmokeBaseItemBuilderAsync(output);
            await SmokeCubeBuilderAsync(output);
            await SmokeRunewordBuilderAsync(output);
            await SmokeLevelBuilderAsync(output);
            await SmokeRememberedViewsAsync();
                File.WriteAllText(System.IO.Path.Combine(output, "visual-builder-passed.json"), "{\"passed\":true}");
                closingApproved = true; (Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)!.Shutdown(0); return;
            }
            if (Program.Arguments.Contains("--row-insert-only"))
            {
                var pane = await OpenDocumentAsync(System.IO.Path.Combine(root, "source/tables/sounds.json"));
                Require(pane != null, "Row-insert smoke needs a table.");
                await SmokeRowInsertAsync(pane!);
                File.WriteAllText(System.IO.Path.Combine(output, "row-insert-passed.json"), "{\"passed\":true}");
                closingApproved = true; (Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)!.Shutdown(0); return;
            }
            await SmokePreviewsAsync(output, Program.Arguments.Skip(index + 3));
            await SmokeItemPreviewsAsync(output);
            await SmokeVisualBuilderAsync(output);
            await SmokeMissileBuilderAsync(output);
            await SmokeMonsterBuilderAsync(output);
            await SmokeBaseItemBuilderAsync(output);
            await SmokeCubeBuilderAsync(output);
            await SmokeRunewordBuilderAsync(output);
            await SmokeLevelBuilderAsync(output);
            await SmokeRememberedViewsAsync();
            await SmokeSkillPreviewAsync(output);
            await SmokeMissilePreviewAsync(output);
            await SmokeStatPreviewAsync(output);
            await SmokeDropAndMonsterPreviewAsync(output);
            await SmokeAffixAndRecipePreviewAsync(output);
            await SmokeCellReferencesAsync(output);
            await SmokeFindInFilesAsync(output);
            await SmokeLayoutAsync(output);
            await SmokeReorderAsync(root);
            await SmokeCatalogRowsAsync();
            await SmokeEditingAidsAsync(root, output);
            await SmokeGitAsync(root, output);
            var detectedGame = System.IO.Path.GetFullPath(System.IO.Path.Combine(output, "game-installation")); Directory.CreateDirectory(detectedGame);
            File.WriteAllText(System.IO.Path.Combine(detectedGame, "D2R.exe"), "fixture"); File.WriteAllText(System.IO.Path.Combine(detectedGame, "D2RLoader.exe"), "fixture");
            new RunSettings(GameDirectory: detectedGame).Save(project!, Profile); RefreshLaunchTargets();
            Require(LaunchTargetPicker.ItemCount == 2 && LaunchTargetPicker.SelectedItem as string == "D2R.exe", "Launch picker did not detect vanilla and loader.");
            LaunchTargetPicker.SelectedItem = "D2RLoader.exe";
            Require(RunSettings.Load(project!, Profile).LaunchTarget == "D2RLoader.exe", "Launch picker did not persist selected target.");
            var settingsTask = ShowSettingsAsync(); await Task.Delay(150);
            var settingsDialog = (Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)!.Windows.Single(w => w.Title?.StartsWith("Run settings") == true);
            Require(settingsDialog.GetVisualDescendants().OfType<TextBox>().Any(t => t.Text == detectedGame), "Run settings did not show the installation folder.");
            using (var settingsImage = new RenderTargetBitmap(new PixelSize((int)settingsDialog.Bounds.Width, (int)settingsDialog.Bounds.Height), new Vector(96,96))) { settingsImage.Render(settingsDialog); settingsImage.Save(System.IO.Path.Combine(output, "game-folder-settings.png"), PngBitmapEncoderOptions.Default); }
            settingsDialog.Close(); await settingsTask;
            RefreshRunControls(); Require(!StopButton.IsVisible, "Idle Stop button is visible.");
            operation = new(); RefreshRunControls(); Require(StopButton.IsVisible, "Cancelable work has no Stop button."); operation.Dispose(); operation = null; RefreshRunControls();
            var unfilteredEntries = ProjectTree.ItemsSource;
            ExplorerSearch.Text = "SOUNDS";
            await SettleExplorerSearchAsync(() => Flatten((IEnumerable<ProjectEntry>)ProjectTree.ItemsSource!).Count() == 3 &&
                Flatten((IEnumerable<ProjectEntry>)ProjectTree.ItemsSource!).Last().Name == "sounds.json", "file-name match");
            var filteredRoots = ((IEnumerable<ProjectEntry>)ProjectTree.ItemsSource!).ToArray();
            var filteredTable = filteredRoots.Single(e => e.Name == "source").Children.Single(e => e.Name == "tables").Children.Single();
            Require(filteredTable.Name == "sounds.json" && filteredTable.IsTable && !filteredTable.Directory, "Explorer search did not preserve table metadata or exclude unrelated files.");
            Require(ProjectTree.GetVisualDescendants().OfType<TreeViewItem>().Any(item => item.DataContext is ProjectEntry { Name: "sounds.json" }), "Search ancestors were not expanded to reveal the matching file.");
            ExplorerSearch.Text = "source\\tables\\cube";
            await SettleExplorerSearchAsync(() => Flatten((IEnumerable<ProjectEntry>)ProjectTree.ItemsSource!).Count() == 3 &&
                Flatten((IEnumerable<ProjectEntry>)ProjectTree.ItemsSource!).Last().Name == "cubemain.json", "relative-path match");
            Require(((IEnumerable<ProjectEntry>)ProjectTree.ItemsSource!).Single().Children.Single().Children.Single().Name == "cubemain.json", "Explorer relative-path search failed.");
            ExplorerSearch.Text = "missing-file-xyz";
            await SettleExplorerSearchAsync(() => ExplorerSearchStatus.IsVisible && !((IEnumerable<ProjectEntry>)ProjectTree.ItemsSource!).Any(), "empty state");
            Require(ExplorerSearchStatus.IsVisible && !((IEnumerable<ProjectEntry>)ProjectTree.ItemsSource!).Any(), "Explorer search did not display the empty state.");
            ClearExplorerSearch.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await SettleExplorerSearchAsync(() => ReferenceEquals(unfilteredEntries, ProjectTree.ItemsSource) && !ExplorerSearchStatus.IsVisible && !ClearExplorerSearch.IsVisible, "cleared search");
            Require(ReferenceEquals(unfilteredEntries, ProjectTree.ItemsSource) && !ExplorerSearchStatus.IsVisible && !ClearExplorerSearch.IsVisible, "Clearing search did not restore the original tree.");
            // Explorer refresh keeps expansion state, reveals a newly created file, and drops deleted entries.
            {
                var newFolder = System.IO.Path.Combine(root, "compatibility", "smoke-notes"); var newFile = System.IO.Path.Combine(newFolder, "readme.txt");
                Directory.CreateDirectory(newFolder); File.WriteAllText(newFile, "smoke");
                Flatten(explorerEntries).Single(p => p.Name == "source").IsExpanded = true;
                await RefreshExplorerAsync(newFile);
                Require(ProjectTree.SelectedItem is ProjectEntry { Name: "readme.txt" }, "Refresh did not select the created file.");
                Require(Flatten(explorerEntries).Single(p => p.Name == "compatibility").IsExpanded && Flatten(explorerEntries).Single(p => p.Name == "smoke-notes").IsExpanded, "Reveal did not expand the ancestors of the created file.");
                Require(Flatten(explorerEntries).Single(p => p.Name == "source").IsExpanded, "Refresh lost the expansion state of an unrelated folder.");
                Directory.Delete(newFolder, true); await RefreshExplorerAsync();
                Require(!Flatten(explorerEntries).Any(p => p.Name == "smoke-notes"), "Refresh kept a deleted folder.");
                // A guide-based table opens as an empty, editable grid and shows up as a table file.
                var gemsRecords = TableData.Create(project!, TableData.FromGuide(ColumnGuide.File("gems")!, "gems"));
                await RefreshExplorerAsync(gemsRecords); await OpenDocumentAsync(gemsRecords); await Task.Delay(200);
                Require(ProjectTree.SelectedItem is ProjectEntry { Name: "gems.json", IsTable: true } && Active is { } gemsPane && gemsPane.Document.Table is { } gemsTable && gemsTable.Records.Count == 0 && gemsTable.Columns.Length > 10, "New guide table did not open as an empty table.");
                Require(tabs.OfType<TabItem>().Any(t => t.Header is StackPanel h && h.Children.OfType<TextBlock>().Any(x => x.Text?.StartsWith("gems") == true && !x.Text.Contains(".json"))), "Table tab title should be the table name.");
                await CloseTabAsync((TabItem)Documents.SelectedItem!); File.Delete(gemsRecords); await RefreshExplorerAsync();
                Flatten(explorerEntries).Single(p => p.Name == "source").IsExpanded = false; FilterExplorer(); await Task.Delay(100);
                var menu = EntryMenu(Flatten(explorerEntries).Single(p => p.Name == "skills.json"));
                Require(menu.OfType<MenuItem>().Select(m => m.Header?.ToString()).Intersect(["Open Table", "New", "Rename…", "Delete…", "Copy Path", "Copy Relative Path", "Open File Location"]).Count() == 7, "Table context menu is missing actions.");
            }
            var projectEntries = (IEnumerable<ProjectEntry>)ProjectTree.ItemsSource!;
            var sourceEntry = projectEntries.Single(p => p.Name == "source"); var tableEntries = sourceEntry.Children.Single(p => p.Name == "tables").Children;
            Require(tableEntries.Count(p => p.IsTable) >= 3 && tableEntries.Where(p => p.IsTable).All(p => !p.Directory && p.Name.EndsWith(".json")), "Table files are not marked as tables.");
            await Task.Delay(100);
            var folder = ProjectTree.GetVisualDescendants().OfType<TreeViewItem>().First(t => t.DataContext is ProjectEntry p && p.Name == "source");
            var toggle = folder.GetVisualDescendants().OfType<ToggleButton>().First();
            Require(toggle.Bounds.Width >= 18 && toggle.Bounds.Height >= 18, $"Folder expander target is too small: {toggle.Bounds}.");
            var click = toggle.TranslatePoint(new Point(3, toggle.Bounds.Height / 2), this)!.Value;
            this.MouseDown(click, MouseButton.Left, RawInputModifiers.None); this.MouseUp(click, MouseButton.Left, RawInputModifiers.None);
            Require(folder.IsExpanded, "Clicking the expanded chevron hit area failed.");
            await Task.Delay(80);
            var tablesFolder = ProjectTree.GetVisualDescendants().OfType<TreeViewItem>().First(t => t.DataContext is ProjectEntry p && p.Name == "tables"); tablesFolder.IsExpanded = true;
            var previewA = System.IO.Path.Combine(root, "source/tables/sounds.json");
            var previewB = System.IO.Path.Combine(root, "source/tables/skills.json");
            var loadingPreview = OpenDocumentAsync(previewA, true);
            Require(previewTab?.Content is StackPanel loadingPanel && loadingPanel.Children.OfType<ProgressBar>().Any(), "Loading tab did not appear immediately.");
            await loadingPreview;
            var firstPreview = previewTab;
            Require(firstPreview?.Header is StackPanel previewHeader && previewHeader.Children.OfType<TextBlock>().Any(header => header.FontStyle == FontStyle.Italic && header.Text!.Contains("preview")), "Preview tab has no visual distinction.");
            await OpenDocumentAsync(previewB, true);
            Require(!tabs.Contains(firstPreview!) && tabs.Count == 1, "Preview was not replaced.");
            await OpenDocumentAsync(previewB);
            Require(previewTab == null && tabs.Count == 1, "Opening permanently did not keep the preview.");
            var editablePreview = await OpenDocumentAsync(previewA, true);
            editablePreview!.Document.SetRaw(editablePreview.Document.Text + " ");
            Require(previewTab == null && tabs.Count == 2, "Editing did not keep the preview open.");
            editablePreview.Document.Undo();
            tabs.Clear(); previewTab = null;
            await Task.WhenAll(OpenDocumentAsync(previewA, true), OpenDocumentAsync(previewA));
            Require(tabs.Count == 1 && previewTab == null, "Rapid preview and permanent open duplicated or replaced a kept tab.");
            await Task.Delay(100);
            var closeButton = ((StackPanel)tabs[0].Header!).Children.OfType<Button>().Single();
            var closePoint = closeButton.TranslatePoint(new Point(12, 12), this)!.Value;
            this.MouseDown(closePoint, MouseButton.Left, RawInputModifiers.None); this.MouseUp(closePoint, MouseButton.Left, RawInputModifiers.None);
            await Task.Delay(100);
            Require(tabs.Count == 0, "Tab close button did not close the file.");
            await OpenDocumentAsync(previewA, true);
            await CloseTabAsync(previewTab!);
            Require(tabs.Count == 0 && previewTab == null, "Closing a preview left stale preview state.");
            await OpenDocumentAsync(previewA);
            var middleTarget = tabs[0];
            await OpenDocumentAsync(previewB); await Task.Delay(100);
            var middlePoint = middleTarget.TranslatePoint(new Point(12, middleTarget.Bounds.Height / 2), this)!.Value;
            this.MouseDown(middlePoint, MouseButton.Middle, RawInputModifiers.None); this.MouseUp(middlePoint, MouseButton.Middle, RawInputModifiers.None);
            await Task.Delay(100);
            Require(!tabs.Contains(middleTarget) && tabs.Count == 1, "Middle-click did not close the targeted background tab.");
            await CloseTabAsync(tabs[0]);
            var abandonedLoad = OpenDocumentAsync(previewA, true);
            await CloseTabAsync(previewTab!);
            await abandonedLoad;
            Require(tabs.Count == 0, "A closed loading tab reopened after loading.");
            await OpenDocumentAsync(previewA);
            await OpenDocumentAsync(previewB, true);
            Documents.SelectedItem = tabs[0];
            SaveOpenFiles(); tabs.Clear(); previewTab = null;
            await RestoreOpenFilesAsync();
            Require(tabs.Select(TabFile).SequenceEqual(new[] { System.IO.Path.GetFullPath(previewA), System.IO.Path.GetFullPath(previewB) }) && Documents.SelectedItem == tabs[0] && previewTab == tabs[1], "Session restore lost tab order, selected file, or preview state.");
            await CloseTabAsync(tabs[1]); SaveOpenFiles(); tabs.Clear(); previewTab = null;
            await RestoreOpenFilesAsync();
            Require(tabs.Count == 1 && TabFile(tabs[0]) == System.IO.Path.GetFullPath(previewA), "Session restore reopened a closed file.");
            var missingSessionFile = System.IO.Path.Combine(output, "removed-session-file.txt");
            File.WriteAllText(missingSessionFile, "temporary");
            await OpenDocumentAsync(missingSessionFile); SaveOpenFiles();
            tabs.Clear(); previewTab = null; File.Delete(missingSessionFile);
            await RestoreOpenFilesAsync();
            Require(tabs.Count == 1 && TabFile(tabs[0]) == System.IO.Path.GetFullPath(previewA), "A missing session file prevented remaining files from reopening.");
            await CloseTabAsync(tabs[0]); SaveOpenFiles(); await RestoreOpenFilesAsync();
            Require(tabs.Count == 0, "An empty session reopened old files.");
            tabs.Clear(); previewTab = null;
            foreach (var name in new[] { "sounds", "cubemain", "skills" })
            {
                var timer = Stopwatch.StartNew(); var pane = await OpenDocumentAsync(System.IO.Path.Combine(root, "source/tables", name + ".json"));
                Require(pane != null, "No table pane."); pane!.Jump(Math.Min(200, pane.Document.Table!.Records.Count - 1));
                await Task.Delay(300);
                var realizedRows = pane.TableGrid.GetVisualDescendants().OfType<DataGridRow>().Count(r => r.IsVisible);
                Require(realizedRows < 80, "Grid realized too many rows.");
                var field = pane.Document.Table.Columns[0]; var before = pane.Document.Table.Cell(0, field);
                var rowView = new RowView(pane.Document, 0, e => throw e); rowView[0] = before + " smoke";
                Require(pane.Document.Table.Cell(0, field) == before + " smoke", "Grid setter did not update document.");
                pane.Document.Undo(); Require(pane.Document.Table.Cell(0, field) == before, "Undo did not restore grid edit.");
                pane.Document.SetRaw("invalid JSON"); pane.Document.ApplySource(); Require(pane.Document.PendingSource, "Invalid source was not preserved."); pane.Document.Undo();
                pane.Jump(0, field); await Task.Delay(40);
                if (Clipboard is { } clipboard)
                {
                    await clipboard.SetTextAsync(before + " pasted"); await pane.PasteAsync();
                    Require(pane.Document.Table!.Cell(0, field) == before + " pasted", "Clipboard paste did not update selected cell.");
                    await pane.CopyAsync(); Require(await clipboard.TryGetTextAsync() == before + " pasted", "Ctrl+C on a single cell did not copy just that cell.");
                    await pane.CopyAsync(false); Require((await clipboard.TryGetTextAsync())?.StartsWith(before + " pasted" + (pane.Document.Table.Columns.Length > 1 ? "\t" : "")) == true, "Clipboard row copy lost selected row.");
                    pane.Document.Undo(); pane.Refresh();
                    if (name == "sounds") { await SmokeRowCopyAsync(pane, clipboard); await SmokeRowInsertAsync(pane); }
                }
                await pane.SortAsync(field); Require(pane.Document.Table!.Cell(0, field) == before, "View sorting mutated source order.");
                Require(pane.TableGrid.Columns.Any(c => c.Header?.ToString()?.EndsWith("▲") == true), "Ascending sort indicator missing.");
                await pane.SortAsync(field); Require(pane.SortDescending && pane.TableGrid.Columns.Any(c => c.Header?.ToString()?.EndsWith("▼") == true), "Descending sort indicator missing.");
                pane.Jump(200, field); pane.ToggleFrozenRows();
                Require(pane.FrozenRows.Contains(200) && pane.FrozenGrid.IsVisible && !((IEnumerable<RowView>)pane.TableGrid.ItemsSource!).Any(r => r.Row == 200), "Frozen row is not separated from scrolling rows.");
                pane.ToggleFrozenColumn(pane.Document.Table.Columns[1]);
                Require(pane.TableGrid.FrozenColumnCount == 2, "Column freeze did not update the grid.");
                // Freezing rebuilds every column control; none may come back read-only unless its column is locked.
                Require(pane.TableGrid.Columns.Where(c => pane.ColumnIndexOf(c) >= 0).All(c => !c.IsReadOnly), "Rebuilt columns refuse editing until the next refresh.");
                pane.Jump(400, field); await Task.Delay(100);
                var horizontal = pane.TableGrid.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Name == "PART_HorizontalScrollbar");
                Require(horizontal.Bounds.Height >= 22 && !horizontal.AllowAutoHide, "Horizontal scrollbar is too small or hides.");
                Require(horizontal.TranslatePoint(new Point(), pane.TableGrid)!.Value.X < 3 && horizontal.Bounds.Width > pane.TableGrid.Bounds.Width - 25, "Scrollbar is offset by frozen columns.");
                horizontal.Value = horizontal.Maximum / 2; await Task.Delay(50);
                var frozenHorizontal = pane.FrozenGrid.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Name == "PART_HorizontalScrollbar");
                Require(Math.Abs(horizontal.Value - frozenHorizontal.Value) < 1, $"Frozen rows lost horizontal alignment: {horizontal.Value}/{frozenHorizontal.Value}; max {horizontal.Maximum}/{frozenHorizontal.Maximum}.");
                pane.Jump(200, field); pane.ToggleRowLocks();
                Require(pane.Document.LockedRows.Contains(200) && pane.Source.IsReadOnly, "Row lock does not protect Source.");
                pane.ToggleRowLocks(); pane.ToggleFrozenRows(); pane.ToggleFrozenColumn(pane.Document.Table.Columns[1]);
                pane.Jump(200, name == "skills" ? pane.Document.Table!.Columns[^1] : field);
                DataGridCell? LiveCell(int row, DataGridColumn column)
                {
                    var container = pane.TableGrid.GetVisualDescendants().OfType<DataGridRow>().FirstOrDefault(r => r.IsVisible && r.DataContext is RowView rv && rv.Row == row); if (container == null) return null;
                    var cell = column.GetCellContent(container)?.GetVisualAncestors().OfType<DataGridCell>().FirstOrDefault();
                    // Realized cells may be clipped or covered by a frozen column. Check the actual hit target.
                    if (cell == null || cell.TranslatePoint(new Point(cell.Bounds.Width / 2, 15), pane.TableGrid) is not { } center ||
                        pane.TableGrid.InputHitTest(center) is not Visual hitVisual || !hitVisual.GetSelfAndVisualAncestors().Contains(cell)) return null;
                    return cell.TranslatePoint(new Point(cell.Bounds.Width, 0), pane.TableGrid) is { X: var right } && right <= pane.TableGrid.Bounds.Width - 20 && cell.TranslatePoint(new Point(), pane.TableGrid) is { X: >= 0 } ? cell : null;
                }
                var fieldColumn = pane.TableGrid.Columns.Single(c => pane.ColumnIndexOf(c) == pane.Document.Table.ColumnIndex(field));
                async Task<int> ClickableRowAsync(int excludedRow)
                {
                    // Jump restores scrolling at Background priority, and the renderer updates hit testing
                    // separately. Wait for a cell that is actually clickable before measuring mouse coordinates.
                    var timeout = Stopwatch.StartNew();
                    while (timeout.Elapsed < TimeSpan.FromSeconds(10))
                    {
                        await Dispatcher.UIThread.InvokeAsync(() => UpdateLayout(), DispatcherPriority.Background);
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                        var row = pane.TableGrid.GetVisualDescendants().OfType<DataGridRow>().Select(r => r.DataContext).OfType<RowView>()
                            .FirstOrDefault(rv => !rv.IsPlaceholder && rv.Row != excludedRow && rv.Row != 200 && LiveCell(rv.Row, fieldColumn)?.TranslatePoint(new Point(0, 15), pane.TableGrid) is { Y: > 50 } point && point.Y < pane.TableGrid.Bounds.Height - 40);
                        if (row != null) return row.Row;
                        await Task.Delay(10);
                    }
                    var targets = pane.TableGrid.GetVisualDescendants().OfType<DataGridRow>().Select(r =>
                    {
                        var cell = fieldColumn.GetCellContent(r)?.GetVisualAncestors().OfType<DataGridCell>().FirstOrDefault();
                        var point = cell?.TranslatePoint(new Point(cell.Bounds.Width / 2, 15), pane.TableGrid);
                        var hit = point is { } p ? pane.TableGrid.InputHitTest(p) as Visual : null;
                        var hitRow = hit?.GetSelfAndVisualAncestors().OfType<DataGridRow>().FirstOrDefault()?.DataContext as RowView;
                        return $"row {(r.DataContext as RowView)?.Row}: {point}, hit {hit?.GetType().Name}/row {hitRow?.Row}";
                    });
                    throw new TimeoutException($"No clickable {name}/{field} cell: {string.Join("; ", targets)}.");
                }
                var clickedRow = await ClickableRowAsync(200);
                var clickableCell = LiveCell(clickedRow, fieldColumn)!;
                var cellPoint = clickableCell.TranslatePoint(new Point(clickableCell.Bounds.Width / 2, 15), this)!.Value;
                this.MouseDown(cellPoint, MouseButton.Left, RawInputModifiers.None); this.MouseUp(cellPoint, MouseButton.Left, RawInputModifiers.None); await Task.Delay(60);
                // Row containers can be recycled when selecting scrolls the grid. Keep the record identity
                // from before the click, and re-query visuals afterwards rather than reading a reused row.
                Require(pane.SelectedRow == clickedRow && pane.SelectedColumn == field, $"Mouse cell selection failed in {name}: expected {clickedRow}/{field}, selected {pane.SelectedRow}/{pane.SelectedColumn}.");
                // Spreadsheet-style cell selection: click, Ctrl+click, drag, bulk edit, and the blank row at the bottom.
                var cols = pane.Document.Table.Columns; int fieldIndex = Array.IndexOf(cols, field);
                Require(pane.SelectedCells.Count == 1 && pane.SelectedCells.Contains((clickedRow, fieldIndex)), $"Click did not select exactly one cell: [{string.Join(" ", pane.SelectedCells)}] expected ({clickedRow}, {fieldIndex}).");
                await Task.Delay(80);
                Require(LiveCell(clickedRow, fieldColumn)?.Background is SolidColorBrush { Color: var paint } && paint == Color.Parse("#4A4123"), "Selected cell is not painted.");
                var clickableRow = pane.TableGrid.GetVisualDescendants().OfType<DataGridRow>().Single(r => r.IsVisible && r.DataContext is RowView rv && rv.Row == clickedRow);
                var rowRectangle = clickableRow.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Rectangle>().FirstOrDefault(r => r.Name == "BackgroundRectangle");
                Require(rowRectangle != null && rowRectangle.Opacity == 0, "Row-wide selection highlight still hides which cells are selected.");
                var secondRowIndex = await ClickableRowAsync(clickedRow);
                var secondCell = LiveCell(secondRowIndex, fieldColumn)!;
                var secondPoint = secondCell.TranslatePoint(new Point(secondCell.Bounds.Width / 2, 15), this)!.Value;
                this.MouseDown(secondPoint, MouseButton.Left, RawInputModifiers.Control); this.MouseUp(secondPoint, MouseButton.Left, RawInputModifiers.Control); await Task.Delay(60);
                Require(pane.SelectedCells.Count == 2 && pane.SelectedCells.Contains((secondRowIndex, fieldIndex)) && pane.SelectedCells.Contains((clickedRow, fieldIndex)), "Ctrl+click did not add a second cell.");
                // Drag across the two right-most columns: after the Jump they are scrolled into view, unlike the columns just right of the frozen one.
                await Task.Delay(600); // outside the double-click window, so the press starts a drag rather than an edit; layout may have scrolled, so re-measure
                // Columns are virtualized: drag across two neighbouring columns that are realized and fully visible for both rows.
                var visibleColumns = pane.TableGrid.Columns.Skip(pane.TableGrid.FrozenColumnCount).Where(c => LiveCell(clickedRow, c) != null && LiveCell(secondRowIndex, c) != null).ToArray();
                Require(visibleColumns.Length >= 2, "Fewer than two scrolling columns are visible for the drag test.");
                var startColumn = visibleColumns[^2]; var nextColumn = visibleColumns[^1]; int startIndex = pane.ColumnIndexOf(startColumn), nextIndex = pane.ColumnIndexOf(nextColumn);
                var dragStart = LiveCell(clickedRow, startColumn)!; var dragTarget = LiveCell(secondRowIndex, nextColumn)!;
                var dragFrom = dragStart.TranslatePoint(new Point(dragStart.Bounds.Width / 2, 15), this)!.Value;
                var dragPoint = dragTarget.TranslatePoint(new Point(dragTarget.Bounds.Width / 2, 15), this)!.Value;
                this.MouseDown(dragFrom, MouseButton.Left, RawInputModifiers.None); this.MouseMove(dragPoint, RawInputModifiers.LeftMouseButton); this.MouseUp(dragPoint, MouseButton.Left, RawInputModifiers.None); await Task.Delay(60);
                Require(pane.SelectedCells.Count == 4 && pane.SelectedCells.Contains((secondRowIndex, nextIndex)) && pane.SelectedCells.Contains((clickedRow, startIndex)), $"Drag did not select a 2×2 block in {name}: {pane.SelectedCells.Count} cells, rows {string.Join(",", pane.SelectedCells.Select(c => c.Row).Distinct().Order())}, cols {string.Join(",", pane.SelectedCells.Select(c => c.Col).Distinct().Order())}; clicked {clickedRow}, second {secondRowIndex}, field {fieldIndex}, next {nextIndex}.");
                if (Clipboard is { } cellClipboard)
                {
                    await pane.CopyAsync(); var block = await cellClipboard.TryGetTextAsync();
                    Require(block != null && block.Count(ch => ch == '\n') == 1 && block.Count(ch => ch == '\t') == 2, "Copying a block did not produce tab-separated rows.");
                }
                // Typing starts editing the current cell without a second click; Enter writes the value into every selected cell.
                pane.TableGrid.Focus(); this.KeyTextInput("7"); await Task.Delay(120);
                var typedEditor = pane.TableGrid.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.IsVisible && t.Text == "7");
                Require(typedEditor != null && typedEditor.CaretIndex == 1, "Typing into a selected cell did not start editing with the typed text.");
                string DisplayedCell(int row, int col) => ((IEnumerable<RowView>)pane.TableGrid.ItemsSource!).Single(r => r.Row == row)[col];
                Require(pane.SelectedCells.All(c => DisplayedCell(c.Row, c.Col) == "7"), "Typing did not update the entire drag selection before commit.");
                Require(pane.TableGrid.GetVisualDescendants().OfType<TextBox>().Count(t => t.IsVisible && t.Text == "7") == 4, "Every selected cell must visibly show an editor with the live value.");
                if (name == "sounds")
                {
                    using var liveBitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96));
                    liveBitmap.Render(this); liveBitmap.Save(System.IO.Path.Combine(output, "live-multi-cell-edit.png"), PngBitmapEncoderOptions.Default);
                }
                this.KeyTextInput("8"); await Task.Delay(60);
                Require(pane.SelectedCells.All(c => DisplayedCell(c.Row, c.Col) == "78"), "Further typing did not update the entire selection live.");
                this.KeyPress(Key.Back, RawInputModifiers.None, PhysicalKey.Backspace, null); await Task.Delay(60);
                Require(pane.SelectedCells.All(c => DisplayedCell(c.Row, c.Col) == "7"), "Backspace did not update the entire selection live.");
                var canceledTargets = pane.SelectedCells.ToArray(); var canceledRevision = pane.Document.Revision;
                this.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); await Task.Delay(100);
                Require(pane.Document.Revision == canceledRevision && canceledTargets.All(c => DisplayedCell(c.Row, c.Col) == pane.Document.Table.Cell(c.Row, cols[c.Col])), "Escape failed to restore the original values without adding an undo step.");
                // A single cell typed character by character is one undo step: undo restores the value from before editing, not one letter.
                {
                pane.SelectCell(clickedRow, startIndex); pane.TableGrid.Focus(); var singleBefore = pane.Document.Table.Cell(clickedRow, cols[startIndex]);
                this.KeyTextInput("a"); await Task.Delay(120); this.KeyTextInput("b"); await Task.Delay(30); this.KeyTextInput("c"); await Task.Delay(30);
                this.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); await Task.Delay(120);
                Require(pane.Document.Table.Cell(clickedRow, cols[startIndex]) == "abc", "Typing several characters into one cell did not commit the whole value.");
                pane.Document.Undo(); pane.RefreshRowValues(clickedRow); await Task.Delay(50);
                Require(pane.Document.Table.Cell(clickedRow, cols[startIndex]) == singleBefore, $"Undo removed a single character instead of the whole typed value (now '{pane.Document.Table.Cell(clickedRow, cols[startIndex])}').");
                }
                // Arrow keys never leave a cell while it is being edited: Left/Right move the caret, Up/Down go to the start/end of the text; the status line counts the characters.
                {
                pane.SelectCell(clickedRow, startIndex); pane.TableGrid.Focus(); var arrowBefore = pane.Document.Table.Cell(clickedRow, cols[startIndex]);
                this.KeyTextInput("("); await Task.Delay(120); this.KeyTextInput("b"); await Task.Delay(30);
                var arrowEditor = pane.TableGrid.GetVisualDescendants().OfType<TextBox>().Single(t => t.IsVisible && t.Text == "(b");
                Require(pane.StatusText.StartsWith($"Editing {cols[startIndex]} · 2 / 255 chars") && pane.StatusText.Contains("never closed"), $"The status line did not follow the edited cell: '{pane.StatusText}'.");
                this.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null); await Task.Delay(30);
                Require(arrowEditor.CaretIndex == 1 && pane.SelectedCells.SequenceEqual([(clickedRow, startIndex)]), "Left arrow left the cell being edited.");
                this.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null); await Task.Delay(60);
                Require(arrowEditor.IsVisible && arrowEditor.CaretIndex == 2 && pane.SelectedRow == clickedRow && pane.SelectedCells.SequenceEqual([(clickedRow, startIndex)]),
                    $"Down arrow moved to another row instead of staying in the cell: editor visible {arrowEditor.IsVisible}, caret {arrowEditor.CaretIndex}, row {pane.SelectedRow} (expected {clickedRow}).");
                this.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null); await Task.Delay(60);
                Require(arrowEditor.CaretIndex == 0 && pane.SelectedRow == clickedRow, "Up arrow left the cell being edited.");
                this.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); await Task.Delay(100);
                Require(pane.Document.Table.Cell(clickedRow, cols[startIndex]) == arrowBefore && !pane.StatusText.StartsWith("Editing"), $"Escape did not restore the cell, or the status line kept the edit: cell '{pane.Document.Table.Cell(clickedRow, cols[startIndex])}' (expected '{arrowBefore}'), status '{pane.StatusText}'.");
                }
                pane.SelectCell(clickedRow, startIndex); pane.SelectCell(secondRowIndex, nextIndex, shift: true);
                pane.TableGrid.Focus(); this.KeyTextInput("7"); await Task.Delay(120);
                this.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); await Task.Delay(120);
                Require(pane.Document.Table.Cell(clickedRow, cols[startIndex]) == "7" && pane.Document.Table.Cell(secondRowIndex, cols[nextIndex]) == "7" && pane.Document.Table.Cell(clickedRow, cols[nextIndex]) == "7", "Committing a typed edit did not fill the other selected cells.");
                pane.Document.Undo(); pane.Refresh(); await Task.Delay(50);
                Require(pane.Document.Table.Cell(clickedRow, cols[startIndex]) != "7" && pane.Document.Table.Cell(secondRowIndex, cols[nextIndex]) != "7", "Undo did not revert the typed multi-cell edit.");
                // Double-clicking a cell inside the block opens it for editing without collapsing the selection; Enter fills the block.
                pane.Jump(secondRowIndex, cols[nextIndex]);
                // Loaded CI runners can take well over half a second to realize the scrolled-to rows; poll instead of trusting one delay.
                DataGridColumn[] visibleForDoubleClick = [];
                for (int attempt = 0; attempt < 20 && visibleForDoubleClick.Length < 2; attempt++)
                {
                    await Task.Delay(attempt == 0 ? 600 : 150); UpdateLayout();
                    visibleForDoubleClick = pane.TableGrid.Columns.Skip(pane.TableGrid.FrozenColumnCount).Where(c => LiveCell(clickedRow, c) != null && LiveCell(secondRowIndex, c) != null).ToArray();
                }
                Require(visibleForDoubleClick.Length >= 2, $"Fewer than two scrolling columns are visible for the double-click test (rows {clickedRow}/{secondRowIndex}, {pane.TableGrid.Columns.Count} columns, {pane.TableGrid.GetVisualDescendants().OfType<DataGridRow>().Count()} rows realized).");
                startColumn = visibleForDoubleClick[^2]; nextColumn = visibleForDoubleClick[^1]; startIndex = pane.ColumnIndexOf(startColumn); nextIndex = pane.ColumnIndexOf(nextColumn);
                pane.SelectCell(clickedRow, startIndex); pane.SelectCell(secondRowIndex, nextIndex, shift: true); await Task.Delay(60);
                var inside = Centre(LiveCell(secondRowIndex, nextColumn)); Require(pane.SelectedCells.Count == 4, "Block selection was lost before the double-click test.");
                this.MouseDown(inside, MouseButton.Left, RawInputModifiers.None); this.MouseUp(inside, MouseButton.Left, RawInputModifiers.None);
                this.MouseDown(inside, MouseButton.Left, RawInputModifiers.None); this.MouseUp(inside, MouseButton.Left, RawInputModifiers.None); await Task.Delay(120);
                var blockEditor = pane.TableGrid.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.IsVisible && !t.IsReadOnly);
                Require(pane.SelectedCells.Count == 4 && blockEditor != null, "Double-clicking inside the block collapsed the selection or did not open the editor.");
                blockEditor!.Focus(); blockEditor.SelectAll(); // headless input does not move keyboard focus on pointer clicks the way the desktop backends do
                this.KeyTextInput("5"); await Task.Delay(60); this.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); await Task.Delay(150);
                Require(pane.Document.Table.Cell(clickedRow, cols[startIndex]) == "5" && pane.Document.Table.Cell(clickedRow, cols[nextIndex]) == "5" && pane.Document.Table.Cell(secondRowIndex, cols[startIndex]) == "5", "Editing via double-click did not fill the other selected cells.");
                pane.Document.Undo(); pane.Refresh(); await Task.Delay(50);
                // Opening a cell inside a block and clicking away without typing is how a value gets read.
                // It must leave the rest of the block alone rather than copying the opened value across it.
                pane.SelectCell(clickedRow, startIndex); pane.SelectCell(secondRowIndex, nextIndex, shift: true); await Task.Delay(60);
                var untouched = pane.SelectedCells.ToDictionary(c => c, c => pane.Document.Table.Cell(c.Row, cols[c.Col]));
                Require(untouched.Count == 4 && untouched.Values.Distinct().Count() > 1, "The accidental-edit test needs a four-cell block whose values differ.");
                int quietRevision = pane.Document.Revision;
                await Task.Delay(600); // outside the double-click window of the previous clicks
                var reopen = Centre(LiveCell(secondRowIndex, nextColumn));
                this.MouseDown(reopen, MouseButton.Left, RawInputModifiers.None); this.MouseUp(reopen, MouseButton.Left, RawInputModifiers.None);
                this.MouseDown(reopen, MouseButton.Left, RawInputModifiers.None); this.MouseUp(reopen, MouseButton.Left, RawInputModifiers.None); await Task.Delay(120);
                Require(pane.TableGrid.GetVisualDescendants().OfType<TextBox>().Any(t => t.IsVisible && !t.IsReadOnly), "Double-click did not reopen an editor for the accidental-edit test.");
                await Task.Delay(600);
                var corner = Centre(LiveCell(clickedRow, startColumn));
                this.MouseDown(corner, MouseButton.Left, RawInputModifiers.None); this.MouseUp(corner, MouseButton.Left, RawInputModifiers.None); await Task.Delay(150);
                Require(pane.Document.Revision == quietRevision && untouched.All(p => pane.Document.Table.Cell(p.Key.Row, cols[p.Key.Col]) == p.Value),
                    $"Opening a cell in a block and clicking away rewrote the selection: [{string.Join(" ", untouched.Select(p => $"{p.Key}={pane.Document.Table.Cell(p.Key.Row, cols[p.Key.Col])}/was {p.Value}"))}].");
                // Ctrl+click two scattered cells, type, then commit by clicking elsewhere: both cells must take the value.
                pane.Jump(secondRowIndex, cols[startIndex]); await Task.Delay(600); // the second row is the lower neighbour; jumping to it keeps both on screen
                // Refresh rebuilt the column objects and scrolled; pick two columns that are on screen now.
                var visibleAfterJump = pane.TableGrid.Columns.Skip(pane.TableGrid.FrozenColumnCount).Where(c => LiveCell(clickedRow, c) != null && LiveCell(secondRowIndex, c) != null).ToArray();
                Require(visibleAfterJump.Length >= 2, $"Fewer than two scrolling columns are visible for the Ctrl+click typing test: rows realized {string.Join(",", pane.TableGrid.GetVisualDescendants().OfType<DataGridRow>().Select(r => (r.DataContext as RowView)?.Row))}; need {clickedRow},{secondRowIndex}; first-row visible {pane.TableGrid.Columns.Count(c => LiveCell(clickedRow, c) != null)}, second-row visible {pane.TableGrid.Columns.Count(c => LiveCell(secondRowIndex, c) != null)}.");
                startColumn = visibleAfterJump[^2]; nextColumn = visibleAfterJump[^1]; startIndex = pane.ColumnIndexOf(startColumn); nextIndex = pane.ColumnIndexOf(nextColumn);
                Point Centre(DataGridCell? c) => (c ?? throw new InvalidDataException("A cell for the Ctrl+click typing test is not on screen.")).TranslatePoint(new Point(c.Bounds.Width / 2, 15), this)!.Value;
                var a = Centre(LiveCell(clickedRow, startColumn)); this.MouseDown(a, MouseButton.Left, RawInputModifiers.None); this.MouseUp(a, MouseButton.Left, RawInputModifiers.None); await Task.Delay(60);
                var b = Centre(LiveCell(secondRowIndex, nextColumn)); this.MouseDown(b, MouseButton.Left, RawInputModifiers.Control); this.MouseUp(b, MouseButton.Left, RawInputModifiers.Control); await Task.Delay(60);
                Require(pane.SelectedCells.Count == 2, "Ctrl+click did not build a two-cell selection for the typing test.");
                this.KeyTextInput("9"); await Task.Delay(120);
                Require(pane.TableGrid.GetVisualDescendants().OfType<TextBox>().Any(t => t.IsVisible && t.Text == "9"), "Typing after Ctrl+click did not start editing.");
                Require(pane.SelectedCells.All(c => DisplayedCell(c.Row, c.Col) == "9") && DisplayedCell(secondRowIndex, startIndex) != "9", "Live typing did not update exactly the Ctrl+clicked cells.");
                Require(pane.TableGrid.GetVisualDescendants().OfType<TextBox>().Count(t => t.IsVisible && t.Text == "9") == 2, "Both Ctrl+clicked cells must visibly show the live editor value.");
                await Task.Delay(600);
                var away = Centre(LiveCell(secondRowIndex, startColumn)); this.MouseDown(away, MouseButton.Left, RawInputModifiers.None); this.MouseUp(away, MouseButton.Left, RawInputModifiers.None); await Task.Delay(150);
                Require(pane.Document.Table.Cell(clickedRow, cols[startIndex]) == "9" && pane.Document.Table.Cell(secondRowIndex, cols[nextIndex]) == "9" && pane.Document.Table.Cell(secondRowIndex, cols[startIndex]) != "9", "Committing by clicking away did not write the typed value into every Ctrl+clicked cell.");
                pane.Document.Undo(); pane.Refresh(); await Task.Delay(50);
                Require(pane.Document.Table.Cell(clickedRow, cols[startIndex]) != "9", "Undo did not revert the Ctrl+click multi-edit.");
                // Match the reported case: drag down one column, type 1, then click another cell.
                pane.Jump(secondRowIndex, cols[startIndex]); await Task.Delay(600);
                var verticalRows = pane.TableGrid.GetVisualDescendants().OfType<DataGridRow>()
                    .Where(r => r.IsVisible && r.DataContext is RowView { IsPlaceholder: false } && r.TranslatePoint(new Point(0, 15), pane.TableGrid) is { Y: > 50 } point && point.Y < pane.TableGrid.Bounds.Height - 40)
                    .OrderBy(r => r.TranslatePoint(new Point(), pane.TableGrid)!.Value.Y).Take(3).Select(r => ((RowView)r.DataContext!).Row).ToArray();
                var verticalColumn = pane.TableGrid.Columns.Skip(pane.TableGrid.FrozenColumnCount).First(c => verticalRows.All(r => LiveCell(r, c) != null));
                int verticalIndex = pane.ColumnIndexOf(verticalColumn);
                var verticalStart = Centre(LiveCell(verticalRows[0], verticalColumn)); var verticalEnd = Centre(LiveCell(verticalRows[^1], verticalColumn));
                this.MouseDown(verticalStart, MouseButton.Left, RawInputModifiers.None); this.MouseMove(verticalEnd, RawInputModifiers.LeftMouseButton); this.MouseUp(verticalEnd, MouseButton.Left, RawInputModifiers.None);
                pane.TableGrid.Focus(); this.KeyTextInput("1"); await Task.Delay(120);
                Require(pane.SelectedCells.Count == 3 && pane.TableGrid.GetVisualDescendants().OfType<TextBox>().Count(t => t.IsVisible && t.Text == "1") == 3, "A vertical selection did not visibly edit all three cells live.");
                var clickAwayColumn = pane.TableGrid.Columns.Skip(pane.TableGrid.FrozenColumnCount).FirstOrDefault(c => c != verticalColumn && LiveCell(verticalRows[^1], c) != null)
                    ?? throw new InvalidOperationException($"No click-away column beside column {verticalIndex}; clickable: {string.Join(",", pane.TableGrid.Columns.Where(c => LiveCell(verticalRows[^1], c) != null).Select(c => pane.ColumnIndexOf(c)))}.");
                var clickAway = Centre(LiveCell(verticalRows[^1], clickAwayColumn));
                this.MouseDown(clickAway, MouseButton.Left, RawInputModifiers.None); this.MouseUp(clickAway, MouseButton.Left, RawInputModifiers.None); await Task.Delay(150);
                Require(verticalRows.All(r => pane.Document.Table.Cell(r, cols[verticalIndex]) == "1" && DisplayedCell(r, verticalIndex) == "1"), $"Clicking away erased a typed value in the vertical selection: {string.Join(", ", verticalRows.Select(r => $"{r}: model='{pane.Document.Table.Cell(r, cols[verticalIndex])}', display='{DisplayedCell(r, verticalIndex)}'"))}; editors: {string.Join(",", pane.TableGrid.GetVisualDescendants().OfType<TextBox>().Where(t => t.IsVisible).Select(t => t.Text))}.");
                Require(verticalRows.All(r => LiveCell(r, verticalColumn)!.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsVisible && t.Text == "1")), "Committed vertical cells do not visibly retain the value after clicking away.");
                pane.Document.Undo(); pane.Refresh(); await Task.Delay(50);
                pane.SelectCell(clickedRow, startIndex); pane.SelectCell(secondRowIndex, nextIndex, control: true); pane.SelectCell(clickedRow, nextIndex, control: true); pane.SelectCell(secondRowIndex, startIndex, control: true);
                pane.ApplyToSelection("bulk", null);
                Require(pane.Document.Table.Cell(clickedRow, cols[startIndex]) == "bulk" && pane.Document.Table.Cell(secondRowIndex, cols[nextIndex]) == "bulk", "Bulk edit did not reach every selected cell.");
                pane.Document.Undo(); pane.Refresh(); await Task.Delay(50);
                Require(pane.Document.Table.Cell(clickedRow, cols[startIndex]) != "bulk" && pane.Document.Table.Cell(secondRowIndex, cols[nextIndex]) != "bulk", "Undo did not revert the bulk edit in one step.");
                var placeholder = ((IEnumerable<RowView>)pane.TableGrid.ItemsSource!).Last(); Require(placeholder.IsPlaceholder, "No blank row at the bottom of the table.");
                int countBefore = pane.Document.Table.Records.Count; placeholder[0] = "brand new";
                Require(!placeholder.IsPlaceholder && placeholder.Row == countBefore && pane.Document.Table.Records.Count == countBefore + 1 && pane.Document.Table.Cell(countBefore, cols[0]) == "brand new", "Typing into the blank row did not create a row.");
                pane.Refresh();
                Require(((IEnumerable<RowView>)pane.TableGrid.ItemsSource!).Count(r => r.IsPlaceholder) == 1 && ((IEnumerable<RowView>)pane.TableGrid.ItemsSource!).Last().IsPlaceholder, "A fresh blank row was not appended after the new row.");
                pane.InsertRows(1, 2);
                Require(pane.Document.Table.Records.Count == countBefore + 3 && pane.Document.Table.Cell(1, cols[0]) == "" && pane.SelectedRow == 1 && pane.Document.Diagnostics.Any(d => d.Severity == "Warning"), "Add rows above did not insert blank rows at the slot with the order advisory.");
                pane.DeleteSelectedRows();
                Require(pane.Document.Table.Records.Count == countBefore + 1 && pane.Document.Table.Cell(countBefore, cols[0]) == "brand new", "Deleting the added rows removed the wrong rows.");
                pane.Document.Undo(); pane.Document.Undo(); pane.Document.Undo(); pane.Document.Undo(); pane.Refresh();
                Require(pane.Document.Table.Records.Count == countBefore, "Undo did not restore the table after row insertion.");
                pane.Jump(clickedRow, field); await Task.Delay(60);
                InspectorTabs.SelectedIndex = 1; await SettleRowEditorAsync();
                Require(RowEditorFields.ItemCount == pane.Document.Table.Columns.Length, $"Row editor omitted columns outside the table window in {name}: {RowEditorFields.ItemCount}/{pane.Document.Table.Columns.Length} fields; selected row {pane.SelectedRow}, editor row {rowEditorRow}, filter '{RowEditorSearch.Text}'.");
                var rowInput = RowEditorFields.GetVisualDescendants().OfType<TextBox>().First(t => !t.IsReadOnly);
                var rowField = Avalonia.Automation.AutomationProperties.GetName(rowInput)!;
                var rowBefore = pane.Document.Table.Cell(pane.SelectedRow, rowField);
                rowInput.Text = rowBefore + " row-editor"; await Task.Delay(60);
                Require(pane.Document.Table.Cell(pane.SelectedRow, rowField) == rowBefore + " row-editor", "Row editor did not apply its edit.");
                pane.Document.Undo(); pane.Refresh();
                Require(pane.Document.Table.Cell(pane.SelectedRow, rowField) == rowBefore, "Row editor edit could not be undone.");
                this.MouseDown(cellPoint, MouseButton.Right, RawInputModifiers.None); this.MouseUp(cellPoint, MouseButton.Right, RawInputModifiers.None); await Task.Delay(60);
                var rowMenu = pane.TableGrid.ContextMenu!;
                Require(rowMenu.IsOpen, "Right-click did not open row actions.");
                var rowToFreeze = pane.SelectedRow;
                var menuHeaders = rowMenu.Items.OfType<MenuItem>().Select(m => m.Header?.ToString() ?? "").ToArray();
                Require(menuHeaders.Contains("Add row above") && menuHeaders.Contains("Add row below") &&
                    menuHeaders.Contains("Clone and Append") && menuHeaders.Contains("Clone and Insert") &&
                    rowMenu.Items.OfType<MenuItem>().SingleOrDefault(m => m.Header?.ToString()?.StartsWith("Delete row") == true) is { IsEnabled: true },
                    "Row menu lacks add/clone/delete row actions, or refuses to delete an original row.");
                var freezeAction = rowMenu.Items.OfType<MenuItem>().First(m => m.Header?.ToString() == "Freeze row");
                rowMenu.Close(); freezeAction.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Require(pane.FrozenRows.Contains(rowToFreeze), "Row context action froze the wrong row.");
                pane.ToggleFrozenRows();
                pane.Jump(200, name == "skills" ? pane.Document.Table!.Columns[^1] : field);
                Require(pane.TableGrid.Columns.Count <= pane.Document.Table!.Columns.Length + 2 && pane.TableGrid.Columns.Count < 80, $"The grid should hold only a window of columns plus spacers: {pane.TableGrid.Columns.Count} of {pane.Document.Table.Columns.Length}.");
                results.Add(new { table = name, elapsedMs = timer.Elapsed.TotalMilliseconds, rows = pane.Document.Table!.Records.Count, displayedColumns = pane.TableGrid.Columns.Count, realizedRows, editingAndUndo = true });
            }
            InspectorTabs.SelectedIndex = 1; await SettleRowEditorAsync();
            var widePane = Active!;
            var realizedFields = RowEditorFields.GetVisualDescendants().OfType<TextBox>().Count();
            Require(RowEditorFields.ItemCount == 322 && realizedFields > 0 && realizedFields < 40, "Wide row editor did not virtualize its inputs.");
            RowEditorSearch.Text = widePane.Document.Table!.Columns[^1];
            var searchText = RowEditorSearch.Text;
            await Task.Delay(60);
            Require(RowEditorFields.ItemCount > 0 && RowEditorFields.ItemCount < 322, "Row search did not filter columns.");
            widePane.Jump(201); await SettleRowEditorAsync();
            Require(RowEditorSearch.Text == searchText && ((RowEditorField[])RowEditorFields.ItemsSource!).All(f => f.Column.Contains(searchText!, StringComparison.OrdinalIgnoreCase)), "Row search was lost when changing rows.");
            RowEditorSearch.Text = "no-column-with-this-name"; await Task.Delay(60);
            Require(RowEditorFields.ItemCount == 0, "Unmatched row search should be empty.");
            RowEditorSearch.Text = ""; await Task.Delay(60); widePane.Jump(200); await SettleRowEditorAsync();
            var columnIndex = 1;
            // The table is still scrolled to the last column from the earlier jump; bring column 1 on screen for the header click.
            widePane.Jump(200, widePane.Document.Table!.Columns[columnIndex]); await SettleRowEditorAsync();
            await Task.Delay(100);
            var columnHeader = widePane.TableGrid.GetVisualDescendants().OfType<DataGridColumnHeader>().First(h => Equals(h.Content, widePane.GridColumnOf(widePane.TableGrid, 1)!.Header));
            var headerPoint = columnHeader.TranslatePoint(new Point(columnHeader.Bounds.Width / 2, 12), this)!.Value;
            this.MouseDown(headerPoint, MouseButton.Right, RawInputModifiers.None); this.MouseUp(headerPoint, MouseButton.Right, RawInputModifiers.None); await Task.Delay(80);
            var columnMenu = columnHeader.ContextMenu!;
            Require(columnMenu is { IsOpen: true }, "Column header right-click did not open its menu.");
            columnMenu!.Close();
            Require(columnMenu.Items.OfType<MenuItem>().First().Header?.ToString()?.StartsWith("Column guide") == true, "Column header menu does not offer the searchable guide first.");
            columnMenu.Items.OfType<MenuItem>().First(m => m.Header?.ToString()?.StartsWith("Freeze ") == true).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Require(widePane.FrozenColumns.Contains(columnIndex), "Column header freeze action failed.");
            widePane.ToggleFrozenColumn(widePane.Document.Table.Columns[columnIndex]);
            var resized = widePane.GridColumnOf(widePane.TableGrid, 1)!; resized.Width = new DataGridLength(123);
            widePane.RefreshColumns();
            Require(widePane.GridColumnOf(widePane.TableGrid, 1)!.Width.Value == 123 && widePane.GridColumnOf(widePane.FrozenGrid, 1)!.Width.Value == 123 && widePane.TableGrid.CanUserResizeColumns, "Column resize was not retained and synchronized.");
            Require(EditorPane.JsonFolds("{\n\"text\": \"[}]\",\n\"items\": [\n1,2\n]\n}").Count() == 2, "JSON folds mishandled braces in strings.");
            await terminal.SendAsync(OperatingSystem.IsWindows() ? "echo studio-terminal-%COMSPEC%" : "printf studio-terminal-$((6*7))");
            var terminalDeadline = DateTime.UtcNow.AddSeconds(5);
            while (!(OperatingSystem.IsWindows() ? terminal.OutputText.Contains("studio-terminal-C:", StringComparison.OrdinalIgnoreCase) : terminal.OutputText.Contains("studio-terminal-42")) && DateTime.UtcNow < terminalDeadline) await Task.Delay(80);
            Require((OperatingSystem.IsWindows() ? terminal.OutputText.Contains("studio-terminal-C:", StringComparison.OrdinalIgnoreCase) : terminal.OutputText.Contains("studio-terminal-42")), "Project shell did not produce output.");
            terminal.Stop();
            var previousFields = (RowEditorField[])RowEditorFields.ItemsSource!;
            var previousRow = widePane.SelectedRow;
            var previousValue = widePane.Document.Table!.Cell(previousRow, previousFields[0].Column);
            var refreshCount = rowEditorRefreshCount;
            for (int i = 0; i < 20; i++) RefreshRowEditor(widePane);
            await SettleRowEditorAsync();
            Require(rowEditorRefreshCount == refreshCount && ReferenceEquals(previousFields, RowEditorFields.ItemsSource), $"Unchanged row rebuilt the field list: refreshes {refreshCount}->{rowEditorRefreshCount}, row {previousRow}->{rowEditorRow}, document revision {widePane.Document.Revision}, editor revision {rowEditorRevision}.");
            var switchWatch = Stopwatch.StartNew();
            widePane.Jump(201, widePane.SelectedColumn); widePane.Jump(202, widePane.SelectedColumn); widePane.Jump(203, widePane.SelectedColumn);
            await SettleRowEditorAsync();
            switchWatch.Stop();
            Require(rowEditorRow == 203 && rowEditorRefreshCount == refreshCount + 1, "Rapid row changes were not coalesced to the latest row.");
            previousFields[0].Value = previousValue + " stale";
            Require(widePane.Document.Table.Cell(previousRow, previousFields[0].Column) == previousValue, "Detached row input changed a previous row.");
            var lastField = ((RowEditorField[])RowEditorFields.ItemsSource!)[^1];
            RowEditorFields.ScrollIntoView(lastField); await Task.Delay(150);
            var lastInput = RowEditorFields.GetVisualDescendants().OfType<TextBox>().Single(t => Avalonia.Automation.AutomationProperties.GetName(t) == lastField.Column);
            var lastBefore = widePane.Document.Table.Cell(203, lastField.Column);
            Require(lastInput.Text == lastBefore, "Virtualized last field shows stale content.");
            lastInput.Text = lastBefore + " virtualized edit"; await Task.Delay(60);
            Require(widePane.Document.Table.Cell(203, lastField.Column) == lastBefore + " virtualized edit", "Last virtualized column did not save its edit.");
            var fieldsBeforeUndo = RowEditorFields.ItemsSource; var refreshesBeforeUndo = rowEditorRefreshCount;
            widePane.Undo(); await Task.Delay(100);
            Require(widePane.Document.Table.Cell(203, lastField.Column) == lastBefore && lastInput.Text == lastBefore, "Virtualized row edit undo failed, or the Row Editor did not pick up the restored value.");
            Require(ReferenceEquals(fieldsBeforeUndo, RowEditorFields.ItemsSource) && rowEditorRefreshCount == refreshesBeforeUndo, "Undo rebuilt the Row Editor fields instead of refreshing them in place.");
            // Focusing a Row Editor field scrolls the table to its cell and selects it, while the field keeps keyboard focus.
            widePane.Jump(203, widePane.Document.Table.Columns[0]); await Task.Delay(100);
            var horizontalBefore = widePane.TableGrid.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Name == "PART_HorizontalScrollbar").Value;
            lastInput.Focus(); await Task.Delay(120);
            Require(lastInput.IsFocused && widePane.SelectedColumn == lastField.Column && widePane.SelectedRow == 203
                && widePane.SelectedCells.SequenceEqual([(203, widePane.Document.Table.Columns.Length - 1)]) && widePane.TableGrid.CurrentColumn is { } revealed && widePane.ColumnIndexOf(revealed) == widePane.Document.Table.Columns.Length - 1
                && widePane.TableGrid.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Name == "PART_HorizontalScrollbar").Value > horizontalBefore,
                $"Focusing a Row Editor field did not reveal its cell: focused {lastInput.IsFocused}, column {widePane.SelectedColumn}, row {widePane.SelectedRow}.");
            InspectorTabs.SelectedIndex = 0; var hiddenFields = RowEditorFields.ItemsSource;
            widePane.Jump(204, widePane.SelectedColumn); await Task.Delay(80);
            Require(ReferenceEquals(hiddenFields, RowEditorFields.ItemsSource), "Hidden Row Editor rebuilt its fields.");
            InspectorTabs.SelectedIndex = 1; await SettleRowEditorAsync();
            Require(rowEditorRow == 204, "Reopened Row Editor missed the latest selection.");
            RowEditorFields.ScrollIntoView(RowEditorFields.Items[0]!); await Task.Delay(100);
            results.Add(new { rowEditorColumns = 322, realizedFields, rapidSwitchMs = switchWatch.Elapsed.TotalMilliseconds, virtualizationAndStaleEventChecks = true });
            await Task.Delay(500); var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96)); bitmap.Render(this); bitmap.Save(System.IO.Path.Combine(output, "studio.png"), PngBitmapEncoderOptions.Default); bitmap.Dispose();
            var jsonFile = System.IO.Path.Combine(output, "folding.json");
            File.WriteAllText(jsonFile, "{\n  \"enabled\": 1,\n  \"nested\": {\n    \"value\": 2\n  }\n}\n", Utf8);
            var jsonPane = (await OpenDocumentAsync(jsonFile))!; await Task.Delay(100);
            Button ActionButton(string name) => jsonPane.GetVisualDescendants().OfType<Button>().First(b => Avalonia.Automation.AutomationProperties.GetName(b) == name);
            var saveIcon = ActionButton("Save this file"); Require(!saveIcon.IsVisible, "Clean document shows Save icon.");
            jsonPane.Source.Text += "\n"; await Task.Delay(80);
            Require(saveIcon.IsVisible && jsonPane.Document.IsDirty, "Source edit did not reveal Save icon.");
            saveIcon.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(!saveIcon.IsVisible && !jsonPane.Document.IsDirty && File.ReadAllText(jsonFile) == jsonPane.Source.Text, "Document Save icon did not save and hide.");
            var jsonText = jsonPane.Source.Text;
            ActionButton("Collapse JSON blocks").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await jsonPane.PendingFolding;
            Require(jsonPane.FoldedBlockCount == 2 && jsonPane.Source.Text == jsonText, "JSON collapse changed text or missed nested blocks.");
            int foldingScans = jsonPane.FoldingScanCount;
            ActionButton("Expand JSON blocks").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await jsonPane.PendingFolding;
            Require(jsonPane.FoldedBlockCount == 0 && jsonPane.Source.TextArea.TextView.Margin.Left >= 10, "Expand JSON or source gutter spacing failed.");
            Require(jsonPane.FoldingScanCount == foldingScans, "Unchanged JSON was rescanned when expanding blocks.");
            jsonPane.Source.Text = "[\n" + string.Join(",\n", Enumerable.Repeat("{\n  \"value\": 1\n}", 4000)) + "\n]";
            ActionButton("Collapse JSON blocks").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var staleFolding = jsonPane.PendingFolding;
            jsonPane.Source.Text = jsonText;
            ActionButton("Collapse JSON blocks").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.WhenAll(staleFolding, jsonPane.PendingFolding);
            Require(jsonPane.FoldedBlockCount == 2 && jsonPane.Source.Text == jsonText, "Stale JSON scan replaced newer document fold ranges.");
            saveIcon.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ActionButton("Expand JSON blocks").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await jsonPane.PendingFolding;
            await Task.Delay(100);
            using (var sourceImage = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96,96))) { sourceImage.Render(this); sourceImage.Save(System.IO.Path.Combine(output, "json-folding.png"), PngBitmapEncoderOptions.Default); }
            await CloseTabAsync(tabs.First(t => t.Content == jsonPane));
            Documents.SelectedItem = tabs.First(t => t.Content == widePane); await SettleRowEditorAsync();
            var markdownFile = System.IO.Path.Combine(output, "preview.md");
            InspectorTabs.SelectedIndex = 1; await Task.Delay(100);
            var rowEditorScroll = RowEditorFields.GetVisualDescendants().OfType<ScrollViewer>().First();
            var rowScrollbar = rowEditorScroll.GetVisualDescendants().OfType<ScrollBar>().First(b => b.Orientation == Orientation.Vertical && b.TemplatedParent == rowEditorScroll);
            var rowInputRight = RowEditorFields.GetVisualDescendants().OfType<TextBox>().First();
            Require(rowInputRight.TranslatePoint(new Point(rowInputRight.Bounds.Width, 0), rowEditorScroll)!.Value.X <= rowScrollbar.TranslatePoint(new Point(0,0), rowEditorScroll)!.Value.X, "Row editor scrollbar overlaps input fields.");
            InspectorTabs.SelectedIndex = 0;
            var newTable = new NewTableWindow(project!); _ = newTable.ShowDialog<TableData?>(this); await Task.Delay(150);
            using (var newTableBitmap = new RenderTargetBitmap(new PixelSize(720, (int)Math.Max(newTable.Bounds.Height, 600)), new Vector(96,96)))
            { newTableBitmap.Render(newTable); newTableBitmap.Save(System.IO.Path.Combine(output, "new-table.png"), PngBitmapEncoderOptions.Default); }
            newTable.Close(null);
            var importWindow = new ImportModWindow(StudioPreferences.DefaultProjectsFolder); _ = importWindow.ShowDialog<ImportPlan?>(this); await Task.Delay(150);
            using (var importBitmap = new RenderTargetBitmap(new PixelSize(760, (int)Math.Max(importWindow.Bounds.Height, 600)), new Vector(96,96)))
            { importBitmap.Render(importWindow); importBitmap.Save(System.IO.Path.Combine(output, "import-mod.png"), PngBitmapEncoderOptions.Default); }
            importWindow.Close(null);
            var tutorial = new QuickStartWindow(); var tutorialDialog = tutorial.ShowDialog(this); await Task.Delay(100);
            using (var tutorialBitmap = new RenderTargetBitmap(new PixelSize(720,640), new Vector(96,96)))
            { tutorialBitmap.Render(tutorial); tutorialBitmap.Save(System.IO.Path.Combine(output, "quick-start.png"), PngBitmapEncoderOptions.Default); }
            for (int step = 0; step < 4; step++) tutorial.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Next").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(tutorial.StepIndex == 4, "Tutorial did not reach its final step.");
            tutorial.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Back").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(tutorial.StepIndex == 3, "Tutorial back navigation failed.");
            tutorial.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Skip for now").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await tutorialDialog;
            var migrationSource = System.IO.Path.Combine(output, "migration-input/data/global/excel"); Directory.CreateDirectory(migrationSource);
            File.WriteAllText(System.IO.Path.Combine(migrationSource, "example.txt"), "name\tvalue\nExample\t1\n", Utf8);
            var migrationCandidate = LegacyMigration.Detect(System.IO.Path.Combine(output, "migration-input")).Single();
            using (var migrationCancellation = new CancellationTokenSource())
            {
                var migrationWindow = new MigrationProgressWindow(migrationCandidate, System.IO.Path.Combine(output, "migrated-" + Guid.NewGuid().ToString("N")), "SmokeMod", migrationCancellation, _ => { });
                var migrationDialog = migrationWindow.ShowDialog<ImportReport?>(this);
                for (int wait = 0; wait < 200 && !migrationWindow.GetVisualDescendants().OfType<Button>().Any(b => b.Content as string == "Open migrated project"); wait++) await Task.Delay(100);
                var openMigrated = migrationWindow.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Content as string == "Open migrated project");
                Require(openMigrated != null, "Migration progress window did not reach success.");
                using (var progressBitmap = new RenderTargetBitmap(new PixelSize(680, 520), new Vector(96, 96)))
                { progressBitmap.Render(migrationWindow); progressBitmap.Save(System.IO.Path.Combine(output, "migration-progress.png"), PngBitmapEncoderOptions.Default); }
                openMigrated!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(await migrationDialog != null, "Migration progress window lost its completed result.");
            }
            foreach (var scriptExtension in new[] { ".js", ".mjs", ".ts", ".py", ".JS", ".bat", ".cmd", ".custom-text", "" })
            {
                var scriptFile = System.IO.Path.Combine(output, "script" + scriptExtension);
                var scriptText = scriptExtension == ".py" ? "print('hello')\n" : scriptExtension is ".bat" or ".cmd" ? "@echo off\r\nrem Copy mod files\r\nset SOURCE=data\r\nxcopy \"%SOURCE%\" \"output\" /E /I\r\npause\r\n" : "const message = 'hello';\n";
                File.WriteAllText(scriptFile, scriptText, Utf8);
                var scriptPane = await OpenDocumentAsync(scriptFile);
                Require(scriptPane != null && scriptPane.Source.IsVisible && scriptPane.Document.Table == null, "Script opened as binary: " + scriptExtension);
                if (scriptExtension is ".js" or ".mjs" or ".ts" or ".py" or ".bat") Require(scriptPane!.Source.SyntaxHighlighting != null, "Missing code highlighting: " + scriptExtension);
                if (scriptExtension == ".bat")
                {
                    await Task.Delay(100);
                    using var codeBitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96,96));
                    codeBitmap.Render(this); codeBitmap.Save(System.IO.Path.Combine(output, "batch-highlighting.png"), PngBitmapEncoderOptions.Default);
                }
                scriptPane!.Document.SetRaw(scriptText + "\n"); scriptPane.Document.ApplySource(); scriptPane.Document.Save();
                Require(File.ReadAllText(scriptFile) == scriptText + "\n", "Script edit did not save: " + scriptExtension);
                scriptPane.Document.Undo(); scriptPane.Refresh();
                Require(scriptPane.Document.Text == scriptText, "Script undo failed: " + scriptExtension);
                scriptPane.Document.Redo(); scriptPane.Refresh();
                await CloseTabAsync(tabs.First(t => t.Content == scriptPane));
            }
            Require(MarkdownPreviewText.Prepare("- [x] Done\n\n```\n- [ ] literal\n```").Contains("☑ Done") && MarkdownPreviewText.Prepare("```\n- [ ] literal\n```").Contains("- [ ] literal"), "Task preview altered literal code or missed a checkbox.");
            File.WriteAllText(markdownFile, "# Mod documentation\n\nA **bold** change, *emphasis*, and ~~removed text~~.\n\n## Build checklist\n\n- [x] Edit source\n- [ ] Deploy mod\n\n| Profile | Status |\n| --- | --- |\n| Standard | Ready |\n| D2RL | Ready |\n\n```json\n{\"enabled\": true}\n```\n\n> Changes remain local until you save.\n", Utf8);
            var markdownPane = await OpenDocumentAsync(markdownFile);
            Require(markdownPane?.MarkdownPreview != null, "Markdown file has no preview.");
            markdownPane!.Document.SetRaw(markdownPane.Document.Text + "\nUnsaved preview text\n");
            markdownPane.ShowMarkdownPreview(); await Task.Delay(300);
            Require(markdownPane.MarkdownPreview!.Markdown!.Contains("Unsaved preview text") && !markdownPane.Source.IsVisible, "Markdown preview ignored unsaved text.");
            using (var mdBitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96)))
            { mdBitmap.Render(this); mdBitmap.Save(System.IO.Path.Combine(output, "markdown-preview.png"), PngBitmapEncoderOptions.Default); }
            markdownPane.Document.Undo(); markdownPane.Refresh();
            Require(!markdownPane.MarkdownPreview.Markdown!.Contains("Unsaved preview text"), "Markdown preview did not refresh after undo.");
            var uniqueFile = System.IO.Path.Combine(root, "source/tables/uniqueitems.json");
            if (File.Exists(uniqueFile))
            {
                var unique = await OpenDocumentAsync(uniqueFile); unique!.Jump(0, "code"); unique.ToggleFrozenRows(); unique.ToggleFrozenColumn("code");
                await Task.Delay(400); await RefreshSemanticInspectorAsync();
                Require(ReferenceList.ItemsSource is List<ReferenceHit> { Count: > 0 }, $"Real unique-item reference did not resolve. Selected {unique.SelectedColumn} / {FieldPicker.SelectedItem}; profile: {ProfileValue.Text}; reference: {ReferenceStatus.Text}");
                var hit = ((List<ReferenceHit>)ReferenceList.ItemsSource!)[0];
                await Task.Delay(100);
                var referenceBitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96)); referenceBitmap.Render(this); referenceBitmap.Save(System.IO.Path.Combine(output, "references-and-freeze.png"), PngBitmapEncoderOptions.Default); referenceBitmap.Dispose();
                InspectorTabs.SelectedIndex = 1; await Task.Delay(100);
                using (var rowBitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96)))
                { rowBitmap.Render(this); rowBitmap.Save(System.IO.Path.Combine(output, "row-editor.png"), PngBitmapEncoderOptions.Default); }
                InspectorTabs.SelectedIndex = 0;
                ReferenceList.SelectedIndex = 0; await OpenReferenceAsync();
                Require(Active?.Document.FilePath == hit.File && Active.SelectedRow == hit.Row, "Reference navigation opened the wrong record.");
                var targetPane = Active!; var targetValue = targetPane.Document.Table!.Cell(hit.Row, hit.Column);
                targetPane.Document.SetCells([(hit.Row, hit.Column, targetValue + "-smoke")]);
                await OpenDocumentAsync(uniqueFile); unique.Jump(0, "code"); await Task.Delay(250); await RefreshSemanticInspectorAsync();
                Require(ReferenceList.ItemsSource is List<ReferenceHit> { Count: 0 }, $"Unsaved reference target failed to invalidate the inspector. Field {FieldPicker.SelectedItem}, profile {ProfileValue.Text}, ref {ReferenceStatus.Text}; buffer {targetPane.Document.Table!.Cell(hit.Row, hit.Column)}");
                targetPane.Document.Undo(); await Task.Delay(250); await RefreshSemanticInspectorAsync();
                Require(ReferenceList.ItemsSource is List<ReferenceHit> { Count: > 0 }, "Undo did not refresh the reference inspector.");
            }
            await SmokeLayoutUpgradeAsync(root);
            File.WriteAllText(System.IO.Path.Combine(output, "ui-smoke.json"), JsonSerializer.Serialize(results, Pretty));
        }
        catch (Exception e)
        {
            exit = 1; Console.Error.WriteLine(e);
            try
            {
                var output = Program.Arguments[Array.IndexOf(Program.Arguments, "--smoke") + 2];
                Directory.CreateDirectory(output);
                File.WriteAllText(System.IO.Path.Combine(output, "failure.txt"), e.ToString());
                using var failureImage = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96));
                failureImage.Render(this); failureImage.Save(System.IO.Path.Combine(output, "failure.png"), PngBitmapEncoderOptions.Default);
            }
            catch (Exception diagnosticError) { Console.Error.WriteLine("Could not capture failure diagnostics: " + diagnosticError.Message); }
        }
        closingApproved = true; (Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)!.Shutdown(exit);
    }
}

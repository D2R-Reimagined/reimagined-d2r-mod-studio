using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ModStudio.Core;
using Reimagined.Integration;
using static ModStudio.Core.Storage;

namespace ModStudio.App;

public partial class MainWindow
{
    private readonly HashSet<string> companionProfiles = [];
    private readonly DispatcherTimer companionTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool companionBusy;
    private readonly Dictionary<string, string> companionStamps = [];
    private void InitializeCompanion()
    {
        companionTimer.Tick += async (_, _) => await RefreshCompanionSnapshotsAsync();
        if (!Program.Arguments.Contains("--smoke")) companionTimer.Start();
        Closed += (_, _) => { companionTimer.Stop(); Program.Integration?.Dispose(); };
    }
    private async Task RefreshCompanionSnapshotsAsync()
    {
        if (companionBusy || operation != null || externalBusy || project is not { } current || companionProfiles.Count == 0) return;
        companionBusy = true;
        try
        {
            foreach (var profile in companionProfiles.ToArray())
            {
                var stamp = await Task.Run(() => LevelEditorWorkspace.ChangeStamp(current, profile));
                if (companionStamps.GetValueOrDefault(profile) != stamp)
                {
                    await Task.Run(() => LevelEditorWorkspace.Prepare(current, profile));
                    companionStamps[profile] = stamp;
                }
                IntegrationFiles.Write(Inside(current.Cache, "integration/" + profile + "/status.json"), new { updated = DateTimeOffset.UtcNow, error = "" });
            }
        }
        catch (Exception ex)
        {
            foreach (var profile in companionProfiles) IntegrationFiles.Write(Inside(current.Cache, "integration/" + profile + "/status.json"), new { updated = DateTimeOffset.UtcNow, error = ex.Message });
            Status.Text = "Level Editor table refresh failed: " + ex.Message;
        }
        finally { companionBusy = false; }
    }
    private async Task<EditorReply> ReceiveCompanionAsync(EditorRequest request)
    {
        try
        {
            request.Validate();
            if (request.Action == "open-scene") return new("unsupported", "Scene navigation belongs to Level Editor.");
            var idleDeadline = DateTime.UtcNow.AddSeconds(30);
            while ((operation != null || externalBusy) && DateTime.UtcNow < idleDeadline) await Task.Delay(100);
            if (operation != null || externalBusy) return new("failed", "Studio is busy. Finish the current operation and retry.");
            if (request.Project is { } context)
            {
                var candidate = ModProject.Open(context.Root);
                Require(candidate.Id == context.Id, "The project identity changed. Relink the workspace.");
                Require(candidate.Profiles.Contains(context.Profile), "The requested profile no longer exists.");
                if (project == null || IntegrationFiles.Canonical(project.Root) != IntegrationFiles.Canonical(context.Root))
                {
                    if (!await MayLeaveAsync()) return new("cancelled", "Project switch cancelled.");
                    await LoadProjectAsync(context.Root);
                    Require(project?.Id == context.Id, "Project opening was cancelled.");
                }
                ProfilePicker.SelectedItem = context.Profile;
                if (request.Action == "open-table-record")
                {
                    var target = request.Target ?? throw new InvalidDataException("Missing table target.");
                    Require(target.Table is { Length: > 0 } && target.Table.All(char.IsAsciiLetterOrDigit), "Invalid table name.");
                    var file = LevelEditorWorkspace.TableSource(project!, context, target.Table!);
                    Require(File.Exists(file), "This table is not part of the editable project. Import it into Studio first: " + target.Table);
                    var pane = await OpenDocumentAsync(file);
                    Require(pane?.Document is { PendingSource: false, Table: not null }, "Apply pending source changes before following this link.");
                    var table = pane!.Document.Table!;
                    var matches = Enumerable.Range(0, table.Records.Count).Where(i => target.SourceId is { Length: > 0 }
                        ? table.Records[i].S("sourceId") == target.SourceId
                        : target.KeyColumn != null && table.Cell(i, target.KeyColumn) == target.KeyValue).ToArray();
                    Require(matches.Length == 1, $"The requested {target.Table} record resolves to {matches.Length} rows. Refresh the Level Editor context.");
                    var column = target.Column ?? target.KeyColumn ?? table.Columns[0];
                    Require(table.ColumnIndex(column) >= 0, "The requested column no longer exists: " + column);
                    // Jump cancels the table's pending initial selection restore and reveals hidden/filtered targets.
                    pane.Jump(matches[0], column);
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    Require(pane.SelectedRow == matches[0] && pane.SelectedColumn == column, "The destination selection changed while opening. Retry the link.");
                    Status.Text = $"Opened {target.Table} · {target.KeyValue ?? target.SourceId} · {column}";
                }
                companionProfiles.Add(context.Profile);
            }
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Show(); Activate();
            return new("opened");
        }
        catch (Exception ex) { ShowError(ex); return new("failed", ex.Message); }
    }
    private async void CompanionClicked(object? sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var selected = new MenuItem { Header = "Open selection in Level Editor", IsEnabled = OperatingSystem.IsWindows() && project != null };
        selected.Click += async (_, _) => await OpenLevelEditorAsync(); menu.Items.Add(selected);
        var workspace = new MenuItem { Header = "Open Level Editor workspace", IsEnabled = OperatingSystem.IsWindows() && project != null };
        workspace.Click += async (_, _) => await OpenLevelEditorAsync(workspaceOnly: true); menu.Items.Add(workspace);
        if (!OperatingSystem.IsWindows()) menu.Items.Add(new MenuItem { Header = "Level Editor is currently available on Windows", IsEnabled = false });
        menu.Items.Add(new Separator()); var settings = new MenuItem { Header = "Companion applications…" };
        settings.Click += async (_, _) => await CompanionSettingsAsync(); menu.Items.Add(settings);
        if (sender is Control button) menu.Open(button);
    }
    private MenuItem LevelEditorItem(string source)
    {
        var item = new MenuItem { Header = OperatingSystem.IsWindows() ? "Open in Level Editor" : "Level Editor requires Windows", IsEnabled = OperatingSystem.IsWindows() };
        item.Click += async (_, _) => await OpenLevelEditorAsync(source); return item;
    }
    private static bool IsSceneFile(string file) => file.EndsWith(".ds1", StringComparison.OrdinalIgnoreCase) ||
        file.Replace('\\', '/').Contains("/hd/env/preset/", StringComparison.OrdinalIgnoreCase) && file.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    private async Task<T?> ChooseCompanionItemAsync<T>(string title, IReadOnlyList<T> choices) where T : class
    {
        if (choices.Count == 1) return choices[0];
        var dialog = new Window { Title = title, Width = 720, Height = 450, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var list = new ListBox { ItemsSource = choices, Margin = new(12) };
        var open = new Button { Content = "Open selected", HorizontalAlignment = HorizontalAlignment.Right, Margin = new(12) };
        open.Click += (_, _) => { if (list.SelectedItem is T value) dialog.Close(value); };
        list.DoubleTapped += (_, _) => { if (list.SelectedItem is T value) dialog.Close(value); };
        var panel = new DockPanel(); DockPanel.SetDock(open, Dock.Bottom); panel.Children.Add(open); panel.Children.Add(list); dialog.Content = panel;
        return await dialog.ShowDialog<T?>(this);
    }
    private async Task OpenLevelEditorAsync(string? source = null, bool workspaceOnly = false, EditorPane? selectedPane = null)
    {
        if (companionBusy) return;
        try
        {
            Require(OperatingSystem.IsWindows(), "Level Editor is currently available on Windows.");
            var current = project ?? throw new InvalidOperationException("Open a project first.");
            Require(operation == null && !externalBusy, "Finish the current operation first.");
            if (tabs.Select(t => t.Content).OfType<EditorPane>().Any(p => p.Document.IsDirty))
            {
                if (await ChooseAsync("Save before opening Level Editor", "Level Editor reads saved project data. Save current edits before continuing?", "Save and continue", "Cancel") != "Save and continue" || !await SaveAllAsync()) return;
            }
            EditorInstallation application;
            try { application = CompanionApps.Resolve(CompanionApps.Level); }
            catch { await CompanionSettingsAsync(); application = CompanionApps.Resolve(CompanionApps.Level); }
            companionBusy = true;
            string profile = Profile;
            var baseData = StudioPreferences.Load(StudioPreferences.DefaultFile).GameDataFolder;
            if (string.IsNullOrWhiteSpace(baseData)) baseData = GameDataFolders().FirstOrDefault() ?? "";
            if (Directory.Exists(Path.Combine(baseData, "data/hd"))) baseData = Path.Combine(baseData, "data");
            if (!Directory.Exists(baseData))
            {
                companionBusy = false; await ChooseGameDataFolderAsync(); companionBusy = true;
                baseData = StudioPreferences.Load(StudioPreferences.DefaultFile).GameDataFolder;
            }
            Require(Directory.Exists(baseData), "Choose the extracted game data folder before opening a scene.");
            Status.Text = "Preparing Level Editor tables…";
            var snapshot = await Task.Run(() => LevelEditorWorkspace.Prepare(current, profile));
            companionProfiles.Add(profile);
            EditorTarget? target = null;
            if (!workspaceOnly)
            {
                var pane = selectedPane ?? Active;
                source ??= pane?.Document.FilePath;
                var maps = new List<string>();
                if (pane?.Document.Table is { } table && pane.SelectedRow >= 0 && (source == pane.Document.FilePath))
                {
                    var effective = LevelEditorWorkspace.Table(snapshot, table.Name, baseData);
                    Require(effective != null, "The selected table is unavailable.");
                    int row = pane.SelectedRow;
                    if (table.Name == "levels")
                    {
                        string id = effective!.Cell(row, "Id"); var presets = LevelEditorWorkspace.Table(snapshot, "lvlprest", baseData);
                        Require(presets != null, "The lvlprest table is unavailable.");
                        for (int r = 0; r < presets!.Records.Count; r++) if (presets.Cell(r, "LevelId") == id)
                            maps.AddRange(Enumerable.Range(1, 6).Select(i => presets.Cell(r, "File" + i)));
                    }
                    else if (table.Name == "lvlprest")
                    {
                        if (pane.SelectedColumn is { } column && column.StartsWith("File", StringComparison.Ordinal) && int.TryParse(column[4..], out int slot) && slot is >= 1 and <= 6) maps.Add(effective!.Cell(row, column));
                        else maps.AddRange(Enumerable.Range(1, 6).Select(i => effective!.Cell(row, "File" + i)));
                    }
                }
                LevelSceneChoice scene;
                if (maps.Count == 0 && source != null && IsSceneFile(source))
                    scene = LevelEditorWorkspace.SceneFile(current, profile, source, baseData);
                else
                {
                    var paths = maps.Where(m => m.Length > 0 && m != "0").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    Require(paths.Length > 0, "Select a levels row, a lvlprest File cell, or a paired scene asset. Generated areas may have no directly assigned preset.");
                    var chosen = await ChooseCompanionItemAsync("Choose the preset / map variant", paths);
                    if (chosen == null) return;
                    scene = LevelEditorWorkspace.Scene(current, profile, chosen, baseData);
                }
                if (scene.NeedsCopy)
                {
                    if (await ChooseAsync("Create editable scene override", "This scene uses base game assets. Copy its missing preset/map into this project's data folder before editing?", "Copy and open", "Cancel") != "Copy and open") return;
                    scene = LevelEditorWorkspace.CopyBasePair(current, scene);
                }
                target = new(Preset: Relative(current.Root, scene.Preset), Map: Relative(current.Root, scene.Map), LogicalMap: scene.LogicalMap);
            }
            var context = new EditorProject(current.Id, current.Root, profile, baseData, LevelEditorWorkspace.Pointer(current, profile));
            var reply = await CompanionApps.SendAsync(application, new(target == null ? "activate" : "open-scene", context, target));
            Status.Text = reply.Success ? "Opened Level Editor · " + profile : reply.Message;
        }
        catch (Exception ex) { ShowError(ex); }
        finally { companionBusy = false; }
    }
    private async Task CompanionSettingsAsync()
    {
        var dialog = new Window { Title = "Companion applications · Level Editor", Width = 700, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var stack = new StackPanel { Margin = new(20), Spacing = 12 };
        var path = new TextBox { Text = CompanionApps.Override(CompanionApps.Level) ?? "", PlaceholderText = "Automatic detection" };
        var status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        void Refresh()
        {
            try { var app = CompanionApps.Resolve(CompanionApps.Level); status.Text = $"{app.Source} · {app.Version}\n{app.Path}"; }
            catch (Exception ex) { status.Text = ex.Message; }
        }
        stack.Children.Add(new TextBlock { Text = "Level Editor launch application (leave blank for automatic detection)" }); stack.Children.Add(path); stack.Children.Add(status);
        var buttons = new WrapPanel();
        var browse = new Button { Content = "Browse…" }; browse.Click += async (_, _) => { var files = await dialog.StorageProvider.OpenFilePickerAsync(new() { Title = "Select Level Editor application", AllowMultiple = false }); if (files.FirstOrDefault()?.TryGetLocalPath() is { } file) path.Text = file; };
        var save = new Button { Content = "Save" }; save.Click += (_, _) => { CompanionApps.SetOverride(CompanionApps.Level, path.Text?.Trim()); Refresh(); };
        var reset = new Button { Content = "Reset to automatic" }; reset.Click += (_, _) => { path.Text = ""; CompanionApps.SetOverride(CompanionApps.Level, null); Refresh(); };
        var test = new Button { Content = "Test connection", IsEnabled = OperatingSystem.IsWindows() }; test.Click += async (_, _) => { try { CompanionApps.SetOverride(CompanionApps.Level, path.Text?.Trim()); var app = await CompanionApps.ProbeAsync(CompanionApps.Resolve(CompanionApps.Level)); status.Text = "Compatible Level Editor · " + app.Version; } catch (Exception ex) { status.Text = ex.Message; } };
        var close = new Button { Content = "Close" }; close.Click += (_, _) => dialog.Close();
        foreach (var b in new[] { browse, save, reset, test, close }) buttons.Children.Add(b);
        stack.Children.Add(buttons); dialog.Content = stack; Refresh(); await dialog.ShowDialog(this);
    }
}

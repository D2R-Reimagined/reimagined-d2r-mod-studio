using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input.Platform;
using Shape = Avalonia.Controls.Shapes.Path;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ModStudio.Core;
using static ModStudio.Core.Storage;

namespace ModStudio.App;

public partial class MainWindow
{
    private List<ProjectEntry> explorerEntries = [];
    private readonly DispatcherTimer explorerSearchTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly DispatcherTimer explorerRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private string explorerSignature = "";

    private void InitializeExplorerSearch()
    {
        ExplorerSearch.TextChanged += (_, _) => {
            ClearExplorerSearch.IsVisible = !string.IsNullOrEmpty(ExplorerSearch.Text);
            explorerSearchTimer.Stop(); explorerSearchTimer.Start();
        };
        explorerSearchTimer.Tick += (_, _) => { explorerSearchTimer.Stop(); FilterExplorer(); };
        ClearExplorerSearch.Click += (_, _) => { ExplorerSearch.Text = ""; ExplorerSearch.Focus(); };
        ExplorerSearch.KeyDown += (_, e) => { if (e.Key == Key.Escape) { ExplorerSearch.Text = ""; e.Handled = true; } };
        explorerRefreshTimer.Tick += async (_, _) => { explorerRefreshTimer.Stop(); await RefreshExplorerAsync(); };
        Closed += (_, _) => { explorerSearchTimer.Stop(); explorerRefreshTimer.Stop(); };
        RevealActiveButton.Content = ExplorerIcons.Tool("M8,2 L8,5 M8,11 L8,14 M2,8 L5,8 M11,8 L14,8 M8,8 m-3,0 a3,3 0 1,0 6,0 a3,3 0 1,0 -6,0");
        CollapseAllButton.Content = ExplorerIcons.Tool("M3,6 L8,2 L13,6 M3,14 L8,10 L13,14");
        RefreshExplorerButton.Content = ExplorerIcons.Tool("M13,8 A5,5 0 1,1 11.5,4.5 M11,2 L11.8,4.8 L9,5.5");
        RevealActiveButton.Click += (_, _) => RevealInExplorer((Documents.SelectedItem as TabItem)?.Content is EditorPane pane ? pane.Document.FilePath : (Documents.SelectedItem as TabItem)?.Tag as string);
        CollapseAllButton.Click += (_, _) => { foreach (var entry in Flatten(explorerEntries)) entry.IsExpanded = false; FilterExplorer(); };
        RefreshExplorerButton.Click += async (_, _) => await RefreshExplorerAsync();
        var treeMenu = new ContextMenu();
        treeMenu.Opening += (_, e) => { treeMenu.ItemsSource = project == null ? null : ContainerMenu(project.Root); if (project == null) e.Cancel = true; };
        ProjectTree.ContextMenu = treeMenu;
        ProjectTree.ItemTemplate = new FuncTreeDataTemplate<ProjectEntry>((entry, _) =>
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new(0, 0, 8, 0), Background = Brushes.Transparent };
            row.Children.Add(ExplorerIcons.For(entry));
            var label = new TextBlock { Text = entry.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            if (entry.Name.StartsWith('.') || entry.Name is "legacy" or "migration-report.json") label.Foreground = new SolidColorBrush(Color.Parse("#8E8578"));
            row.Children.Add(label);
            ToolTip.SetTip(row, entry.IsTable ? "Table · double-click to open; the schema is editable in Source view" : entry.Path);
            var menu = new ContextMenu(); menu.Opening += (_, _) => menu.ItemsSource = EntryMenu(entry); row.ContextMenu = menu;
            row.AddHandler(PointerPressedEvent, (_, e) => { if (e.GetCurrentPoint(row).Properties.IsRightButtonPressed) ProjectTree.SelectedItem = entry; }, RoutingStrategies.Tunnel);
            return row;
        }, entry => entry.Children);
    }

    private void FilterExplorer()
    {
        var query = ExplorerSearch.Text ?? "";
        // Keep original nodes untouched so clearing the search restores expansion state.
        var entries = string.IsNullOrWhiteSpace(query) ? explorerEntries : ProjectEntry.Filter(explorerEntries, query);
        ProjectTree.ItemsSource = entries;
        ExplorerSearchStatus.IsVisible = !string.IsNullOrWhiteSpace(query) && entries.Count == 0;
    }

    private static IEnumerable<ProjectEntry> Flatten(IEnumerable<ProjectEntry> entries) => entries.SelectMany(e => new[] { e }.Concat(Flatten(e.Children)));
    private static string Signature(IEnumerable<ProjectEntry> entries) => string.Join('\n', Flatten(entries).Select(e => e.Path));
    private void SetExplorerEntries(List<ProjectEntry> entries) { explorerEntries = entries; explorerSignature = Signature(entries); FilterExplorer(); }
    /// <summary>External file changes: re-read the tree only when the set of paths actually changed, so atomic-write renames of an existing file leave the current tree (and its selection) alone.</summary>
    private void QueueExplorerRefresh() { if (project != null && !Program.Arguments.Contains("--smoke")) { explorerRefreshTimer.Stop(); explorerRefreshTimer.Start(); } }
    private async Task RefreshExplorerAsync(string? select = null)
    {
        if (project == null) return; var current = project;
        try
        {
            var entries = await Task.Run(() => ProjectEntry.Read(current.Root));
            if (project != current) return;
            if (select == null && Signature(entries) == explorerSignature) return;
            var expanded = Flatten(explorerEntries).Where(e => e.IsExpanded).Select(e => e.Path).ToHashSet(PathComparer);
            foreach (var entry in Flatten(entries)) entry.IsExpanded = expanded.Contains(entry.Path);
            SetExplorerEntries(entries);
            if (select != null) RevealInExplorer(select);
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static bool SamePath(string a, string b) => PathComparer.Equals(System.IO.Path.TrimEndingDirectorySeparator(a), System.IO.Path.TrimEndingDirectorySeparator(b));
    /// <summary>Selects a file in the tree, expanding its ancestors.</summary>
    private void RevealInExplorer(string? path)
    {
        if (path == null || project == null) return; path = System.IO.Path.GetFullPath(path);
        if (!string.IsNullOrWhiteSpace(ExplorerSearch.Text)) ExplorerSearch.Text = "";
        List<ProjectEntry>? Find(IEnumerable<ProjectEntry> entries)
        {
            foreach (var entry in entries)
            {
                if (SamePath(entry.Path, path)) return [entry];
                if (entry.Directory && Contains(entry.Path, path) && Find(entry.Children) is { } below) { below.Insert(0, entry); return below; }
            }
            return null;
        }
        var chain = Find(explorerEntries); if (chain == null) return;
        foreach (var ancestor in chain.SkipLast(1)) ancestor.IsExpanded = true;
        FilterExplorer(); ProjectTree.SelectedItem = chain[^1];
        Dispatcher.UIThread.Post(() => { try { (ProjectTree.TreeContainerFromItem(chain[^1]) as Control)?.BringIntoView(); } catch { } }, DispatcherPriority.Background);
    }

    private async void TreeTapped(object? sender, TappedEventArgs e) => await OpenTreeSelectionAsync(true);
    private async void TreeDoubleTapped(object? sender, TappedEventArgs e) => await OpenTreeSelectionAsync();
    private async void TreeKeyDown(object? sender, KeyEventArgs e)
    {
        var entry = ProjectTree.SelectedItem as ProjectEntry;
        if (e.Key == Key.Enter) { await OpenTreeSelectionAsync(); e.Handled = true; }
        else if (e.Key == Key.F2 && entry != null) { e.Handled = true; await RenameEntryAsync(entry); }
        else if (e.Key == Key.Delete && entry != null) { e.Handled = true; await DeleteEntryAsync(entry); }
    }
    private async Task OpenTreeSelectionAsync(bool preview = false) { if (ProjectTree.SelectedItem is ProjectEntry { Directory: false } entry) { try { await OpenDocumentAsync(entry.Path, preview); } catch (Exception ex) { ShowError(ex); } } }

    // Menus ---------------------------------------------------------------------------------------------------------

    /// <summary>The folder an entry's "New" actions target: the entry itself for folders, otherwise its parent.</summary>
    private static string ContainerOf(ProjectEntry entry) => entry.Directory ? entry.Path : System.IO.Path.GetDirectoryName(entry.Path)!;
    private static string DiskPath(ProjectEntry entry) => entry.Path;
    private MenuItem Item(string header, Func<Task> action, string? gesture = null)
    {
        var item = new MenuItem { Header = header, InputGesture = gesture == null ? null : KeyGesture.Parse(gesture) };
        item.Click += async (_, _) => { try { await action(); } catch (Exception ex) { ShowError(ex); } };
        return item;
    }
    private List<object> ContainerMenu(string folder) =>
    [
        Item("New File…", () => NewFileAsync(folder)), Item("New Folder…", () => NewFolderAsync(folder)), Item("New Table…", NewTableAsync), new Separator(),
        Item("Refresh", () => RefreshExplorerAsync(project!.Root)), Item("Collapse All", () => { CollapseAllButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return Task.CompletedTask; }),
        new Separator(), CreateOpenLocationItem(folder, true), CreateExternalEditorItem()
    ];
    private List<object> EntryMenu(ProjectEntry entry)
    {
        var items = new List<object>();
        if (!entry.Directory) items.Add(Item(entry.IsTable ? "Open Table" : "Open", () => OpenDocumentAsync(entry.Path)));
        if (!entry.Directory && IsSceneFile(entry.Path)) items.Add(LevelEditorItem(entry.Path));
        if (items.Count > 0) items.Add(new Separator());
        var create = new MenuItem { Header = "New" }; var container = ContainerOf(entry);
        create.Items.Add(Item("File…", () => NewFileAsync(container))); create.Items.Add(Item("Folder…", () => NewFolderAsync(container)));
        create.Items.Add(Item("Table…", NewTableAsync));
        items.Add(create);
        items.Add(new Separator());
        items.Add(Item("Rename…", () => RenameEntryAsync(entry), "F2"));
        items.Add(Item("Delete…", () => DeleteEntryAsync(entry), "Delete"));
        items.Add(new Separator());
        items.Add(Item("Copy Path", () => CopyTextAsync(DiskPath(entry))));
        items.Add(Item("Copy Relative Path", () => CopyTextAsync(Relative(project!.Root, DiskPath(entry)))));
        items.Add(CreateOpenLocationItem(entry.Path, entry.Directory));
        if (entry.Directory) items.Add(CreateExternalEditorItem());
        else if (IsExternalTable(entry.Path)) items.Add(CreateExternalEditorItem(entry.Path));
        return items;
    }
    private async Task CopyTextAsync(string text) { if (Clipboard is { } clipboard) { await clipboard.SetTextAsync(text); Status.Text = "Copied " + text; } }

    // File operations -----------------------------------------------------------------------------------------------

    private static void ValidateEntryName(string name)
    {
        Require(!string.IsNullOrWhiteSpace(name), "Enter a name.");
        Require(name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0 && !name.Contains('/') && !name.Contains('\\'), "Names cannot contain path separators or characters your file system rejects.");
        Require(name is not ("." or ".."), "Choose a real name.");
    }
    private async Task<string?> AskNameAsync(string title, string label, string initial)
    {
        var dialog = new Window { Title = title, Width = 460, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        var box = new TextBox { Text = initial }; var error = new TextBlock { Foreground = new SolidColorBrush(Color.Parse("#E39A6B")), TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var panel = new StackPanel { Margin = new(20), Spacing = 12 }; panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }); panel.Children.Add(box); panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var cancel = new Button { Content = "Cancel" }; var ok = new Button { Content = "OK", Classes = { "accent" } }; buttons.Children.Add(cancel); buttons.Children.Add(ok); panel.Children.Add(buttons); dialog.Content = panel;
        void Accept() { try { ValidateEntryName(box.Text ?? ""); dialog.Close(box.Text!.Trim()); } catch (Exception ex) { error.Text = ex.Message; error.IsVisible = true; } }
        ok.Click += (_, _) => Accept(); cancel.Click += (_, _) => dialog.Close(null);
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Accept(); e.Handled = true; } else if (e.Key == Key.Escape) { dialog.Close(null); e.Handled = true; } };
        dialog.Opened += (_, _) => { box.Focus(); var stem = System.IO.Path.GetFileNameWithoutExtension(initial); box.SelectionStart = 0; box.SelectionEnd = stem.Length > 0 ? stem.Length : initial.Length; };
        return await dialog.ShowDialog<string?>(this);
    }
    private async Task NewFileAsync(string folder)
    {
        var name = await AskNameAsync("New file", "File name (created in " + Relative(project!.Root, folder).TrimEnd('/') + "/)", "new-file.txt"); if (name == null) return;
        var path = System.IO.Path.Combine(folder, name); Require(!File.Exists(path) && !Directory.Exists(path), "Something with that name already exists.");
        Directory.CreateDirectory(folder); File.WriteAllBytes(path, []);
        await RefreshExplorerAsync(path); await OpenDocumentAsync(path);
    }
    private async Task NewFolderAsync(string folder)
    {
        var name = await AskNameAsync("New folder", "Folder name (created in " + Relative(project!.Root, folder).TrimEnd('/') + "/)", "new-folder"); if (name == null) return;
        var path = System.IO.Path.Combine(folder, name); Require(!File.Exists(path) && !Directory.Exists(path), "Something with that name already exists.");
        Directory.CreateDirectory(path); await RefreshExplorerAsync(path);
        if (Flatten(explorerEntries).FirstOrDefault(e => SamePath(e.Path, path)) is { } created) { created.IsExpanded = true; FilterExplorer(); }
    }
    private async Task NewTableAsync()
    {
        Require(project != null, "Open a project first.");
        var table = await new NewTableWindow(project!).ShowDialog<TableData?>(this); if (table == null) return;
        var records = TableData.Create(project!, table);
        Log($"Created table {table.Name} with {table.Columns.Length} columns and {table.Records.Count} rows."); await RefreshExplorerAsync(records); await OpenDocumentAsync(records);
    }
    /// <summary>Open documents keep their original path, so anything inside a renamed or deleted entry is closed first (with the usual recovery prompt); declining aborts the operation.</summary>
    private async Task<bool> CloseTabsUnderAsync(string path)
    {
        var affected = tabs.Where(t => ((t.Content as EditorPane)?.Document.FilePath ?? t.Tag as string) is { } file && (SamePath(file, path) || Contains(path, file))).ToArray();
        foreach (var tab in affected) { await CloseTabAsync(tab); if (tabs.Contains(tab)) return false; }
        return true;
    }
    private async Task RenameEntryAsync(ProjectEntry entry)
    {
        var source = DiskPath(entry); var kind = entry.IsTable ? "table" : entry.Directory ? "folder" : "file";
        var name = await AskNameAsync("Rename " + kind, "New name", System.IO.Path.GetFileName(source)); if (name == null || name == System.IO.Path.GetFileName(source)) return;
        var target = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(source)!, name);
        Require(!File.Exists(target) && !Directory.Exists(target) || SamePath(source, target), "Something with that name already exists.");
        if (!await CloseTabsUnderAsync(source)) return;
        if (Directory.Exists(source)) Directory.Move(source, target); else File.Move(source, target);
        Log($"Renamed {Relative(project!.Root, source)} to {name}");
        await RefreshExplorerAsync(target);
    }
    private async Task DeleteEntryAsync(ProjectEntry entry)
    {
        var source = DiskPath(entry); var kind = entry.IsTable ? "table" : entry.Directory ? "folder and everything inside it" : "file";
        var relative = Relative(project!.Root, source);
        if (await ChooseAsync("Delete " + relative, $"Permanently delete this {kind}? It is removed from disk immediately; use Git or a backup to restore it.", "Delete", "Cancel") != "Delete") return;
        if (!await CloseTabsUnderAsync(source)) return;
        if (Directory.Exists(source)) Directory.Delete(source, recursive: true); else File.Delete(source);
        Log("Deleted " + relative); await RefreshExplorerAsync();
    }
}

/// <summary>Small stroked glyphs for the project tree, in the muted palette JetBrains-style trees use so names stay the loudest thing in the panel.</summary>
internal static class ExplorerIcons
{
    private static readonly IBrush Folder = new SolidColorBrush(Color.Parse("#C9A45C")), Table = new SolidColorBrush(Color.Parse("#7FB7C8")), Json = new SolidColorBrush(Color.Parse("#D8BC86")),
        Image = new SolidColorBrush(Color.Parse("#8FBF7F")), Text = new SolidColorBrush(Color.Parse("#B9AD97")), Muted = new SolidColorBrush(Color.Parse("#8E8578"));
    private static Shape Glyph(string data, IBrush stroke, IBrush? fill = null) => new()
    {
        Data = Geometry.Parse(data), Stroke = stroke, Fill = fill, StrokeThickness = 1.2, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
        Width = 16, Height = 16, Stretch = Stretch.None, VerticalAlignment = VerticalAlignment.Center
    };
    public static Control Tool(string data) => Glyph(data, Text);
    public static Control For(ProjectEntry entry)
    {
        if (entry.IsTable) return Glyph("M2,3 H14 V13 H2 Z M2,6.5 H14 M2,9.5 H14 M6,3 V13 M10,3 V13", Table);
        if (entry.Directory) return Glyph("M1.5,4 V13 H14.5 V6 H8 L6.5,4 Z", Folder, new SolidColorBrush(Color.Parse("#3A3326")));
        var extension = System.IO.Path.GetExtension(entry.Name).ToLowerInvariant();
        return extension switch
        {
            ".json" => Glyph("M6,2.5 C4,2.5 4.5,5 4.5,6 C4.5,7.5 3,8 3,8 C3,8 4.5,8.5 4.5,10 C4.5,11 4,13.5 6,13.5 M10,2.5 C12,2.5 11.5,5 11.5,6 C11.5,7.5 13,8 13,8 C13,8 11.5,8.5 11.5,10 C11.5,11 12,13.5 10,13.5", Json),
            ".png" or ".jpg" or ".jpeg" or ".dds" or ".sprite" or ".dc6" or ".dcc" or ".pcx" or ".webp" or ".bmp" => Glyph("M2.5,3 H13.5 V13 H2.5 Z M2.5,11 L6,7.5 L8.5,10 L10.5,8.5 L13.5,11 M10.5,5.5 m-1,0 a1,1 0 1,0 2,0 a1,1 0 1,0 -2,0", Image),
            ".txt" or ".md" or ".tbl" or ".csv" => Glyph("M4,2.5 H9.5 L12.5,5.5 V13.5 H4 Z M9.5,2.5 V5.5 H12.5 M6,8 H10.5 M6,10.5 H10.5", Text),
            _ => Glyph("M4,2.5 H9.5 L12.5,5.5 V13.5 H4 Z M9.5,2.5 V5.5 H12.5", Muted)
        };
    }
}

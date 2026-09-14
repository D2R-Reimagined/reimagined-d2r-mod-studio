using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using ModStudio.Core;
using static ModStudio.Core.Storage;

namespace ModStudio.App;

public partial class MainWindow
{
    private int inspectionGeneration;
    private Dictionary<string, TableData> OpenBuffers(IEnumerable<string> names)
    {
        var wanted = names.ToHashSet(StringComparer.Ordinal);
        return tabs.Select(t => t.Content).OfType<EditorPane>().Where(p => p.Document.Table != null && !p.Document.PendingSource && wanted.Contains(p.Document.Table.Name))
            .ToDictionary(p => p.Document.FilePath, p => new TableData((JsonObject)p.Document.Table!.Schema.DeepClone(), (JsonArray)p.Document.Table.Records.DeepClone()));
    }
    private async Task RefreshSemanticInspectorAsync()
    {
        int generation = ++inspectionGeneration;
        if (InspectorTabs.SelectedIndex != 0) return;
        if (ProfileValue == null || ReferenceList == null) return;
        ReferenceList.ItemsSource = null; EditProfileButton.IsVisible = false; GoToReferenceButton.IsVisible = false; ProfileValue.Text = ""; ReferenceStatus.Text = "";
        if (project == null || Active is not { } pane || pane.Document.Table is not { IsCatalog: false } table || pane.Document.PendingSource || pane.SelectedRow < 0 || pane.SelectedRow >= table.Records.Count || FieldPicker.SelectedItem is not string column) return;
        var selectedProject = project; int row = pane.SelectedRow; string selectedProfile = Profile, value = table.Cell(row, column);
        try
        {
            await Task.Delay(150); if (generation != inspectionGeneration) return;
            // Project rules win; otherwise the bundled data guide tells us which table a column points at (navigation only).
            var rule = Semantics.Rules(selectedProject).FirstOrDefault(r => r.Table == table.Name && r.Column == column);
            bool fromGuide = rule?.ReferenceTables == null;
            var navigation = rule?.ReferenceTables != null ? CellReferences.FromSemantic(rule) : CellReferences.Rule(table.Name, column);
            var index = CurrentReferenceIndex();
            var buffers = ReferenceBuffers(navigation?.ReferenceTables ?? []);
            var pending = PendingReferenceTables();
            var fields = table.Columns.ToDictionary(c => c, c => table.Cell(row, c));
            try
            {
                var profileValue = ProfileEditing.ReadCell(selectedProject, table, row, column, selectedProfile);
                ProfileValue.Text = $"{selectedProfile}: {profileValue.Effective}\nShared: {value}\n{profileValue.Reason}";
                EditProfileButton.IsVisible = true;
            }
            catch (Exception e) { ProfileValue.Text = "Profile needs review: " + e.Message; }
            if (navigation == null) { ReferenceStatus.Text = "No reference rule for this field yet."; return; }
            var targets = string.Join(", ", navigation.Targets);
            ReferenceStatus.Text = "Resolving shared-source reference…";
            var result = await Task.Run(() => index.Resolve(selectedProject, navigation, value, buffers, pending, source: fields));
            var hits = result.Hits.ToList();
            if (generation != inspectionGeneration || selectedProject != project) return;
            ReferenceList.ItemsSource = hits;
            var origin = fromGuide ? $"Data guide: this points at {targets}. " : "";
            ReferenceStatus.Text = value.Length == 0 ? origin + "Empty reference." : hits.Count == 0 ? origin + "Unresolved in available project tables. Base-game resources may be missing." : origin + $"{hits.Count} match(es). Select one and press Go to, or double-click. Unsaved open target tables are included.";
            if (result.Issues.Count > 0) ReferenceStatus.Text += "\n" + string.Join("\n", result.Issues);
            if (hits.Count > 0) { ReferenceList.SelectedIndex = 0; GoToReferenceButton.IsVisible = true; GoToReferenceButton.Content = hits.Count == 1 ? $"Go to {Path.GetFileName(Path.GetDirectoryName(hits[0].File))} row {hits[0].Row}" : "Go to selected referenced row"; }
        }
        catch (Exception e) { if (generation == inspectionGeneration) { ProfileValue.Text = e.Message; ReferenceStatus.Text = "Resolve this issue before editing the profile."; } }
    }
    private async void ReferenceDoubleTapped(object? sender, TappedEventArgs e) => await OpenReferenceAsync();
    private async void GoToReferenceClicked(object? sender, RoutedEventArgs e) => await OpenReferenceAsync();
    private async void ReferenceKeyDown(object? sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await OpenReferenceAsync(); } }
    private async Task OpenReferenceAsync()
    {
        try
        {
            if (ReferenceList.SelectedItem is not ReferenceHit hit) return;
            var pane = await OpenDocumentAsync(hit.File);
            if (pane?.Document is { PendingSource: false, Table: { } table } && hit.FindRow(table) is var row && row >= 0) pane.JumpToReference(row, hit.Column);
            else Status.Text = "Reference changed. Select the source again to refresh its matches.";
        }
        catch (Exception e) { ShowError(e); }
    }
    private async void EditProfileClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            Require(project != null && Active?.Document.Table != null && FieldPicker.SelectedItem is string, "Select a table cell.");
            var pane = Active!; var table = pane.Document.Table!; int row = pane.SelectedRow; var column = (string)FieldPicker.SelectedItem!; var profile = Profile;
            Require(!pane.Document.IsDirty && !pane.Document.ExternalChange, "Save or reload the shared table before editing its profile override.");
            Require(!pane.Document.LockedRows.Contains(row) && !pane.Document.LockedColumns.Contains(column), "Unlock this cell before editing its profile override.");
            Require(!tabs.Select(t => t.Content).OfType<EditorPane>().Any(p => p.Document.IsDirty && Contains(System.IO.Path.Combine(project!.Root, "compatibility", profile), p.Document.FilePath)), "Save the open profile files before using this dialog.");
            var reviewed = ProfileEditing.ReadCell(project!, table, row, column, profile);
            var dialog = new Window { Title = $"{profile} override · {table.Name}/{column}", Width = 640, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var panel = new StackPanel { Margin = new(20), Spacing = 12 };
            panel.Children.Add(new TextBlock { Text = $"Row {row} · shared value: {reviewed.Shared}\nThis override applies to all declared table banks.", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            var value = new TextBox { Text = reviewed.Effective }; panel.Children.Add(new TextBlock { Text = "Profile value" }); panel.Children.Add(value);
            var reason = new TextBox { Text = reviewed.RuleFile == null ? "" : reviewed.Reason, PlaceholderText = "Why does this profile need the change?" }; panel.Children.Add(reason);
            var message = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap }; panel.Children.Add(message);
            var save = new Button { Content = "Save profile override" }; panel.Children.Add(save);
            save.Click += (_, _) =>
            {
                try { Require(!pane.Document.ExternalChange, "Shared table changed on disk. Reload and review again."); var path = ProfileEditing.WriteCell(project!, table, row, column, profile, value.Text ?? "", reason.Text ?? "", reviewed); dialog.Close(path); }
                catch (Exception ex) { message.Text = ex.Message; }
            };
            dialog.Content = panel; var result = await dialog.ShowDialog<string?>(this);
            if (result != null) { buildDiagnostics.Clear(); RefreshStatus(); Log("Saved profile override: " + result); await RefreshSemanticInspectorAsync(); }
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private async void CheckSemanticsClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            Require(project != null && operation == null, "Open a project and wait for active work.");
            Require(!tabs.Select(t => t.Content).OfType<EditorPane>().Any(p => p.Document.PendingSource), "Apply pending source before checking references.");
            Require(!tabs.Select(t => t.Content).OfType<EditorPane>().Any(p => p.Document.IsDirty && p.Document.FilePath == Inside(project!.Root, "source/semantics.json")), "Save semantic rule changes before running checks.");
            var rules = Semantics.Rules(project!); var buffers = OpenBuffers(rules.Select(r => r.Table).Concat(rules.SelectMany(r => r.ReferenceTables ?? [])));
            var revisions = tabs.Select(t => t.Content).OfType<EditorPane>().ToDictionary(p => p.Document, p => p.Document.Revision);
            var selectedProject = project!; int workspaceAtStart = workspaceRevision; operation = new(); RefreshRunControls(); SemanticStatus.Text = "Checking shared-source references and rules…";
            try
            {
                var result = await Task.Run(() => Semantics.Check(selectedProject, buffers, operation.Token));
                if (workspaceRevision != workspaceAtStart || revisions.Any(p => p.Key.Revision != p.Value)) { SemanticStatus.Text = "Source changed during checks; rerun to get current results"; return; }
                buildDiagnostics.Clear(); buildDiagnostics.AddRange(result.Diagnostics); RefreshStatus();
                SemanticStatus.Text = $"{result.Rules} rules · {result.Cells:N0} cells · {result.Diagnostics.Count} diagnostics · shared source only";
            }
            finally { operation.Dispose(); operation = null; RefreshRunControls(); }
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private async void EditRulesClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            Require(project != null, "Open a project first."); var file = Inside(project!.Root, "source/semantics.json");
            if (!File.Exists(file)) AtomicWrite(file, Utf8.GetBytes(Json(new JsonObject { ["schemaVersion"] = 1, ["rules"] = new JsonArray() })), requireAbsent: true);
            await OpenDocumentAsync(file);
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private async void CompareClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            Require(Active?.Document.Table != null && !Active.Document.PendingSource, "Select a valid table to compare with disk."); var pane = Active!;
            var current = new TableData((JsonObject)pane.Document.Table!.Schema.DeepClone(), (JsonArray)pane.Document.Table.Records.DeepClone());
            var differences = await Task.Run(() => { var disk = new Document(pane.Document.FilePath); Require(disk.Table != null && !disk.PendingSource, "Disk table is invalid; compare Source manually."); return TableDiff.Compare(disk.Table!, current); });
            var dialog = new Window { Title = "Cell changes against disk · " + current.Name, Width = 900, Height = 560, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var list = new ListBox { ItemsSource = differences };
            list.DoubleTapped += (_, _) => { if (list.SelectedItem is CellDifference d) { pane.Jump(d.Row, d.Column); dialog.Close(); } };
            var panel = new DockPanel(); var label = new TextBlock { Text = $"{differences.Count} cell/row-width changes. Double-click to navigate. This is a comparison with disk, not Git history.", Margin = new(12), TextWrapping = Avalonia.Media.TextWrapping.Wrap }; DockPanel.SetDock(label, Dock.Top); panel.Children.Add(label); panel.Children.Add(list); dialog.Content = panel; await dialog.ShowDialog(this);
        }
        catch (Exception ex) { ShowError(ex); }
    }
}

using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using ModStudio.Core;

namespace ModStudio.App;

/// <summary>Who is playing and with how much magic find: shared by the Drop and Monster previews.</summary>
internal sealed class DropInputState
{
    public int Players { get; set; } = 1;
    public int Party { get; set; } = 1;
    public int MagicFind { get; set; }
    public event Action? Changed;
    public void RaiseChanged() => Changed?.Invoke();
}

public partial class MainWindow
{
    private readonly DropPreviewResolver dropResolver = new();
    private readonly PreviewWorkQueue dropWork = new();
    private readonly ContentControl dropPreviewContent = new();
    private readonly DropInputState dropInputs = new();
    private readonly ComboBox dropLocale = new() { ItemsSource = ModProject.GameLocales, SelectedItem = "enUS", MinWidth = 90 };
    private readonly NumericUpDown dropItemLevel = new() { Minimum = 1, Maximum = 120, Value = null, Width = 100, FormatString = "0", PlaceholderText = "auto", ShowButtonSpinner = false };
    private readonly TextBox dropFilter = new() { Width = 160, PlaceholderText = "Find an item…" };
    private string? dropLocaleProject; private bool refreshingDropLocales;
    private string DropLocale => dropLocale.SelectedItem as string ?? "enUS";
    private TabItem dropPreviewTab = null!;
    private CancellationTokenSource? dropPreviewCancellation;
    private int resolvedDropWorkspace = -1;
    private string? resolvedDropProject;
    internal Task PendingDropPreview { get; private set; } = Task.CompletedTask;
    internal DropPreviewResult? LastDropPreview { get; private set; }
    private const string NoDrop = "Select a row in treasureclassex, or an item in uniqueitems, setitems, weapons, armor or misc.";
    private static bool IsDropTable(string? name) => name == "treasureclassex" || DropPreviewResolver.ItemTables.Contains(name);

    /// <summary>Players, party and magic find boxes bound to the shared state; each preview tab gets its own set.</summary>
    private IEnumerable<Control> DropInputFields(Func<string, Control, string, Control> field)
    {
        NumericUpDown Box(int minimum, int maximum, Func<int> get, Action<int> set)
        {
            var box = new NumericUpDown { Minimum = minimum, Maximum = maximum, Value = get(), Width = 100, FormatString = "0" };
            bool syncing = false;
            box.ValueChanged += (_, _) =>
            {
                var value = (int)(box.Value ?? minimum);
                if (syncing || value == get()) return;
                set(value); dropInputs.RaiseChanged();
            };
            // The other tab's boxes follow a change made here.
            dropInputs.Changed += () =>
            {
                if ((int)(box.Value ?? minimum) == get()) return;
                syncing = true; try { box.Value = get(); } finally { syncing = false; }
            };
            return box;
        }
        var players = Box(1, 8, () => dropInputs.Players, v => dropInputs.Players = v);
        var party = Box(1, 8, () => dropInputs.Party, v => dropInputs.Party = v);
        var magicFind = Box(0, 10000, () => dropInputs.MagicFind, v => dropInputs.MagicFind = v);
        magicFind.ShowButtonSpinner = false;
        yield return field("Players", players, "The game's player count, or /players in single player. NoDrop shrinks as it rises.");
        yield return field("Party nearby", party, "Players in your party and near the kill count fully toward NoDrop; the others count half.");
        yield return field("Magic find %", magicFind, "Magic find raises unique, set and rare odds with diminishing returns, and magic odds in full.");
    }
    private static Control OptionField(string label, Control input, string tip)
    {
        ToolTip.SetTip(input, tip);
        var field = new StackPanel { Spacing = 2, Margin = new(0, 0, 8, 4) };
        field.Children.Add(new TextBlock { Text = label, FontSize = 11 }); field.Children.Add(input);
        return field;
    }

    private void InitializeDropPreview()
    {
        var options = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new(0, 0, 0, 8) };
        foreach (var field in DropInputFields(OptionField)) options.Children.Add(field);
        options.Children.Add(OptionField("Item level", dropItemLevel, "The monster level the drop is rolled at, which gates uniques and sets. Empty takes the TC's level or the first monster that uses it."));
        options.Children.Add(OptionField("Find", dropFilter, "Only list drops whose name or code contains this."));
        options.Children.Add(OptionField("Locale", dropLocale, "The string catalog locale names are shown in."));
        var panel = new DockPanel { Margin = new(8) }; DockPanel.SetDock(options, Dock.Top); panel.Children.Add(options);
        panel.Children.Add(new ScrollViewer { Content = dropPreviewContent, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        dropPreviewTab = new TabItem { Header = new TextBlock { Text = "Drops", FontSize = 12 }, Content = panel, IsVisible = false };
        ToolTip.SetTip(dropPreviewTab, "Drop Preview: what a treasure class drops, or which monsters drop an item");
        InspectorTabs.Items.Add(dropPreviewTab);
        dropLocale.SelectionChanged += (_, _) => { if (!refreshingDropLocales && dropLocale.SelectedItem != null) RefreshDropPreview(); };
        dropItemLevel.ValueChanged += (_, _) => RefreshDropPreview(); dropFilter.TextChanged += (_, _) => RefreshDropPreview();
        dropInputs.Changed += () => { RefreshDropPreview(); RefreshMonsterPreview(); };
        InspectorTabs.SelectionChanged += (_, _) => RefreshDropPreview();
        Closed += (_, _) => dropPreviewCancellation?.Cancel();
        dropPreviewContent.Content = new TextBlock { Text = NoDrop, TextWrapping = TextWrapping.Wrap };
    }
    private void RefreshDropLocales()
    {
        if (project?.Root == dropLocaleProject) return;
        dropLocaleProject = project?.Root;
        var locales = project?.Locales() ?? ModProject.GameLocales;
        var current = DropLocale; refreshingDropLocales = true;
        try { dropLocale.ItemsSource = locales; dropLocale.SelectedItem = locales.Contains(current) ? current : locales.Contains("enUS") ? "enUS" : locales.FirstOrDefault(); }
        finally { refreshingDropLocales = false; }
    }
    private void RefreshDropPreview()
    {
        if (InspectorTabs == null || dropPreviewTab == null) return;
        dropPreviewCancellation?.Cancel(); LastDropPreview = null;
        bool available = IsDropTable(Active?.Document.Table?.Name);
        if (!available && InspectorTabs.SelectedItem == dropPreviewTab) InspectorTabs.SelectedIndex = 0;
        dropPreviewTab.IsVisible = available;
        if (!available) { dropPreviewContent.Content = new TextBlock { Text = NoDrop, TextWrapping = TextWrapping.Wrap }; return; }
        if (InspectorTabs.SelectedItem != dropPreviewTab) return;
        RefreshDropLocales();
        if (Active is { } pane) PendingDropPreview = LoadDropPreviewAsync(pane, pane.SelectedRow);
    }
    /// <summary>Drop and monster previews read most game tables; any unsaved one could make their numbers stale.</summary>
    private EditorPane? DirtyGameTable(EditorPane except) => tabs.Select(t => t.Content).OfType<EditorPane>().FirstOrDefault(p => p != except && p.Document.IsDirty &&
        (p.Document.Table != null || p.Document.FilePath.Contains("compatibility" + System.IO.Path.DirectorySeparatorChar)));
    private async Task LoadDropPreviewAsync(EditorPane pane, int row)
    {
        var work = dropPreviewCancellation = new CancellationTokenSource(); var token = work.Token;
        var selectedProject = project; var table = pane.Document.Table; var profile = Profile;
        int revision = pane.Document.Revision, workspace = workspaceRevision;
        string locale = DropLocale;
        var options = new DropPreviewOptions(dropInputs.Players, dropInputs.Party, dropInputs.MagicFind, dropItemLevel.Value is { } level ? (int)level : null, (dropFilter.Text ?? "").Trim());
        if (selectedProject == null || table == null || row < 0 || row >= table.Records.Count)
        {
            dropPreviewContent.Content = new TextBlock { Text = NoDrop, TextWrapping = TextWrapping.Wrap };
            dropPreviewCancellation = null; work.Dispose(); return;
        }
        try
        {
            dropPreviewContent.Content = new TextBlock { Text = "Rolling drops…" };
            await Task.Delay(150, token);
            Storage.Require(!pane.Document.PendingSource, "Apply the source changes before previewing this row.");
            // Clone only the selected row on the UI thread. Mutable dependency tables are never shared with a worker.
            var record = (JsonObject)table.Records[row]!.DeepClone(); var name = table.Name;
            var dirty = DirtyGameTable(pane);
            Storage.Require(dirty == null, "Save the edited dependency before previewing: " + dirty?.Document.FilePath);
            var result = await dropWork.RunAsync(ct => {
                if (resolvedDropProject != selectedProject.Root || resolvedDropWorkspace != workspace) { dropResolver.Clear(); resolvedDropProject = selectedProject.Root; resolvedDropWorkspace = workspace; }
                return dropResolver.Resolve(selectedProject, name, record, profile, locale, ct, options);
            }, token);
            if (token.IsCancellationRequested || selectedProject != project || revision != pane.Document.Revision || workspace != workspaceRevision || Active != pane || Profile != profile) return;
            LastDropPreview = result;
            dropPreviewContent.Content = DropCard(result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            var failed = new DropPreviewResult("Preview unavailable", [], [], [], 0, [ex.Message]); LastDropPreview = failed;
            dropPreviewContent.Content = DropCard(failed);
        }
        finally { if (ReferenceEquals(dropPreviewCancellation, work)) dropPreviewCancellation = null; work.Dispose(); }
    }
    private static Control DropCard(DropPreviewResult result)
    {
        var parts = new List<Control>();
        if (result.Lines.Length > 0) parts.Add(PreviewCards.Prose(result.Text));
        foreach (var section in result.Sections.Where(s => s.BeforeLevels || s.Title == "Entries")) parts.Add(PreviewCards.Section(section));
        if (result.Drops.Length > 0) parts.Add(PreviewCards.Table(result.Drops, [
            ("Per kill", d => d.PerKill), ("Item", d => d.Item), ("Unique", d => d.Unique), ("Set", d => d.Set), ("Rare", d => d.Rare), ("Magic", d => d.Magic)], (d, header) => header == "Item" && d.ItemCell is { } cell ? [cell] : null));
        foreach (var section in result.Sections.Where(s => !s.BeforeLevels && s.Title != "Entries")) parts.Add(PreviewCards.Section(section, muted: section.Title == "Assumptions"));
        if (result.Issues.Length > 0) parts.Add(PreviewCards.Prose(["Problems", .. result.Issues], Brushes.Salmon));
        return PreviewCards.Card(result.Name, parts);
    }
}

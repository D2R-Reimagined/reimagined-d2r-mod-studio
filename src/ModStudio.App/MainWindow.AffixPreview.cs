using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using ModStudio.Core;

namespace ModStudio.App;

public partial class MainWindow
{
    private readonly AffixPreviewResolver affixResolver = new();
    private readonly PreviewWorkQueue affixWork = new();
    private readonly ContentControl affixPreviewContent = new();
    private readonly ComboBox affixLocale = new() { ItemsSource = ModProject.GameLocales, SelectedItem = "enUS", MinWidth = 90 };
    private readonly NumericUpDown affixItemLevel = new() { Minimum = 1, Maximum = 99, Value = 85, Width = 100, FormatString = "0" };
    private readonly CheckBox affixRare = new() { Content = "Rare items" };
    private readonly TextBox affixFilter = new() { Width = 100, PlaceholderText = "Find…" };
    private string? affixLocaleProject; private bool refreshingAffixLocales;
    private string AffixLocale => affixLocale.SelectedItem as string ?? "enUS";
    private TabItem affixPreviewTab = null!;
    private CancellationTokenSource? affixPreviewCancellation;
    private int resolvedAffixWorkspace = -1;
    private string? resolvedAffixProject;
    internal Task PendingAffixPreview { get; private set; } = Task.CompletedTask;
    internal AffixPreviewResult? LastAffixPreview { get; private set; }
    private const string NoAffix = "Select a base item in weapons, armor or misc, or an affix in magicprefix, magicsuffix or automagic.";
    private static bool IsAffixTable(string? name) => AffixPreviewResolver.BaseTables.Contains(name) || AffixPreviewResolver.AffixTables.Contains(name);

    private void InitializeAffixPreview()
    {
        var options = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new(0, 0, 0, 8) };
        options.Children.Add(OptionField("Item level", affixItemLevel, "The item level the affixes roll at; each base turns it into an affix level with its qlvl."));
        options.Children.Add(OptionField("Find", affixFilter, "Only list affixes whose name or effect contains this."));
        options.Children.Add(OptionField("Quality", affixRare, "Rare items only take affixes marked rare."));
        options.Children.Add(OptionField("Locale", affixLocale, "The string catalog locale names are shown in."));
        var panel = new DockPanel { Margin = new(8) }; DockPanel.SetDock(options, Dock.Top); panel.Children.Add(options);
        panel.Children.Add(new ScrollViewer { Content = affixPreviewContent, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        affixPreviewTab = new TabItem { Header = new TextBlock { Text = "Affixes", FontSize = 12 }, Content = panel, IsVisible = false };
        ToolTip.SetTip(affixPreviewTab, "Affix Preview: the prefixes and suffixes a base item can roll, or where an affix can appear");
        InspectorTabs.Items.Add(affixPreviewTab);
        affixLocale.SelectionChanged += (_, _) => { if (!refreshingAffixLocales && affixLocale.SelectedItem != null) RefreshAffixPreview(); };
        affixItemLevel.ValueChanged += (_, _) => RefreshAffixPreview(); affixFilter.TextChanged += (_, _) => RefreshAffixPreview();
        affixRare.IsCheckedChanged += (_, _) => RefreshAffixPreview();
        InspectorTabs.SelectionChanged += (_, _) => RefreshAffixPreview();
        Closed += (_, _) => affixPreviewCancellation?.Cancel();
        affixPreviewContent.Content = new TextBlock { Text = NoAffix, TextWrapping = TextWrapping.Wrap };
    }
    private void RefreshAffixLocales()
    {
        if (project?.Root == affixLocaleProject) return;
        affixLocaleProject = project?.Root;
        var locales = project?.Locales() ?? ModProject.GameLocales;
        var current = AffixLocale; refreshingAffixLocales = true;
        try { affixLocale.ItemsSource = locales; affixLocale.SelectedItem = locales.Contains(current) ? current : locales.Contains("enUS") ? "enUS" : locales.FirstOrDefault(); }
        finally { refreshingAffixLocales = false; }
    }
    private void RefreshAffixPreview()
    {
        if (InspectorTabs == null || affixPreviewTab == null) return;
        affixPreviewCancellation?.Cancel(); LastAffixPreview = null;
        bool available = IsAffixTable(Active?.Document.Table?.Name);
        if (!available && InspectorTabs.SelectedItem == affixPreviewTab) InspectorTabs.SelectedIndex = 0;
        affixPreviewTab.IsVisible = available;
        if (!available) { affixPreviewContent.Content = new TextBlock { Text = NoAffix, TextWrapping = TextWrapping.Wrap }; return; }
        if (InspectorTabs.SelectedItem != affixPreviewTab) return;
        RefreshAffixLocales();
        if (Active is { } pane) PendingAffixPreview = LoadAffixPreviewAsync(pane, pane.SelectedRow);
    }
    private async Task LoadAffixPreviewAsync(EditorPane pane, int row)
    {
        var work = affixPreviewCancellation = new CancellationTokenSource(); var token = work.Token;
        var selectedProject = project; var table = pane.Document.Table; var profile = Profile;
        int revision = pane.Document.Revision, workspace = workspaceRevision;
        string locale = AffixLocale;
        var options = new AffixPreviewOptions((int)(affixItemLevel.Value ?? 85), affixRare.IsChecked == true, (affixFilter.Text ?? "").Trim());
        if (selectedProject == null || table == null || row < 0 || row >= table.Records.Count)
        {
            affixPreviewContent.Content = new TextBlock { Text = NoAffix, TextWrapping = TextWrapping.Wrap };
            affixPreviewCancellation = null; work.Dispose(); return;
        }
        try
        {
            affixPreviewContent.Content = new TextBlock { Text = "Rolling affixes…" };
            await Task.Delay(150, token);
            Storage.Require(!pane.Document.PendingSource, "Apply the source changes before previewing this row.");
            // Clone only the selected row on the UI thread. Mutable dependency tables are never shared with a worker.
            var record = (JsonObject)table.Records[row]!.DeepClone(); var name = table.Name;
            var dirty = DirtyGameTable(pane);
            Storage.Require(dirty == null, "Save the edited dependency before previewing: " + dirty?.Document.FilePath);
            var result = await affixWork.RunAsync(ct => {
                if (resolvedAffixProject != selectedProject.Root || resolvedAffixWorkspace != workspace) { affixResolver.Clear(); resolvedAffixProject = selectedProject.Root; resolvedAffixWorkspace = workspace; }
                return affixResolver.Resolve(selectedProject, name, record, profile, locale, ct, options);
            }, token);
            if (token.IsCancellationRequested || selectedProject != project || revision != pane.Document.Revision || workspace != workspaceRevision || Active != pane || Profile != profile) return;
            LastAffixPreview = result;
            affixPreviewContent.Content = AffixCard(result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            var failed = new AffixPreviewResult("Preview unavailable", [], [], [], [], [ex.Message]); LastAffixPreview = failed;
            affixPreviewContent.Content = AffixCard(failed);
        }
        finally { if (ReferenceEquals(affixPreviewCancellation, work)) affixPreviewCancellation = null; work.Dispose(); }
    }
    private static Control AffixCard(AffixPreviewResult result)
    {
        var parts = new List<Control>();
        if (result.Lines.Length > 0) parts.Add(PreviewCards.Prose(result.Lines));
        (string, Func<AffixLine, string>)[] columns = [("Chance", a => a.Chance), ("Name", a => a.Name), ("Effect", a => a.Effect), ("Affix lvl", a => a.Levels), ("Req", a => a.Required), ("Group", a => a.Group)];
        foreach (var (title, rows) in new[] { ("PREFIXES", result.Prefixes), ("SUFFIXES", result.Suffixes) })
            if (rows.Length > 0)
            {
                parts.Add(new TextBlock { Text = title, Foreground = Brushes.Tan, FontSize = 11, Margin = new(0, 4, 0, 0) });
                parts.Add(PreviewCards.Table(rows, columns));
            }
        foreach (var section in result.Sections) parts.Add(PreviewCards.Section(section.Title, section.Lines, muted: section.Title == "Assumptions"));
        if (result.Issues.Length > 0) parts.Add(PreviewCards.Prose(["Problems", .. result.Issues], Brushes.Salmon));
        return PreviewCards.Card(result.Name, parts);
    }
}

using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using ModStudio.Core;

namespace ModStudio.App;

public partial class MainWindow
{
    private readonly StatPreviewResolver statResolver = new();
    private readonly PreviewWorkQueue statWork = new();
    private readonly ContentControl statPreviewContent = new();
    private readonly ComboBox statLocale = new() { ItemsSource = ModProject.GameLocales, SelectedItem = "enUS", MinWidth = 90 };
    private readonly NumericUpDown statMin = new() { Minimum = -1000000, Maximum = 1000000, Value = null, Width = 115, FormatString = "0", PlaceholderText = "auto" };
    private readonly NumericUpDown statMax = new() { Minimum = -1000000, Maximum = 1000000, Value = null, Width = 115, FormatString = "0", PlaceholderText = "auto" };
    private readonly TextBox statParameter = new() { Width = 115, PlaceholderText = "auto" };
    private readonly NumericUpDown statLevel = new() { Minimum = 1, Maximum = 99, Value = CalcAssumptions.DefaultCharacterLevel, Width = 115, FormatString = "0" };
    private string? statLocaleProject; private bool refreshingStatLocales;
    private string StatLocale => statLocale.SelectedItem as string ?? "enUS";
    private TabItem statPreviewTab = null!;
    private CancellationTokenSource? statPreviewCancellation;
    private int resolvedStatWorkspace = -1;
    private string? resolvedStatProject;
    internal Task PendingStatPreview { get; private set; } = Task.CompletedTask;
    internal StatPreviewResult? LastStatPreview { get; private set; }
    private const string NoStat = "Select a row in properties or itemstatcost.";
    private static bool IsStatTable(string? name) => name is "properties" or "itemstatcost";

    private void InitializeStatPreview()
    {
        var options = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new(0, 0, 0, 8) };
        Control Field(string label, Control input, string tip)
        {
            ToolTip.SetTip(input, tip);
            var field = new StackPanel { Spacing = 2, Margin = new(0, 0, 12, 4) };
            field.Children.Add(new TextBlock { Text = label, FontSize = 11 }); field.Children.Add(input);
            return field;
        }
        options.Children.Add(Field("Sample min", statMin, "The low value to show. Empty takes the first authored use of the property."));
        options.Children.Add(Field("Sample max", statMax, "The high value to show. Empty takes the first authored use of the property."));
        options.Children.Add(Field("Parameter", statParameter, "The parameter (skill, per-level value…) to show. Empty takes the first authored use."));
        options.Children.Add(Field("Character level", statLevel, "The character level per-level stats are shown at."));
        options.Children.Add(Field("Locale", statLocale, "The string catalog locale tooltips are shown in."));
        var panel = new DockPanel { Margin = new(8) }; DockPanel.SetDock(options, Dock.Top); panel.Children.Add(options);
        panel.Children.Add(new ScrollViewer { Content = statPreviewContent, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        statPreviewTab = new TabItem { Header = new TextBlock { Text = "Stat Preview", FontSize = 12 }, Content = panel, IsVisible = false };
        InspectorTabs.Items.Add(statPreviewTab);
        statLocale.SelectionChanged += (_, _) => { if (!refreshingStatLocales && statLocale.SelectedItem != null) RefreshStatPreview(); };
        statMin.ValueChanged += (_, _) => RefreshStatPreview(); statMax.ValueChanged += (_, _) => RefreshStatPreview();
        statLevel.ValueChanged += (_, _) => RefreshStatPreview(); statParameter.TextChanged += (_, _) => RefreshStatPreview();
        InspectorTabs.SelectionChanged += (_, _) => RefreshStatPreview();
        Closed += (_, _) => statPreviewCancellation?.Cancel();
        statPreviewContent.Content = new TextBlock { Text = NoStat, TextWrapping = TextWrapping.Wrap };
    }
    /// <summary>Offers the project's catalog locales; keeps the current choice when it is still available.</summary>
    private void RefreshStatLocales()
    {
        if (project?.Root == statLocaleProject) return;
        statLocaleProject = project?.Root;
        var locales = project?.Locales() ?? ModProject.GameLocales;
        var current = StatLocale; refreshingStatLocales = true;
        try { statLocale.ItemsSource = locales; statLocale.SelectedItem = locales.Contains(current) ? current : locales.Contains("enUS") ? "enUS" : locales.FirstOrDefault(); }
        finally { refreshingStatLocales = false; }
    }
    private void RefreshStatPreview()
    {
        if (InspectorTabs == null || statPreviewTab == null) return;
        statPreviewCancellation?.Cancel(); LastStatPreview = null;
        bool available = IsStatTable(Active?.Document.Table?.Name);
        if (!available && InspectorTabs.SelectedItem == statPreviewTab) InspectorTabs.SelectedIndex = 0;
        statPreviewTab.IsVisible = available;
        if (!available) { statPreviewContent.Content = new TextBlock { Text = NoStat, TextWrapping = TextWrapping.Wrap }; return; }
        if (InspectorTabs.SelectedItem != statPreviewTab) return;
        RefreshStatLocales();
        if (Active is { } pane) PendingStatPreview = LoadStatPreviewAsync(pane, pane.SelectedRow);
    }
    private async Task LoadStatPreviewAsync(EditorPane pane, int row)
    {
        var work = statPreviewCancellation = new CancellationTokenSource(); var token = work.Token;
        var selectedProject = project; var table = pane.Document.Table; var profile = Profile;
        int revision = pane.Document.Revision, workspace = workspaceRevision;
        string locale = StatLocale;
        var options = new StatPreviewOptions(statMin.Value, statMax.Value, (statParameter.Text ?? "").Trim(), (int)(statLevel.Value ?? CalcAssumptions.DefaultCharacterLevel));
        if (selectedProject == null || table == null || row < 0 || row >= table.Records.Count)
        {
            statPreviewContent.Content = new TextBlock { Text = NoStat, TextWrapping = TextWrapping.Wrap };
            statPreviewCancellation = null; work.Dispose(); return;
        }
        try
        {
            statPreviewContent.Content = new TextBlock { Text = "Resolving stat…" };
            await Task.Delay(120, token);
            Storage.Require(!pane.Document.PendingSource, "Apply the source changes before previewing this row.");
            // Clone only the selected row on the UI thread. Mutable dependency tables are never shared with a worker.
            var record = (JsonObject)table.Records[row]!.DeepClone();
            var name = table.Name;
            var dirtyDependency = tabs.Select(t => t.Content).OfType<EditorPane>().FirstOrDefault(p => p != pane && p.Document.IsDirty &&
                (IsStatTable(p.Document.Table?.Name) || StatPreviewResolver.UseTables.Contains(p.Document.Table?.Name) || p.Document.Table?.IsCatalog == true
                 || p.Document.FilePath.Contains("compatibility" + System.IO.Path.DirectorySeparatorChar)));
            Storage.Require(dirtyDependency == null, "Save the edited dependency before previewing: " + dirtyDependency?.Document.FilePath);
            var result = await statWork.RunAsync(ct => {
                if (resolvedStatProject != selectedProject.Root || resolvedStatWorkspace != workspace) { statResolver.Clear(); resolvedStatProject = selectedProject.Root; resolvedStatWorkspace = workspace; }
                return statResolver.Resolve(selectedProject, name, record, profile, locale, ct, options);
            }, token);
            if (token.IsCancellationRequested || selectedProject != project || revision != pane.Document.Revision || workspace != workspaceRevision || Active != pane || Profile != profile) return;
            LastStatPreview = result;
            statPreviewContent.Content = StatCard(result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            var failed = new StatPreviewResult("Preview unavailable", [], [], "", [ex.Message]); LastStatPreview = failed;
            statPreviewContent.Content = StatCard(failed);
        }
        finally { if (ReferenceEquals(statPreviewCancellation, work)) statPreviewCancellation = null; work.Dispose(); }
    }
    private static Control StatCard(StatPreviewResult result)
    {
        var parts = new List<Control>();
        if (result.Lines.Length > 0) parts.Add(PreviewCards.Prose(result.Lines));
        foreach (var section in result.Sections) parts.Add(PreviewCards.Section(section.Title, section.Lines));
        // Save Bits overflows are the reason to look here, so they read as problems rather than an incomplete preview.
        if (result.Issues.Length > 0) parts.Add(PreviewCards.Prose(["Problems", .. result.Issues], Brushes.Salmon));
        return PreviewCards.Card(result.Name, parts);
    }
}

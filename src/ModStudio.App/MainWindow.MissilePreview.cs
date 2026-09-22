using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using ModStudio.Core;

namespace ModStudio.App;

public partial class MainWindow
{
    private readonly MissilePreviewResolver missileResolver = new();
    private readonly PreviewWorkQueue missileWork = new();
    private readonly ContentControl missilePreviewContent = new();
    private readonly ComboBox missileLocale = new() { ItemsSource = ModProject.GameLocales, SelectedItem = "enUS", MinWidth = 90 };
    private string? missileLocaleProject; private bool refreshingMissileLocales;
    private string MissileLocale => missileLocale.SelectedItem as string ?? "enUS";
    private TabItem missilePreviewTab = null!;
    private CancellationTokenSource? missilePreviewCancellation;
    private int resolvedMissileWorkspace = -1;
    private string? resolvedMissileProject;
    internal Task PendingMissilePreview { get; private set; } = Task.CompletedTask;
    internal MissilePreviewResult? LastMissilePreview { get; private set; }
    private const string NoMissile = "Select a row in missiles.";
    private CalcInputsPanel missileInputs = null!;

    private void InitializeMissilePreview()
    {
        var options = missileInputs = new CalcInputsPanel(calcInputs, missileLocale);
        var panel = new DockPanel { Margin = new(8) }; DockPanel.SetDock(options, Dock.Top); panel.Children.Add(options);
        panel.Children.Add(new ScrollViewer { Content = missilePreviewContent, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        missilePreviewTab = new TabItem { Header = new TextBlock { Text = "Missile Preview", FontSize = 12 }, Content = panel, IsVisible = false };
        InspectorTabs.Items.Add(missilePreviewTab);
        missileLocale.SelectionChanged += (_, _) => { if (!refreshingMissileLocales && missileLocale.SelectedItem != null) RefreshMissilePreview(); };
        InspectorTabs.SelectionChanged += (_, _) => RefreshMissilePreview();
        Closed += (_, _) => missilePreviewCancellation?.Cancel();
        missilePreviewContent.Content = new TextBlock { Text = NoMissile, TextWrapping = TextWrapping.Wrap };
    }
    /// <summary>Offers the project's catalog locales; keeps the current choice when it is still available.</summary>
    private void RefreshMissileLocales()
    {
        if (project?.Root == missileLocaleProject) return;
        missileLocaleProject = project?.Root;
        var locales = project?.Locales() ?? ModProject.GameLocales;
        var current = MissileLocale; refreshingMissileLocales = true;
        try { missileLocale.ItemsSource = locales; missileLocale.SelectedItem = locales.Contains(current) ? current : locales.Contains("enUS") ? "enUS" : locales.FirstOrDefault(); }
        finally { refreshingMissileLocales = false; }
    }
    private void RefreshMissilePreview()
    {
        if (InspectorTabs == null || missilePreviewTab == null) return;
        missilePreviewCancellation?.Cancel(); LastMissilePreview = null;
        bool available = Active?.Document.Table?.Name == "missiles";
        if (!available && InspectorTabs.SelectedItem == missilePreviewTab) InspectorTabs.SelectedIndex = 0;
        missilePreviewTab.IsVisible = available;
        if (!available) { missilePreviewContent.Content = new TextBlock { Text = NoMissile, TextWrapping = TextWrapping.Wrap }; return; }
        if (InspectorTabs.SelectedItem != missilePreviewTab) return;
        RefreshMissileLocales();
        if (Active is { } pane) PendingMissilePreview = LoadMissilePreviewAsync(pane, pane.SelectedRow);
    }
    private async Task LoadMissilePreviewAsync(EditorPane pane, int row)
    {
        var work = missilePreviewCancellation = new CancellationTokenSource(); var token = work.Token;
        var selectedProject = project; var table = pane.Document.Table; var profile = Profile;
        int revision = pane.Document.Revision, workspace = workspaceRevision;
        string locale = MissileLocale; var calcOptions = calcInputs.Options();
        if (selectedProject == null || table == null || row < 0 || row >= table.Records.Count)
        {
            missilePreviewContent.Content = new TextBlock { Text = NoMissile, TextWrapping = TextWrapping.Wrap };
            missilePreviewCancellation = null; work.Dispose(); return;
        }
        try
        {
            missilePreviewContent.Content = new TextBlock { Text = "Resolving missile…" };
            await Task.Delay(120, token);
            Storage.Require(!pane.Document.PendingSource, "Apply the source changes before previewing this missile.");
            // Clone only the selected row on the UI thread. Mutable dependency tables are never shared with a worker.
            var record = (JsonObject)table.Records[row]!.DeepClone();
            var dirtyDependency = tabs.Select(t => t.Content).OfType<EditorPane>().FirstOrDefault(p => p != pane && p.Document.IsDirty &&
                (p.Document.Table?.Name is "skills" or "missiles" || p.Document.FilePath.Contains("compatibility" + System.IO.Path.DirectorySeparatorChar)));
            Storage.Require(dirtyDependency == null, "Save the edited dependency before previewing: " + dirtyDependency?.Document.FilePath);
            var result = await missileWork.RunAsync(ct => {
                if (resolvedMissileProject != selectedProject.Root || resolvedMissileWorkspace != workspace) { missileResolver.Clear(); resolvedMissileProject = selectedProject.Root; resolvedMissileWorkspace = workspace; }
                return missileResolver.Resolve(selectedProject, record, profile, locale, ct, calcOptions);
            }, token);
            if (token.IsCancellationRequested || selectedProject != project || revision != pane.Document.Revision || workspace != workspaceRevision || Active != pane || Profile != profile) return;
            LastMissilePreview = result;
            missileInputs.Show(result.MaxLevel, result.Inputs);
            missilePreviewContent.Content = MissileCard(result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            var failed = new MissilePreviewResult("Preview unavailable", 0, 0, [], [], [], [], [ex.Message]); LastMissilePreview = failed;
            missilePreviewContent.Content = MissileCard(failed);
        }
        finally { if (ReferenceEquals(missilePreviewCancellation, work)) missilePreviewCancellation = null; work.Dispose(); }
    }
    private static Control MissileCard(MissilePreviewResult result)
    {
        var parts = new List<Control>();
        if (result.Lines.Length > 0) parts.Add(PreviewCards.Prose(result.Lines));
        // What fires it and where it goes comes first; the level curve can run to dozens of rows.
        foreach (var section in result.Sections.Where(s => s.BeforeLevels)) parts.Add(PreviewCards.Section(section.Title, section.Lines));
        if (result.Levels.Length > 0) parts.Add(PreviewCards.Table(result.Levels, [
            ("Lvl", l => l.Level.ToString()), ("Damage", l => l.Physical), ("Elemental", l => l.Elemental),
            ("Length", l => l.Duration), ("Velocity", l => l.Velocity), ("Synergy", l => l.Synergy), ("With synergies", l => l.WithSynergy)]));
        foreach (var section in result.Sections.Where(s => !s.BeforeLevels)) parts.Add(PreviewCards.Section(section.Title, section.Lines, muted: section.Title == "Assumptions"));
        if (result.Issues.Length > 0) parts.Add(PreviewCards.Prose(["Incomplete preview", .. result.Issues], Brushes.Salmon));
        return PreviewCards.Card(result.Name, parts);
    }
}

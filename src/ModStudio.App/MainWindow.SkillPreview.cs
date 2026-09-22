using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using ModStudio.Core;

namespace ModStudio.App;

public partial class MainWindow
{
    private readonly SkillPreviewResolver skillResolver = new();
    private readonly PreviewWorkQueue skillWork = new();
    private readonly ContentControl skillPreviewContent = new();
    private readonly ComboBox skillLocale = new() { ItemsSource = ModProject.GameLocales, SelectedItem = "enUS", MinWidth = 90 };
    private string? skillLocaleProject; private bool refreshingSkillLocales;
    private string SkillLocale => skillLocale.SelectedItem as string ?? "enUS";
    private TabItem skillPreviewTab = null!;
    private CancellationTokenSource? skillPreviewCancellation;
    private int resolvedSkillWorkspace = -1;
    private string? resolvedSkillProject;
    internal Task PendingSkillPreview { get; private set; } = Task.CompletedTask;
    internal SkillPreviewResult? LastSkillPreview { get; private set; }
    private const string NoSkill = "Select a row in skills.";

    private void InitializeSkillPreview()
    {
        var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new(0, 0, 0, 8) };
        options.Children.Add(new TextBlock { Text = "Locale", VerticalAlignment = VerticalAlignment.Center });
        options.Children.Add(skillLocale);
        var panel = new DockPanel { Margin = new(8) }; DockPanel.SetDock(options, Dock.Top); panel.Children.Add(options);
        panel.Children.Add(new ScrollViewer { Content = skillPreviewContent, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        skillPreviewTab = new TabItem { Header = new TextBlock { Text = "Skill Preview", FontSize = 12 }, Content = panel, IsVisible = false };
        InspectorTabs.Items.Add(skillPreviewTab);
        skillLocale.SelectionChanged += (_, _) => { if (!refreshingSkillLocales && skillLocale.SelectedItem != null) RefreshSkillPreview(); };
        InspectorTabs.SelectionChanged += (_, _) => RefreshSkillPreview();
        Closed += (_, _) => skillPreviewCancellation?.Cancel();
        skillPreviewContent.Content = new TextBlock { Text = NoSkill, TextWrapping = TextWrapping.Wrap };
    }
    /// <summary>Offers the project's catalog locales; keeps the current choice when it is still available.</summary>
    private void RefreshSkillLocales()
    {
        if (project?.Root == skillLocaleProject) return;
        skillLocaleProject = project?.Root;
        var locales = project?.Locales() ?? ModProject.GameLocales;
        var current = SkillLocale; refreshingSkillLocales = true;
        try { skillLocale.ItemsSource = locales; skillLocale.SelectedItem = locales.Contains(current) ? current : locales.Contains("enUS") ? "enUS" : locales.FirstOrDefault(); }
        finally { refreshingSkillLocales = false; }
    }
    private void RefreshSkillPreview()
    {
        if (InspectorTabs == null || skillPreviewTab == null) return;
        skillPreviewCancellation?.Cancel(); LastSkillPreview = null;
        bool available = Active?.Document.Table?.Name == "skills";
        if (!available && InspectorTabs.SelectedItem == skillPreviewTab) InspectorTabs.SelectedIndex = 0;
        skillPreviewTab.IsVisible = available;
        if (!available) { skillPreviewContent.Content = new TextBlock { Text = NoSkill, TextWrapping = TextWrapping.Wrap }; return; }
        if (InspectorTabs.SelectedItem != skillPreviewTab) return;
        RefreshSkillLocales();
        if (Active is { } pane) PendingSkillPreview = LoadSkillPreviewAsync(pane, pane.SelectedRow);
    }
    private async Task LoadSkillPreviewAsync(EditorPane pane, int row)
    {
        var work = skillPreviewCancellation = new CancellationTokenSource(); var token = work.Token;
        var selectedProject = project; var table = pane.Document.Table; var profile = Profile;
        int revision = pane.Document.Revision, workspace = workspaceRevision;
        string locale = SkillLocale;
        if (selectedProject == null || table == null || row < 0 || row >= table.Records.Count)
        {
            skillPreviewContent.Content = new TextBlock { Text = NoSkill, TextWrapping = TextWrapping.Wrap };
            skillPreviewCancellation = null; work.Dispose(); return;
        }
        try
        {
            skillPreviewContent.Content = new TextBlock { Text = "Resolving skill…" };
            await Task.Delay(120, token);
            Storage.Require(!pane.Document.PendingSource, "Apply the source changes before previewing this skill.");
            // Clone only the selected row on the UI thread. Mutable dependency tables are never shared with a worker.
            var record = (JsonObject)table.Records[row]!.DeepClone();
            var dirtyDependency = tabs.Select(t => t.Content).OfType<EditorPane>().FirstOrDefault(p => p != pane && p.Document.IsDirty &&
                (p.Document.Table?.Name is "skilldesc" || p.Document.Table?.IsCatalog == true || p.Document.FilePath.Contains("compatibility" + System.IO.Path.DirectorySeparatorChar)));
            Storage.Require(dirtyDependency == null, "Save the edited dependency before previewing: " + dirtyDependency?.Document.FilePath);
            var result = await skillWork.RunAsync(ct => {
                if (resolvedSkillProject != selectedProject.Root || resolvedSkillWorkspace != workspace) { skillResolver.Clear(); resolvedSkillProject = selectedProject.Root; resolvedSkillWorkspace = workspace; }
                return skillResolver.Resolve(selectedProject, record, profile, locale, ct);
            }, token);
            if (token.IsCancellationRequested || selectedProject != project || revision != pane.Document.Revision || workspace != workspaceRevision || Active != pane || Profile != profile) return;
            LastSkillPreview = result;
            skillPreviewContent.Content = SkillCard(result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            var failed = new SkillPreviewResult("Preview unavailable", "", 0, [], [], [], [ex.Message]); LastSkillPreview = failed;
            skillPreviewContent.Content = SkillCard(failed);
        }
        finally { if (ReferenceEquals(skillPreviewCancellation, work)) skillPreviewCancellation = null; work.Dispose(); }
    }
    private static Control SkillCard(SkillPreviewResult result)
    {
        var panel = new StackPanel { Spacing = 8, Margin = new(12) };
        panel.Children.Add(new SelectableTextBlock { Text = result.Name, FontSize = 18, Foreground = Brushes.Tan, TextWrapping = TextWrapping.Wrap });
        if (result.Lines.Length > 0) panel.Children.Add(new SelectableTextBlock { Text = string.Join("\n", result.Lines), Foreground = Brushes.LightSteelBlue, TextWrapping = TextWrapping.Wrap });
        if (result.Levels.Length > 0) panel.Children.Add(LevelTable(result.Levels));
        if (result.Descriptions.Length > 0)
            panel.Children.Add(new SelectableTextBlock { Text = string.Join("\n", result.Descriptions), Foreground = Brushes.LightSteelBlue, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        if (result.Issues.Length > 0) panel.Children.Add(new SelectableTextBlock { Text = "Incomplete preview\n" + string.Join("\n", result.Issues), Foreground = Brushes.Salmon, TextWrapping = TextWrapping.Wrap });
        return new Border { Background = new SolidColorBrush(Color.Parse("#151515")), Child = panel };
    }
    /// <summary>The level curve as a compact table, showing only the fields this skill actually authors.</summary>
    private static Control LevelTable(SkillLevelPreview[] levels)
    {
        (string Header, Func<SkillLevelPreview, string> Value)[] candidates = [
            ("Lvl", l => l.Level.ToString()), ("Mana", l => l.Mana), ("Damage", l => l.Physical),
            ("Elemental", l => l.Elemental), ("Length", l => l.Duration), ("Attack", l => l.AttackRating)];
        var columns = candidates.Where((c, i) => i == 0 || levels.Any(l => c.Value(l).Length > 0)).ToArray();
        var grid = new Grid { ColumnDefinitions = new(string.Join(',', columns.Select(_ => "Auto"))), RowDefinitions = new(string.Join(',', Enumerable.Repeat("Auto", levels.Length + 1))) };
        for (int c = 0; c < columns.Length; c++)
        {
            var header = new TextBlock { Text = columns[c].Header, Foreground = Brushes.Tan, FontSize = 11, Margin = new(6, 3), FontWeight = FontWeight.SemiBold };
            Grid.SetColumn(header, c); grid.Children.Add(header);
        }
        for (int r = 0; r < levels.Length; r++)
            for (int c = 0; c < columns.Length; c++)
            {
                // Banded rows keep a long level curve readable across a narrow inspector.
                var cell = new SelectableTextBlock { Text = columns[c].Value(levels[r]), FontSize = 11, Margin = new(6, 2) };
                var holder = new Border { Child = cell, Background = r % 2 == 1 ? new SolidColorBrush(Color.Parse("#1D1E1F")) : null };
                Grid.SetRow(holder, r + 1); Grid.SetColumn(holder, c); grid.Children.Add(holder);
            }
        // Only the table scrolls sideways; the text around it wraps to the inspector's width.
        return new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalAlignment = HorizontalAlignment.Left };
    }
}

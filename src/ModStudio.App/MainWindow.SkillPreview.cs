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
    /// <summary>Level and assumptions shared by the skill and missile previews.</summary>
    private readonly CalcInputState calcInputs = new();
    private CalcInputsPanel skillInputs = null!;
    /// <summary>The shared level and assumptions, for the smoke.</summary>
    internal CalcInputState CalcInputs => calcInputs;

    private void InitializeSkillPreview()
    {
        var options = skillInputs = new CalcInputsPanel(calcInputs, skillLocale);
        var panel = new DockPanel { Margin = new(8) }; DockPanel.SetDock(options, Dock.Top); panel.Children.Add(options);
        calcInputs.Changed += () => { RefreshSkillPreview(); RefreshMissilePreview(); };
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
        string locale = SkillLocale; var calcOptions = calcInputs.Options();
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
                (p.Document.Table?.Name is "skilldesc" or "missiles" or "difficultylevels" || p.Document.Table?.IsCatalog == true || p.Document.FilePath.Contains("compatibility" + System.IO.Path.DirectorySeparatorChar)));
            Storage.Require(dirtyDependency == null, "Save the edited dependency before previewing: " + dirtyDependency?.Document.FilePath);
            var result = await skillWork.RunAsync(ct => {
                if (resolvedSkillProject != selectedProject.Root || resolvedSkillWorkspace != workspace) { skillResolver.Clear(); resolvedSkillProject = selectedProject.Root; resolvedSkillWorkspace = workspace; }
                return skillResolver.Resolve(selectedProject, record, profile, locale, ct, calcOptions);
            }, token);
            if (token.IsCancellationRequested || selectedProject != project || revision != pane.Document.Revision || workspace != workspaceRevision || Active != pane || Profile != profile) return;
            LastSkillPreview = result;
            skillInputs.Show(result.MaxLevel, result.Inputs);
            skillPreviewContent.Content = SkillCard(result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            var failed = new SkillPreviewResult("Preview unavailable", "", 0, 0, [], [], [], [], [ex.Message]); LastSkillPreview = failed;
            skillPreviewContent.Content = SkillCard(failed);
        }
        finally { if (ReferenceEquals(skillPreviewCancellation, work)) skillPreviewCancellation = null; work.Dispose(); }
    }
    private static Control SkillCard(SkillPreviewResult result)
    {
        var parts = new List<Control>();
        if (result.Lines.Length > 0) parts.Add(PreviewCards.Prose(result.Lines));
        // The tooltip, calculations and functions explain the row; the level curve can run to dozens of rows, so it comes after.
        foreach (var section in result.Sections.Where(s => s.BeforeLevels)) parts.Add(PreviewCards.Section(section.Title, section.Lines));
        if (result.Levels.Length > 0) parts.Add(PreviewCards.Table(result.Levels, [
            ("Lvl", l => l.Level.ToString()), ("Mana", l => l.Mana), ("Damage", l => l.Physical),
            ("Elemental", l => l.Elemental), ("Length", l => l.Duration), ("Attack", l => l.AttackRating),
            ("Synergy", l => l.Synergy), ("With synergies", l => l.WithSynergy)]));
        foreach (var section in result.Sections.Where(s => !s.BeforeLevels)) parts.Add(PreviewCards.Section(section.Title, section.Lines, muted: true));
        if (result.Issues.Length > 0) parts.Add(PreviewCards.Prose(["Incomplete preview", .. result.Issues], Brushes.Salmon));
        return PreviewCards.Card(result.Name, parts);
    }
}

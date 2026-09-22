using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using ModStudio.Core;

namespace ModStudio.App;

public partial class MainWindow
{
    private readonly MonsterPreviewResolver monsterResolver = new();
    private readonly PreviewWorkQueue monsterWork = new();
    private readonly ContentControl monsterPreviewContent = new();
    private readonly ComboBox monsterLocale = new() { ItemsSource = ModProject.GameLocales, SelectedItem = "enUS", MinWidth = 90 };
    private string? monsterLocaleProject; private bool refreshingMonsterLocales;
    private string MonsterLocale => monsterLocale.SelectedItem as string ?? "enUS";
    private TabItem monsterPreviewTab = null!;
    private CancellationTokenSource? monsterPreviewCancellation;
    private int resolvedMonsterWorkspace = -1;
    private string? resolvedMonsterProject;
    internal Task PendingMonsterPreview { get; private set; } = Task.CompletedTask;
    internal MonsterPreviewResult? LastMonsterPreview { get; private set; }
    private const string NoMonster = "Select a row in monstats or superuniques.";
    private static bool IsMonsterTable(string? name) => name is "monstats" or "superuniques";

    private void InitializeMonsterPreview()
    {
        var options = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new(0, 0, 0, 8) };
        foreach (var field in DropInputFields(OptionField)) options.Children.Add(field);
        options.Children.Add(OptionField("Locale", monsterLocale, "The string catalog locale names are shown in."));
        var panel = new DockPanel { Margin = new(8) }; DockPanel.SetDock(options, Dock.Top); panel.Children.Add(options);
        panel.Children.Add(new ScrollViewer { Content = monsterPreviewContent, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        monsterPreviewTab = new TabItem { Header = new TextBlock { Text = "Monster Preview", FontSize = 12 }, Content = panel, IsVisible = false };
        InspectorTabs.Items.Add(monsterPreviewTab);
        monsterLocale.SelectionChanged += (_, _) => { if (!refreshingMonsterLocales && monsterLocale.SelectedItem != null) RefreshMonsterPreview(); };
        InspectorTabs.SelectionChanged += (_, _) => RefreshMonsterPreview();
        Closed += (_, _) => monsterPreviewCancellation?.Cancel();
        monsterPreviewContent.Content = new TextBlock { Text = NoMonster, TextWrapping = TextWrapping.Wrap };
    }
    private void RefreshMonsterLocales()
    {
        if (project?.Root == monsterLocaleProject) return;
        monsterLocaleProject = project?.Root;
        var locales = project?.Locales() ?? ModProject.GameLocales;
        var current = MonsterLocale; refreshingMonsterLocales = true;
        try { monsterLocale.ItemsSource = locales; monsterLocale.SelectedItem = locales.Contains(current) ? current : locales.Contains("enUS") ? "enUS" : locales.FirstOrDefault(); }
        finally { refreshingMonsterLocales = false; }
    }
    private void RefreshMonsterPreview()
    {
        if (InspectorTabs == null || monsterPreviewTab == null) return;
        monsterPreviewCancellation?.Cancel(); LastMonsterPreview = null;
        bool available = IsMonsterTable(Active?.Document.Table?.Name);
        if (!available && InspectorTabs.SelectedItem == monsterPreviewTab) InspectorTabs.SelectedIndex = 0;
        monsterPreviewTab.IsVisible = available;
        if (!available) { monsterPreviewContent.Content = new TextBlock { Text = NoMonster, TextWrapping = TextWrapping.Wrap }; return; }
        if (InspectorTabs.SelectedItem != monsterPreviewTab) return;
        RefreshMonsterLocales();
        if (Active is { } pane) PendingMonsterPreview = LoadMonsterPreviewAsync(pane, pane.SelectedRow);
    }
    private async Task LoadMonsterPreviewAsync(EditorPane pane, int row)
    {
        var work = monsterPreviewCancellation = new CancellationTokenSource(); var token = work.Token;
        var selectedProject = project; var table = pane.Document.Table; var profile = Profile;
        int revision = pane.Document.Revision, workspace = workspaceRevision;
        string locale = MonsterLocale;
        var options = new DropPreviewOptions(dropInputs.Players, dropInputs.Party, dropInputs.MagicFind);
        if (selectedProject == null || table == null || row < 0 || row >= table.Records.Count)
        {
            monsterPreviewContent.Content = new TextBlock { Text = NoMonster, TextWrapping = TextWrapping.Wrap };
            monsterPreviewCancellation = null; work.Dispose(); return;
        }
        try
        {
            monsterPreviewContent.Content = new TextBlock { Text = "Resolving monster…" };
            await Task.Delay(150, token);
            Storage.Require(!pane.Document.PendingSource, "Apply the source changes before previewing this row.");
            // Clone only the selected row on the UI thread. Mutable dependency tables are never shared with a worker.
            var record = (JsonObject)table.Records[row]!.DeepClone(); var name = table.Name;
            var dirty = DirtyGameTable(pane);
            Storage.Require(dirty == null, "Save the edited dependency before previewing: " + dirty?.Document.FilePath);
            var result = await monsterWork.RunAsync(ct => {
                if (resolvedMonsterProject != selectedProject.Root || resolvedMonsterWorkspace != workspace) { monsterResolver.Clear(); resolvedMonsterProject = selectedProject.Root; resolvedMonsterWorkspace = workspace; }
                return monsterResolver.Resolve(selectedProject, name, record, profile, locale, ct, options);
            }, token);
            if (token.IsCancellationRequested || selectedProject != project || revision != pane.Document.Revision || workspace != workspaceRevision || Active != pane || Profile != profile) return;
            LastMonsterPreview = result;
            monsterPreviewContent.Content = MonsterCard(result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            var failed = new MonsterPreviewResult("Preview unavailable", [], [], [], [ex.Message]); LastMonsterPreview = failed;
            monsterPreviewContent.Content = MonsterCard(failed);
        }
        finally { if (ReferenceEquals(monsterPreviewCancellation, work)) monsterPreviewCancellation = null; work.Dispose(); }
    }
    private static Control MonsterCard(MonsterPreviewResult result)
    {
        var parts = new List<Control>();
        if (result.Lines.Length > 0) parts.Add(PreviewCards.Prose(result.Lines));
        if (result.Levels.Length > 0)
        {
            parts.Add(PreviewCards.Table(result.Levels, [
                ("Difficulty", l => l.Difficulty), ("Lvl", l => l.Level), ("Life", l => l.Life), ("Defense", l => l.Defense),
                ("Attack", l => l.AttackRating), ("Damage", l => l.Damage), ("Exp", l => l.Experience)]));
            // Resistances get their own table, left out when the monster authors none.
            if (result.Levels.Any(l => new[] { l.Physical, l.Magic, l.Fire, l.Lightning, l.Cold, l.Poison }.Any(v => v.Length > 0)))
                parts.Add(PreviewCards.Table(result.Levels, [
                ("Resist", l => l.Difficulty + " " + l.Level), ("Phys", l => l.Physical), ("Magic", l => l.Magic), ("Fire", l => l.Fire),
                ("Light", l => l.Lightning), ("Cold", l => l.Cold), ("Poison", l => l.Poison)]));
        }
        foreach (var section in result.Sections) parts.Add(PreviewCards.Section(section.Title, section.Lines, muted: section.Title == "Assumptions"));
        if (result.Issues.Length > 0) parts.Add(PreviewCards.Prose(["Problems", .. result.Issues], Brushes.Salmon));
        return PreviewCards.Card(result.Name, parts);
    }
}

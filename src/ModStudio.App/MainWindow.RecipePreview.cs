using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using ModStudio.Core;

namespace ModStudio.App;

public partial class MainWindow
{
    private readonly RecipePreviewResolver recipeResolver = new();
    private readonly PreviewWorkQueue recipeWork = new();
    private readonly ContentControl recipePreviewContent = new();
    private readonly ComboBox recipeLocale = new() { ItemsSource = ModProject.GameLocales, SelectedItem = "enUS", MinWidth = 90 };
    private string? recipeLocaleProject; private bool refreshingRecipeLocales;
    private string RecipeLocale => recipeLocale.SelectedItem as string ?? "enUS";
    private TabItem recipePreviewTab = null!;
    private CancellationTokenSource? recipePreviewCancellation;
    private int resolvedRecipeWorkspace = -1;
    private string? resolvedRecipeProject;
    internal Task PendingRecipePreview { get; private set; } = Task.CompletedTask;
    internal RecipePreviewResult? LastRecipePreview { get; private set; }
    private const string NoRecipe = "Select a row in cubemain or runes.";
    private static bool IsRecipeTable(string? name) => name is "cubemain" or "runes";

    private void InitializeRecipePreview()
    {
        var options = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new(0, 0, 0, 8) };
        options.Children.Add(OptionField("Locale", recipeLocale, "The string catalog locale names are shown in."));
        var panel = new DockPanel { Margin = new(8) }; DockPanel.SetDock(options, Dock.Top); panel.Children.Add(options);
        panel.Children.Add(new ScrollViewer { Content = recipePreviewContent, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        recipePreviewTab = new TabItem { Header = new TextBlock { Text = "Recipe Preview", FontSize = 12 }, Content = panel, IsVisible = false };
        InspectorTabs.Items.Add(recipePreviewTab);
        recipeLocale.SelectionChanged += (_, _) => { if (!refreshingRecipeLocales && recipeLocale.SelectedItem != null) RefreshRecipePreview(); };
        InspectorTabs.SelectionChanged += (_, _) => RefreshRecipePreview();
        Closed += (_, _) => recipePreviewCancellation?.Cancel();
        recipePreviewContent.Content = new TextBlock { Text = NoRecipe, TextWrapping = TextWrapping.Wrap };
    }
    private void RefreshRecipeLocales()
    {
        if (project?.Root == recipeLocaleProject) return;
        recipeLocaleProject = project?.Root;
        var locales = project?.Locales() ?? ModProject.GameLocales;
        var current = RecipeLocale; refreshingRecipeLocales = true;
        try { recipeLocale.ItemsSource = locales; recipeLocale.SelectedItem = locales.Contains(current) ? current : locales.Contains("enUS") ? "enUS" : locales.FirstOrDefault(); }
        finally { refreshingRecipeLocales = false; }
    }
    private void RefreshRecipePreview()
    {
        if (InspectorTabs == null || recipePreviewTab == null) return;
        recipePreviewCancellation?.Cancel(); LastRecipePreview = null;
        bool available = IsRecipeTable(Active?.Document.Table?.Name);
        if (!available && InspectorTabs.SelectedItem == recipePreviewTab) InspectorTabs.SelectedIndex = 0;
        recipePreviewTab.IsVisible = available;
        if (!available) { recipePreviewContent.Content = new TextBlock { Text = NoRecipe, TextWrapping = TextWrapping.Wrap }; return; }
        if (InspectorTabs.SelectedItem != recipePreviewTab) return;
        RefreshRecipeLocales();
        if (Active is { } pane) PendingRecipePreview = LoadRecipePreviewAsync(pane, pane.SelectedRow);
    }
    private async Task LoadRecipePreviewAsync(EditorPane pane, int row)
    {
        var work = recipePreviewCancellation = new CancellationTokenSource(); var token = work.Token;
        var selectedProject = project; var table = pane.Document.Table; var profile = Profile;
        int revision = pane.Document.Revision, workspace = workspaceRevision;
        string locale = RecipeLocale;
        if (selectedProject == null || table == null || row < 0 || row >= table.Records.Count)
        {
            recipePreviewContent.Content = new TextBlock { Text = NoRecipe, TextWrapping = TextWrapping.Wrap };
            recipePreviewCancellation = null; work.Dispose(); return;
        }
        try
        {
            recipePreviewContent.Content = new TextBlock { Text = "Reading recipe…" };
            await Task.Delay(150, token);
            Storage.Require(!pane.Document.PendingSource, "Apply the source changes before previewing this row.");
            // Clone only the selected row on the UI thread. Mutable dependency tables are never shared with a worker.
            var record = (JsonObject)table.Records[row]!.DeepClone(); var name = table.Name;
            var dirty = DirtyGameTable(pane);
            Storage.Require(dirty == null, "Save the edited dependency before previewing: " + dirty?.Document.FilePath);
            var result = await recipeWork.RunAsync(ct => {
                if (resolvedRecipeProject != selectedProject.Root || resolvedRecipeWorkspace != workspace) { recipeResolver.Clear(); resolvedRecipeProject = selectedProject.Root; resolvedRecipeWorkspace = workspace; }
                return recipeResolver.Resolve(selectedProject, name, record, profile, locale, ct);
            }, token);
            if (token.IsCancellationRequested || selectedProject != project || revision != pane.Document.Revision || workspace != workspaceRevision || Active != pane || Profile != profile) return;
            LastRecipePreview = result;
            recipePreviewContent.Content = RecipeCard(result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            var failed = new RecipePreviewResult("Preview unavailable", [], [], [ex.Message]); LastRecipePreview = failed;
            recipePreviewContent.Content = RecipeCard(failed);
        }
        finally { if (ReferenceEquals(recipePreviewCancellation, work)) recipePreviewCancellation = null; work.Dispose(); }
    }
    private static Control RecipeCard(RecipePreviewResult result)
    {
        var parts = new List<Control>();
        if (result.Lines.Length > 0) parts.Add(PreviewCards.Prose(result.Text));
        foreach (var section in result.Sections) parts.Add(PreviewCards.Section(section));
        if (result.Issues.Length > 0) parts.Add(PreviewCards.Prose(["Problems", .. result.Issues], Brushes.Salmon));
        return PreviewCards.Card(result.Name, parts);
    }
}

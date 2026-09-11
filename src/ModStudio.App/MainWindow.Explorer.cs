using Avalonia.Input;
using Avalonia.Threading;

namespace ModStudio.App;

public partial class MainWindow
{
    private List<ProjectEntry> explorerEntries = [];
    private readonly DispatcherTimer explorerSearchTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };

    private void InitializeExplorerSearch()
    {
        ExplorerSearch.TextChanged += (_, _) => {
            ClearExplorerSearch.IsVisible = !string.IsNullOrEmpty(ExplorerSearch.Text);
            explorerSearchTimer.Stop(); explorerSearchTimer.Start();
        };
        explorerSearchTimer.Tick += (_, _) => { explorerSearchTimer.Stop(); FilterExplorer(); };
        ClearExplorerSearch.Click += (_, _) => { ExplorerSearch.Text = ""; ExplorerSearch.Focus(); };
        ExplorerSearch.KeyDown += (_, e) => { if (e.Key == Key.Escape) { ExplorerSearch.Text = ""; e.Handled = true; } };
        Closed += (_, _) => explorerSearchTimer.Stop();
    }

    private void FilterExplorer()
    {
        var query = ExplorerSearch.Text ?? "";
        // Keep original nodes untouched so clearing the search restores expansion state.
        var entries = string.IsNullOrWhiteSpace(query) ? explorerEntries : ProjectEntry.Filter(explorerEntries, query);
        ProjectTree.ItemsSource = entries;
        ExplorerSearchStatus.IsVisible = !string.IsNullOrWhiteSpace(query) && entries.Count == 0;
    }
}

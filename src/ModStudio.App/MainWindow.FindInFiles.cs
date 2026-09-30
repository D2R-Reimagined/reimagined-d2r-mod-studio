using Avalonia.Input;
using ModStudio.Core;

namespace ModStudio.App;

public partial class MainWindow
{
    private FindInFilesWindow? findInFiles;
    private SearchCache? searchCache;
    private CancellationTokenSource? searchWarm;

    /// <summary>Drops the previous project's search cache (stopping its warm-up) and starts an empty one; searches fill it on demand.</summary>
    private void ResetSearchCache()
    {
        searchWarm?.Cancel(); searchWarm = null; searchCache = new SearchCache();
    }

    /// <summary>
    /// Fills the search cache on a low-priority background thread so the first search is already warm. Called once the project
    /// and its restored tabs are up, so indexing never competes with loading. The previous session's snapshot is loaded first,
    /// so only files changed since then are read and parsed (thousands on a first open, which churns the GC for a few seconds).
    /// </summary>
    private void StartSearchIndexing(ModProject opened)
    {
        if (project != opened || searchCache is not { } cache || searchWarm != null) return;
        var work = searchWarm = new();
        new Thread(() =>
        {
            try { cache.Warm(opened, work.Token, snapshot: SearchCache.SnapshotFile(opened)); }
            catch (OperationCanceledException) { }
            catch (Exception e) { Console.Error.WriteLine("Search cache warm-up failed: " + e.Message); }
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Search index" }.Start();
    }

    private void InitializeFindInFiles()
    {
        FindInFilesButton.Click += (_, _) => ShowFindInFiles();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))) { ShowFindInFiles(); e.Handled = true; }
        };
    }

    private EditorPane? PaneFor(string file)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return tabs.Select(t => t.Content).OfType<EditorPane>().FirstOrDefault(p => string.Equals(p.Document.FilePath, file, comparison));
    }

    /// <summary>Opens (or raises) Find in files, seeded with the selected cell or text of the active editor the way IDEs do.</summary>
    internal FindInFilesWindow? ShowFindInFiles(string? seed = null)
    {
        if (project == null) { Status.Text = "Open a project to search its files."; return null; }
        if (seed == null && Active is { } pane)
        {
            if (pane.Source.IsVisible) seed = pane.Source.SelectedText;
            else if (pane.Document.Table is { } table && !pane.Document.PendingSource && pane.SelectedCells.Count == 1 && pane.SelectedRow >= 0 && pane.SelectedRow < table.Records.Count) seed = table.Cell(pane.SelectedRow, pane.SelectedColumn);
        }
        if (findInFiles == null)
        {
            findInFiles = new FindInFilesWindow(new FindInFilesHost(() => project, PaneFor, () => tabs.Select(t => t.Content).OfType<EditorPane>(), (file, activate) => OpenDocumentAsync(file, activate: activate), ShowError, text => Status.Text = text, () => searchCache));
            findInFiles.Closed += (_, _) => findInFiles = null;
            findInFiles.Show(this);
        }
        findInFiles.Seed(seed);
        return findInFiles;
    }
}

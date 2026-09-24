using Avalonia.Controls;
using ModStudio.Core;

namespace ModStudio.App;

/// <summary>The Visual Builder's link to the window: the project, profile and game data it resolves against, and when to resolve again.</summary>
public partial class MainWindow
{
    private void AttachVisualBuilder(EditorPane pane)
    {
        if (!VisualBuilder.Supports(pane.Document.Table?.Name)) return;
        pane.VisualBuilderFactory = owner => new VisualBuilderView(owner, new VisualBuilderHost(
            () => project, () => Profile, () => workspaceRevision, GameDataFolders, DirtyItemDependency,
            async () => { await ChooseGameDataFolderAsync(); RefreshVisualBuilders(); }));
    }

    /// <summary>
    /// An open table the item preview reads from disk that has unsaved edits, so a preview would show stale values; null when
    /// there is none. The item's own table is read from memory and does not count.
    /// </summary>
    private string? DirtyItemDependency(EditorPane pane) => tabs.Select(t => t.Content).OfType<EditorPane>().FirstOrDefault(p => p != pane && p.Document.IsDirty &&
        (p.Document.Table?.Name is "weapons" or "armor" or "misc" or "properties" or "itemstatcost" or "sets" or "skills" or "skilldesc" || p.Document.Table?.IsCatalog == true
            || p.Document.FilePath.Contains("compatibility" + System.IO.Path.DirectorySeparatorChar)))?.Document.FilePath;

    /// <summary>Something the builders read changed (another document, the profile, files on disk, the game data folder).</summary>
    private void RefreshVisualBuilders(Document? changed = null)
    {
        foreach (var pane in tabs.Select(t => t.Content).OfType<EditorPane>())
            if (pane.VisualBuilder is VisualBuilderView view && pane.Document != changed) view.Invalidate();
    }
}

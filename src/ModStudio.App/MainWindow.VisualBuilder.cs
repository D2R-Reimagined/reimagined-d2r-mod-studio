using Avalonia.Controls;
using ModStudio.Core;

namespace ModStudio.App;

/// <summary>The Visual Builder's link to the window: the project, profile and game data it resolves against, and when to resolve again.</summary>
public partial class MainWindow
{
    private void AttachVisualBuilder(EditorPane pane)
    {
        if (!VisualBuilder.Supports(pane.Document.Table?.Name)) return;
        pane.VisualBuilderFactory = owner =>
        {
            var host = new VisualBuilderHost(() => project, () => Profile, () => workspaceRevision, GameDataFolders, DirtyItemDependency,
                async () => { await ChooseGameDataFolderAsync(); RefreshVisualBuilders(); },
                name => project == null ? null : FindOpenDocument(TableData.FileFor(project, "tables", name)),
                async name => project == null || !File.Exists(TableData.FileFor(project, "tables", name)) ? null : (await OpenDocumentAsync(TableData.FileFor(project, "tables", name), false, false))?.Document,
                MonsterCard, OpenInBuilderAsync);
            return owner.Document.Table?.Name switch
            {
                "missiles" => new MissileBuilderView(owner, host),
                "monstats" => new MonsterBuilderView(owner, host),
                "weapons" or "armor" or "misc" => new BaseItemBuilderView(owner, host),
                _ => new VisualBuilderView(owner, host)
            };
        };
    }

    /// <summary>
    /// An open table the item preview reads from disk that has unsaved edits, so a preview would show stale values; null when
    /// there is none. The item's own table is read from memory and does not count.
    /// </summary>
    private string? DirtyItemDependency(EditorPane pane) => tabs.Select(t => t.Content).OfType<EditorPane>().FirstOrDefault(p => p != pane && p.Document.IsDirty &&
        (p.Document.Table?.Name is "weapons" or "armor" or "misc" or "properties" or "itemstatcost" or "sets" or "skills" or "skilldesc" || p.Document.Table?.IsCatalog == true
            || p.Document.FilePath.Contains("compatibility" + System.IO.Path.DirectorySeparatorChar)))?.Document.FilePath;

    /// <summary>Opens a table in its Visual Builder on the first row whose column holds the value.</summary>
    private async Task OpenInBuilderAsync(string table, string column, string value)
    {
        if (project == null) return;
        var file = TableData.FileFor(project, "tables", table);
        Storage.Require(File.Exists(file), $"This project has no {table} table.");
        var pane = await OpenDocumentAsync(file);
        if (pane == null) return;
        pane.ShowVisualBuilder();
        if (pane.VisualBuilder is IVisualBuilder builder && builder.SelectWhere(column, value)) Status.Text = $"Opened {table} · {value}";
    }

    /// <summary>Something the builders read changed (another document, the profile, files on disk, the game data folder).</summary>
    private void RefreshVisualBuilders(Document? changed = null)
    {
        foreach (var pane in tabs.Select(t => t.Content).OfType<EditorPane>())
            if (pane.Document != changed) (pane.VisualBuilder as IVisualBuilder)?.Invalidate();
    }
}

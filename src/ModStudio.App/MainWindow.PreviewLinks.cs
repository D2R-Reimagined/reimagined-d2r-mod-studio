using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using ModStudio.Core;
using static ModStudio.Core.Storage;

namespace ModStudio.App;

public partial class MainWindow
{
    private void InitializePreviewLinks() => AddHandler(PreviewLinkText.LinkClickedEvent, PreviewLinkClicked);

    /// <summary>A preview link opens its cell; a value read from several cells offers them in a menu at the pointer.</summary>
    private async void PreviewLinkClicked(object? sender, PreviewLinkEventArgs e)
    {
        e.Handled = true;
        if (e.Targets.Length == 1) { await OpenCellLinkAsync(e.Targets[0]); return; }
        var items = e.Targets.Select(target =>
        {
            var item = new MenuItem { Header = target.ToString() };
            item.Click += async (_, _) => await OpenCellLinkAsync(target);
            return item;
        }).ToList<Control>();
        var menu = new ContextMenu { ItemsSource = items, Placement = PlacementMode.Pointer };
        e.Anchor.ContextMenu = menu;
        menu.Closed += (_, _) => { if (e.Anchor.ContextMenu == menu) e.Anchor.ContextMenu = null; };
        menu.Open(e.Anchor);
    }

    /// <summary>Opens the table a preview read and moves to the cell; false (with a status line) when that row or table is gone.</summary>
    internal async Task<bool> OpenCellLinkAsync(CellLink link)
    {
        try
        {
            if (project is not { } selectedProject) return false;
            var file = Semantics.TableFile(selectedProject, link.Table);
            if (!File.Exists(file)) { Status.Text = $"Missing table: {link.Table}."; return false; }
            var pane = await OpenDocumentAsync(file);
            if (project != selectedProject || pane?.Document is not { PendingSource: false, Table: { } table })
            { Status.Text = $"Apply the source changes in {link.Table} before following preview links into it."; return false; }
            int row = Enumerable.Range(0, table.Records.Count).FirstOrDefault(r => table.Records[r].S("sourceId") == link.SourceId, -1);
            if (row < 0) { Status.Text = $"{link.Table} no longer has the row this preview read. Refresh the preview."; return false; }
            pane.JumpToReference(row, link.Column);
            Status.Text = $"Opened {link.Table} · {link.Column} · row {row}";
            return true;
        }
        catch (Exception ex) { ShowError(ex); return false; }
    }
}

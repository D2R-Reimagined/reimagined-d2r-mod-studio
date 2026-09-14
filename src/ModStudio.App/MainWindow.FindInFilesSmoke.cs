using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using ModStudio.Core;
using static ModStudio.Core.Storage;

namespace ModStudio.App;

public partial class MainWindow
{
    private async Task SmokeFindInFilesAsync(string output)
    {
        TableData.Write(Semantics.TableFile(project!, "findsmoke"), TableData.FromTsv(Utf8.GetBytes("name\tcode\tlevel\nBroad Sword\tbsw\t5\nSmokeblade Axe\tbax\t7\n"), "findsmoke", "global/excel/findsmoke.txt"));
        var tableFile = Semantics.TableFile(project!, "findsmoke");
        TableData.Write(Semantics.TableFile(project!, "findtall"), TableData.FromTsv(Utf8.GetBytes("name\tcode\n" + string.Concat(Enumerable.Range(0, 80).Select(i => i == 45 ? "Smokeblade tall\ttall\n" : $"Filler {i}\tf{i}\n"))), "findtall", "global/excel/findtall.txt"));
        var tallFile = Semantics.TableFile(project!, "findtall");
        var notes = Inside(project!.Root, "source/find-smoke-notes.md"); File.WriteAllText(notes, "# Notes\n\nThe smokeblade needs a nerf.\n", Utf8);
        // An active tab to keep: background opens must not steal it.
        await OpenDocumentAsync(notes); var selectedBefore = Documents.SelectedItem;
        Require((selectedBefore as TabItem)?.Content is EditorPane { Document.FilePath: var active } && active == notes, "Smoke setup: notes file did not open.");
        async Task Wait(Func<bool> ready, string problem)
        {
            for (int attempt = 0; attempt < 500 && !ready(); attempt++) await Task.Delay(10);
            Require(ready(), problem);
        }
        var window = ShowFindInFiles("smokeblade")!;
        Require(window.Query.Text == "smokeblade" && window.CanResize && window.Owner == this, "Find in files did not open as a resizable, seeded child window.");
        await Wait(() => window.Result is { } r && r.Hits.Any(h => h.File == tableFile) && r.Hits.Any(h => h.File == notes), "Find in files did not return the table cell and the text line.");
        var cell = window.Result!.Hits.Single(h => h.IsCell && h.File == tableFile);
        var line = window.Result.Hits.Single(h => !h.IsCell && h.File == notes);
        Require(cell.Row == 1 && cell.Column == "name" && cell.Label == "Smokeblade Axe" && line.Line == 3 && line.Offset == 4, $"Unexpected hits: {cell} / {line}");
        Require(window.StatusText.StartsWith("3 matches in 3 of "), "Result summary: " + window.StatusText);

        // A hit deep in a long table is scrolled to the middle of the preview, not its last visible line.
        window.Results.SelectedItem = window.Result.Hits.Single(h => h.File == tallFile);
        await Wait(() => window.PreviewGrid.IsVisible && window.PreviewGrid.ItemsSource is PreviewRow[] { Length: 80 }, "Tall table preview did not show.");
        await Task.Delay(200); window.UpdateLayout();
        var tallRow = window.PreviewGrid.GetVisualDescendants().OfType<DataGridRow>().FirstOrDefault(r => r.DataContext is PreviewRow { Row: 45 });
        var tallY = tallRow?.TranslatePoint(new Point(0, 0), window.PreviewGrid)?.Y ?? -1;
        Require(tallRow != null && tallY > window.PreviewGrid.Bounds.Height * 0.3 && tallY < window.PreviewGrid.Bounds.Height * 0.7, $"Hit row is not centered in the preview: y={tallY:F0} of {window.PreviewGrid.Bounds.Height:F0}.");

        // The selected cell hit previews as an editable grid, with every row of the table and the match highlighted.
        window.Results.SelectedItem = cell;
        await Wait(() => window.PreviewGrid.IsVisible && window.PreviewGrid.ItemsSource is PreviewRow[] { Length: 2 } && window.PreviewGrid.SelectedItem is PreviewRow { Row: 1 }, "Table preview did not show the hit row.");
        Require(window.PreviewGrid.Columns.Select(c => c.Header?.ToString()).SequenceEqual(["name", "code", "level"]), "Preview columns: " + string.Join(", ", window.PreviewGrid.Columns.Select(c => c.Header)));
        await Task.Delay(150); window.UpdateLayout();
        using (var image = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96))) { image.Render(window); image.Save(System.IO.Path.Combine(output, "find-in-files.png"), PngBitmapEncoderOptions.Default); }

        // Editing in the preview opens the file in a background tab and changes its unsaved buffer; the active tab stays put.
        var rows = (PreviewRow[])window.PreviewGrid.ItemsSource!;
        Require(rows[1][0] == "Smokeblade Axe" && rows[0][1] == "bsw", "Preview rows do not read table cells.");
        rows[1][0] = "Smokeblade Axe (nerfed)";
        await Wait(() => PaneFor(tableFile) is { Document.IsDirty: true }, "A preview edit did not reach a document.");
        Require(Documents.SelectedItem == selectedBefore, $"Editing from Find in files switched the active tab: before={(selectedBefore as TabItem)?.Tag ?? ((selectedBefore as TabItem)?.Content as EditorPane)?.Document.FilePath ?? "null"} after={(Documents.SelectedItem as TabItem)?.Tag ?? ((Documents.SelectedItem as TabItem)?.Content as EditorPane)?.Document.FilePath ?? "null"}");
        Require(PaneFor(tableFile)!.Document.Table!.Cell(1, "name") == "Smokeblade Axe (nerfed)" && rows[1][0] == "Smokeblade Axe (nerfed)", "Preview edit did not land in the document, or the preview no longer reads the live document.");
        window.Query.Text = "nerfed";
        await Wait(() => window.Result is { Hits.Count: 1 } r && r.Hits[0].Text == "Smokeblade Axe (nerfed)", "Search did not see the unsaved edit.");
        Require(window.Previewed?.Row == 1 && window.PreviewGrid.IsVisible, "Preview did not follow the refined search.");

        // Enter opens the hit in the editor at the cell; a text hit opens Source view at its line with the match selected.
        await window.OpenSelectedAsync(close: false);
        await Wait(() => Active is { } pane && pane.Document.FilePath == tableFile && pane.SelectedRow == 1 && pane.SelectedColumn == "name", "Opening a cell hit did not select the cell in the editor.");
        // A broad search fills the list past the viewport; scrolling recycles containers, which re-templates them without an item.
        window.Query.Text = "e";
        await Wait(() => window.Result is { Hits.Count: > 60 }, "Broad search did not fill the results list.");
        window.Results.ScrollIntoView(window.Result!.Hits[^1]); window.UpdateLayout(); await Task.Delay(50);
        window.Results.ScrollIntoView(window.Result.Hits[0]); window.UpdateLayout(); await Task.Delay(50);
        window.Query.Text = "smokeblade";
        await Wait(() => window.Result is { Hits.Count: 3 } r && r.Hits.Any(h => !h.IsCell && h.File == notes), "Text hit disappeared.");
        window.Results.SelectedItem = window.Result!.Hits.Single(h => !h.IsCell && h.File == notes);
        await Wait(() => window.PreviewText.IsVisible && window.PreviewText.Text.Contains("smokeblade needs") && window.PreviewText.SelectedText.Equals("smokeblade", StringComparison.OrdinalIgnoreCase), "Text preview did not show the file with the match selected.");
        await window.OpenSelectedAsync(close: false);
        await Wait(() => Active is { } pane && pane.Document.FilePath == notes && pane.Source.IsVisible && pane.Source.TextArea.Caret.Line == 3, "Opening a text hit did not land on its line.");
        window.Close();
        Require(findInFiles == null, "Closing Find in files did not release it.");
        // Ctrl+Shift+F from the editor reopens the window seeded with the selection (the match ShowSourceLine selected).
        this.KeyPress(Key.F, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.F, null); await Task.Delay(50);
        Require(findInFiles is { } reopened && reopened.Query.Text == "smokeblade", "Ctrl+Shift+F did not open Find in files seeded with the editor selection.");
        findInFiles!.Close();

        // Leave the fixture as it was.
        PaneFor(tableFile)!.Document.Undo();
        foreach (var tab in tabs.Where(t => (t.Content as EditorPane)?.Document.FilePath is { } path && (path == tableFile || path == notes)).ToArray()) await CloseTabAsync(tab);
        File.Delete(tableFile); File.Delete(tallFile); File.Delete(notes);
    }
}

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.VisualTree;
using ModStudio.Core;
using static ModStudio.Core.Storage;

namespace ModStudio.App;

public partial class MainWindow
{
    private async Task SmokeRowCopyAsync(EditorPane pane, IClipboard clipboard)
    {
        var table = pane.Document.Table!;
        var columns = table.Columns;
        Require(columns.Length > 30, "Row-copy fixture needs offscreen columns.");
        var destinationBefore = columns.Select(c => table.Cell(1, c)).ToArray();
        pane.Jump(0, columns[30]); await Task.Delay(100); UpdateLayout();
        void ClickRowNumber(int row, RawInputModifiers modifiers = RawInputModifiers.None)
        {
            var header = pane.TableGrid.GetVisualDescendants().OfType<DataGridRowHeader>().First(h => h.GetVisualAncestors().OfType<DataGridRow>().FirstOrDefault() is { IsVisible: true, DataContext: RowView view } && view.Row == row);
            var point = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), this)!.Value;
            this.MouseDown(point, MouseButton.Left, modifiers); this.MouseUp(point, MouseButton.Left, modifiers);
        }
        ClickRowNumber(0); await Task.Delay(60);
        Require(pane.RowSelectionMode && pane.SelectedCells.Count == columns.Length && pane.SelectedCells.All(c => c.Row == 0), "Clicking a row number did not select all columns.");
        await clipboard.SetTextAsync(""); pane.TableGrid.Focus();
        this.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, null);
        string? copiedRow = null; var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(3) && (copiedRow = await clipboard.TryGetTextAsync()) == "") await Task.Delay(20);
        Require(copiedRow?.Split('\t').Length == columns.Length && copiedRow.Split('\t')[30] == table.Cell(0, columns[30]) && copiedRow.Split('\t')[^1] == table.Cell(0, columns[^1]), "Row copy missed offscreen columns.");
        ClickRowNumber(1); await Task.Delay(60);
        Require(pane.RowSelectionMode && pane.SelectedCells.Count == columns.Length && pane.SelectedCells.All(c => c.Row == 1), "Clicking the destination row number did not replace the row selection.");
        pane.TableGrid.Focus(); this.KeyPress(Key.V, RawInputModifiers.Control, PhysicalKey.V, null);
        timer.Restart();
        while (timer.Elapsed < TimeSpan.FromSeconds(3) && table.Cell(1, columns[1]) != table.Cell(0, columns[1])) await Task.Delay(20);
        Require(columns.All(c => table.Cell(1, c) == (table.IsIdentityColumn(c) ? destinationBefore[Array.IndexOf(columns, c)] : table.Cell(0, c))), "Row paste did not replace every editable column while preserving destination identity.");
        pane.Document.Undo(); pane.Refresh();
        Require(columns.Select(c => table.Cell(1, c)).SequenceEqual(destinationBefore), "One undo did not restore the entire pasted row.");
        var secondBefore = columns.Select(c => table.Cell(2, c)).ToArray();
        pane.Jump(0, columns[30]); await Task.Delay(100); UpdateLayout();
        ClickRowNumber(0); await Task.Delay(40); await pane.CopyAsync();
        ClickRowNumber(1); ClickRowNumber(2, RawInputModifiers.Shift); await Task.Delay(60);
        Require(pane.SelectedCells.Count == columns.Length * 2 && pane.SelectedCells.Select(c => c.Row).Distinct().Order().SequenceEqual([1, 2]), "Shift-click row numbers did not select both destination rows.");
        await pane.PasteAsync();
        Require(new[] { 1, 2 }.All(row => columns.All(c => table.IsIdentityColumn(c) || table.Cell(row, c) == table.Cell(0, c))), "One copied row did not fill both selected rows.");
        pane.Document.Undo(); pane.Refresh();
        Require(columns.Select(c => table.Cell(1, c)).SequenceEqual(destinationBefore) && columns.Select(c => table.Cell(2, c)).SequenceEqual(secondBefore), "One undo did not restore both pasted rows.");
        await SmokeFullRowIntoAddedCellAsync(pane);
        await SmokeRowSelectionEditAsync(pane);
        await SmokeHighlightsAsync(pane);
    }

    /// <summary>
    /// A row picked from its header marks the row; it is not a request to write the same value into all forty
    /// columns. The first edit inside such a row must collapse to the cell being typed.
    /// </summary>
    private async Task SmokeRowSelectionEditAsync(EditorPane pane)
    {
        var table = pane.Document.Table!; var columns = table.Columns;
        int column = 30; var before = columns.Select(c => table.Cell(0, c)).ToArray();
        pane.Jump(0, columns[column]); await Task.Delay(100); UpdateLayout();
        var header = pane.TableGrid.GetVisualDescendants().OfType<DataGridRowHeader>().First(h => h.GetVisualAncestors().OfType<DataGridRow>().FirstOrDefault() is { IsVisible: true, DataContext: RowView view } && view.Row == 0);
        var point = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), this)!.Value;
        this.MouseDown(point, MouseButton.Left, RawInputModifiers.None); this.MouseUp(point, MouseButton.Left, RawInputModifiers.None); await Task.Delay(60);
        Require(pane.RowSelectionMode && pane.SelectedCells.Count == columns.Length, "Clicking a row number did not select the whole row.");
        pane.TableGrid.Focus(); this.KeyTextInput("Z"); await Task.Delay(120);
        Require(pane.SelectedCells.Count == 1 && pane.SelectedCells.Contains((0, column)),
            $"Typing in a header-selected row did not collapse the edit to the typed cell: {pane.SelectedCells.Count} cells selected.");
        Require(pane.TableGrid.GetVisualDescendants().OfType<TextBox>().Count(t => t.IsVisible && t.Text == "Z") == 1, "Typing in a header-selected row opened an editor in more than one column.");
        this.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); await Task.Delay(120);
        Require(table.Cell(0, columns[column]) == "Z", $"The typed cell was not committed: '{table.Cell(0, columns[column])}'.");
        for (int i = 0; i < columns.Length; i++)
            Require(i == column || table.Cell(0, columns[i]) == before[i], $"Typing in a header-selected row also wrote into {columns[i]}.");
        pane.Document.Undo(); pane.Refresh();
        Require(columns.Select(c => table.Cell(0, c)).SequenceEqual(before), "Undo did not restore the row-selection edit.");
    }

    /// <summary>Row and column highlights paint, follow their records through an insert, and clear from the toolbar button.</summary>
    private async Task SmokeHighlightsAsync(EditorPane pane)
    {
        var table = pane.Document.Table!; var columns = table.Columns;
        Require(table.Records.Count > 6, "Highlight smoke needs a few rows.");
        int revision = pane.Document.Revision;
        var clear = this.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Clear the highlighted rows and columns");
        Require(clear is { IsVisible: false }, "The clear-highlights button is missing or shown before anything is highlighted.");
        pane.Jump(2, columns[1]); await Task.Delay(100); UpdateLayout();
        pane.SelectCell(2, 1); pane.ToggleRowHighlight(); pane.ToggleColumnHighlight(3);
        await Task.Delay(120); UpdateLayout(); await Task.Delay(60);
        Require(pane.HighlightedRows.Contains(2) && pane.HighlightedColumns.Contains(3) && pane.HasHighlights, "Highlighting a row and a column did not record them.");
        Require(clear!.IsVisible, "The clear-highlights button stayed hidden while rows were highlighted.");
        Require(pane.Document.Revision == revision, "Highlighting changed the document.");
        var painted = pane.TableGrid.GetVisualDescendants().OfType<DataGridRow>().FirstOrDefault(r => r.IsVisible && r.DataContext is RowView v && v.Row == 2);
        Require(painted != null && painted.GetVisualDescendants().OfType<DataGridCell>().Any(c => c.Background is SolidColorBrush { Color: var tint } && tint == Color.Parse("#26384A")), "A highlighted row is not painted.");
        using (var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96)))
        {
            bitmap.Render(this);
            bitmap.Save(System.IO.Path.Combine(Program.Arguments[Array.IndexOf(Program.Arguments, "--smoke") + 2], "row-column-highlights.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        // Highlights mark records, so rows inserted above them carry them down.
        pane.InsertRows(0); await Task.Delay(60);
        Require(pane.HighlightedRows.Contains(3) && !pane.HighlightedRows.Contains(2), $"Row highlight did not follow its record past an insert: [{string.Join(" ", pane.HighlightedRows)}].");
        // Undo rebuilds the view without row mapping (frozen rows and the cell selection stay put the same way),
        // so the highlight keeps its row number; it must at least stay inside the table.
        pane.Document.Undo(); pane.Refresh(); await Task.Delay(60);
        Require(pane.HighlightedRows.All(r => r >= 0 && r < pane.Document.Table!.Records.Count), "Undoing the insert left a highlight outside the table.");
        pane.ClearHighlights(); await Task.Delay(60);
        Require(!pane.HasHighlights && !clear.IsVisible, "Clearing highlights left them behind or kept the button on the toolbar.");
    }

    private async Task SmokeFullRowIntoAddedCellAsync(EditorPane pane)
    {
        // Add row below selects one cell, not a row header. A full-row clipboard still pastes all offscreen fields.
        var table = pane.Document.Table!; var columns = table.Columns;
        Require(columns.Length > 30 && table.Records.Count > 1, "Full-row paste fixture needs a wide table and two rows.");
        var destinationBefore = columns.Select(c => table.Cell(1, c)).ToArray();
        pane.Jump(0, columns[30]); await pane.CopyAsync(false);
        int originalCount = table.Records.Count;
        pane.InsertRows(1);
        pane.SelectCell(1, 30);
        Require(!pane.RowSelectionMode && pane.SelectedColumn == columns[30], "Inserted row did not keep a cell selection on the current page.");
        await pane.PasteAsync();
        Require(columns.All(c => table.Cell(1, c) == (table.IsIdentityColumn(c) ? "" : table.Cell(0, c))), "Full-row paste into an inserted cell lost offscreen fields or copied identity.");
        pane.Document.Undo(); pane.Document.Undo(); pane.Refresh();
        Require(table.Records.Count == originalCount && columns.Select(c => table.Cell(1, c)).SequenceEqual(destinationBefore), "Undo did not restore the inserted-row paste.");
    }
}

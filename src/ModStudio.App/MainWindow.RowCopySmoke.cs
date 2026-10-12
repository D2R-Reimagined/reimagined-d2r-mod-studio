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
        await SmokeRowFillAsync(pane, clipboard);
        await SmokeCutAsync(pane, clipboard);
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

    /// <summary>Ctrl+X copies the selection and clears it in one undo step; a header-picked row keeps its identity columns.</summary>
    private async Task SmokeCutAsync(EditorPane pane, IClipboard clipboard)
    {
        var table = pane.Document.Table!; var columns = table.Columns;
        int column = Enumerable.Range(1, columns.Length - 1).First(c => !table.IsIdentityColumn(columns[c]) && table.Cell(0, columns[c]) != "");
        var before = columns.Select(c => table.Cell(0, c)).ToArray();
        pane.Jump(0, columns[column]); pane.SelectCell(0, column); await Task.Delay(60);
        await clipboard.SetTextAsync(""); pane.TableGrid.Focus();
        this.KeyPress(Key.X, RawInputModifiers.Control, PhysicalKey.X, null);
        string? cutCell = null; var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(3) && (cutCell = await clipboard.TryGetTextAsync()) == "") await Task.Delay(20);
        Require(cutCell == before[column] && table.Cell(0, columns[column]) == "", $"Ctrl+X on a cell did not copy and clear it: clipboard '{cutCell}', cell '{table.Cell(0, columns[column])}'.");
        Require(columns.Where((_, i) => i != column).All(c => table.Cell(0, c) == before[Array.IndexOf(columns, c)]), "Ctrl+X on a cell cleared other cells.");
        pane.Document.Undo(); pane.Refresh();
        Require(columns.Select(c => table.Cell(0, c)).SequenceEqual(before), "One undo did not restore the cut cell.");

        pane.Jump(0, columns[30]); await Task.Delay(100); UpdateLayout();
        var header = pane.TableGrid.GetVisualDescendants().OfType<DataGridRowHeader>().First(h => h.GetVisualAncestors().OfType<DataGridRow>().FirstOrDefault() is { IsVisible: true, DataContext: RowView view } && view.Row == 0);
        var point = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), this)!.Value;
        this.MouseDown(point, MouseButton.Left, RawInputModifiers.None); this.MouseUp(point, MouseButton.Left, RawInputModifiers.None); await Task.Delay(60);
        Require(pane.RowSelectionMode, "Clicking a row number did not select the row for cutting.");
        await clipboard.SetTextAsync(""); pane.TableGrid.Focus();
        this.KeyPress(Key.X, RawInputModifiers.Control, PhysicalKey.X, null);
        string? cutRow = null; timer.Restart();
        while (timer.Elapsed < TimeSpan.FromSeconds(3) && (cutRow = await clipboard.TryGetTextAsync()) == "") await Task.Delay(20);
        Require(cutRow == string.Join('\t', before), "Ctrl+X on a header-picked row did not copy every column.");
        Require(columns.All(c => table.Cell(0, c) == (table.IsIdentityColumn(c) ? before[Array.IndexOf(columns, c)] : "")), "Ctrl+X on a row did not clear every editable column while keeping its identity.");
        pane.Document.Undo(); pane.Refresh();
        Require(columns.Select(c => table.Cell(0, c)).SequenceEqual(before), "One undo did not restore the cut row.");
    }

    /// <summary>
    /// Several cells copied from one row fill every selected row: cells picked down a column take them from that column,
    /// rows picked by their header take them back in the columns they were copied from, and a whole row fills every row with a selected cell.
    /// </summary>
    private async Task SmokeRowFillAsync(EditorPane pane, IClipboard clipboard)
    {
        var table = pane.Document.Table!; var columns = table.Columns;
        Require(table.Records.Count > 4, "Row-fill smoke needs a few rows.");
        int first = Enumerable.Range(30, columns.Length - 32).First(i => Enumerable.Range(i, 3).All(c => !table.IsIdentityColumn(columns[c])));
        int[] copied = [first, first + 1, first + 2];
        pane.Document.SetCells(copied.Select(c => (0, columns[c], $"fill{c}")));
        var before = Enumerable.Range(1, 3).ToDictionary(r => r, r => columns.Select(c => table.Cell(r, c)).ToArray());
        bool Untouched(int row, Func<int, bool> filled) => columns.Select((c, i) => filled(i) || table.Cell(row, c) == before[row][i]).All(ok => ok);
        bool Filled(int row) => copied.All(c => table.Cell(row, columns[c]) == $"fill{c}");
        bool Restored() => Enumerable.Range(1, 3).All(r => columns.Select(c => table.Cell(r, c)).SequenceEqual(before[r]));

        pane.Jump(0, columns[first]); await Task.Delay(100); UpdateLayout();
        pane.SelectCell(0, first); pane.SelectCell(0, first + 2, shift: true); await pane.CopyAsync();
        Require(await clipboard.TryGetTextAsync() == string.Join('\t', copied.Select(c => $"fill{c}")), "Copying three cells of a row did not produce one tab-separated line.");

        // Cells picked down one column: the copied row starts at that column in every picked row.
        pane.SelectCell(1, first); pane.SelectCell(3, first, shift: true);
        Require(pane.SelectedCells.Count == 3, "Shift-click did not select a column of three cells.");
        await pane.PasteAsync();
        Require(new[] { 1, 2, 3 }.All(r => Filled(r) && Untouched(r, copied.Contains)), "Pasting one copied row into a column of selected cells did not fill every selected row.");
        Require(pane.SelectedCells.Count == 9, $"The filled block is not selected after the paste: {pane.SelectedCells.Count} cells.");
        pane.Document.Undo(); pane.Refresh();
        Require(Restored(), "One undo did not restore every filled row.");

        // Rows picked by their numbers: the cells go back into the columns they were copied from, not the first columns.
        pane.Jump(1, columns[first]); await Task.Delay(100); UpdateLayout();
        void ClickRowNumber(int row, RawInputModifiers modifiers = RawInputModifiers.None)
        {
            var header = pane.TableGrid.GetVisualDescendants().OfType<DataGridRowHeader>().First(h => h.GetVisualAncestors().OfType<DataGridRow>().FirstOrDefault() is { IsVisible: true, DataContext: RowView view } && view.Row == row);
            var point = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), this)!.Value;
            this.MouseDown(point, MouseButton.Left, modifiers); this.MouseUp(point, MouseButton.Left, modifiers);
        }
        ClickRowNumber(1); ClickRowNumber(3, RawInputModifiers.Shift); await Task.Delay(60);
        Require(pane.RowSelectionMode && pane.SelectedCells.Select(c => c.Row).Distinct().Order().SequenceEqual([1, 2, 3]), "Shift-click row numbers did not select three rows.");
        pane.TableGrid.Focus(); this.KeyPress(Key.V, RawInputModifiers.Control, PhysicalKey.V, null);
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(3) && !Filled(3)) await Task.Delay(20);
        Require(new[] { 1, 2, 3 }.All(r => Filled(r) && Untouched(r, copied.Contains)), "Pasting copied cells into header-picked rows did not fill their original columns in every row.");
        pane.Document.Undo(); pane.Refresh();
        Require(Restored(), "One undo did not restore the header-picked rows.");

        // A whole copied row fills every row with a selected cell, keeping each row's identity.
        pane.Jump(0, columns[first]); await pane.CopyAsync(false);
        pane.SelectCell(1, first); pane.SelectCell(2, first, shift: true);
        await pane.PasteAsync();
        Require(new[] { 1, 2 }.All(r => columns.All(c => table.Cell(r, c) == (table.IsIdentityColumn(c) ? before[r][Array.IndexOf(columns, c)] : table.Cell(0, c)))) && Untouched(3, _ => false),
            "A whole copied row did not fill both rows with a selected cell.");
        pane.Document.Undo(); pane.Refresh();
        Require(Restored(), "One undo did not restore the whole-row fill.");
        pane.Document.Undo(); pane.Refresh();
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

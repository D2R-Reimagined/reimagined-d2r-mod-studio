using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
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

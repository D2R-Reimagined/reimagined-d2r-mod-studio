using Avalonia.Controls;
using Avalonia.Media;
using ModStudio.Core;

namespace ModStudio.App;

/// <summary>
/// View-only row and column highlights: a marker the user paints on the table to keep track of what they are
/// comparing while they work. Highlights never touch the file, are independent of the cell selection, and the
/// toolbar button that clears them is only shown while something is highlighted.
/// </summary>
public sealed partial class EditorPane
{
    private readonly HashSet<int> highlightedRows = [];
    private readonly HashSet<int> highlightedColumns = [];
    private Button? clearHighlightsButton;
    private static readonly IBrush HighlightBrush = new SolidColorBrush(Color.Parse("#26384A"));
    /// <summary>Where a highlighted row crosses a highlighted column; both markers stay readable.</summary>
    private static readonly IBrush HighlightCrossBrush = new SolidColorBrush(Color.Parse("#31506D"));
    public IReadOnlyCollection<int> HighlightedRows => highlightedRows;
    public IReadOnlyCollection<int> HighlightedColumns => highlightedColumns;
    public bool HasHighlights => highlightedRows.Count > 0 || highlightedColumns.Count > 0;

    /// <summary>Highlights the rows the row commands act on, or removes the highlight when they all carry one.</summary>
    public void ToggleRowHighlight()
    {
        var rows = SelectedRowsForCommands();
        Storage.Require(rows.Length > 0, "Select rows to highlight.");
        if (rows.All(highlightedRows.Contains)) highlightedRows.ExceptWith(rows); else highlightedRows.UnionWith(rows);
        HighlightsChanged();
    }
    public void ToggleColumnHighlight(int index)
    {
        if (Document.Table == null || index < 0 || index >= Document.Table.Columns.Length) return;
        if (!highlightedColumns.Remove(index)) highlightedColumns.Add(index);
        HighlightsChanged(headers: true);
    }
    public void ClearHighlights()
    {
        bool hadColumns = highlightedColumns.Count > 0;
        highlightedRows.Clear(); highlightedColumns.Clear();
        HighlightsChanged(headers: hadColumns);
    }
    private void HighlightsChanged(bool headers = false)
    {
        if (headers && Document.Table != null && !Document.PendingSource) UpdateColumnHeaders();
        UpdateHighlightButton(); QueuePaint();
    }
    private void UpdateHighlightButton() { if (clearHighlightsButton != null) clearHighlightsButton.IsVisible = HasHighlights; }
    /// <summary>Drops highlights whose row or column is gone after a re-parse, a delete or a schema change.</summary>
    private void PruneHighlights()
    {
        if (Document.Table is not { } table) return;
        highlightedRows.RemoveWhere(r => r < 0 || r >= table.Records.Count);
        highlightedColumns.RemoveWhere(c => c < 0 || c >= table.Columns.Length);
    }
    /// <summary>Row highlights follow their records when rows are inserted or removed above them.</summary>
    private void ShiftHighlightedRows(int from, int delta)
    {
        var moved = highlightedRows.Where(r => r >= from).ToArray(); highlightedRows.ExceptWith(moved);
        highlightedRows.UnionWith(moved.Select(r => r + delta).Where(r => r >= from));
    }
}

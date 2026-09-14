using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using ModStudio.Core;

namespace ModStudio.App;

public sealed partial class EditorPane
{
    private static readonly IBrush ReferenceRowBrush = new SolidColorBrush(Color.Parse("#393321"));
    internal int HighlightedReferenceRow { get; private set; } = -1;

    /// <summary>Reveals a reference destination and calls out its row without selecting every cell for editing.</summary>
    public void JumpToReference(int row, string column)
    {
        if (Document.Table is not { } table || Document.PendingSource || row < 0 || row >= table.Records.Count) return;
        var grid = frozenRows.Contains(row) ? FrozenGrid : TableGrid;
        var item = (grid.ItemsSource as IEnumerable<RowView>)?.FirstOrDefault(r => r.Row == row);
        if (tableHost.IsVisible && item != null)
        {
            Reveal(row, column);
            var target = grid.Columns.FirstOrDefault(c => columnMap.TryGetValue(c, out var i) && table.Columns[i] == column);
            if (target != null) RestoreAfterLayout(grid, item, target, true);
        }
        else Jump(row, column);
        HighlightedReferenceRow = row;
        QueuePaint();
    }

    private void ClearReferenceHighlight()
    {
        if (HighlightedReferenceRow < 0) return;
        HighlightedReferenceRow = -1;
        QueuePaint();
    }

    public event Action<EditorPane, int, string, Control>? ReferenceRequested;

    private bool HasCellReference(int column) => Document.Table is { IsCatalog: false } table &&
        column >= 0 && column < table.Columns.Length && CellReferences.Rule(table.Name, table.Columns[column]) != null;

    private static bool IsReferenceButton(object? source) => source is Visual visual &&
        visual.GetSelfAndVisualAncestors().OfType<Button>().Any(button => button.Classes.Contains("cellReference"));

    private void RequestCellReference(int row, int column, Control anchor)
    {
        if (!activeGrid.CommitEdit(DataGridEditingUnit.Cell, true) || !HasCellReference(column) ||
            Document.PendingSource || row < 0 || row >= Document.Table!.Records.Count) return;
        if (!string.IsNullOrWhiteSpace(Document.Table.Cell(row, Document.Table.Columns[column])))
            ReferenceRequested?.Invoke(this, row, Document.Table.Columns[column], anchor);
    }
}

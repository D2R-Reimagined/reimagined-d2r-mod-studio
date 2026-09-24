using Avalonia.Controls;

namespace ModStudio.App;

/// <summary>The Column Guide tab of the bottom panel: the data guide for the selected cell's column, following the selection live.</summary>
public partial class MainWindow
{
    private ColumnGuideView columnGuide = null!;
    private TabItem columnGuideTab = null!;
    /// <summary>A column asked for explicitly (header menu, Row Editor label) while the selection is elsewhere; it holds until the selection moves.</summary>
    private (EditorPane Pane, string Column, int Row, string Selected)? columnGuidePin;

    private void InitializeColumnGuide()
    {
        columnGuide = new ColumnGuideView(ShowError);
        columnGuideTab = new TabItem { Header = new TextBlock { Text = "Column Guide", FontSize = 13 }, Content = columnGuide };
        ToolTip.SetTip(columnGuideTab, "The data guide for the column of the selected cell (F1)");
        BottomTabs.Items.Add(columnGuideTab);
        BottomTabs.SelectionChanged += (_, _) => RefreshColumnGuide();
    }

    /// <summary>Brings up the Column Guide tab on a column of a pane (toolbar button, F1, header menu, Row Editor label).</summary>
    private void OpenColumnGuide(EditorPane pane, string column)
    {
        columnGuidePin = (pane, column, pane.SelectedRow, pane.SelectedColumn);
        ShowBottomTab(BottomTabs.Items.IndexOf(columnGuideTab));
        RefreshColumnGuide();
    }

    private void RefreshColumnGuide()
    {
        // Only the visible tab is kept current; selecting it catches up.
        if (columnGuide == null || !BottomPanel.IsVisible || BottomTabs.SelectedItem != columnGuideTab) return;
        var pane = Active; var table = pane?.Document.Table;
        if (pane == null || table == null) { columnGuide.Show(null, null, null); return; }
        int row = pane.SelectedRow; bool hasRow = row >= 0 && row < table.Records.Count;
        if (columnGuidePin is { } pin && (pin.Pane != pane || pin.Row != row || pin.Selected != pane.SelectedColumn)) columnGuidePin = null;
        var column = columnGuidePin?.Column ?? (hasRow ? pane.SelectedColumn : null);
        var value = hasRow && column != null && table.ColumnIndex(column) >= 0 ? table.Cell(row, column) : null;
        columnGuide.Show(table, column, value);
    }
}

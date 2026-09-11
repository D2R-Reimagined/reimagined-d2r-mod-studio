using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Media;

namespace ModStudio.App;

public partial class MainWindow
{
    private void RefreshRowEditor(EditorPane pane)
    {
        RowEditorFields.Children.Clear();
        var document = pane.Document;
        var table = document.Table;
        int row = pane.SelectedRow;
        if (table == null || document.PendingSource || row < 0 || row >= table.Records.Count)
        {
            RowEditorLabel.Text = document.PendingSource ? "Apply valid source before editing rows." : "Select a table row";
            return;
        }
        RowEditorLabel.Text = $"{table.Name} · row {row} · {table.Columns.Length} fields";
        RowEditorStatus.Text = "Edits apply immediately to the shared source. Save to write to disk.";
        var identities = (table.Schema["identityColumns"] as JsonArray)?.Select(x => x!.GetValue<string>()).ToHashSet() ?? [];
        foreach (var column in table.Columns)
        {
            bool identity = table.IsCatalog ? column is "id" or "Key" : identities.Contains(column);
            bool locked = document.LockedRows.Contains(row) || document.LockedColumns.Contains(column);
            var field = new StackPanel { Spacing = 3 };
            field.Children.Add(new TextBlock { Text = column + (identity ? " · identity (read-only)" : locked ? " · locked" : ""), TextWrapping = TextWrapping.Wrap });
            var input = new TextBox { Text = table.Cell(row, column), IsReadOnly = identity || locked,
                AcceptsReturn = table.IsCatalog, TextWrapping = TextWrapping.Wrap, MinHeight = 32, MaxHeight = 180 };
            Avalonia.Automation.AutomationProperties.SetName(input, column);
            bool updating = false;
            input.TextChanged += (_, _) =>
            {
                if (updating || input.IsReadOnly || Active != pane || pane.SelectedRow != row || document.PendingSource) return;
                try
                {
                    document.SetCells([(row, column, input.Text ?? "")]);
                    pane.RefreshRowValues(row);
                    if (FieldPicker.SelectedItem as string == column) CellValue.Text = input.Text;
                    RowEditorStatus.Text = "Edits applied · Save to write to disk. Undo is available in the table toolbar.";
                }
                catch (Exception ex)
                {
                    RowEditorStatus.Text = ex.Message;
                    updating = true; input.Text = document.Table!.Cell(row, column); updating = false;
                }
            };
            field.Children.Add(input); RowEditorFields.Children.Add(field);
        }
    }
}

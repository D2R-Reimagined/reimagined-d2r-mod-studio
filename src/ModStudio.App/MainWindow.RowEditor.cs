using System.ComponentModel;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Threading;
using ModStudio.Core;

namespace ModStudio.App;

public partial class MainWindow
{
    private bool rowEditorRefreshQueued;
    private EditorPane? rowEditorPane;
    private int rowEditorRow = -1, rowEditorRevision = -1;
    private TableData? rowEditorTable;
    private string rowEditorLocks = "";
    private int rowEditorRefreshCount;
    private RowEditorField[] rowEditorAllFields = [];
    private void FilterRowEditor()
    {
        var term = (RowEditorSearch.Text ?? "").Trim();
        RowEditorFields.ItemsSource = term.Length == 0 ? rowEditorAllFields : rowEditorAllFields.Where(f => f.Column.Contains(term, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (rowEditorTable != null) RowEditorLabel.Text = $"{rowEditorTable.Name} · row {rowEditorRow} · {RowEditorFields.ItemCount}/{rowEditorAllFields.Length} fields";
    }

    private void InitializeRowEditor()
    {
        if (!Program.Arguments.Contains("--smoke"))
            try { RowEditorSearch.Text = StudioPreferences.Load(StudioPreferences.DefaultFile).RowEditorSearch; } catch (Exception) { }
        var searchSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        searchSave.Tick += (_, _) => {
            searchSave.Stop();
            if (Program.Arguments.Contains("--smoke")) return;
            try { var prefs = StudioPreferences.Load(StudioPreferences.DefaultFile); prefs.RowEditorSearch = RowEditorSearch.Text ?? ""; prefs.Save(StudioPreferences.DefaultFile); }
            catch (Exception ex) { ShowError(ex); }
        };
        Closed += (_, _) => searchSave.Stop();
        RowEditorSearch.TextChanged += (_, _) => { FilterRowEditor(); searchSave.Stop(); searchSave.Start(); };
        RowEditorFields.ItemTemplate = new FuncDataTemplate<RowEditorField>((field, _) =>
        {
            if (field == null) return new Border();
            var panel = new StackPanel { Spacing = 3, Margin = new(0, 0, 24, 10) };
            var label = new TextBlock { Text = field.Label, TextWrapping = TextWrapping.Wrap };
            if (field.Guide != null) { label.TextDecorations = TextDecorations.Underline; label.Foreground = new SolidColorBrush(Color.Parse("#D8BC86")); }
            panel.Children.Add(label);
            var input = new TextBox { IsReadOnly = field.ReadOnly, AcceptsReturn = field.Multiline,
                TextWrapping = TextWrapping.Wrap, MinHeight = 32, MaxHeight = 180 };
            Avalonia.Automation.AutomationProperties.SetName(input, field.Column);
            input.Bind(TextBox.TextProperty, new Binding(nameof(RowEditorField.Value)) { Source = field, Mode = BindingMode.TwoWay });
            panel.Children.Add(input);
            // Hovering the label or the input shows what the column means, from the bundled data guide.
            if (field.Guide != null) ToolTip.SetTip(panel, ColumnGuideTooltip.Create(field.Table, field.Column, field.Guide));
            return panel;
        });
        InspectorTabs.SelectionChanged += (_, e) =>
        {
            if (e.Source != InspectorTabs) return;
            RefreshRowEditor(Active);
            _ = RefreshSemanticInspectorAsync();
        };
    }

    private void RefreshRowEditor(EditorPane? pane)
    {
        // SelectionChanged and CurrentCellChanged often arrive together. Work on the latest active row.
        if (rowEditorRefreshQueued || InspectorTabs?.SelectedIndex != 1) return;
        rowEditorRefreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            rowEditorRefreshQueued = false;
            if (InspectorTabs.SelectedIndex != 1) return;
            UpdateRowEditor();
        }, DispatcherPriority.Background);
    }

    private void UpdateRowEditor()
    {
        var pane = Active;
        var document = pane?.Document;
        var table = document?.Table;
        int row = pane?.SelectedRow ?? -1;
        if (pane == null || table == null || document!.PendingSource || row < 0 || row >= table.Records.Count)
        {
            rowEditorAllFields = []; RowEditorFields.ItemsSource = null;
            rowEditorPane = null; rowEditorTable = null;
            RowEditorLabel.Text = document?.PendingSource == true ? "Apply valid source before editing rows." : "Select a table row";
            return;
        }
        var locks = string.Join('\n', document.LockedColumns.Order()) + "|" + document.LockedRows.Contains(row);
        if (rowEditorPane == pane && rowEditorRow == row && rowEditorRevision == document.Revision &&
            rowEditorTable == table && rowEditorLocks == locks) return;
        rowEditorPane = pane; rowEditorRow = row; rowEditorRevision = document.Revision;
        rowEditorTable = table; rowEditorLocks = locks; rowEditorRefreshCount++;
        RowEditorLabel.Text = $"{table.Name} · row {row} · {table.Columns.Length} fields";
        RowEditorStatus.Text = "Edits apply immediately to the shared source. Save to write to disk.";
        var identities = (table.Schema["identityColumns"] as JsonArray)?.Select(x => x!.GetValue<string>()).ToHashSet() ?? [];
        rowEditorAllFields = table.Columns.Select(column =>
        {
            bool identity = table.IsCatalog ? column is "id" or "Key" : identities.Contains(column);
            bool locked = document.LockedRows.Contains(row) || document.LockedColumns.Contains(column);
            return new RowEditorField(column, column + (identity ? " · identity (read-only)" : locked ? " · locked" : ""),
                table.Cell(row, column), identity || locked, table.IsCatalog, table.Name, table.IsCatalog ? null : ColumnGuide.Find(table.Name, column), field =>
                {
                    // Detached controls and queued binding updates must never edit a newly selected row/document.
                    if (Active != pane || pane.SelectedRow != row || document.PendingSource || field.ReadOnly ||
                        RowEditorFields.ItemsSource is not RowEditorField[] current || !current.Contains(field)) return;
                    if (document.Table!.Cell(row, column) == field.Value) return;
                    try
                    {
                        document.SetCells([(row, column, field.Value)]);
                        rowEditorRevision = document.Revision;
                        pane.RefreshRowValues(row);
                        if (FieldPicker.SelectedItem as string == column) CellValue.Text = field.Value;
                        RowEditorStatus.Text = "Edits applied · Save to write to disk. Undo is available in the table toolbar.";
                    }
                    catch (Exception ex)
                    {
                        RowEditorStatus.Text = ex.Message;
                        field.Reset(document.Table!.Cell(row, column));
                    }
                });
        }).ToArray();
        FilterRowEditor();
    }

    private sealed class RowEditorField(string column, string label, string value, bool readOnly, bool multiline, string table, ColumnGuideEntry? guide, Action<RowEditorField> edit) : INotifyPropertyChanged
    {
        public string Column { get; } = column;
        public string Table { get; } = table;
        public ColumnGuideEntry? Guide { get; } = guide;
        public string Label { get; } = label;
        public bool ReadOnly { get; } = readOnly;
        public bool Multiline { get; } = multiline;
        private string current = value;
        public string Value
        {
            get => current;
            set { if (current == value) return; current = value; edit(this); PropertyChanged?.Invoke(this, new(nameof(Value))); }
        }
        public void Reset(string value) { current = value; PropertyChanged?.Invoke(this, new(nameof(Value))); }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}

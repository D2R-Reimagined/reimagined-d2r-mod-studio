using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using ModStudio.Core;
using static ModStudio.Core.Storage;

namespace ModStudio.App;

/// <summary>Tool panels minimize to a slim strip (as in JetBrains IDEs); clicking a strip tab restores the panel and selects that tool.</summary>
public partial class MainWindow
{
    private sealed record ToolPanel(string Key, Control Panel, GridSplitter Splitter, Control Strip, Func<GridLength> Size, Action<GridLength> Resize, GridLength Default)
    {
        public GridLength Saved { get; set; } = Default;
        public bool Visible => Panel.IsVisible;
    }
    private ToolPanel[] toolPanels = [];

    private void InitializeLayout()
    {
        toolPanels =
        [
            new("explorer", ExplorerPanel, LeftSplitter, LeftStrip, () => WorkspaceGrid.ColumnDefinitions[1].Width, w => WorkspaceGrid.ColumnDefinitions[1].Width = w, new GridLength(240)),
            new("inspector", InspectorPanel, RightSplitter, RightStrip, () => WorkspaceGrid.ColumnDefinitions[5].Width, w => WorkspaceGrid.ColumnDefinitions[5].Width = w, new GridLength(270)),
            new("bottom", BottomPanel, BottomSplitter, BottomStrip, () => RootGrid.RowDefinitions[3].Height, h => RootGrid.RowDefinitions[3].Height = h, new GridLength(190)),
        ];
        HideExplorerButton.Content = ExplorerIcons.Tool("M10,3 L5,8 L10,13"); HideExplorerButton.Click += (_, _) => SetPanelVisible("explorer", false);
        HideInspectorButton.Content = ExplorerIcons.Tool("M6,3 L11,8 L6,13"); HideInspectorButton.Click += (_, _) => SetPanelVisible("inspector", false);
        HideBottomButton.Content = ExplorerIcons.Tool("M3,6 L8,11 L13,6"); HideBottomButton.Click += (_, _) => SetPanelVisible("bottom", false);
        foreach (var tab in InspectorTabs.Items.OfType<TabItem>()) tab.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) RebuildStrips(); };
        RebuildStrips();
        if (Program.Arguments.Contains("--smoke")) return;
        try { foreach (var key in StudioPreferences.Load(StudioPreferences.DefaultFile).HiddenPanels) SetPanelVisible(key, false, persist: false); }
        catch (Exception) { }
    }

    private void SetPanelVisible(string key, bool visible, bool persist = true)
    {
        var panel = toolPanels.FirstOrDefault(p => p.Key == key);
        if (panel == null || panel.Visible == visible) return;
        if (visible)
        {
            panel.Panel.IsVisible = panel.Splitter.IsVisible = true; panel.Strip.IsVisible = false;
            panel.Resize(panel.Saved.Value >= 40 ? panel.Saved : panel.Default);
        }
        else
        {
            var current = panel.Size();
            if (current.IsAbsolute && current.Value >= 40) panel.Saved = current;
            panel.Resize(new GridLength(0));
            panel.Panel.IsVisible = panel.Splitter.IsVisible = false; panel.Strip.IsVisible = true;
        }
        if (!persist || Program.Arguments.Contains("--smoke")) return;
        try
        {
            var prefs = StudioPreferences.Load(StudioPreferences.DefaultFile);
            prefs.HiddenPanels = toolPanels.Where(p => !p.Visible).Select(p => p.Key).ToList();
            prefs.Save(StudioPreferences.DefaultFile);
        }
        catch (Exception ex) { Status.Text = "Could not save layout: " + ex.Message; }
    }

    /// <summary>Selects a bottom tab and restores the panel if it was minimized, so pushed output (errors, builds) is not hidden.</summary>
    private void ShowBottomTab(int index) { SetPanelVisible("bottom", true); BottomTabs.SelectedIndex = index; }

    private void RebuildStrips()
    {
        LeftStripItems.Children.Clear();
        LeftStripItems.Children.Add(StripTab("Project", -90, "Show the project explorer", () => SetPanelVisible("explorer", true)));
        RightStripItems.Children.Clear();
        foreach (var tab in InspectorTabs.Items.OfType<TabItem>().Where(t => t.IsVisible))
            RightStripItems.Children.Add(StripTab(HeaderText(tab), 90, $"Show {HeaderText(tab)}", () => { SetPanelVisible("inspector", true); InspectorTabs.SelectedItem = tab; }));
        BottomStripItems.Children.Clear();
        foreach (var tab in BottomTabs.Items.OfType<TabItem>().Where(t => t.IsVisible))
            BottomStripItems.Children.Add(StripTab(HeaderText(tab), 0, $"Show {HeaderText(tab)}", () => { SetPanelVisible("bottom", true); BottomTabs.SelectedItem = tab; }));
    }

    private static string HeaderText(TabItem tab) => (tab.Header as TextBlock)?.Text ?? tab.Header?.ToString() ?? "";

    private static Control StripTab(string text, double angle, string tip, Action open)
    {
        var button = new Button { Classes = { "stripTab" }, Content = new TextBlock { Text = text, FontSize = 11 } };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => open();
        return angle == 0 ? button : new LayoutTransformControl { LayoutTransform = new RotateTransform(angle), Child = button };
    }

    private async Task SmokeLayoutAsync(string output)
    {
        double documentsBefore = Documents.Bounds.Width;
        HideExplorerButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); HideInspectorButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); HideBottomButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(100); UpdateLayout();
        Require(toolPanels.All(p => !p.Visible && p.Strip.IsVisible && !p.Splitter.IsVisible), "Hiding did not collapse every panel to its strip.");
        Require(WorkspaceGrid.ColumnDefinitions[1].ActualWidth == 0 && WorkspaceGrid.ColumnDefinitions[5].ActualWidth == 0 && RootGrid.RowDefinitions[3].ActualHeight == 0, "Minimized panels still occupy grid space.");
        Require(Documents.Bounds.Width > documentsBefore + 400, $"Documents did not grow when side panels were minimized ({documentsBefore} -> {Documents.Bounds.Width}).");
        Require(LeftStrip.Bounds.Width is > 0 and < 40 && RightStrip.Bounds.Width is > 0 and < 40 && BottomStrip.Bounds.Height is > 0 and < 40, "Strips are not slim bars.");
        var stripLabels = BottomStripItems.Children.SelectMany(c => c.GetVisualDescendants().OfType<TextBlock>()).Select(t => t.Text).ToArray();
        Require(stripLabels.SequenceEqual(["Problems", "Build / Game output", "Changes", "Terminal"]), "Bottom strip tabs: " + string.Join(", ", stripLabels));
        using (var image = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96))) { image.Render(this); image.Save(System.IO.Path.Combine(output, "panels-minimized.png"), PngBitmapEncoderOptions.Default); }
        ShowBottomTab(1); Require(BottomPanel.IsVisible && BottomTabs.SelectedIndex == 1 && !BottomStrip.IsVisible, "Pushed output did not restore the bottom panel.");
        BottomStripItems.Children.Clear(); HideBottomButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); RebuildStrips();
        ((Button)BottomStripItems.Children[2]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(BottomPanel.IsVisible && HeaderText((TabItem)BottomTabs.SelectedItem!) == "Changes", "Bottom strip tab did not restore and select its tool.");
        ((Button)LeftStripItems.Children[0].GetVisualDescendants().OfType<Button>().First()).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var rowEditorStrip = RightStripItems.Children.SelectMany(c => c.GetVisualDescendants().OfType<Button>()).Single(b => ((TextBlock)b.Content!).Text == "Row Editor");
        rowEditorStrip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(100); UpdateLayout();
        Require(toolPanels.All(p => p.Visible && !p.Strip.IsVisible && p.Splitter.IsVisible), "Strip tabs did not restore every panel.");
        Require(InspectorTabs.SelectedIndex == 1, "Right strip tab did not select Row Editor.");
        Require(Math.Abs(WorkspaceGrid.ColumnDefinitions[1].ActualWidth - 240) < 1 && Math.Abs(WorkspaceGrid.ColumnDefinitions[5].ActualWidth - 270) < 1 && Math.Abs(RootGrid.RowDefinitions[3].ActualHeight - 190) < 1, "Restored panels did not regain their sizes.");
        InspectorTabs.SelectedIndex = 0;
    }

    private async Task SmokeReorderAsync(string root)
    {
        var file = System.IO.Path.Combine(root, "source/tables/cubemain.json"); if (!File.Exists(file)) return;
        var pane = (await OpenDocumentAsync(file))!; var table = pane.Document.Table!; var key = table.Columns[0]; var cols = table.Columns.ToArray();
        string Names(int n) => string.Join("|", Enumerable.Range(0, n).Select(i => pane.Document.Table!.Cell(i, key)));
        var before = Names(5); var original = Enumerable.Range(0, 5).Select(i => table.Cell(i, key)).ToArray(); var moving = original[2];
        pane.SelectCell(2, 0); pane.MoveRows([2], 0); await Task.Delay(50);
        Require(pane.Document.Table!.Cell(0, key) == moving && pane.SelectedRow == 0 && pane.SelectedCells.Contains((0, 0)), "Row move did not place the row first or carry the selection.");
        Require(((IEnumerable<RowView>)pane.TableGrid.ItemsSource!).First() is { Row: 0 } view && view[0] == moving, "Grid did not refresh after the row move.");
        pane.Document.Undo(); pane.Refresh(); Require(Names(5) == before, "Undo did not restore the row order.");
        pane.SelectCell(1, 0); pane.SelectCell(3, 0, control: true); pane.MoveRows([1, 3], 5);
        Require(Names(5) == string.Join("|", new[] { 0, 2, 4, 1, 3 }.Select(i => original[i])), "Multi-row move did not keep the moved rows together before the target.");
        pane.Document.Undo(); pane.Refresh(); Require(Names(5) == before, "Undo did not restore the multi-row move.");
        pane.SelectCell(0, 3); pane.MoveColumn(3, 2);
        Require(pane.Document.Table!.Columns[2] == cols[3] && pane.Document.Table.Columns[3] == cols[2] && pane.SelectedCells.Contains((0, 2)), "Column move did not reorder the schema or carry the selection.");
        var headers = pane.TableGrid.Columns.OrderBy(c => c.DisplayIndex).Select(c => c.Header?.ToString() ?? "").ToList();
        int Shown(string column) => headers.FindIndex(h => h.Contains(column, StringComparison.Ordinal));
        Require(Shown(cols[3]) >= 0 && Shown(cols[3]) == Shown(cols[2]) - 1, "Grid headers did not follow the column move: " + string.Join(", ", headers.Take(5)));
        pane.Document.Undo(); pane.Refresh(); Require(pane.Document.Table!.Columns.SequenceEqual(cols), "Undo did not restore the column order.");
        Require(EditorPane.ColumnMoveTarget([1, 0, 2, 3], 0, []) == 1 && EditorPane.ColumnMoveTarget([0, 2, 1, 3], 1, []) == 2 && EditorPane.ColumnMoveTarget([2, 0, 1], 2, []) == 0
            && EditorPane.ColumnMoveTarget([0, 1, 3, 2], 3, []) == 2 && EditorPane.ColumnMoveTarget([5, 0, 1, 2], 5, [5]) == -1 && EditorPane.ColumnMoveTarget([5, 1, 0, 2], 0, [5]) == 1, "Display-order to physical column mapping is wrong.");
        Require(!pane.Document.IsDirty, "Reorder smoke left the document dirty after undo.");
        // Drag markers: press a row header and drag two rows down; press a column header and drag it right.
        var output = Program.Arguments[Array.IndexOf(Program.Arguments, "--smoke") + 2];
        Documents.SelectedItem = tabs.First(t => t.Content == pane); await Task.Delay(200); UpdateLayout();
        var rowHeaders = pane.TableGrid.GetVisualDescendants().OfType<DataGridRowHeader>().Where(h => h.Bounds.Height > 0).OrderBy(h => h.TranslatePoint(new Point(0, 0), this)!.Value.Y).ToArray();
        Require(rowHeaders.Length > 4, $"Not enough row headers realized for the drag smoke: {rowHeaders.Length}, rows {pane.TableGrid.GetVisualDescendants().OfType<DataGridRow>().Count()}, frozen {pane.FrozenRows.Count}.");
        var start = rowHeaders[1].TranslatePoint(new Point(rowHeaders[1].Bounds.Width / 2, rowHeaders[1].Bounds.Height / 2), this)!.Value;
        var end = rowHeaders[3].TranslatePoint(new Point(rowHeaders[3].Bounds.Width / 2, rowHeaders[3].Bounds.Height * 0.8), this)!.Value;
        this.MouseDown(start, MouseButton.Left, RawInputModifiers.None); this.MouseMove(new Point(start.X, start.Y + 10), RawInputModifiers.LeftMouseButton); this.MouseMove(end, RawInputModifiers.LeftMouseButton); await Task.Delay(50); UpdateLayout();
        var rowMarker = pane.GetVisualDescendants().OfType<Border>().Where(b => b.IsVisible && b.Bounds.Height == 3 && b.HorizontalAlignment == HorizontalAlignment.Stretch).ToArray();
        Require(rowMarker.Length == 1, "Row drop marker did not appear during the drag.");
        using (var image = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96))) { image.Render(this); image.Save(System.IO.Path.Combine(output, "row-drag.png"), PngBitmapEncoderOptions.Default); }
        this.MouseUp(end, MouseButton.Left, RawInputModifiers.None); await Task.Delay(50);
        Require(!rowMarker[0].IsVisible && pane.Document.Table!.Cell(3, key) == original[1] && pane.Document.Table.Cell(1, key) == original[2], "Dropping the row did not move it below the row it was released on.");
        pane.Document.Undo(); pane.Refresh(); Require(Names(5) == before, "Undo did not restore the dragged row.");
        await Task.Delay(150); UpdateLayout();
        var columnHeaders = pane.TableGrid.GetVisualDescendants().OfType<DataGridColumnHeader>().Where(h => h.Bounds.Width > 0).OrderBy(h => h.TranslatePoint(new Point(0, 0), this)!.Value.X).ToArray();
        Require(columnHeaders.Length > 4, $"Not enough column headers realized for the drag smoke: {columnHeaders.Length}.");
        var from = columnHeaders[2].TranslatePoint(new Point(columnHeaders[2].Bounds.Width / 2, columnHeaders[2].Bounds.Height / 2), this)!.Value;
        var to = columnHeaders[4].TranslatePoint(new Point(columnHeaders[4].Bounds.Width * 0.8, columnHeaders[4].Bounds.Height / 2), this)!.Value;
        this.MouseDown(from, MouseButton.Left, RawInputModifiers.None); this.MouseMove(new Point(from.X + 12, from.Y), RawInputModifiers.LeftMouseButton); this.MouseMove(to, RawInputModifiers.LeftMouseButton); await Task.Delay(50); UpdateLayout();
        var columnMarker = pane.GetVisualDescendants().OfType<Border>().Where(b => b.IsVisible && b.Bounds.Width == 3 && b.VerticalAlignment == VerticalAlignment.Stretch).ToArray();
        Require(columnMarker.Length == 1, "Column drop marker did not appear during the header drag.");
        using (var image = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96))) { image.Render(this); image.Save(System.IO.Path.Combine(output, "column-drag.png"), PngBitmapEncoderOptions.Default); }
        this.MouseUp(to, MouseButton.Left, RawInputModifiers.None); await Task.Delay(100);
        Require(!columnMarker[0].IsVisible, "Column drop marker stayed visible after the drop.");
        // The marker was drawn on the far edge of the header under the pointer, so the grid's own drop must land the column there.
        Require(pane.Document.Table!.Columns[3] == cols[1] && pane.Document.Table.Columns[1] == cols[2], "Column drop did not land where the marker showed: " + string.Join(",", pane.Document.Table.Columns.Take(5)));
        pane.Document.Undo(); pane.Refresh();
        Require(pane.Document.Table!.Columns.SequenceEqual(cols) && !pane.Document.IsDirty, "Column drag did not undo cleanly.");
        await CloseTabAsync(tabs.First(t => t.Content == pane));
    }

    /// <summary>Opening a project that still has a folder-per-table converts it (the smoke run answers the prompt with Convert).</summary>
    private async Task SmokeLayoutUpgradeAsync(string root)
    {
        var folder = System.IO.Path.Combine(root, "source/tables/legacy-smoke"); Directory.CreateDirectory(folder);
        var table = TableData.FromTsv(Utf8.GetBytes("name\tvalue\r\nA\t1\r\n"), "legacy-smoke", "global/excel/legacy-smoke.txt");
        WriteJson(System.IO.Path.Combine(folder, "schema.json"), table.Schema); WriteJson(System.IO.Path.Combine(folder, "records.json"), table.Records);
        await LoadProjectAsync(root);
        var file = System.IO.Path.Combine(root, "source/tables/legacy-smoke.json");
        Require(File.Exists(file) && !Directory.Exists(folder) && project != null, "Opening a folder-per-table project did not convert it.");
        Require(Flatten(explorerEntries).Any(e => e.Name == "legacy-smoke.json" && e.IsTable), "Converted table is not listed in the explorer.");
        var pane = (await OpenDocumentAsync(file))!; Require(pane.Document.Table?.Cell(0, "name") == "A", "Converted table did not open in the table editor.");
        await CloseTabAsync(tabs.First(t => t.Content == pane)); File.Delete(file); await RefreshExplorerAsync();
    }
}

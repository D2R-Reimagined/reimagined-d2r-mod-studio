using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using ModStudio.Core;

namespace ModStudio.App;

/// <summary>
/// The Column Guide tab of the bottom panel, the interactive counterpart of <see cref="ColumnGuideTooltip"/>. It follows the
/// selected cell of the active table: the description and format sit on the left, and the documented values fill the right,
/// searchable, with the row matching the cell's value highlighted and scrolled into view.
/// </summary>
internal sealed class ColumnGuideView : Border
{
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#A8A29A"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#D8BC86"));
    private static readonly IBrush Mono = new SolidColorBrush(Color.Parse("#C9D4E0"));
    private static readonly IBrush Match = new SolidColorBrush(Color.Parse("#4A4123"));
    public const string OpenHint = "Press F1, use the toolbar's Column guide button, or right-click the header for the searchable guide";
    public const string EmptyText = "Click into a table cell and the guide for its column appears here: what the column means, its format, and every documented value, with the cell's current value highlighted.";
    private sealed record GuideRow(string[] Cells, bool IsCurrent);

    private readonly Action<Exception> error;
    private (string Table, string Column, string? Value)? shown;
    private TextBox? search;

    public ColumnGuideView(Action<Exception> error) { this.error = error; Padding = new(10, 6); ShowMessage(EmptyText); }

    /// <summary>The column currently displayed, or null while the tab shows a message.</summary>
    public string? Column => shown?.Column;

    /// <summary>Shows a column's guide. Showing the same column again keeps the search text, so moving between rows stays cheap.</summary>
    public void Show(TableData? table, string? column, string? currentValue)
    {
        if (table == null || table.IsCatalog || string.IsNullOrEmpty(column)) { ShowMessage(table?.IsCatalog == true ? "String catalogs have no column guide." : EmptyText); return; }
        var key = (table.Name, column, currentValue);
        if (shown == key) return;
        var entry = ColumnGuide.Find(table.Name, column);
        if (entry == null) { ShowMessage($"‘{column}’ has no entry in the bundled d2rdoc data guide for {table.Name}."); shown = key; return; }
        var keep = shown is { } previous && previous.Table == table.Name && previous.Column == column ? search?.Text : null;
        Child = Create(table.Name, column, entry, currentValue, keep);
        shown = key;
    }

    private void ShowMessage(string text)
    {
        shown = null; search = null;
        Child = new TextBlock { Text = text, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new(2, 4) };
    }

    private Control Create(string tableName, string column, ColumnGuideEntry entry, string? currentValue, string? searchText)
    {
        search = null;
        // Left: what the column is. Right: its values, which get the height of the panel to scroll in.
        var about = new StackPanel { Spacing = 6 };
        var titles = new WrapPanel { Orientation = Orientation.Horizontal };
        titles.Children.Add(new TextBlock { Text = column, FontWeight = FontWeight.SemiBold, FontSize = 15, Foreground = Accent, Margin = new(0, 0, 8, 0) });
        if (!column.Equals(entry.Name, StringComparison.OrdinalIgnoreCase)) titles.Children.Add(new TextBlock { Text = entry.Name, Foreground = Muted, VerticalAlignment = VerticalAlignment.Bottom, Margin = new(0, 0, 8, 0) });
        if (!string.IsNullOrEmpty(entry.Type)) titles.Children.Add(new TextBlock { Text = entry.Type, Foreground = Muted, FontStyle = FontStyle.Italic, VerticalAlignment = VerticalAlignment.Bottom, Margin = new(0, 0, 8, 0) });
        titles.Children.Add(new TextBlock { Text = tableName, Foreground = Muted, FontSize = 11, VerticalAlignment = VerticalAlignment.Bottom });
        about.Children.Add(titles);
        about.Children.Add(new SelectableTextBlock { Text = entry.Description, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(entry.Format)) about.Children.Add(new SelectableTextBlock { Text = "Format: " + entry.Format, TextWrapping = TextWrapping.Wrap, Foreground = Mono });
        var current = (currentValue ?? "").Trim();
        if (current.Length > 0) about.Children.Add(new SelectableTextBlock { Text = "Current value: " + current, TextWrapping = TextWrapping.Wrap, Foreground = Muted });
        Control? values = null;
        if (entry.Table is { Length: > 1 }) values = CreateValues(entry, current, searchText, about);
        if (entry.Bits is { Length: > 0 })
        {
            var bits = entry.Bits.Select((text, i) => (text, i)).Where(b => !string.IsNullOrWhiteSpace(b.text)).Select(b => $"bit {b.i} ({1L << b.i}): {b.text}");
            long.TryParse(current, out var mask);
            var set = mask > 0 ? entry.Bits.Select((text, i) => (text, i)).Where(b => !string.IsNullOrWhiteSpace(b.text) && (mask & (1L << b.i)) != 0).Select(b => $"bit {b.i}: {b.text}").ToArray() : [];
            if (set.Length > 0) about.Children.Add(new SelectableTextBlock { Text = $"Set in ‘{mask}’: " + string.Join(" · ", set), TextWrapping = TextWrapping.Wrap, Foreground = Accent });
            var bitList = new SelectableTextBlock { Text = string.Join("\n", bits), TextWrapping = TextWrapping.Wrap, Foreground = Mono };
            if (values == null) values = new ScrollViewer { Content = bitList, AllowAutoHide = false };
            else about.Children.Add(bitList);
        }
        var footer = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new(0, 4, 0, 0) };
        var url = ColumnGuide.Url(tableName, column);
        var open = new Button { Content = "Open online guide ↗", Padding = new(10, 4), Margin = new(0, 0, 10, 0) };
        ToolTip.SetTip(open, url);
        open.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception ex) { error(ex); } };
        footer.Children.Add(open);
        footer.Children.Add(new TextBlock { Text = "d2rdoc data guide · " + ColumnGuide.Generated[..Math.Min(10, ColumnGuide.Generated.Length)], Foreground = Muted, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        about.Children.Add(footer);
        var aboutScroll = new ScrollViewer { Content = about, AllowAutoHide = false, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        if (values == null) return aboutScroll;
        var layout = new Grid { ColumnDefinitions = new("2*,12,3*") };
        Grid.SetColumn(values, 2); layout.Children.Add(aboutScroll); layout.Children.Add(values);
        return layout;
    }

    private Control CreateValues(ColumnGuideEntry entry, string current, string? searchText, StackPanel about)
    {
        var table = entry.Table!; int columns = table.Max(r => r.Length);
        var headings = entry.TableHasHeading ? table[0] : null; var body = table.Skip(headings == null ? 0 : 1).ToArray();
        // Codes match on the first column; a value that is itself descriptive can match anywhere.
        bool IsCurrent(string[] row) => current.Length > 0 && (row.Length > 0 && row[0].Trim().Equals(current, StringComparison.OrdinalIgnoreCase) ||
            current.Length > 1 && row.Skip(1).Any(c => c.Trim().Equals(current, StringComparison.OrdinalIgnoreCase)));
        var all = body.Select(r => new GuideRow(r, IsCurrent(r))).ToArray();
        int total = entry.TableTruncated > 0 ? entry.TableTruncated : body.Length;
        var panel = new DockPanel();
        var searchRow = new DockPanel { Margin = new(0, 0, 0, 4) };
        var count = new TextBlock { Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 0, 0) };
        DockPanel.SetDock(count, Dock.Right); searchRow.Children.Add(count);
        var box = search = new TextBox { PlaceholderText = $"Search {total} values…", Text = searchText ?? "" }; searchRow.Children.Add(box);
        DockPanel.SetDock(searchRow, Dock.Top); panel.Children.Add(searchRow);
        Grid Row(string[] cells, bool heading, bool highlight)
        {
            var grid = new Grid { Background = highlight ? Match : Brushes.Transparent };
            for (int c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(c == columns - 1 ? 3 : c == 0 ? 1 : 2, GridUnitType.Star)));
            for (int c = 0; c < cells.Length; c++)
            {
                var cell = new SelectableTextBlock { Text = cells[c], TextWrapping = TextWrapping.Wrap, Margin = new(4, 2, 12, 2),
                    FontWeight = heading || highlight && c == 0 ? FontWeight.SemiBold : FontWeight.Normal, Foreground = heading ? Muted : c == 0 ? Mono : Brushes.White };
                Grid.SetColumn(cell, c); grid.Children.Add(cell);
            }
            return grid;
        }
        if (headings != null) { var head = Row(headings, true, false); DockPanel.SetDock(head, Dock.Top); panel.Children.Add(head); }
        if (total > body.Length) { var more = new TextBlock { Text = $"… {total - body.Length} more values are only in the online guide", Foreground = Muted }; DockPanel.SetDock(more, Dock.Bottom); panel.Children.Add(more); }
        var list = new ListBox { Background = Brushes.Transparent, BorderThickness = new(0), Padding = new(0), SelectionMode = SelectionMode.Single };
        list.Styles.Add(new Style(x => x.OfType<ListBoxItem>()) { Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(0)), new Setter(ListBoxItem.MinHeightProperty, 0.0), new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
        ScrollViewer.SetAllowAutoHide(list, false); ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        list.ItemTemplate = new FuncDataTemplate<GuideRow>((row, _) => row == null ? new Border() : Row(row.Cells, false, row.IsCurrent));
        void Apply()
        {
            var term = (box.Text ?? "").Trim();
            var shownRows = term.Length == 0 ? all : all.Where(r => r.Cells.Any(c => c.Contains(term, StringComparison.OrdinalIgnoreCase))).ToArray();
            list.ItemsSource = shownRows;
            count.Text = term.Length == 0 ? $"{body.Length}{(total > body.Length ? $" of {total}" : "")}" : $"{shownRows.Length} of {body.Length}";
            var found = shownRows.FirstOrDefault(r => r.IsCurrent);
            if (found != null) { list.SelectedItem = found; Dispatcher.UIThread.Post(() => list.ScrollIntoView(found), DispatcherPriority.Background); }
        }
        box.TextChanged += (_, _) => Apply();
        Apply();
        panel.Children.Add(list);
        if (current.Length > 0 && !all.Any(r => r.IsCurrent)) about.Children.Add(new TextBlock { Text = $"‘{current}’ is not one of the documented values.", Foreground = Muted, TextWrapping = TextWrapping.Wrap });
        return panel;
    }
}

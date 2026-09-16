using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using ModStudio.Core;

namespace ModStudio.App;

/// <summary>
/// The interactive counterpart of <see cref="ColumnGuideTooltip"/>: a flyout that stays open while the user scrolls its value
/// table with the wheel or scrollbar, filters it, and follows the link to the online guide. The row matching the current cell
/// value is highlighted and scrolled into view, so a numeric code can be read off without hunting for it.
/// </summary>
internal static class ColumnGuideFlyout
{
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#A8A29A"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#D8BC86"));
    private static readonly IBrush Mono = new SolidColorBrush(Color.Parse("#C9D4E0"));
    private static readonly IBrush Match = new SolidColorBrush(Color.Parse("#4A4123"));
    public const string OpenHint = "Press F1, use the toolbar's Column guide button, or right-click the header for the searchable guide";
    private sealed record GuideRow(string[] Cells, bool IsCurrent);

    /// <summary>Opens the guide for a column. Undocumented columns get a short note instead, so the shortcut never does nothing.</summary>
    public static void Show(Control anchor, TableData? table, string column, string? currentValue, Action<Exception> error)
    {
        var entry = table == null || table.IsCatalog ? null : ColumnGuide.Find(table.Name, column);
        var flyout = new Flyout { Placement = PlacementMode.Bottom, ShowMode = FlyoutShowMode.Standard };
        flyout.Content = entry == null
            ? new TextBlock { Text = table == null ? "No table is selected." : $"‘{column}’ has no entry in the bundled d2rdoc data guide for {table.Name}.", TextWrapping = TextWrapping.Wrap, MaxWidth = 360, Margin = new(4) }
            : Create(table!.Name, column, entry, currentValue, error, () => flyout.Hide());
        flyout.ShowAt(anchor);
    }

    public static Control Create(string tableName, string column, ColumnGuideEntry entry, string? currentValue, Action<Exception> error, Action close)
    {
        var panel = new StackPanel { Spacing = 8, Width = 520 };
        var header = new DockPanel();
        var closeButton = new Button { Content = "✕", Padding = new(8, 2), MinHeight = 0, Height = 26, VerticalAlignment = VerticalAlignment.Top };
        ToolTip.SetTip(closeButton, "Close (Escape)"); closeButton.Click += (_, _) => close();
        DockPanel.SetDock(closeButton, Dock.Right); header.Children.Add(closeButton);
        var titles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        titles.Children.Add(new TextBlock { Text = column, FontWeight = FontWeight.SemiBold, FontSize = 15, Foreground = Accent });
        if (!column.Equals(entry.Name, StringComparison.OrdinalIgnoreCase)) titles.Children.Add(new TextBlock { Text = entry.Name, Foreground = Muted, VerticalAlignment = VerticalAlignment.Bottom });
        if (!string.IsNullOrEmpty(entry.Type)) titles.Children.Add(new TextBlock { Text = entry.Type, Foreground = Muted, FontStyle = FontStyle.Italic, VerticalAlignment = VerticalAlignment.Bottom });
        header.Children.Add(titles); panel.Children.Add(header);
        panel.Children.Add(new SelectableTextBlock { Text = entry.Description, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(entry.Format)) panel.Children.Add(new SelectableTextBlock { Text = "Format: " + entry.Format, TextWrapping = TextWrapping.Wrap, Foreground = Mono });
        TextBox? search = null;
        if (entry.Table is { Length: > 1 })
        {
            int columns = entry.Table.Max(r => r.Length);
            var headings = entry.TableHasHeading ? entry.Table[0] : null; var body = entry.Table.Skip(headings == null ? 0 : 1).ToArray();
            var current = (currentValue ?? "").Trim();
            // Codes match on the first column; a value that is itself descriptive can match anywhere.
            bool IsCurrent(string[] row) => current.Length > 0 && (row.Length > 0 && row[0].Trim().Equals(current, StringComparison.OrdinalIgnoreCase) ||
                current.Length > 1 && row.Skip(1).Any(c => c.Trim().Equals(current, StringComparison.OrdinalIgnoreCase)));
            var all = body.Select(r => new GuideRow(r, IsCurrent(r))).ToArray();
            int total = entry.TableTruncated > 0 ? entry.TableTruncated : body.Length;
            var searchRow = new DockPanel();
            var count = new TextBlock { Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 0, 0) };
            DockPanel.SetDock(count, Dock.Right); searchRow.Children.Add(count);
            search = new TextBox { PlaceholderText = $"Search {total} values…" }; searchRow.Children.Add(search);
            panel.Children.Add(searchRow);
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
            if (headings != null) panel.Children.Add(Row(headings, true, false));
            var list = new ListBox { MaxHeight = 380, Background = Brushes.Transparent, BorderThickness = new(0), Padding = new(0), SelectionMode = SelectionMode.Single };
            list.Styles.Add(new Style(x => x.OfType<ListBoxItem>()) { Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(0)), new Setter(ListBoxItem.MinHeightProperty, 0.0), new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
            ScrollViewer.SetAllowAutoHide(list, false); ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            list.ItemTemplate = new FuncDataTemplate<GuideRow>((row, _) => row == null ? new Border() : Row(row.Cells, false, row.IsCurrent));
            void Apply()
            {
                var term = (search.Text ?? "").Trim();
                var shown = term.Length == 0 ? all : all.Where(r => r.Cells.Any(c => c.Contains(term, StringComparison.OrdinalIgnoreCase))).ToArray();
                list.ItemsSource = shown;
                count.Text = term.Length == 0 ? $"{body.Length}{(total > body.Length ? $" of {total}" : "")}" : $"{shown.Length} of {body.Length}";
                var found = shown.FirstOrDefault(r => r.IsCurrent);
                if (found != null) { list.SelectedItem = found; Dispatcher.UIThread.Post(() => list.ScrollIntoView(found), DispatcherPriority.Background); }
            }
            search.TextChanged += (_, _) => Apply();
            Apply();
            panel.Children.Add(list);
            if (current.Length > 0 && !all.Any(r => r.IsCurrent)) panel.Children.Add(new TextBlock { Text = $"Current value ‘{current}’ is not one of the documented values.", Foreground = Muted, TextWrapping = TextWrapping.Wrap });
            if (total > body.Length) panel.Children.Add(new TextBlock { Text = $"… {total - body.Length} more values are only in the online guide", Foreground = Muted });
        }
        if (entry.Bits is { Length: > 0 })
        {
            var bits = entry.Bits.Select((text, i) => (text, i)).Where(b => !string.IsNullOrWhiteSpace(b.text)).Select(b => $"bit {b.i} ({1L << b.i}): {b.text}");
            long.TryParse(currentValue?.Trim(), out var mask);
            var set = mask > 0 ? entry.Bits.Select((text, i) => (text, i)).Where(b => !string.IsNullOrWhiteSpace(b.text) && (mask & (1L << b.i)) != 0).Select(b => $"bit {b.i}: {b.text}").ToArray() : [];
            if (set.Length > 0) panel.Children.Add(new SelectableTextBlock { Text = $"Set in ‘{mask}’: " + string.Join(" · ", set), TextWrapping = TextWrapping.Wrap, Foreground = Accent });
            panel.Children.Add(new ScrollViewer { MaxHeight = 220, Content = new SelectableTextBlock { Text = string.Join("\n", bits), TextWrapping = TextWrapping.Wrap, Foreground = Mono }, AllowAutoHide = false });
        }
        var footer = new DockPanel { Margin = new(0, 4, 0, 0) };
        var url = ColumnGuide.Url(tableName, column);
        var open = new Button { Content = "Open online guide ↗", Padding = new(10, 4) };
        ToolTip.SetTip(open, url);
        open.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception ex) { error(ex); } };
        DockPanel.SetDock(open, Dock.Right); footer.Children.Add(open);
        footer.Children.Add(new TextBlock { Text = "d2rdoc data guide · " + ColumnGuide.Generated[..Math.Min(10, ColumnGuide.Generated.Length)], Foreground = Muted, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(footer);
        var root = new Border { Child = panel, Padding = new(4) };
        root.KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; close(); } };
        if (search != null) root.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => search.Focus(), DispatcherPriority.Input);
        return root;
    }
}

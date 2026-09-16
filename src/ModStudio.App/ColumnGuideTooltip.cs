using Avalonia.Controls;
using Avalonia.Media;
using ModStudio.Core;

namespace ModStudio.App;

/// <summary>
/// Hover card describing a game-table column, from the bundled d2rdoc data guide. A tooltip cannot be scrolled or clicked, so
/// the card stays short and points at <see cref="ColumnGuideFlyout"/> for the complete, searchable value table.
/// </summary>
internal static class ColumnGuideTooltip
{
    public const int TableRows = 6;
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#A8A29A"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#D8BC86"));
    private static readonly IBrush Mono = new SolidColorBrush(Color.Parse("#C9D4E0"));
    /// <summary>Attaches the guide card when the column is documented; otherwise leaves the fallback tip (typically the full column name).</summary>
    public static bool Attach(Control target, TableData? table, string column, object? fallback = null)
    {
        var entry = table == null || table.IsCatalog ? null : ColumnGuide.Find(table.Name, column);
        if (entry == null) { ToolTip.SetTip(target, fallback); return false; }
        ToolTip.SetTip(target, Create(table!.Name, column, entry));
        return true;
    }
    public static Control Create(string tableName, string column, ColumnGuideEntry entry)
    {
        var panel = new StackPanel { Spacing = 6, MaxWidth = 460 };
        var header = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(new TextBlock { Text = column, FontWeight = FontWeight.SemiBold, Foreground = Accent });
        if (!column.Equals(entry.Name, StringComparison.OrdinalIgnoreCase)) header.Children.Add(new TextBlock { Text = entry.Name, Foreground = Muted });
        if (!string.IsNullOrEmpty(entry.Type)) header.Children.Add(new TextBlock { Text = entry.Type, Foreground = Muted, FontStyle = FontStyle.Italic });
        panel.Children.Add(header);
        panel.Children.Add(new TextBlock { Text = entry.Description, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(entry.Format)) panel.Children.Add(new TextBlock { Text = "Format: " + entry.Format, TextWrapping = TextWrapping.Wrap, Foreground = Mono });
        // The hint comes before the value table: a narrow card may clip the table's bottom, and the hint is what makes the rest reachable.
        if (entry.Table is { Length: > 1 } || entry.Bits is { Length: > 0 }) panel.Children.Add(new TextBlock { Text = ColumnGuideFlyout.OpenHint, Foreground = Accent, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        if (entry.Table is { Length: > 1 })
        {
            var grid = new Grid { Margin = new(0, 4, 0, 0) };
            int columns = entry.Table.Max(r => r.Length);
            // Auto columns take their unwrapped desired width before the last column is
            // measured, which can squeeze descriptions down to one character per line.
            // Share the available width so every column can wrap, favoring descriptions.
            for (int c = 0; c < columns; c++)
                grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(c == columns - 1 ? 3 : c == 0 ? 1 : 2, GridUnitType.Star)));
            bool heading = entry.TableHasHeading;
            var rows = entry.Table.Take(TableRows + (heading ? 1 : 0)).ToArray();
            for (int r = 0; r < rows.Length; r++)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                bool isHeading = heading && r == 0;
                for (int c = 0; c < rows[r].Length; c++)
                {
                    var cell = new TextBlock { Text = rows[r][c], TextWrapping = TextWrapping.Wrap, Margin = new(0, 1, 12, 1), MaxWidth = 300,
                        FontWeight = isHeading ? FontWeight.SemiBold : FontWeight.Normal, Foreground = isHeading ? Muted : c == 0 ? Mono : Brushes.White };
                    Grid.SetRow(cell, r); Grid.SetColumn(cell, c); grid.Children.Add(cell);
                }
            }
            panel.Children.Add(grid);
            int total = entry.TableTruncated > 0 ? entry.TableTruncated : entry.Table.Length - (heading ? 1 : 0);
            if (total > TableRows) panel.Children.Add(new TextBlock { Text = $"… {total - TableRows} more values in the searchable guide", Foreground = Muted });
        }
        if (entry.Bits is { Length: > 0 })
        {
            var bits = entry.Bits.Select((text, i) => (text, i)).Where(b => !string.IsNullOrWhiteSpace(b.text)).Take(TableRows).Select(b => $"bit {b.i} ({1L << b.i}): {b.text}");
            panel.Children.Add(new TextBlock { Text = string.Join("\n", bits), TextWrapping = TextWrapping.Wrap, Foreground = Mono, Margin = new(0, 4, 0, 0) });
            if (entry.Bits.Count(b => !string.IsNullOrWhiteSpace(b)) > TableRows) panel.Children.Add(new TextBlock { Text = "… more flags in the online guide", Foreground = Muted });
        }
        panel.Children.Add(new TextBlock { Text = "d2rdoc data guide · " + ColumnGuide.Url(tableName, column), Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 0) });
        // A tooltip cannot be scrolled; the viewer only bounds the card's height so a narrow card is clipped instead of covering the screen.
        return new ScrollViewer
        {
            Content = panel, MaxWidth = 460, MaxHeight = 560,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Top,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            AllowAutoHide = false
        };
    }
}

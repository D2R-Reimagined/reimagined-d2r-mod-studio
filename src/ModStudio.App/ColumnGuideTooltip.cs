using Avalonia.Controls;
using Avalonia.Media;
using ModStudio.Core;

namespace ModStudio.App;

/// <summary>Hover card describing a game-table column, from the bundled d2rdoc data guide.</summary>
internal static class ColumnGuideTooltip
{
    public const int TableRows = 12;
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
        if (entry.Table is { Length: > 1 })
        {
            var grid = new Grid { Margin = new(0, 4, 0, 0) };
            int columns = entry.Table.Max(r => r.Length);
            for (int c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(c == columns - 1 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto));
            var rows = entry.Table.Take(TableRows + 1).ToArray();
            for (int r = 0; r < rows.Length; r++)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                for (int c = 0; c < rows[r].Length; c++)
                {
                    var cell = new TextBlock { Text = rows[r][c], TextWrapping = TextWrapping.Wrap, Margin = new(0, 1, 12, 1), MaxWidth = 300,
                        FontWeight = r == 0 ? FontWeight.SemiBold : FontWeight.Normal, Foreground = r == 0 ? Muted : c == 0 ? Mono : Brushes.White };
                    Grid.SetRow(cell, r); Grid.SetColumn(cell, c); grid.Children.Add(cell);
                }
            }
            panel.Children.Add(grid);
            int total = entry.TableTruncated > 0 ? entry.TableTruncated : entry.Table.Length - 1;
            if (total > TableRows) panel.Children.Add(new TextBlock { Text = $"… {total - TableRows} more values in the online guide", Foreground = Muted });
        }
        if (entry.Bits is { Length: > 0 })
        {
            var bits = entry.Bits.Select((text, i) => (text, i)).Where(b => !string.IsNullOrWhiteSpace(b.text)).Take(TableRows).Select(b => $"bit {b.i} ({1L << b.i}): {b.text}");
            panel.Children.Add(new TextBlock { Text = string.Join("\n", bits), TextWrapping = TextWrapping.Wrap, Foreground = Mono, Margin = new(0, 4, 0, 0) });
            if (entry.Bits.Count(b => !string.IsNullOrWhiteSpace(b)) > TableRows) panel.Children.Add(new TextBlock { Text = "… more flags in the online guide", Foreground = Muted });
        }
        panel.Children.Add(new TextBlock { Text = "d2rdoc data guide · " + ColumnGuide.Url(tableName, column), Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 0) });
        return panel;
    }
}

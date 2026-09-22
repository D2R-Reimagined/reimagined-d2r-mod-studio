using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace ModStudio.App;

/// <summary>Shared layout for the inspector preview cards: a title, wrapped prose, labelled sections and a level table.</summary>
internal static class PreviewCards
{
    private static readonly IBrush Heading = Brushes.Tan;
    private static readonly IBrush Body = Brushes.LightSteelBlue;
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#A8A29A"));
    public static Control Card(string title, IEnumerable<Control> parts)
    {
        // The inspector's scrollbar floats over the content, so the right margin keeps wrapped text clear of it.
        var panel = new StackPanel { Spacing = 8, Margin = new(12, 12, 22, 12) };
        panel.Children.Add(new SelectableTextBlock { Text = title, FontSize = 18, Foreground = Heading, TextWrapping = TextWrapping.Wrap });
        foreach (var part in parts) panel.Children.Add(part);
        return new Border { Background = new SolidColorBrush(Color.Parse("#151515")), Child = panel };
    }
    public static Control Prose(IEnumerable<string> lines, IBrush? brush = null, double size = 12) =>
        new SelectableTextBlock { Text = string.Join("\n", lines), Foreground = brush ?? Body, FontSize = size, TextWrapping = TextWrapping.Wrap };
    /// <summary>
    /// A labelled block: its heading, then its lines. Leading spaces in a line are kept, so indented trees stay readable.
    /// A muted section is background (assumptions, notes) rather than something the row does.
    /// </summary>
    public static Control Section(string title, IEnumerable<string> lines, bool muted = false)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new SelectableTextBlock { Text = title.ToUpperInvariant(), Foreground = Heading, FontSize = 11, Margin = new(0, 4, 0, 2) });
        panel.Children.Add(new SelectableTextBlock { Text = string.Join("\n", lines), Foreground = muted ? Muted : Body, FontSize = muted ? 11 : 12, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas, Menlo, monospace") });
        return panel;
    }
    /// <summary>
    /// The level curve as a compact table. Columns whose every value is empty are dropped, so a skill or missile only
    /// shows the fields it actually authors; the first column always stays.
    /// </summary>
    public static Control Table<T>(IReadOnlyList<T> rows, (string Header, Func<T, string> Value)[] candidates)
    {
        var columns = candidates.Where((c, i) => i == 0 || rows.Any(r => c.Value(r).Length > 0)).ToArray();
        var grid = new Grid
        {
            ColumnDefinitions = new(string.Join(',', columns.Select(_ => "Auto"))),
            RowDefinitions = new(string.Join(',', Enumerable.Repeat("Auto", rows.Count + 1)))
        };
        for (int c = 0; c < columns.Length; c++)
        {
            var header = new TextBlock { Text = columns[c].Header, Foreground = Heading, FontSize = 11, Margin = new(6, 3), FontWeight = FontWeight.SemiBold };
            Grid.SetColumn(header, c); grid.Children.Add(header);
        }
        for (int r = 0; r < rows.Count; r++)
            for (int c = 0; c < columns.Length; c++)
            {
                // Banded rows keep a long level curve readable across a narrow inspector.
                var cell = new SelectableTextBlock { Text = columns[c].Value(rows[r]), FontSize = 11, Margin = new(6, 2) };
                var holder = new Border { Child = cell, Background = r % 2 == 1 ? new SolidColorBrush(Color.Parse("#1D1E1F")) : null };
                Grid.SetRow(holder, r + 1); Grid.SetColumn(holder, c); grid.Children.Add(holder);
            }
        // Only the table scrolls sideways; the text around it wraps to the inspector's width. The scrollbar floats over the
        // content, so a strip below the last row keeps it from covering that row.
        grid.Margin = new(0, 0, 0, 24);
        return new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalAlignment = HorizontalAlignment.Left };
    }
}

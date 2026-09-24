using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ModStudio.Core;

namespace ModStudio.App;

/// <summary>An item's HD inventory picture in a framed box, with where it came from underneath; offers the game data folder when there is none.</summary>
internal sealed class ItemPicture : StackPanel
{
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#A8A29A"));
    private readonly ContentControl host = new() { HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock note = new() { FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap };
    private WriteableBitmap? bitmap;

    public ItemPicture()
    {
        Spacing = 6; Width = 214;
        Children.Add(new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#1A1712"), 0), new GradientStop(Color.Parse("#0C0B09"), 1) }
            },
            BorderBrush = new SolidColorBrush(Color.Parse("#4A4030")), BorderThickness = new(1), CornerRadius = new(4), MinHeight = 214, Padding = new(8, 16), Child = host
        });
        Children.Add(note);
        host.Content = new TextBlock { Text = "Loading picture…", Foreground = Muted, FontSize = 12 };
        DetachedFromVisualTree += (_, _) => { bitmap?.Dispose(); bitmap = null; };
    }

    public void Show(ItemSprite sprite, VisualBuilderHost builder, bool haveGameData, Action<string, bool> error)
    {
        if (sprite.Pixels is { } pixels)
        {
            var next = Bitmaps.From(pixels.Width, pixels.Height, pixels.Rgba);
            var image = new Image { Source = next, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, MaxWidth = 196, MaxHeight = 392 };
            RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
            host.Content = image; bitmap?.Dispose(); bitmap = next;
            note.Text = $"{sprite.File!.Relative.Replace("data/hd/global/ui/items/", "")} · from {sprite.File.Origin} · via {sprite.Via}";
            ToolTip.SetTip(note, sprite.File.Path);
            return;
        }
        var panel = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = "No picture", Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center });
        if (!haveGameData) panel.Children.Add(Bitmaps.ChooseGameDataButton(builder, error));
        host.Content = panel; bitmap?.Dispose(); bitmap = null;
        note.Text = string.Join("\n", sprite.Notes);
        ToolTip.SetTip(note, null);
    }

    public void Fail(string message) { host.Content = new TextBlock { Text = "Picture unavailable", Foreground = Muted }; note.Text = message; }
}

/// <summary>The game's item tooltip look: centred lines on black in a serif face, each block in its own colour, values that link to their cells.</summary>
internal static class GameTooltip
{
    public static readonly FontFamily Font = new("Palatino Linotype, Book Antiqua, Georgia, serif");
    public static readonly IBrush White = new SolidColorBrush(Color.Parse("#E6E6E6"));

    public sealed record Block(IReadOnlyList<PreviewText> Lines, IBrush Brush, IBrush Link, double Size = 15, double Above = 0);

    public static Control Card(IEnumerable<Block> blocks)
    {
        var panel = new StackPanel { Spacing = 2, Margin = new(22, 16, 22, 18), HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var block in blocks.Where(b => b.Lines.Count > 0))
            panel.Children.Add(new PreviewLinkText(block.Lines, block.Link)
            {
                Foreground = block.Brush, FontSize = block.Size, FontFamily = Font, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping = TextWrapping.Wrap, Margin = new(0, block.Above, 0, 0)
            });
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(242, 8, 8, 9)), BorderBrush = new SolidColorBrush(Color.Parse("#3B3325")), BorderThickness = new(1),
            CornerRadius = new(2), MinWidth = 300, MaxWidth = 560, Child = panel,
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 18, Color = Color.FromArgb(160, 0, 0, 0) })
        };
    }
}

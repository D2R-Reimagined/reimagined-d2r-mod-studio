using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using ModStudio.Core;

namespace ModStudio.App;

/// <summary>
/// A flyout for TransLvl / Utrans cells: every hue-shift table of the act palette drawn as a strip of the colours it
/// produces, so the number for "a good blue" is picked by eye instead of by launching the game once per value. The
/// palette (pal.pl2) is the user's own: it is looked up under the project and the deployment, or chosen once and remembered.
/// </summary>
internal static class ColorTransformPicker
{
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#A8A29A")), Accent = new SolidColorBrush(Color.Parse("#D8BC86")), Match = new SolidColorBrush(Color.Parse("#4A4123"));
    private sealed record Row(int Table, string Label, Bitmap Strip, bool IsCurrent);
    private static PaletteShifts? loaded; private static string loadedFrom = "";

    /// <summary>The palette in use, from the remembered file or the first found under the given roots; null when none is known yet.</summary>
    public static PaletteShifts? Palette(IEnumerable<string> roots, out string source, out string? failure)
    {
        failure = null;
        var preferred = Program.Arguments.Contains("--smoke") ? "" : SafePreferences()?.PalettePl2 ?? "";
        var file = preferred.Length > 0 && File.Exists(preferred) ? preferred : PaletteShifts.Find(roots);
        if (file == null) { source = ""; return null; }
        if (loaded != null && loadedFrom == file) { source = file; return loaded; }
        try { loaded = PaletteShifts.Load(file); loadedFrom = file; source = file; return loaded; }
        catch (Exception ex) { failure = ex.Message; source = file; return null; }
    }
    private static StudioPreferences? SafePreferences() { try { return StudioPreferences.Load(StudioPreferences.DefaultFile); } catch { return null; } }

    public static void Show(Control anchor, string tableName, string column, string currentValue, IEnumerable<string> roots, Action<string> apply, Action<Exception> error)
    {
        var flyout = new Flyout { Placement = PlacementMode.Bottom, ShowMode = FlyoutShowMode.Standard };
        var window = TopLevel.GetTopLevel(anchor)?.ClientSize ?? new Size(1440, 920);
        flyout.Content = Create(tableName, column, currentValue, roots, value => { apply(value); flyout.Hide(); }, error, () => flyout.Hide(), Math.Clamp(window.Height * 0.6, 360, 900));
        flyout.ShowAt(anchor);
    }

    public static Control Create(string tableName, string column, string currentValue, IEnumerable<string> roots, Action<string> apply, Action<Exception> error, Action close, double listHeight = 420)
    {
        var panel = new StackPanel { Spacing = 8, Width = 560 };
        var header = new DockPanel();
        var closeButton = new Button { Content = "✕", Padding = new(8, 2), MinHeight = 0, Height = 26, VerticalAlignment = VerticalAlignment.Top };
        ToolTip.SetTip(closeButton, "Close (Escape)"); closeButton.Click += (_, _) => close();
        DockPanel.SetDock(closeButton, Dock.Right); header.Children.Add(closeButton);
        header.Children.Add(new TextBlock { Text = $"Colour transform · {column}", FontWeight = FontWeight.SemiBold, FontSize = 15, Foreground = Accent });
        panel.Children.Add(header);
        panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text = tableName == "superuniques"
            ? "Each row is one hue table of the act palette, drawn as the colours it turns the palette into. Utrans: 0 or empty picks a random table; 30 and above fall back to 2."
            : "Each row is one hue table of the act palette, drawn as the colours it turns the palette into. The monster's sprite is recoloured through the chosen table." });
        var body = new StackPanel { Spacing = 8 }; panel.Children.Add(body);
        var status = new TextBlock { Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        var pick = new Button { Content = "Choose pal.pl2…", Padding = new(10, 4) };
        ToolTip.SetTip(pick, "An act palette from your extracted game data, for example data/global/palette/ACT1/pal.pl2. Remembered for next time.");
        var footer = new DockPanel { Margin = new(0, 4, 0, 0) }; DockPanel.SetDock(pick, Dock.Right); footer.Children.Add(pick); footer.Children.Add(status); panel.Children.Add(footer);
        var root = new Border { Child = panel, Padding = new(4) };
        root.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) { e.Handled = true; close(); } };

        void Fill()
        {
            body.Children.Clear();
            var palette = Palette(roots, out var source, out var failure);
            if (palette == null)
            {
                status.Text = failure != null ? $"{source}: {failure}" : "No pal.pl2 found under the project or its deployment. Choose one from your extracted game data (data/global/palette/ACT1/pal.pl2).";
                body.Children.Add(new TextBlock { Text = "The transform swatches need an act palette. Studio does not ship game data; extract it once (for example with CascView) and point Studio at any act's pal.pl2.", TextWrapping = TextWrapping.Wrap, Foreground = Muted });
                return;
            }
            status.Text = "Palette: " + source;
            var current = currentValue.Trim(); int.TryParse(current, out var currentTable);
            var rows = new List<Row> { new(-1, "none", Strip(palette.Transformed(-1)), false) };
            for (int table = 0; table < PaletteShifts.Tables; table++)
                rows.Add(new(table, palette.IsIdentity(table) ? $"{table} · unchanged" : table.ToString(), Strip(palette.Transformed(table)), current.Length > 0 && currentTable == table));
            var list = new ListBox { MaxHeight = listHeight, Background = Brushes.Transparent, BorderThickness = new(0), Padding = new(0), SelectionMode = SelectionMode.Single, ItemsSource = rows };
            list.Styles.Add(new Style(x => x.OfType<ListBoxItem>()) { Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(0)), new Setter(ListBoxItem.MinHeightProperty, 0.0), new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
            ScrollViewer.SetAllowAutoHide(list, false);
            list.ItemTemplate = new FuncDataTemplate<Row>((row, _) =>
            {
                if (row == null) return new Border();
                var grid = new Grid { ColumnDefinitions = new("110,*"), Background = row.IsCurrent ? Match : Brushes.Transparent, Margin = new(0, 1) };
                var label = new TextBlock { Text = row.Label, VerticalAlignment = VerticalAlignment.Center, Margin = new(6, 0), FontWeight = row.IsCurrent ? FontWeight.SemiBold : FontWeight.Normal, Foreground = row.Table < 0 ? Muted : Brushes.White };
                var image = new Image { Source = row.Strip, Height = 18, Stretch = Stretch.Fill, Margin = new(0, 0, 6, 0) };
                RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.None);
                Grid.SetColumn(image, 1); grid.Children.Add(label); grid.Children.Add(image);
                ToolTip.SetTip(grid, row.Table < 0 ? "The act palette without any shift, for reference" : $"Use transform {row.Table}");
                return grid;
            });
            list.SelectionChanged += (_, _) => { if (list.SelectedItem is Row { Table: >= 0 } picked) { try { apply(picked.Table.ToString()); } catch (Exception ex) { error(ex); } } };
            body.Children.Add(list);
            var found = rows.FirstOrDefault(r => r.IsCurrent);
            if (found != null) Dispatcher.UIThread.Post(() => list.ScrollIntoView(found), DispatcherPriority.Background);
        }
        pick.Click += async (_, _) =>
        {
            try
            {
                if (TopLevel.GetTopLevel(root) is not { } top) return;
                var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose an act palette (pal.pl2)", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("Diablo II palette") { Patterns = ["*.pl2"] }] });
                var path = files.FirstOrDefault()?.TryGetLocalPath(); if (path == null) return;
                loaded = PaletteShifts.Load(path); loadedFrom = path;
                if (!Program.Arguments.Contains("--smoke")) { var prefs = StudioPreferences.Load(StudioPreferences.DefaultFile); prefs.PalettePl2 = path; prefs.Save(StudioPreferences.DefaultFile); }
                Fill();
            }
            catch (Exception ex) { error(ex); }
        };
        Fill();
        return root;
    }

    /// <summary>A 256 × 1 bitmap of the colours, one pixel per palette index; stretched by the row that shows it.</summary>
    private static Bitmap Strip((byte R, byte G, byte B)[] colors)
    {
        var bitmap = new WriteableBitmap(new PixelSize(256, 1), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var frame = bitmap.Lock())
        {
            var pixels = new byte[256 * 4];
            for (int i = 0; i < 256; i++) { pixels[i * 4] = colors[i].B; pixels[i * 4 + 1] = colors[i].G; pixels[i * 4 + 2] = colors[i].R; pixels[i * 4 + 3] = 255; }
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, frame.Address, pixels.Length);
        }
        return bitmap;
    }
}

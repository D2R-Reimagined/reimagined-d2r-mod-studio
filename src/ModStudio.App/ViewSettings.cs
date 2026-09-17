using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using ModStudio.Core;

namespace ModStudio.App;

/// <summary>
/// How documents are shown, shared by every pane and kept in the Studio preferences: the table grid's font, the source
/// editor's font, column letters and item hover cards. Changing anything re-styles the open panes at once. Ctrl+wheel and
/// Ctrl+plus/minus in a pane change the size of the view under the pointer through <see cref="Zoom"/>.
/// </summary>
internal static class ViewSettings
{
    public const string DefaultSourceFontFamily = "Consolas, Menlo, monospace";
    public const double DefaultSourceFontSize = 13, DefaultTableFontSize = 15, MinFontSize = 8, MaxFontSize = 40;
    /// <summary>Fonts offered by name; anything else can still be typed. Only fonts installed on the machine take effect, the rest fall back.</summary>
    public static readonly string[] MonospaceFonts = ["Consolas", "Cascadia Code", "Cascadia Mono", "JetBrains Mono", "Fira Code", "Source Code Pro", "Courier New", "Menlo", "DejaVu Sans Mono"];
    public static readonly string[] TableFonts = ["Segoe UI", "Consolas", "Cascadia Mono", "JetBrains Mono", "Fira Code", "Source Code Pro", "Verdana", "Tahoma", "Arial"];
    public static string SourceFontFamily { get; private set; } = "";
    public static double SourceFontSize { get; private set; } = DefaultSourceFontSize;
    public static string TableFontFamily { get; private set; } = "";
    public static double TableFontSize { get; private set; } = DefaultTableFontSize;
    public static event Action? Changed;

    public static void Load(StudioPreferences preferences)
    {
        SourceFontFamily = preferences.SourceFontFamily ?? ""; SourceFontSize = Clamp(preferences.SourceFontSize, DefaultSourceFontSize);
        TableFontFamily = preferences.TableFontFamily ?? ""; TableFontSize = Clamp(preferences.TableFontSize, DefaultTableFontSize);
    }
    public static void Store(StudioPreferences preferences)
    {
        preferences.SourceFontFamily = SourceFontFamily; preferences.SourceFontSize = SourceFontSize;
        preferences.TableFontFamily = TableFontFamily; preferences.TableFontSize = TableFontSize;
    }
    public static void Set(string sourceFamily, double sourceSize, string tableFamily, double tableSize)
    {
        sourceFamily = sourceFamily.Trim(); tableFamily = tableFamily.Trim(); sourceSize = Clamp(sourceSize, DefaultSourceFontSize); tableSize = Clamp(tableSize, DefaultTableFontSize);
        if (sourceFamily == SourceFontFamily && tableFamily == TableFontFamily && Math.Abs(sourceSize - SourceFontSize) < 0.01 && Math.Abs(tableSize - TableFontSize) < 0.01) return;
        SourceFontFamily = sourceFamily; SourceFontSize = sourceSize; TableFontFamily = tableFamily; TableFontSize = tableSize;
        Changed?.Invoke();
    }
    /// <summary>Steps the source or table font size (0 resets it), for Ctrl+wheel and Ctrl+plus/minus/0.</summary>
    public static void Zoom(bool source, int steps)
    {
        // A fast wheel delivers several notches before the first restyle has painted; they are folded into one step of the right size.
        if (steps == 0) { pendingZoom = null; ApplyZoom(source, 0); return; }
        if (pendingZoom is { } pending && pending.Source == source) { pendingZoom = (source, pending.Steps + steps); return; }
        pendingZoom = (source, steps);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (pendingZoom is { } queued) { pendingZoom = null; ApplyZoom(queued.Source, queued.Steps); } }, Avalonia.Threading.DispatcherPriority.Render);
    }
    private static (bool Source, int Steps)? pendingZoom;
    private static void ApplyZoom(bool source, int steps)
    {
        double current = source ? SourceFontSize : TableFontSize, fallback = source ? DefaultSourceFontSize : DefaultTableFontSize;
        double next = steps == 0 ? fallback : Math.Clamp(Math.Round(current) + steps, MinFontSize, MaxFontSize);
        if (source) Set(SourceFontFamily, next, TableFontFamily, TableFontSize); else Set(SourceFontFamily, SourceFontSize, TableFontFamily, next);
    }
    private static double Clamp(double size, double fallback) => double.IsFinite(size) && size > 0 ? Math.Clamp(size, MinFontSize, MaxFontSize) : fallback;
    private static FontFamily Family(string name, string fallback) => new(name.Length == 0 ? fallback : name + ", " + fallback);

    public static void Apply(TextEditor editor) { editor.FontFamily = Family(SourceFontFamily, DefaultSourceFontFamily); editor.FontSize = SourceFontSize; }
    /// <summary>Rows grow with the font so the text is never clipped; the app-wide TextBox minimum (32) is what the 15pt default was sized for.</summary>
    public static double TableRowHeight => Math.Max(24, Math.Round(TableFontSize * 2));
    public static void Apply(DataGrid grid)
    {
        if (TableFontFamily.Length == 0) grid.ClearValue(TemplatedControl.FontFamilyProperty); else grid.FontFamily = Family(TableFontFamily, "Segoe UI, sans-serif");
        grid.FontSize = TableFontSize; grid.RowHeight = TableRowHeight;
    }
    /// <summary>Keeps a control styled with the current settings for as long as it is in the tree.</summary>
    public static void Follow(Control control, Action apply)
    {
        apply();
        control.AttachedToVisualTree += (_, _) => { apply(); Changed += apply; };
        control.DetachedFromVisualTree += (_, _) => Changed -= apply;
    }

    /// <summary>The View settings dialog: fonts for the table and the source editor with live samples, plus the display toggles.</summary>
    public static async Task ShowDialogAsync(Window owner)
    {
        var dialog = new Window { Title = "View settings", Width = 560, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new(22), Spacing = 8 };
        TextBlock Heading(string text) => new() { Text = text, Foreground = new SolidColorBrush(Color.Parse("#D8BC86")), FontWeight = FontWeight.SemiBold, Margin = new(0, 8, 0, 0) };
        (TextBox Family, NumericUpDown Size, TextBlock Sample) FontRow(string family, double size, string[] choices, string placeholder, string sampleText)
        {
            var row = new Grid { ColumnDefinitions = new("*,Auto,120"), Margin = new(0, 2, 0, 0) };
            var box = new TextBox { Text = family, PlaceholderText = placeholder };
            ToolTip.SetTip(box, "Pick a font from the list or type any installed font's name. Empty uses the default. Fonts that are not installed fall back to the default.");
            // The list opens on a click, no typing needed; each entry is drawn in its own font so the choice can be made by eye.
            var choose = new Button { Content = "▾", Padding = new(8, 4), Margin = new(4, 0, 0, 0), MinHeight = 0, VerticalAlignment = VerticalAlignment.Stretch };
            ToolTip.SetTip(choose, "Choose from common fonts");
            var list = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
            var defaultItem = new MenuItem { Header = "Default" + (placeholder.Length > 0 ? " · " + placeholder.Replace(" (default)", "") : "") }; defaultItem.Click += (_, _) => box.Text = ""; list.Items.Add(defaultItem); list.Items.Add(new Separator());
            foreach (var choice in choices)
            {
                var item = new MenuItem { Header = new TextBlock { Text = choice, FontFamily = Family(choice, "Segoe UI, sans-serif") } };
                item.Click += (_, _) => box.Text = choice; list.Items.Add(item);
            }
            choose.Click += (_, _) => list.ShowAt(choose);
            var numeric = new NumericUpDown { Value = (decimal)size, Minimum = (decimal)MinFontSize, Maximum = (decimal)MaxFontSize, Increment = 1, FormatString = "0", Margin = new(8, 0, 0, 0) };
            Grid.SetColumn(choose, 1); Grid.SetColumn(numeric, 2); row.Children.Add(box); row.Children.Add(choose); row.Children.Add(numeric); panel.Children.Add(row);
            var sample = new TextBlock { Text = sampleText, TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 0) }; panel.Children.Add(sample);
            return (box, numeric, sample);
        }
        panel.Children.Add(Heading("Table grid"));
        var table = FontRow(TableFontFamily, TableFontSize, TableFonts, "Segoe UI (default)", "skeleton1 · Returned · 0 · (pa15*cond('Difficulty',normal)+pst1)*256");
        panel.Children.Add(Heading("Source editor (JSON, text)"));
        var source = FontRow(SourceFontFamily, SourceFontSize, MonospaceFonts, DefaultSourceFontFamily + " (default)", "{ \"skill\": \"Fire Arrow\", \"calc1\": \"(pa15*cond('Difficulty',normal))/256\" }");
        panel.Children.Add(new TextBlock { Text = "Ctrl + mouse wheel, Ctrl + plus / minus and Ctrl + 0 change the size of the view under the pointer; the change is kept here.", Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 6, 0, 0) });
        panel.Children.Add(Heading("Display"));
        var letters = new CheckBox { Content = "Column letters (A, B … AA) in table headers and the Row Editor", IsChecked = EditorPane.ColumnLetters };
        var hoverCards = new CheckBox { Content = "Item hover cards over unique / set item rows", IsChecked = EditorPane.ItemHoverCards };
        panel.Children.Add(letters); panel.Children.Add(hoverCards);
        void Preview()
        {
            table.Sample.FontFamily = Family((table.Family.Text ?? "").Trim(), "Segoe UI, sans-serif"); table.Sample.FontSize = Clamp((double)(table.Size.Value ?? (decimal)DefaultTableFontSize), DefaultTableFontSize);
            source.Sample.FontFamily = Family((source.Family.Text ?? "").Trim(), DefaultSourceFontFamily); source.Sample.FontSize = Clamp((double)(source.Size.Value ?? (decimal)DefaultSourceFontSize), DefaultSourceFontSize);
        }
        foreach (var (family, size, _) in new[] { table, source }) { family.TextChanged += (_, _) => Preview(); size.ValueChanged += (_, _) => Preview(); }
        Preview();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new(0, 12, 0, 0) };
        var reset = new Button { Content = "Reset fonts" }; reset.Click += (_, _) => { table.Family.Text = ""; table.Size.Value = (decimal)DefaultTableFontSize; source.Family.Text = ""; source.Size.Value = (decimal)DefaultSourceFontSize; };
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => dialog.Close();
        var apply = new Button { Content = "Apply", Classes = { "accent" } };
        apply.Click += (_, _) =>
        {
            Set(source.Family.Text ?? "", (double)(source.Size.Value ?? (decimal)DefaultSourceFontSize), table.Family.Text ?? "", (double)(table.Size.Value ?? (decimal)DefaultTableFontSize));
            if (EditorPane.ColumnLetters != (letters.IsChecked == true)) { EditorPane.ColumnLetters = letters.IsChecked == true; EditorPane.RaiseColumnLettersChanged(); }
            if (EditorPane.ItemHoverCards != (hoverCards.IsChecked == true)) { EditorPane.ItemHoverCards = hoverCards.IsChecked == true; EditorPane.RaiseItemHoverCardsChanged(); }
            dialog.Close();
        };
        buttons.Children.Add(reset); buttons.Children.Add(cancel); buttons.Children.Add(apply); panel.Children.Add(buttons);
        dialog.Content = new ScrollViewer { Content = panel };
        await dialog.ShowDialog(owner);
    }
}

using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace ModStudio.App;

/// <summary>Dialogs of the particle editor: a colour picker for HDR colours, a texture/mesh picker, save-as-new-effect and a simple choice.</summary>
internal static class ParticleDialogs
{
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#A8A29A")), Error = new SolidColorBrush(Color.Parse("#E39A6B"));

    private static Window? Owner(Control anchor) => TopLevel.GetTopLevel(anchor) as Window;
    private static Window Dialog(string title, double width) => new() { Title = title, Width = width, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
    private static StackPanel Buttons(params Button[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        foreach (var b in buttons) panel.Children.Add(b);
        return panel;
    }

    public static async Task<string?> ChooseAsync(Control anchor, string title, string message, params string[] options)
    {
        if (Owner(anchor) is not { } owner) return null;
        var dialog = Dialog(title, 520); var stack = new StackPanel { Margin = new(20), Spacing = 16 };
        stack.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        var buttons = new WrapPanel(); foreach (var option in options) { var button = new Button { Content = option, Margin = new(0, 0, 8, 0) }; button.Click += (_, _) => dialog.Close(option); buttons.Children.Add(button); }
        stack.Children.Add(buttons); dialog.Content = stack;
        return await dialog.ShowDialog<string?>(owner);
    }

    /// <summary>
    /// Picks a colour. Channels may exceed 1 (the game's effects are HDR), so hue and saturation are edited on the colour
    /// scaled to its brightest channel and Intensity carries that channel's value.
    /// </summary>
    public static async Task<float[]?> ColorAsync(Control anchor, float[] initial, string title)
    {
        if (Owner(anchor) is not { } owner) return null;
        var dialog = Dialog(title, 420); var value = (float[])initial.Clone(); bool syncing = false;
        var swatch = new Border { Height = 48, CornerRadius = new(4) };
        var hue = new Slider { Minimum = 0, Maximum = 360 }; var saturation = new Slider { Minimum = 0, Maximum = 1 };
        var intensity = new Slider { Minimum = 0, Maximum = Math.Max(2, Math.Ceiling(initial[..3].Max() * 2)) };
        var boxes = Enumerable.Range(0, value.Length).Select(_ => new TextBox { Width = 72 }).ToArray();
        var error = new TextBlock { Foreground = Error, IsVisible = false, TextWrapping = TextWrapping.Wrap };
        void Show(bool fromBoxes)
        {
            syncing = true;
            swatch.Background = new SolidColorBrush(ParticleInspectorPane.ToColor(value));
            if (!fromBoxes) for (int i = 0; i < boxes.Length; i++) boxes[i].Text = ParticleInspectorPane.F(value[i]);
            var (h, s, v) = ModStudio.Core.ParticleEdits.ToHsv(value); hue.Value = h; saturation.Value = s; if (v > intensity.Maximum) intensity.Maximum = Math.Ceiling(v); intensity.Value = v;
            syncing = false;
        }
        void FromSliders()
        {
            if (syncing) return;
            var rgb = ModStudio.Core.ParticleEdits.FromHsv(hue.Value, saturation.Value, intensity.Value); rgb.CopyTo(value, 0);
            syncing = true; for (int i = 0; i < 3; i++) boxes[i].Text = ParticleInspectorPane.F(value[i]); swatch.Background = new SolidColorBrush(ParticleInspectorPane.ToColor(value)); syncing = false;
        }
        hue.ValueChanged += (_, _) => FromSliders(); saturation.ValueChanged += (_, _) => FromSliders(); intensity.ValueChanged += (_, _) => FromSliders();
        foreach (var (box, i) in boxes.Select((b, i) => (b, i)))
            box.TextChanged += (_, _) =>
            {
                if (syncing) return;
                if (float.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && float.IsFinite(v) && v >= 0) { value[i] = v; error.IsVisible = false; Show(true); }
                else { error.Text = "Channels are numbers of 0 or more."; error.IsVisible = true; }
            };
        var panel = new StackPanel { Margin = new(20), Spacing = 10 };
        panel.Children.Add(swatch);
        foreach (var (label, slider) in new[] { ("Hue", hue), ("Saturation", saturation), ("Intensity", intensity) })
        {
            var row = new DockPanel(); var text = new TextBlock { Text = label, Width = 80, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(text, Dock.Left); row.Children.Add(text); row.Children.Add(slider); panel.Children.Add(row);
        }
        var numbers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        string[] names = ["R", "G", "B", "A"];
        for (int i = 0; i < boxes.Length; i++) { numbers.Children.Add(new TextBlock { Text = names[i], Foreground = Muted, VerticalAlignment = VerticalAlignment.Center }); numbers.Children.Add(boxes[i]); }
        panel.Children.Add(numbers);
        panel.Children.Add(new TextBlock { Text = "Values above 1 glow brighter in game; the swatch scales them down to show the hue.", Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(error);
        var ok = new Button { Content = "OK", Classes = { "accent" } }; var cancel = new Button { Content = "Cancel" };
        ok.Click += (_, _) => { if (!error.IsVisible) dialog.Close(value); }; cancel.Click += (_, _) => dialog.Close(null);
        panel.Children.Add(Buttons(cancel, ok)); dialog.Content = panel; Show(false);
        return await dialog.ShowDialog<float[]?>(owner);
    }
    /// <summary>Picks a data-relative file of one type from the given candidates, or a typed path. Thumbnails load as rows come into view.</summary>
    public static async Task<string?> PickResourceAsync(Control anchor, string current, string extension, List<string> candidates, Action<string, Border> thumbnail)
    {
        if (Owner(anchor) is not { } owner) return null;
        var dialog = new Window { Title = "Choose " + extension.TrimStart('.') + " file", Width = 760, Height = 620, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var folder = current.Contains('/') ? current[..(current.LastIndexOf('/') + 1)].Replace("data/hd/", "") : "";
        var filter = new TextBox { Text = folder, PlaceholderText = "Filter by path" };
        var list = new ListBox { ItemTemplate = new FuncDataTemplate<string>((path, _) =>
        {
            var row = new DockPanel { Margin = new(2) }; var thumb = new Border { Width = 40, Height = 40, Margin = new(0, 0, 10, 0), Background = new SolidColorBrush(Color.Parse("#121315")) };
            DockPanel.SetDock(thumb, Dock.Left); row.Children.Add(thumb); row.Children.Add(new TextBlock { Text = path, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            if (path != null) thumbnail(path, thumb);
            return row;
        }) };
        var chosen = new TextBox { Text = current };
        var count = new TextBlock { Foreground = Muted, FontSize = 11 };
        void Filter()
        {
            var terms = (filter.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var shown = candidates.Where(c => terms.All(t => c.Contains(t, StringComparison.OrdinalIgnoreCase))).ToList();
            list.ItemsSource = shown; count.Text = $"{shown.Count:N0} of {candidates.Count:N0} {extension} files in the project and game data";
            if (shown.FindIndex(c => c.Equals(current, StringComparison.OrdinalIgnoreCase)) is >= 0 and var index) { list.SelectedIndex = index; list.ScrollIntoView(index); }
        }
        filter.TextChanged += (_, _) => Filter();
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is string s) chosen.Text = s; };
        list.DoubleTapped += (_, _) => { if (list.SelectedItem is string s) dialog.Close(s); };
        var ok = new Button { Content = "Use this file", Classes = { "accent" } }; var cancel = new Button { Content = "Cancel" };
        ok.Click += (_, _) => { var text = (chosen.Text ?? "").Trim(); if (text.Length > 0) dialog.Close(text); }; cancel.Click += (_, _) => dialog.Close(null);
        var grid = new Grid { RowDefinitions = new("Auto,Auto,*,Auto,Auto,Auto"), Margin = new(16) };
        var hint = new TextBlock { Text = "A texture used by effects should be a D2R .texture (BC or RGBA). A file the game data and the project both lack is reported missing in game.", Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8) };
        grid.Children.Add(hint); Grid.SetRow(filter, 1); grid.Children.Add(filter);
        list.Margin = new(0, 8); Grid.SetRow(list, 2); grid.Children.Add(list);
        Grid.SetRow(count, 3); grid.Children.Add(count); chosen.Margin = new(0, 8); Grid.SetRow(chosen, 4); grid.Children.Add(chosen);
        var buttons = Buttons(cancel, ok); Grid.SetRow(buttons, 5); grid.Children.Add(buttons);
        dialog.Content = grid; Filter();
        return await dialog.ShowDialog<string?>(owner);
    }

    /// <summary>Asks for the copy's name and which JSON files that use the original should use the copy instead.</summary>
    public static async Task<(string Name, IReadOnlyCollection<string> Retarget)?> CloneAsync(Control anchor, string initial, IReadOnlyList<string> usages, Func<string, string> label)
    {
        if (Owner(anchor) is not { } owner) return null;
        var dialog = Dialog("Save as new effect", 560);
        var name = new TextBox { Text = initial }; var error = new TextBlock { Foreground = Error, IsVisible = false, TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Margin = new(20), Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "Name of the new effect (saved in the project beside where this one lives)", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(name);
        var checks = new List<(CheckBox Box, string File)>();
        if (usages.Count > 0)
        {
            panel.Children.Add(new TextBlock { Text = "Also point these files at the new effect (game data files are copied into the project first). Leave them unticked to keep the original in use there.", TextWrapping = TextWrapping.Wrap, Foreground = Muted, FontSize = 12 });
            var list = new StackPanel { Spacing = 2 };
            foreach (var usage in usages) { var box = new CheckBox { Content = new TextBlock { Text = label(usage) } }; checks.Add((box, usage)); list.Children.Add(box); }
            panel.Children.Add(new ScrollViewer { Content = list, MaxHeight = 240 });
        }
        else panel.Children.Add(new TextBlock { Text = "No JSON file names this effect, so nothing else changes. Point a missile or overlay JSON at the new file to use it.", TextWrapping = TextWrapping.Wrap, Foreground = Muted, FontSize = 12 });
        panel.Children.Add(error);
        var ok = new Button { Content = "Save", Classes = { "accent" } }; var cancel = new Button { Content = "Cancel" };
        void Accept()
        {
            var text = (name.Text ?? "").Trim();
            if (text.Length == 0 || !text.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) { error.Text = "Use letters, digits, - and _ only."; error.IsVisible = true; return; }
            dialog.Close((text, (IReadOnlyCollection<string>)[.. checks.Where(c => c.Box.IsChecked == true).Select(c => c.File)]));
        }
        ok.Click += (_, _) => Accept(); cancel.Click += (_, _) => dialog.Close(null);
        name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Accept(); e.Handled = true; } };
        panel.Children.Add(Buttons(cancel, ok)); dialog.Content = panel;
        dialog.Opened += (_, _) => { name.Focus(); name.SelectAll(); };
        return await dialog.ShowDialog<(string, IReadOnlyCollection<string>)?>(owner);
    }
}

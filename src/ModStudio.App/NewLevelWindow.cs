using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ModStudio.Core;

namespace ModStudio.App;

/// <summary>
/// What a new level drawn on the world map becomes: its levels row (copying <paramref name="TemplateRow"/>'s settings
/// when it is not -1) and, when <paramref name="Map"/> is set, a lvlprest row naming that map and the map itself, either
/// blank or a copy of the template's (<paramref name="CopyMap"/>).
/// </summary>
internal sealed record NewLevelPlan(NewLevelRequest Level, int TemplateRow, string? Map, bool CopyMap);

/// <summary>A level another can be built from, as the template list shows it.</summary>
internal sealed record LevelTemplate(int Row, string Id, string Title, int Act, int DrlgType)
{
    public override string ToString() => Row < 0 ? "(none: start blank)" : $"{Title} · Id {Id} · Act {Act + 1}";
}

/// <summary>What the new-level form needs from the builder.</summary>
internal sealed record NewLevelContext(int Act, int X, int Y, int Width, int Height, int Id, LevelTemplate[] Templates, int DefaultTemplate, bool PresetsAvailable,
    Func<int, int, int, int, IReadOnlyList<string>> Collisions, Func<int, (string Map, int SizeX, int SizeY)?> TemplateMap, Func<string, string?> MapProblem);

/// <summary>
/// The form a rectangle drawn on the world map opens: name the level, check where it goes and how big it is, pick what it
/// copies and whether it gets a preset map. Size and offset start from the rectangle.
/// </summary>
internal sealed class NewLevelWindow : Window
{
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#D8BC86")), Muted = new SolidColorBrush(Color.Parse("#B9AD97")), Warn = new SolidColorBrush(Color.Parse("#E39A6B"));
    private readonly NewLevelContext context;
    private readonly TextBox name = new(), x = Number(), y = Number(), width = Number(), height = Number(), map = new() { PlaceholderText = "Act1/Custom/my_level.ds1" };
    private readonly ComboBox generation = new() { ItemsSource = new[] { "Preset: a fixed map (DrlgType 2)", "Maze: rooms from lvlmaze (DrlgType 1)" }, SelectedIndex = 0, Width = 320 };
    private readonly ComboBox template = new() { Width = 420 };
    private readonly CheckBox files = new() { Content = "Add a lvlprest row and create its map (DS1 and HD preset JSON)", IsChecked = true };
    private readonly RadioButton blank = new() { Content = "Blank map of this size", IsChecked = true, GroupName = "map" }, copy = new() { Content = "Copy the template's map (the size becomes the map's)", GroupName = "map" };
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 }, error = new() { TextWrapping = TextWrapping.Wrap, Foreground = Warn };
    private readonly Button create = new() { Content = "Create level", Classes = { "accent" } };
    private readonly StackPanel filesPanel = new() { Spacing = 8, Margin = new(24, 0, 0, 0) };
    private bool mapEdited, updating;

    internal NewLevelPlan? Result { get; private set; }
    internal ComboBox Generation => generation;
    internal Button CreateButton => create;
    internal string SummaryText => summary.Text ?? "";
    internal string ErrorText => error.Text ?? "";

    private static TextBox Number() => new() { Width = 80 };

    public NewLevelWindow(NewLevelContext context)
    {
        this.context = context;
        Title = "New level"; Width = 720; SizeToContent = SizeToContent.Height; CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new(26), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = $"New level in Act {context.Act + 1}", FontSize = 24, Foreground = Accent });
        panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Muted, Text = $"Id {context.Id}, added at the bottom of levels. SizeX/SizeY (every difficulty) and OffsetX/OffsetY are written from the rectangle you drew; adjust them here. Link it from another level's Vis#/Warp# afterwards." });

        panel.Children.Add(Row("Name (pointer)", name));
        var place = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var (label, box) in new[] { ("X", x), ("Y", y), ("Width", width), ("Height", height) })
        {
            place.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Foreground = Muted, Margin = new(label == "X" ? 0 : 8, 0, 0, 0) });
            place.Children.Add(box);
        }
        panel.Children.Add(Row("Offset and size (tiles)", place));
        panel.Children.Add(Row("Generation", generation));
        panel.Children.Add(Row("Copy settings from", template));
        panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Muted, FontSize = 11, Text = "The template's monsters, tiles (LevelType), sound, rules and lighting are copied. Its names, waypoint, links and position are not." });

        panel.Children.Add(files);
        filesPanel.Children.Add(Row("Map (under global/tiles)", map));
        filesPanel.Children.Add(blank); filesPanel.Children.Add(copy);
        panel.Children.Add(filesPanel);
        panel.Children.Add(new Border { Padding = new(14), Background = new SolidColorBrush(Color.Parse("#292727")), Child = summary });
        panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => Close();
        buttons.Children.Add(cancel); buttons.Children.Add(create); panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

        updating = true;
        name.Text = $"Act {context.Act + 1} - New Level {context.Id}";
        (x.Text, y.Text, width.Text, height.Text) = (Text(context.X), Text(context.Y), Text(context.Width), Text(context.Height));
        template.ItemsSource = context.Templates;
        template.SelectedItem = context.Templates.FirstOrDefault(t => t.Row == context.DefaultTemplate) ?? context.Templates.FirstOrDefault();
        if (template.SelectedItem is LevelTemplate { DrlgType: 1 }) generation.SelectedIndex = 1;
        files.IsEnabled = context.PresetsAvailable;
        if (!context.PresetsAvailable) files.IsChecked = false;
        updating = false;
        SuggestMap();

        foreach (var box in new[] { name, x, y, width, height }) box.TextChanged += (_, _) => { if (box == name) SuggestMap(); Refresh(); };
        map.TextChanged += (_, _) => { if (!updating) mapEdited = true; Refresh(); };
        generation.SelectionChanged += (_, _) => Refresh();
        template.SelectionChanged += (_, _) => { SuggestMap(); Refresh(); };
        files.IsCheckedChanged += (_, _) => Refresh();
        copy.IsCheckedChanged += (_, _) => Refresh();
        create.Click += (_, _) => { if (Build() is { } plan) { Result = plan; Close(); } };
        Opened += (_, _) => { name.Focus(); name.SelectAll(); };
        Refresh();
    }

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static Control Row(string label, Control input)
    {
        var grid = new Grid { ColumnDefinitions = new("170,*") };
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(input, 1); grid.Children.Add(input);
        return grid;
    }

    private LevelTemplate? Chosen => template.SelectedItem as LevelTemplate is { Row: >= 0 } t ? t : null;
    private (string Map, int SizeX, int SizeY)? TemplateMap => Chosen is { } t ? context.TemplateMap(t.Row) : null;

    /// <summary>Until the map path is typed, it follows the name, in the template map's folder.</summary>
    private void SuggestMap()
    {
        if (mapEdited) return;
        var folder = TemplateMap is { } t && t.Map.LastIndexOf('/') is var slash and > 0 ? t.Map[..slash] : $"Act{context.Act + 1}";
        updating = true;
        map.Text = $"{folder}/Custom/{LevelWorld.Slug(name.Text ?? "")}.ds1";
        updating = false;
    }

    private void Refresh()
    {
        if (updating) return;
        bool preset = generation.SelectedIndex == 0;
        files.IsVisible = preset; filesPanel.IsVisible = preset && files.IsChecked == true;
        copy.IsEnabled = TemplateMap != null;
        if (!copy.IsEnabled && copy.IsChecked == true) blank.IsChecked = true;
        if (copy.IsChecked == true && TemplateMap is { } source && (width.Text, height.Text) != (Text(source.SizeX), Text(source.SizeY)))
        {
            updating = true; (width.Text, height.Text) = (Text(source.SizeX), Text(source.SizeY)); updating = false;
        }
        width.IsEnabled = height.IsEnabled = copy.IsChecked != true || !filesPanel.IsVisible;
        var plan = Build();
        create.IsEnabled = plan != null;
        if (plan == null) return;
        var lines = new List<string> { $"levels: {plan.Level.Name} · Id {context.Id} · {(preset ? "preset" : "maze")} · {plan.Level.Width} × {plan.Level.Height} tiles at {plan.Level.X}, {plan.Level.Y}" };
        if (plan.Map != null)
        {
            lines.Add($"lvlprest: a row with LevelId {context.Id}, File1 {plan.Map}");
            lines.Add($"data/global/tiles/{plan.Map}{(plan.CopyMap ? " (copied)" : $" (blank, {plan.Level.Width + 1} × {plan.Level.Height + 1} cells)")}");
            lines.Add($"data/{LevelWorld.PresetFor(plan.Map)}{(plan.CopyMap ? " (copied)" : " (no entities yet)")}");
        }
        summary.Text = string.Join("\n", lines);
    }

    private NewLevelPlan? Build()
    {
        void Fail(string message) => error.Text = message;
        error.Text = "";
        int Parse(TextBox box) => int.TryParse((box.Text ?? "").Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : int.MinValue;
        int px = Parse(x), py = Parse(y), pw = Parse(width), ph = Parse(height);
        var levelName = (name.Text ?? "").Trim();
        if (levelName.Length == 0) { Fail("Name the level: other tables point at it by Name."); return null; }
        if (levelName.Contains('\t') || levelName.Contains('\n')) { Fail("A name cannot hold tabs or line breaks."); return null; }
        if (px < 0 || py < 0) { Fail("The offset must be whole numbers of at least 0."); return null; }
        if (pw is < 1 or > 511 || ph is < 1 or > 511) { Fail("Width and height must be 1–511 tiles."); return null; }
        if (context.Collisions(px, py, pw, ph) is { Count: > 0 } blocked) { Fail("Overlaps " + string.Join(", ", blocked) + ". Move it or make it smaller."); return null; }
        bool preset = generation.SelectedIndex == 0;
        string? mapPath = null;
        if (preset && files.IsChecked == true && context.PresetsAvailable)
        {
            try { mapPath = LevelWorld.MapPath(map.Text ?? ""); }
            catch (InvalidDataException ex) { Fail(ex.Message); return null; }
            if (context.MapProblem(mapPath) is { } problem) { Fail(problem); return null; }
        }
        return new(new(levelName, context.Act, preset ? 2 : 1, px, py, pw, ph), Chosen?.Row ?? -1, mapPath, mapPath != null && copy.IsChecked == true && TemplateMap != null);
    }
}

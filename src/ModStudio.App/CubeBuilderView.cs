using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ModStudio.Core;

namespace ModStudio.App;

/// <summary>
/// The Visual Builder for cube recipes: the recipe drawn as the items that go in and what comes out, with their pictures,
/// quantities and qualities; each input and output edited by picking the item and switching its modifiers on and off
/// (the cell keeps the order it was written in); the outputs' levels and added properties; and the recipe preview's
/// warnings, such as an earlier recipe that takes the same items first.
/// </summary>
internal sealed class CubeBuilderView : TableBuilderView<CubeEntry, CubeBuilderCatalog>
{
    private static readonly IBrush RecipeBrush = new SolidColorBrush(Color.Parse("#D8BC86")), MagicBrush = new SolidColorBrush(Color.Parse("#7878FF")), MagicLink = new SolidColorBrush(Color.Parse("#A3A3FF"));
    private static readonly (string Key, string Label)[] Qualities = [("low", "Low quality"), ("nor", "Normal"), ("hiq", "Superior"), ("mag", "Magic"), ("set", "Set"), ("rar", "Rare"), ("uni", "Unique"), ("crf", "Crafted"), ("tmp", "Tempered")];
    private static readonly (string Key, string Label)[] InputTiers = [("bas", "Normal tier"), ("exc", "Exceptional"), ("eli", "Elite")];
    private static readonly (string Key, string Label)[] OutputTiers = [("exc", "Upgrade to exceptional"), ("eli", "Upgrade to elite")];
    private static readonly (string Key, string Label)[] Ethereal = [("eth", "Ethereal"), ("noe", "Not ethereal")];
    private static readonly (string Key, string Label)[] InputFlags = [("nos", "No sockets"), ("upg", "Can be upgraded"), ("nru", "Not a runeword"), ("id", "Identified")];
    private static readonly (string Key, string Label)[] OutputFlags = [("eth", "Ethereal"), ("mod", "Keep input 1's mods"), ("reg", "Reroll unique"), ("rep", "Repair"), ("rch", "Refill charges"), ("uns", "Destroy socketed"), ("rem", "Remove socketed")];
    private static readonly (string Prefix, string Column, string Title)[] Outputs = [("", "output", "Output"), ("b ", "output b", "Output B"), ("c ", "output c", "Output C")];

    private readonly CubeBuilderResolver catalogResolver = new();
    private readonly RecipePreviewResolver previewResolver = new();
    private readonly PreviewWorkQueue previewWork = new();
    private CancellationTokenSource? previewCancellation, pictureCancellation;
    private (string? Project, int Workspace) previewContext;
    private readonly Dictionary<string, WriteableBitmap?> pictures = new(StringComparer.OrdinalIgnoreCase);
    private string pictureContext = "";
    private ContentControl cube = new(), warnings = new(), details = new();
    private TextBlock total = new();
    private readonly List<CellEditor> cells = [];
    private readonly Dictionary<string, ContentControl> modLines = new(StringComparer.Ordinal);

    /// <summary>The structured controls of one input or output cell, rewritten from the cell whenever it changes.</summary>
    private sealed record CellEditor(string Column, bool Output, AutoCompleteBox Head, TextBox Quantity, ComboBox Quality, ComboBox Tier, ComboBox? Eth, Dictionary<string, CheckBox> Flags, Dictionary<string, TextBox> Values);

    internal RecipePreviewResult? LastPreview { get; private set; }
    internal Task PendingPictures { get; private set; } = Task.CompletedTask;
    internal Control Cube => cube;
    internal Control Warnings => warnings;
    internal Control? Cell(string column, string part) => cells.FirstOrDefault(c => c.Column == column) is { } cell ? part switch
    {
        "head" => cell.Head, "qty" => cell.Quantity, "quality" => cell.Quality, "tier" => cell.Tier, "eth" => cell.Eth,
        _ => cell.Flags.GetValueOrDefault(part) ?? (Control?)cell.Values.GetValueOrDefault(part)
    } : null;

    public CubeBuilderView(EditorPane pane, VisualBuilderHost host) : base(pane, host) => Ready();

    protected override string ListHeading => "CUBE RECIPES";
    protected override string SearchHint => "Search by description, input or output";
    protected override string AddLabel => "+ New recipe";
    protected override string Noun => "a recipe";
    protected override string NameColumn => "description";
    protected override IBrush TitleBrush => RecipeBrush;
    protected override string MoreColumnsHint => " · everything else in cubemain";
    protected override string TitleText(TableData table, int row) => table.Cell(row, "description") is { Length: > 0 } text ? text : $"Recipe row {row}";

    protected override JsonObject NewRow(TableData table)
    {
        var fields = new JsonObject();
        foreach (var (column, value) in new[] { ("description", "New recipe"), ("enabled", "1"), ("version", "100"), ("numinputs", "1"), ("input 1", "any") })
            if (table.ColumnIndex(column) >= 0) fields[column] = value;
        return fields;
    }

    protected override IReadOnlyList<object> ListRows(TableData table) => CubeBuilderResolver.Rows(table);
    protected override CubeBuilderCatalog LoadCatalog(ModProject project, string profile, string locale, IReadOnlyList<object> rows, bool fresh, CancellationToken token)
    {
        if (fresh) catalogResolver.Clear();
        return catalogResolver.Catalog(project, profile, locale, [.. rows.Cast<CubeRow>()], token);
    }
    protected override IReadOnlyList<CubeEntry> EntriesOf(CubeBuilderCatalog catalog) => catalog.Entries;
    protected override int RowOf(CubeEntry entry) => entry.Row;
    protected override string SourceIdOf(CubeEntry entry) => entry.SourceId;
    protected override bool IsInactive(CubeEntry entry) => entry.Inactive;
    protected override string SearchTextOf(CubeEntry entry) => entry.SearchText;

    protected override Control EntryView(CubeEntry entry)
    {
        var panel = new StackPanel { Margin = new(2, 3) };
        panel.Children.Add(new TextBlock { Text = entry.Description.Length > 0 ? entry.Description : $"Row {entry.Row}", FontSize = 13, Foreground = entry.Enabled ? RecipeBrush : Muted, TextTrimming = TextTrimming.CharacterEllipsis });
        panel.Children.Add(new TextBlock { Text = (entry.Enabled ? "" : "disabled · ") + entry.Summary, FontSize = 11, Foreground = Muted, TextTrimming = TextTrimming.CharacterEllipsis });
        ToolTip.SetTip(panel, $"row {entry.Row}\n{entry.Summary}");
        return panel;
    }

    protected override void OnSelect() => LastPreview = null;
    protected override void OnInvalidate() => pictureContext = "";
    protected override void Stop() { previewCancellation?.Cancel(); pictureCancellation?.Cancel(); }

    // ── Cards ──────────────────────────────────────────────────────────────────────────────────────────

    protected override void BuildCards(StackPanel root, TableData table)
    {
        cells.Clear(); modLines.Clear();
        cube = new ContentControl(); warnings = new ContentControl(); total = new TextBlock { FontSize = 12, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center };
        root.Children.Add(new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#1A1712"), 0), new GradientStop(Color.Parse("#0C0B09"), 1) }
            },
            BorderBrush = new SolidColorBrush(Color.Parse("#4A4030")), BorderThickness = new(1), CornerRadius = new(4), Padding = new(16, 12),
            Child = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = "HORADRIC CUBE", Foreground = RecipeBrush, FontSize = 11, FontFamily = TooltipFont }, cube } }
        });
        root.Children.Add(warnings);
        root.Children.Add(RecipeCard(table));
        var inputs = new StackPanel { Spacing = 10 };
        for (int i = 1; i <= 7; i++) if (table.ColumnIndex("input " + i) >= 0) inputs.Children.Add(CellRow(table, "input " + i, $"Input {i}", false));
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var match = new Button { Content = "Set numinputs to this", Padding = new(8, 2), MinHeight = 0, FontSize = 11 };
        ToolTip.SetTip(match, "numinputs must equal the number of items in the cube, counting quantities, or the recipe never matches");
        match.Click += (_, _) => { if (Table is { } t && SelectedRow >= 0) Commit("numinputs", InputCount(t, SelectedRow).ToString(CultureInfo.InvariantCulture)); };
        header.Children.Add(total); header.Children.Add(match);
        root.Children.Add(Card("Inputs", inputs, header, "Each input is an item, an item type or a unique/set name, then its quantity and conditions. Order does not matter to the game."));
        foreach (var (prefix, column, title) in Outputs)
            if (table.ColumnIndex(column) >= 0) root.Children.Add(OutputCard(table, prefix, column, title));
        details = new ContentControl { Content = new TextBlock { Text = "Resolving…", Foreground = Muted } };
        root.Children.Add(new Expander { Header = "Everything the recipe preview reads from this row", Content = details, HorizontalAlignment = HorizontalAlignment.Stretch });
    }

    private Control RecipeCard(TableData table)
    {
        var fields = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 12, LineSpacing = 10 };
        void Add(Control? control) { if (control != null) fields.Children.Add(control); }
        Add(Field(table, "description", "Description", 420));
        if (table.ColumnIndex("enabled") >= 0) fields.Children.Add(Toggle("enabled", "Enabled", "Only enabled recipes are used by the cube."));
        if (table.ColumnIndex("min diff") >= 0) fields.Children.Add(Labeled("Difficulty", Numbered("min diff", 200, "0 · every difficulty", "1 · Nightmare and Hell", "2 · Hell only"), ColumnGuide.Find("cubemain", "min diff")?.Description));
        foreach (var (column, label, width) in new[] { ("version", "Version", 80), ("class", "Class only", 80), ("firstLadderSeason", "First ladder season", 90), ("lastLadderSeason", "Last ladder season", 90),
            ("numinputs", "numinputs", 80), ("op", "Condition (op)", 80), ("param", "Param", 80), ("value", "Value", 80) })
            Add(Field(table, column, label, width));
        return Card("Recipe", fields, note: "When the recipe is used: difficulty, class, ladder, and an optional condition on input 1's stats (op, param, value).");
    }

    /// <summary>One input or output cell: the item picked from everything a cell can name, its quantity, qualities and flags, and the raw cell underneath.</summary>
    private Control CellRow(TableData table, string column, string label, bool output)
    {
        var head = new AutoCompleteBox
        {
            PlaceholderText = output ? "item, type, unique, useitem…" : "item, type, unique or any", Width = 230, MinHeight = 30, FilterMode = AutoCompleteFilterMode.Custom,
            MinimumPrefixLength = 1, MaxDropDownHeight = 340, ItemsSource = catalog?.Choices ?? [],
            ItemFilter = (text, item) => item is CubeChoice choice && (output || choice.Kind != "special" || choice.Head == "any") && VisualBuilder.Matches(choice.SearchText, text ?? ""),
            ItemTemplate = new FuncDataTemplate<CubeChoice>((choice, _) => choice == null ? new TextBlock() : Option(choice.Head, choice.Label + " · " + choice.Kind))
        };
        AutomationProperties.SetName(head, column + " item");
        var quantity = new TextBox { Width = 56, MinHeight = 30, PlaceholderText = "1", VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(quantity, column + " qty");
        ComboBox Group(string any, (string Key, string Label)[] options) => new() { Width = 160, ItemsSource = options.Select(o => o.Label).Prepend(any).ToArray(), SelectedIndex = 0 };
        var quality = Group(output ? "Keep quality" : "Any quality", Qualities);
        var tier = Group(output ? "Keep tier" : "Any tier", output ? OutputTiers : InputTiers);
        var eth = output ? null : Group("Ethereal or not", Ethereal);
        var flags = new Dictionary<string, CheckBox>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, text) in output ? OutputFlags : InputFlags) flags[key] = new CheckBox { Content = text, FontSize = 12 };
        var values = new Dictionary<string, TextBox>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in output ? new[] { "sock", "pre", "suf", "lvl" } : ["sock"]) values[key] = new TextBox { Width = 60, MinHeight = 30, PlaceholderText = key, VerticalContentAlignment = VerticalAlignment.Center };
        var editor = new CellEditor(column, output, head, quantity, quality, tier, eth, flags, values);
        cells.Add(editor);

        // Every structured control rewrites the cell through the same tokens, keeping what it does not touch.
        void Apply(Action<CubeTokens> change)
        {
            if (loading || Table is not { } t || SelectedRow < 0) return;
            var tokens = CubeTokens.Parse(t.Cell(SelectedRow, column));
            change(tokens);
            Commit(column, tokens.ToString());
        }
        void CommitHead() { var text = (head.Text ?? "").Trim(); Apply(tokens => tokens.Head = text); }
        head.TextChanged += (_, _) => { var text = head.Text ?? ""; if (text.Length == 0 || catalog?.Find(text) != null) CommitHead(); };
        head.LostFocus += (_, _) => { if (!head.IsKeyboardFocusWithin) CommitHead(); };
        head.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitHead(); };
        quantity.TextChanged += (_, _) => Apply(tokens => tokens.SetQuantity(int.TryParse(quantity.Text, out var n) ? n : 1));
        quality.SelectionChanged += (_, _) => Apply(tokens => tokens.Choose(Qualities.Select(q => q.Key), quality.SelectedIndex > 0 ? Qualities[quality.SelectedIndex - 1].Key : null));
        var tiers = output ? OutputTiers : InputTiers;
        tier.SelectionChanged += (_, _) => Apply(tokens => tokens.Choose(tiers.Select(q => q.Key), tier.SelectedIndex > 0 ? tiers[tier.SelectedIndex - 1].Key : null));
        if (eth != null) eth.SelectionChanged += (_, _) => Apply(tokens => tokens.Choose(Ethereal.Select(q => q.Key), eth.SelectedIndex > 0 ? Ethereal[eth.SelectedIndex - 1].Key : null));
        foreach (var (key, box) in flags) box.IsCheckedChanged += (_, _) => Apply(tokens => tokens.Flag(key, box.IsChecked == true));
        foreach (var (key, box) in values) box.TextChanged += (_, _) => Apply(tokens => tokens.Set(key, (box.Text ?? "").Trim()));

        var top = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 10, LineSpacing = 6 };
        top.Children.Add(Labeled(label, head, ColumnGuide.Find("cubemain", column)?.Description));
        top.Children.Add(Labeled("Quantity", quantity));
        top.Children.Add(Labeled("Quality", quality));
        top.Children.Add(Labeled("Tier", tier));
        if (eth != null) top.Children.Add(Labeled("Ethereal", eth));
        foreach (var (key, box) in values) top.Children.Add(Labeled(key switch { "sock" => "Sockets", "pre" => "Prefix row", "suf" => "Suffix row", _ => "Level (portal)" }, box,
            key switch { "sock" => "sock=N: exactly N sockets", "pre" => "pre=N: magicprefix row to add", "suf" => "suf=N: magicsuffix row to add", _ => "lvl=N: the level a red portal opens to" }));
        var clear = new Button { Content = "✕", Padding = new(8, 2), MinHeight = 0, FontSize = 12, Foreground = Muted, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Bottom };
        ToolTip.SetTip(clear, "Clear this cell");
        clear.Click += (_, _) => Commit(column, "");
        top.Children.Add(clear);
        var flagRow = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 12 };
        foreach (var box in flags.Values) flagRow.Children.Add(box);
        var raw = Text(column, "the cell as written", 280);
        flagRow.Children.Add(Labeled("Cell", raw, "The whole cell as the game reads it; edit it directly for anything the controls above do not cover."));
        return new StackPanel { Spacing = 6, Children = { top, flagRow } };
    }

    private Control OutputCard(TableData table, string prefix, string column, string title)
    {
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(CellRow(table, column, title, true));
        var levels = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 12 };
        foreach (var (suffix, label) in new[] { ("lvl", "Fixed item level"), ("plvl", "% of player level"), ("ilvl", "% of input 1's level") })
            if (Field(table, prefix + suffix, label, 96) is { } field) levels.Children.Add(field);
        body.Children.Add(levels);
        var mods = new StackPanel { Spacing = 4 };
        var head = new Grid { ColumnDefinitions = new("60,2*,*,1.2*,*,*"), ColumnSpacing = 6 };
        string[] heads = ["", "Property", "Chance %", "Parameter", "Min", "Max"];
        for (int i = 1; i < heads.Length; i++) { var text = new TextBlock { Text = heads[i], FontSize = 11, Foreground = Muted }; SetColumn(text, i); head.Children.Add(text); }
        mods.Children.Add(head);
        for (int i = 1; i <= 5; i++)
        {
            var mod = $"{prefix}mod {i}"; if (table.ColumnIndex(mod) < 0) continue;
            var row = new Grid { ColumnDefinitions = new("60,2*,*,1.2*,*,*"), RowDefinitions = new("Auto,Auto"), ColumnSpacing = 6 };
            var label = new TextBlock { Text = "Mod " + i, VerticalAlignment = VerticalAlignment.Center, Foreground = GameTooltip.White, FontSize = 12 }; row.Children.Add(label);
            Control[] inputs = [Choice(mod, "property", double.NaN, () => catalog?.Properties ?? [], p => p.SearchText, p => Option(p.Code, p.Tooltip.Length > 0 ? p.Tooltip : p.Notes), code => catalog?.Properties.Any(p => p.Code == code) == true),
                Text(mod + " chance", "", double.NaN), Text(mod + " param", "", double.NaN), Text(mod + " min", "", double.NaN), Text(mod + " max", "", double.NaN)];
            for (int c = 0; c < inputs.Length; c++) { SetColumn(inputs[c], c + 1); row.Children.Add(inputs[c]); }
            var line = new ContentControl { Margin = new(4, 2, 0, 0) }; SetRow(line, 1); SetColumn(line, 1); SetColumnSpan(line, 5); row.Children.Add(line);
            modLines[mod] = line;
            mods.Children.Add(row);
        }
        body.Children.Add(mods);
        return Card(title, body, note: "What the cube gives back: the item and its quality, its level, and up to five properties added to it (each with an optional chance).");
    }

    /// <summary>How many items the inputs put in the cube, counting quantities.</summary>
    private static int InputCount(TableData table, int row) =>
        Enumerable.Range(1, 7).Select(i => table.ColumnIndex("input " + i) >= 0 ? table.Cell(row, "input " + i).Trim() : "").Where(v => v.Length > 0).Sum(v => CubeTokens.Parse(v).Quantity);

    protected override void OnSynced(TableData table, int row)
    {
        foreach (var cell in cells)
        {
            var tokens = CubeTokens.Parse(table.ColumnIndex(cell.Column) >= 0 ? table.Cell(row, cell.Column) : "");
            if (!cell.Head.IsKeyboardFocusWithin && cell.Head.Text != tokens.Head) cell.Head.Text = tokens.Head;
            var quantity = tokens.Has("qty") ? tokens.Value("qty") : "";
            if (cell.Quantity.Text != quantity) cell.Quantity.Text = quantity;
            int Index((string Key, string Label)[] options) { var chosen = tokens.Chosen(options.Select(o => o.Key)); return chosen == null ? 0 : Array.FindIndex(options, o => o.Key.Equals(chosen, StringComparison.OrdinalIgnoreCase)) + 1; }
            cell.Quality.SelectedIndex = Index(Qualities);
            cell.Tier.SelectedIndex = Index(cell.Output ? OutputTiers : InputTiers);
            if (cell.Eth != null) cell.Eth.SelectedIndex = Index(Ethereal);
            foreach (var (key, box) in cell.Flags) if (box.IsChecked != tokens.Has(key)) box.IsChecked = tokens.Has(key);
            foreach (var (key, box) in cell.Values) { var value = tokens.Value(key) ?? ""; if (box.Text != value) box.Text = value; }
            cell.Head.ItemsSource ??= catalog?.Choices;
        }
        int count = InputCount(table, row), declared = int.TryParse(table.Cell(row, "numinputs"), out var n) ? n : 0;
        total.Text = $"{count} item{(count == 1 ? "" : "s")} in the cube" + (declared != count ? $" · numinputs says {declared}" : "");
        total.Foreground = declared != count ? Brushes.Salmon : Muted;
        DrawCube(table, row);
    }

    protected override void OnCatalogLoaded()
    {
        foreach (var cell in cells) cell.Head.ItemsSource = catalog?.Choices;
        pictureContext = "";
        if (Table is { } table && SelectedRow >= 0) DrawCube(table, SelectedRow);
    }

    // ── The cube drawing ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Inputs, an arrow, outputs: each a card with its picture, quantity, name and conditions. A card opens its cell's editor.</summary>
    private void DrawCube(TableData table, int row)
    {
        string Cell(string column) => table.ColumnIndex(column) >= 0 ? table.Cell(row, column).Trim() : "";
        var inputs = Enumerable.Range(1, 7).Select(i => "input " + i).Where(c => Cell(c).Length > 0).ToArray();
        var outputs = Outputs.Select(o => o.Column).Where(c => Cell(c).Length > 0).ToArray();
        var firstInput = inputs.Length > 0 ? CubeTokens.Parse(Cell(inputs[0])).Head : "";
        var panel = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 10, LineSpacing = 10, VerticalAlignment = VerticalAlignment.Center };
        foreach (var column in inputs) panel.Children.Add(ItemCard(column, CubeTokens.Parse(Cell(column)), false, firstInput));
        if (inputs.Length == 0) panel.Children.Add(new TextBlock { Text = "No inputs", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = "➜", FontSize = 34, Foreground = RecipeBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0) });
        foreach (var column in outputs) panel.Children.Add(ItemCard(column, CubeTokens.Parse(Cell(column)), true, firstInput));
        if (outputs.Length == 0) panel.Children.Add(new TextBlock { Text = "No output", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center });
        cube.Content = panel;
        RequestPictures(inputs.Concat(outputs).Select(c => CubeTokens.Parse(Cell(c)).Head).Append(firstInput));
    }

    private Control ItemCard(string column, CubeTokens tokens, bool output, string firstInput)
    {
        var choice = catalog?.Find(tokens.Head);
        var pictured = tokens.Head is "useitem" or "usetype" ? firstInput : tokens.Head;
        var quality = tokens.Chosen(Qualities.Select(q => q.Key));
        IBrush brush = quality switch
        {
            "uni" => new SolidColorBrush(Color.Parse("#C7B377")), "set" => new SolidColorBrush(Color.Parse("#3DDB3D")), "rar" => new SolidColorBrush(Color.Parse("#E8E36B")),
            "mag" => MagicBrush, "crf" or "tmp" => new SolidColorBrush(Color.Parse("#E8A04C")), "low" => Muted, _ => choice?.Kind switch
            {
                "unique" => new SolidColorBrush(Color.Parse("#C7B377")), "set" => new SolidColorBrush(Color.Parse("#3DDB3D")), _ => GameTooltip.White
            }
        };
        var picture = new Grid { Width = 84, Height = 96 };
        if (pictures.GetValueOrDefault(pictured) is { } bitmap)
        {
            var image = new Image { Source = bitmap, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, MaxWidth = 80, MaxHeight = 92 };
            RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
            picture.Children.Add(image);
        }
        else picture.Children.Add(new TextBlock { Text = tokens.Head is "Cow Portal" or "Pandemonium Portal" or "Pandemonium Finale Portal" or "Red Portal" ? "🌀" : "?", FontSize = 28, Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        if (tokens.Quantity > 1) picture.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(220, 0, 0, 0)), CornerRadius = new(3), Padding = new(5, 1), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Child = new TextBlock { Text = "×" + tokens.Quantity, Foreground = GameTooltip.White, FontSize = 13, FontWeight = FontWeight.Bold } });
        var words = tokens.Keys.Where(k => !k.Equals("qty", StringComparison.OrdinalIgnoreCase))
            .Select(k => tokens.Value(k) is { } value ? $"{k}={value}" : (output ? OutputFlags : InputFlags).Concat(Qualities).Concat(output ? OutputTiers : InputTiers).Concat(Ethereal)
                .FirstOrDefault(o => o.Key.Equals(k, StringComparison.OrdinalIgnoreCase)).Label ?? k).ToArray();
        var name = tokens.Head switch { "useitem" => "The same item", "usetype" => "Same type", _ => choice?.Label ?? tokens.Head };
        var body = new StackPanel { Spacing = 3, Width = 130 };
        body.Children.Add(picture);
        body.Children.Add(new TextBlock { Text = name, Foreground = brush, FontSize = 12, FontFamily = TooltipFont, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
        if (words.Length > 0) body.Children.Add(new TextBlock { Text = string.Join(" · ", words), Foreground = Muted, FontSize = 10, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
        if (choice == null && tokens.Head.Length > 0) body.Children.Add(new TextBlock { Text = "not an item, type or name", Foreground = Brushes.Salmon, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center });
        var button = new Button { Content = body, Padding = new(6), Background = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), BorderBrush = output ? brush : new SolidColorBrush(Color.Parse("#4A4030")), BorderThickness = new(1) };
        ToolTip.SetTip(button, $"{column}: {tokens}\nClick to edit");
        AutomationProperties.SetName(button, "cube " + column);
        button.Click += (_, _) => FocusEditor(column);
        return button;
    }

    /// <summary>Loads the pictures of the named items on a worker; the cube is redrawn when they arrive.</summary>
    private void RequestPictures(IEnumerable<string> heads)
    {
        var project = host.Project(); if (project == null || catalog == null) return;
        var gameData = host.GameData(); var context = project.Root + "|" + string.Join(';', gameData);
        if (context != pictureContext) { pictures.Clear(); pictureContext = context; }
        var wanted = heads.Where(h => h.Length > 0 && !pictures.ContainsKey(h)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(h => (Head: h, Choice: catalog.Find(h))).ToArray();
        if (wanted.Length == 0) return;
        foreach (var (head, _) in wanted) pictures[head] = null;
        PendingPictures = LoadPicturesAsync(project, gameData, wanted);
    }

    private async Task LoadPicturesAsync(ModProject project, IReadOnlyList<string> gameData, (string Head, CubeChoice? Choice)[] wanted)
    {
        pictureCancellation?.Cancel();
        var work = pictureCancellation = new CancellationTokenSource(); var token = work.Token;
        try
        {
            var loaded = await Task.Run(() => wanted.Select(w => (w.Head, Sprite: w.Choice is { PictureCode.Length: > 0 } c
                ? ItemSprites.Resolve(project, gameData, c.PictureTable is "uniqueitems" or "setitems" ? c.PictureTable : "uniqueitems", c.PictureIndex, c.PictureCode, c.BaseTable, c.Tier, token) : null)).ToArray(), token);
            if (token.IsCancellationRequested) return;
            foreach (var (head, sprite) in loaded)
                pictures[head] = sprite?.Pixels is { } pixels ? Bitmaps.From(pixels.Width, pixels.Height, pixels.Rgba) : null;
            if (Table is { } table && SelectedRow >= 0) DrawCube(table, SelectedRow);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { foreach (var (head, _) in wanted) if (pictures.TryGetValue(head, out var b) && b == null) pictures.Remove(head); }
        finally { if (ReferenceEquals(pictureCancellation, work)) pictureCancellation = null; work.Dispose(); }
    }

    // ── Preview ────────────────────────────────────────────────────────────────────────────────────────

    protected override async Task PreviewAsync()
    {
        previewCancellation?.Cancel();
        var work = previewCancellation = new CancellationTokenSource(); var token = work.Token;
        var project = host.Project(); var record = SelectedRecord(); int row = SelectedRow;
        if (project == null || record == null) { work.Dispose(); if (ReferenceEquals(previewCancellation, work)) previewCancellation = null; return; }
        string profile = host.Profile(), language = Locale; int workspace = host.Workspace(), revision = Document.Revision;
        try
        {
            if (host.DirtyDependency(pane) is { } dirty) { warnings.Content = new TextBlock { Text = "Save the edited dependency to preview: " + dirty, Foreground = Brushes.Salmon, TextWrapping = TextWrapping.Wrap }; return; }
            if (previewContext != (project.Root, workspace)) { previewResolver.Clear(); previewContext = (project.Root, workspace); }
            var result = await previewWork.RunAsync(ct => previewResolver.Resolve(project, "cubemain", record, profile, language, ct), token);
            if (token.IsCancellationRequested || revision != Document.Revision || SelectedRow != row) return;
            LastPreview = result;
            warnings.Content = result.Issues.Length == 0 ? null : new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(40, 229, 83, 61)), BorderBrush = new SolidColorBrush(Color.Parse("#7A3A30")), BorderThickness = new(1), CornerRadius = new(4), Padding = new(12, 8),
                Child = new SelectableTextBlock { Text = "⚠ " + string.Join("\n⚠ ", result.Issues), Foreground = Brushes.Salmon, FontSize = 12, TextWrapping = TextWrapping.Wrap }
            };
            // Each output mod's line as the preview reads it, beside the mod's fields.
            var lines = result.Sections.Where(s => s.Title.StartsWith("Output", StringComparison.Ordinal)).SelectMany(s => s.Text).ToArray();
            foreach (var (mod, slot) in modLines)
            {
                // The preview indents mod lines as "  + <property>"; beside the fields only the property reads.
                static PreviewText Unindent(PreviewText line)
                {
                    int cut = line.Text.StartsWith("  + ", StringComparison.Ordinal) ? 4 : 0;
                    return line with { Text = line.Text[cut..], Links = [.. line.Links.Where(k => k.Start >= cut).Select(k => k with { Start = k.Start - cut })] };
                }
                var own = lines.Where(l => l.Links.Any(k => k.Targets.Any(t => t.Column == mod))).Select(Unindent).ToArray();
                slot.Content = own.Length > 0 ? new PreviewLinkText(own, MagicLink) { Foreground = MagicBrush, FontSize = 12, TextWrapping = TextWrapping.Wrap } : null;
            }
            details.Content = MainWindow.RecipeCard(result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) warnings.Content = new TextBlock { Text = ex.Message, Foreground = Brushes.Salmon, TextWrapping = TextWrapping.Wrap }; }
        finally { if (ReferenceEquals(previewCancellation, work)) previewCancellation = null; work.Dispose(); }
    }
}

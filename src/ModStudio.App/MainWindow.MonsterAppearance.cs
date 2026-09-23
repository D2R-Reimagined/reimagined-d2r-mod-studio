using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ModStudio.Core;
using static ModStudio.Core.Storage;

namespace ModStudio.App;

/// <summary>
/// The Monster Preview's HD appearance section: the unit JSON and variant file a monstats row resolves to, a picker over the
/// variant's colour entries (defaulting to the row's TransLvl), and the entry's transform values, editable and saved back
/// into the project's copy of the variant file.
/// </summary>
public partial class MainWindow
{
    private static readonly IBrush AppearanceHeading = Brushes.Tan, AppearanceBody = Brushes.LightSteelBlue, AppearanceMuted = new SolidColorBrush(Color.Parse("#A8A29A"));
    private static readonly string[] MaskChannels = ["R", "G", "B"];
    /// <summary>Unsaved transform edits by variant file and entry, so a preview refresh (any file change, another row) keeps them.</summary>
    private readonly Dictionary<string, VariantTransform[]> appearanceDrafts = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The entry picked per variant file and default entry, when it is not the one TransLvl names.</summary>
    private readonly Dictionary<string, string> appearanceChoices = new(StringComparer.OrdinalIgnoreCase);
    internal string? LastAppearanceSave { get; private set; }

    /// <summary>
    /// Where base-game HD files are read from: the chosen extracted data folder, and the data folder the remembered pal.pl2
    /// sits in. MODSTUDIO_GAME_DATA adds one for smoke runs, which do not read preferences.
    /// </summary>
    private static IReadOnlyList<string> GameDataFolders()
    {
        var folders = new List<string>();
        if (Environment.GetEnvironmentVariable("MODSTUDIO_GAME_DATA") is { Length: > 0 } fromEnvironment) folders.Add(fromEnvironment);
        if (!Program.Arguments.Contains("--smoke"))
            try
            {
                var prefs = StudioPreferences.Load(StudioPreferences.DefaultFile);
                if (prefs.GameDataFolder.Length > 0) folders.Add(prefs.GameDataFolder);
                // <data>/global/palette/ACT1/pal.pl2
                if (prefs.PalettePl2.Length > 0 && Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(prefs.PalettePl2)))) is { Length: > 0 } fromPalette) folders.Add(fromPalette);
            }
            catch (Exception) { }
        return [.. folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private async Task ChooseGameDataFolderAsync()
    {
        var folder = await PickFolderAsync("Choose your extracted game data (the folder holding hd/ and global/)");
        if (folder == null) return;
        Require(HdAppearance.Locate(folder, "data/hd/character/enemy/fallen1.json") != null || Directory.Exists(Path.Combine(folder, "hd")) || Directory.Exists(Path.Combine(folder, "data", "hd")),
            $"{folder} has no hd/ folder. Choose the extracted data folder that holds hd/ and global/.");
        if (!Program.Arguments.Contains("--smoke")) { var prefs = StudioPreferences.Load(StudioPreferences.DefaultFile); prefs.GameDataFolder = folder; prefs.Save(StudioPreferences.DefaultFile); }
        RefreshMonsterPreview();
    }

    private Control AppearanceSection(MonsterAppearance appearance)
    {
        var holder = new ContentControl();
        void Render() => holder.Content = AppearanceContent(appearance, Render);
        Render();
        return holder;
    }

    private Control AppearanceContent(MonsterAppearance appearance, Action render)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new SelectableTextBlock { Text = "HD APPEARANCE", Foreground = AppearanceHeading, FontSize = 11, Margin = new(0, 4, 0, 2) });
        TextBlock Line(string text, IBrush? brush = null) => new SelectableTextBlock { Text = text, Foreground = brush ?? AppearanceBody, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        Control FileLine(string label, HdFile file)
        {
            var row = new WrapPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new SelectableTextBlock { Text = $"{label}: {file.Relative} · {file.Origin}", Foreground = AppearanceBody, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
            if (file.InProject)
            {
                var open = new Button { Content = "Open", Padding = new(8, 1), MinHeight = 0, FontSize = 11, Margin = new(8, 0, 0, 0) };
                ToolTip.SetTip(open, file.Path);
                open.Click += async (_, _) => { try { await OpenDocumentAsync(file.Path); } catch (Exception ex) { ShowError(ex); } };
                row.Children.Add(open);
            }
            return row;
        }
        if (appearance.Unit != null) panel.Children.Add(FileLine("Unit", appearance.Unit));
        if (appearance.Variant != null) panel.Children.Add(FileLine("Variant", appearance.Variant));
        foreach (var note in appearance.Notes) panel.Children.Add(Line(note, AppearanceMuted));
        foreach (var issue in appearance.Issues) panel.Children.Add(Line(issue, Brushes.Salmon));
        if (appearance.Variant is { } variant && appearance.Entries.Length > 0) AddEntryEditor(panel, appearance, variant, render);
        var dataRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new(0, 4, 0, 0) };
        var folders = GameDataFolders();
        dataRow.Children.Add(new TextBlock { Text = folders.Count > 0 ? "Game data: " + string.Join(", ", folders) : "Game data: not chosen", Foreground = AppearanceMuted, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        var choose = new Button { Content = folders.Count > 0 ? "Change…" : "Choose game data folder…", Padding = new(8, 1), MinHeight = 0, FontSize = 11, Margin = new(8, 0, 0, 0) };
        ToolTip.SetTip(choose, "Your extracted D2R data (for example with CascView): the folder holding hd/ and global/. Base-game unit and variant files are read from it; nothing is copied until you save an edit.");
        choose.Click += async (_, _) => { try { await ChooseGameDataFolderAsync(); } catch (Exception ex) { ShowError(ex); } };
        dataRow.Children.Add(choose);
        panel.Children.Add(dataRow);
        return panel;
    }

    private void AddEntryEditor(StackPanel panel, MonsterAppearance appearance, HdFile variant, Action render)
    {
        string DraftKey(VariantEntry entry) => variant.Path + "|" + (entry.Custom ? "custom:" : "") + entry.Name;
        var choiceKey = variant.Relative + "|" + appearance.DefaultEntry;
        var entry = appearance.Entries.FirstOrDefault(e => appearanceChoices.TryGetValue(choiceKey, out var chosen) && e.Label == chosen)
            ?? appearance.Entries.FirstOrDefault(e => !e.Custom && e.Name == appearance.DefaultEntry) ?? appearance.Entries[0];

        var usage = $"{appearance.FamilyRows} monstats row{(appearance.FamilyRows == 1 ? "" : "s")} use BaseId {appearance.BaseId}";
        panel.Children.Add(new SelectableTextBlock { Text = usage + (appearance.DefaultEntry != null ? $"; TransLvl {appearance.TransLvl} picks {appearance.DefaultEntry}." : "."), Foreground = AppearanceMuted, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        if (appearance.SharedWith.Length > 0)
            panel.Children.Add(new SelectableTextBlock { Text = $"This variant file is also loaded by {string.Join(", ", appearance.SharedWith)}. Saving an edit recolours them too.", Foreground = Brushes.Salmon, FontSize = 12, TextWrapping = TextWrapping.Wrap });

        var picker = new ComboBox { ItemsSource = appearance.Entries.Select(e => e.Label + (appearanceDrafts.ContainsKey(DraftKey(e)) ? " •" : "") + (e.Name == appearance.DefaultEntry && !e.Custom ? " · TransLvl" : "")).ToArray(), MinWidth = 200, FontSize = 12 };
        picker.SelectedIndex = Array.IndexOf(appearance.Entries, entry);
        ToolTip.SetTip(picker, "The variant entry to show and edit. level0–level4 follow TransLvl; cold and poison are the chilled and poisoned tints; custom entries are colour shifts a state selects by index. • marks unsaved edits.");
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedIndex < 0 || appearance.Entries[picker.SelectedIndex] == entry) return;
            appearanceChoices[choiceKey] = appearance.Entries[picker.SelectedIndex].Label; render();
        };
        var pickerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new(0, 4, 0, 0) };
        pickerRow.Children.Add(new TextBlock { Text = "Entry", Foreground = AppearanceHeading, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        pickerRow.Children.Add(picker);
        panel.Children.Add(pickerRow);

        var draftKey = DraftKey(entry);
        var draft = appearanceDrafts.TryGetValue(draftKey, out var saved) ? saved.Select(t => t with { Values = [.. t.Values] }).ToArray() : entry.Transforms.Select(t => t with { Values = [.. t.Values] }).ToArray();
        var invalid = new HashSet<(int, int)>();
        var save = new Button { Content = "Save to mod", Padding = new(10, 3), MinHeight = 0 };
        var revert = new Button { Content = "Revert", Padding = new(10, 3), MinHeight = 0 };
        var status = new TextBlock { Foreground = AppearanceMuted, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        bool Changed() => draft.Where((t, i) => !t.SameValues(entry.Transforms[i])).Any();
        void Update()
        {
            bool changed = Changed();
            if (changed) appearanceDrafts[draftKey] = draft.Select(t => t with { Values = [.. t.Values] }).ToArray(); else appearanceDrafts.Remove(draftKey);
            save.IsEnabled = changed && invalid.Count == 0; revert.IsEnabled = changed;
            status.Text = invalid.Count > 0 ? "Fix the highlighted values before saving." : !changed ? "" : variant.InProject ? "Unsaved changes." : $"Unsaved changes. Saving copies the base-game file to {variant.Relative} in the project first.";
        }

        for (int t = 0; t < draft.Length; t++) panel.Children.Add(TransformGrid(t, draft[t], invalid, Update));

        save.Click += async (_, _) =>
        {
            try
            {
                Require(project != null, "Open a project first.");
                var target = variant.InProject ? variant.Path : Inside(project!.Root, variant.Relative);
                var open = tabs.Select(t => t.Content).OfType<EditorPane>().FirstOrDefault(p => string.Equals(p.Document.FilePath, target, StringComparison.OrdinalIgnoreCase));
                Require(open is not { Document.IsDirty: true }, $"Save or discard the open edits to {Path.GetFileName(target)} first.");
                var selectedProject = project!; var values = draft.Select(t => t with { Values = [.. t.Values] }).ToArray();
                save.IsEnabled = false; status.Text = "Saving…";
                var written = await Task.Run(() => HdAppearance.SaveEntry(selectedProject, variant, entry.Name, entry.Custom, values));
                appearanceDrafts.Remove(draftKey); LastAppearanceSave = written;
                Status.Text = $"Saved {entry.Name} to {Relative(selectedProject.Root, written)}" + (variant.InProject ? "" : " (copied from the game data)");
                RefreshMonsterPreview();
            }
            catch (Exception ex) { status.Text = ex.Message; ShowError(ex); Update(); }
        };
        revert.Click += (_, _) => { appearanceDrafts.Remove(draftKey); render(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new(0, 6, 0, 0) }; buttons.Children.Add(save); buttons.Children.Add(revert);
        panel.Children.Add(buttons); panel.Children.Add(status);
        panel.Children.Add(new SelectableTextBlock { Foreground = AppearanceMuted, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Text = "Each entry holds three transforms, one per channel of the unit's KTINT mask texture. Blizzard does not document what each vector component does; colorTint reads as RGBA (the swatch), and vanilla leaves colorAdjustment at 0, 0, 0, 1 when an entry changes nothing. Scroll over a value to nudge it by 0.01 (Shift: 0.1). Check the result in game." });
        Update();
    }

    private static Control TransformGrid(int index, VariantTransform transform, HashSet<(int, int)> invalid, Action changed)
    {
        var grid = new Grid { ColumnDefinitions = new("Auto,Auto,Auto,Auto,Auto,Auto") };
        grid.RowDefinitions = new(string.Join(',', Enumerable.Repeat("Auto", HdAppearance.Fields.Length + 2)));
        var title = new TextBlock { Text = $"Mask {(index < MaskChannels.Length ? MaskChannels[index] : (index + 1).ToString(CultureInfo.InvariantCulture))} · {transform.Name}", Foreground = AppearanceHeading, FontSize = 12, FontWeight = FontWeight.SemiBold, Margin = new(0, 0, 0, 2) };
        Grid.SetColumnSpan(title, 6); grid.Children.Add(title);
        for (int c = 0; c < 4; c++)
        {
            var header = new TextBlock { Text = HdAppearance.Components[c], Foreground = AppearanceMuted, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center };
            Grid.SetRow(header, 1); Grid.SetColumn(header, c + 1); grid.Children.Add(header);
        }
        var swatch = new Border { Width = 28, Height = 18, CornerRadius = new(3), BorderBrush = AppearanceMuted, BorderThickness = new(1), Margin = new(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        void PaintSwatch()
        {
            int tint = Array.IndexOf(HdAppearance.Fields, "colorTint");
            byte Channel(int c) => (byte)Math.Round(Math.Clamp(transform.Get(tint, c), 0, 1) * 255);
            swatch.Background = new SolidColorBrush(Color.FromArgb(Channel(3), Channel(0), Channel(1), Channel(2)));
            ToolTip.SetTip(swatch, $"colorTint as RGBA: {Channel(0)}, {Channel(1)}, {Channel(2)}, alpha {Channel(3)}");
        }
        for (int f = 0; f < HdAppearance.Fields.Length; f++)
        {
            var label = new TextBlock { Text = HdAppearance.Fields[f], Foreground = AppearanceBody, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 8, 0) };
            Grid.SetRow(label, f + 2); grid.Children.Add(label);
            for (int c = 0; c < 4; c++)
            {
                int field = f, component = c;
                var box = new TextBox { Text = Show(transform.Get(f, c)), Width = 58, MinWidth = 0, FontSize = 11, Padding = new(4, 1), MinHeight = 0, Margin = new(1) };
                ToolTip.SetTip(box, $"{transform.Name}.{HdAppearance.Fields[f]}.{HdAppearance.Components[c]}");
                box.TextChanged += (_, _) =>
                {
                    bool ok = double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value);
                    if (ok) { transform.Values[field * 4 + component] = value; invalid.Remove((field, component)); } else invalid.Add((field, component));
                    box.BorderBrush = ok ? null : Brushes.Salmon;
                    PaintSwatch(); changed();
                };
                box.PointerWheelChanged += (_, e) =>
                {
                    if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return;
                    var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.1 : 0.01;
                    box.Text = Show(Math.Round(value + Math.Sign(e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X) * step, 4));
                    e.Handled = true;
                };
                Grid.SetRow(box, f + 2); Grid.SetColumn(box, c + 1); grid.Children.Add(box);
            }
            if (HdAppearance.Fields[f] == "colorTint") { Grid.SetRow(swatch, f + 2); Grid.SetColumn(swatch, 5); grid.Children.Add(swatch); }
        }
        PaintSwatch();
        // Like the level tables, only the grid scrolls sideways in a narrow inspector; the strip below keeps the floating scrollbar off the last row.
        grid.Margin = new(0, 6, 0, 18);
        return new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, HorizontalAlignment = HorizontalAlignment.Left };
        static string Show(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    }
}

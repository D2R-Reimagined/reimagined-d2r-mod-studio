using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ModStudio.Core;
using static ModStudio.Core.Storage;

namespace ModStudio.App;

/// <summary>
/// Creates a source table the project does not have yet. Either import a native .txt (vanilla rows are kept and protected,
/// exactly like project import) or start empty with the columns the d2rdoc guide documents for that file.
/// </summary>
public sealed class NewTableWindow : Window
{
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#D8BC86")), Muted = new SolidColorBrush(Color.Parse("#B9AD97")), Warn = new SolidColorBrush(Color.Parse("#E39A6B"));
    private readonly ModProject project;
    private readonly TextBox filter = new() { PlaceholderText = "Filter tables…" }, file = new() { PlaceholderText = "Path to a .txt exported from the game or another mod" }, name = new();
    private readonly ListBox tables = new() { Height = 220 };
    private readonly RadioButton fromFile = new() { Content = "Import a .txt file (keeps the original rows, marked as protected)", IsChecked = true, GroupName = "src" }, fromGuide = new() { Content = "Start empty with the columns documented in the data guide", GroupName = "src" };
    private readonly TextBlock overview = new() { TextWrapping = TextWrapping.Wrap, Foreground = Muted, FontSize = 12 }, summary = new() { TextWrapping = TextWrapping.Wrap }, error = new() { TextWrapping = TextWrapping.Wrap, Foreground = Warn, IsVisible = false };
    private readonly Button create = new() { Content = "Create table", Classes = { "accent" } };
    private readonly IReadOnlyList<ColumnGuideFile> guide = ColumnGuide.Files;
    private readonly HashSet<string> existing;
    private bool nameEdited, updating;

    public NewTableWindow(ModProject project)
    {
        this.project = project;
        existing = Directory.Exists(Path.Combine(project.Root, "source/tables")) ? Directory.GetDirectories(Path.Combine(project.Root, "source/tables")).Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
        Title = "New table"; Width = 720; SizeToContent = SizeToContent.Height; CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new(26), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "New table", FontSize = 24, Foreground = Accent });
        panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Muted, Text = "Pick the game file this table represents. Tables the project already has are listed but disabled. Studio builds the native global/excel file from the JSON records you edit." });
        panel.Children.Add(filter); panel.Children.Add(tables); panel.Children.Add(overview);
        panel.Children.Add(fromFile);
        var fileRow = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(24, 0, 0, 0) }; fileRow.Children.Add(file);
        var browse = new Button { Content = "Browse…", Margin = new(6, 0, 0, 0) }; Grid.SetColumn(browse, 1); fileRow.Children.Add(browse); panel.Children.Add(fileRow);
        panel.Children.Add(fromGuide);
        var nameRow = new Grid { ColumnDefinitions = new("150,*") }; nameRow.Children.Add(new TextBlock { Text = "Table name", VerticalAlignment = VerticalAlignment.Center }); Grid.SetColumn(name, 1); nameRow.Children.Add(name); panel.Children.Add(nameRow);
        panel.Children.Add(new Border { Padding = new(14), Background = new SolidColorBrush(Color.Parse("#292727")), Child = summary }); panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => Close(null); buttons.Children.Add(cancel); buttons.Children.Add(create); panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

        tables.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<ColumnGuideFile>((entry, _) =>
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock { Text = entry.Title, Width = 200 });
            row.Children.Add(new TextBlock { Text = existing.Contains(entry.Key) ? "already in project" : $"{entry.Fields.Length} documented fields", Foreground = Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            row.IsEnabled = !existing.Contains(entry.Key);
            return row;
        });
        Populate();
        filter.TextChanged += (_, _) => Populate();
        tables.SelectionChanged += (_, _) => { if (!updating) { nameEdited = false; Refresh(); } };
        file.TextChanged += (_, _) => { if (updating) return; var stem = TableData.NameFor(file.Text ?? ""); var match = guide.FirstOrDefault(g => string.Equals(g.Key, stem, StringComparison.OrdinalIgnoreCase)); if (match != null) { updating = true; tables.SelectedItem = match; updating = false; } nameEdited = false; Refresh(); };
        browse.Click += async (_, _) =>
        {
            var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Select a game table (.txt)", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("Game tables") { Patterns = ["*.txt"] }] });
            var path = picked.FirstOrDefault()?.TryGetLocalPath(); if (path != null) { fromFile.IsChecked = true; file.Text = path; }
        };
        name.TextChanged += (_, _) => { if (!updating) { nameEdited = true; Refresh(); } };
        fromFile.IsCheckedChanged += (_, _) => Refresh(); fromGuide.IsCheckedChanged += (_, _) => Refresh();
        create.Click += (_, _) => { var table = Build(); if (table != null) Close(table); };
        Opened += (_, _) => filter.Focus();
        Refresh();
    }

    private ColumnGuideFile? Selected => tables.SelectedItem as ColumnGuideFile;
    private void Populate()
    {
        var query = (filter.Text ?? "").Trim(); var keep = Selected;
        updating = true;
        tables.ItemsSource = guide.Where(g => query.Length == 0 || g.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || g.Overview.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (keep != null && ((IEnumerable<ColumnGuideFile>)tables.ItemsSource!).Contains(keep)) tables.SelectedItem = keep;
        updating = false;
    }
    private void Refresh()
    {
        updating = true;
        var selected = Selected; var importing = fromFile.IsChecked == true; var path = (file.Text ?? "").Trim();
        file.IsEnabled = importing;
        overview.Text = selected == null ? "Select a table, or choose a .txt file and Studio matches it to the guide when it can." : selected.Overview.Length > 0 ? selected.Overview : selected.Title;
        if (!nameEdited) name.Text = selected != null ? TableData.NameFor(selected.Key) : importing && path.Length > 0 ? TableData.NameFor(path) : "";
        var tableName = name.Text ?? "";
        summary.Text = importing
            ? path.Length == 0 ? "Choose the .txt to import. Its header row becomes the columns and every row is kept as protected game data." : $"Import {Path.GetFileName(path)} as \"{tableName}\", written to {(selected?.Target ?? "global/excel/" + Path.GetFileName(path))} on build."
            : selected == null ? "Select a table to start from its documented columns." : $"Create an empty \"{tableName}\" with {selected.Headers().Length} columns from the guide, written to {selected.Target} on build. Check the columns against a vanilla file before shipping: the guide documents fields, not the exact header order.";
        error.IsVisible = false; create.IsEnabled = importing ? path.Length > 0 : selected != null;
        updating = false;
    }
    private TableData? Build()
    {
        try
        {
            var tableName = (name.Text ?? "").Trim(); Require(tableName.Length > 0, "Enter a table name.");
            Require(!existing.Contains(tableName), $"The project already has a table named {tableName}. Open it from the tree instead.");
            var selected = Selected;
            if (fromFile.IsChecked == true)
            {
                var path = Path.GetFullPath((file.Text ?? "").Trim()); Require(File.Exists(path), "Choose an existing .txt file.");
                var bytes = File.ReadAllBytes(path); var target = selected?.Target ?? "global/excel/" + Path.GetFileName(path);
                var table = TableData.FromTsv(bytes, tableName, target);
                Require(table.EncodeTsv().SequenceEqual(bytes), "This file would not round-trip losslessly (mixed line endings or an unusual encoding). Normalize it first.");
                return table;
            }
            Require(selected != null, "Select a table from the guide.");
            return TableData.FromGuide(selected!, tableName);
        }
        catch (Exception ex) { error.Text = ex.Message; error.IsVisible = true; return null; }
    }
}

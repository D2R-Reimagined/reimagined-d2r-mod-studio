using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ModStudio.Core;

namespace ModStudio.App;

/// <summary>
/// Adds, renames, deletes and configures the project's profiles (compatibility/&lt;id&gt;/). Closes with the profile that
/// should be selected afterwards, or null when nothing changed.
/// </summary>
public sealed class ProfileManagerWindow : Window
{
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#D8BC86")), Muted = new SolidColorBrush(Color.Parse("#B9AD97")), Warn = new SolidColorBrush(Color.Parse("#E39A6B"));
    private const string Inherit = "(inherit)", None = "(none)";
    private readonly ModProject project;
    private readonly Func<string, string?> blocker;
    private readonly ListBox list = new() { Width = 230, MinHeight = 320 };
    private readonly TextBox name = new(), modName = new(), savePath = new();
    private readonly ComboBox basedOn = new() { HorizontalAlignment = HorizontalAlignment.Stretch }, runtime = new() { HorizontalAlignment = HorizontalAlignment.Stretch }, strings = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly WrapPanel colors = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 }, error = new() { TextWrapping = TextWrapping.Wrap, Foreground = Warn, IsVisible = false };
    private readonly Button rename = new() { Content = "Rename" }, save = new() { Content = "Save settings", Classes = { "accent" } }, delete = new() { Content = "Delete…" }, create = new() { Content = "New profile…" };
    private string? color;
    private string? selectAfter;
    private bool changed, loading;

    /// <param name="blocker">Why a profile's folder cannot be moved or removed right now (open or unsaved files in it), or null.</param>
    public ProfileManagerWindow(ModProject project, string current, Func<string, string?> blocker)
    {
        this.project = project; this.blocker = blocker; selectAfter = current;
        Title = "Profiles"; Width = 860; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new(26), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "Profiles", FontSize = 24, Foreground = Accent });
        panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Muted, Text = "Every profile builds the shared source with its own overrides (compatibility/<name>/). A profile based on another builds everything its parent does, then applies its own changes on top; settings it leaves at (inherit) come from the parent." });
        var body = new Grid { ColumnDefinitions = new("Auto,24,*") };
        var left = new DockPanel();
        var leftButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new(0, 8, 0, 0) }; leftButtons.Children.Add(create); leftButtons.Children.Add(delete);
        DockPanel.SetDock(leftButtons, Dock.Bottom); left.Children.Add(leftButtons); left.Children.Add(list);
        body.Children.Add(left);
        var form = new StackPanel { Spacing = 10 }; Grid.SetColumn(form, 2); body.Children.Add(form);
        Grid Row(string label, Control control, Control? extra = null)
        {
            var row = new Grid { ColumnDefinitions = new("140,*,Auto") };
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(control, 1); row.Children.Add(control);
            if (extra != null) { extra.Margin = new(6, 0, 0, 0); Grid.SetColumn(extra, 2); row.Children.Add(extra); }
            return row;
        }
        form.Children.Add(Row("Name", name, rename));
        form.Children.Add(Row("Based on", basedOn));
        form.Children.Add(Row("Runtime", runtime));
        form.Children.Add(Row("Strings", strings));
        form.Children.Add(Row("Mod folder name", modName));
        form.Children.Add(Row("Save path", savePath));
        form.Children.Add(Row("Color", colors));
        form.Children.Add(new Border { Padding = new(14), Background = new SolidColorBrush(Color.Parse("#292727")), Child = summary });
        form.Children.Add(error);
        var saveRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; saveRow.Children.Add(save); form.Children.Add(saveRow);
        panel.Children.Add(body);
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right }; close.Click += (_, _) => Close(changed ? selectAfter : null); panel.Children.Add(close);
        Content = panel;
        modName.PlaceholderText = "inherited"; savePath.PlaceholderText = "inherited (e.g. MyModLadder/)";
        runtime.ItemsSource = new[] { Inherit, ProfileChain.LoaderRuntime, ProfileChain.PlainRuntime };
        strings.ItemsSource = new[] { Inherit, "full", "standard" };
        ToolTip.SetTip(runtime, "loader: built for D2RLoader; the profile may ship d2rloader/ assets. plain: the game alone.");
        ToolTip.SetTip(strings, "full: every translation as written. standard: the compact translations plain D2R's string size limit needs.");
        list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((id, _) =>
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            string? parent = null, configured = null;
            try { var settings = ProfileManager.Load(project, id); parent = settings.BasedOn; configured = settings.Color; } catch (Exception) { }
            row.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new(3), Background = ProfileColors.Brush(ProfileColors.For(id, configured)), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = id, VerticalAlignment = VerticalAlignment.Center });
            if (parent != null) row.Children.Add(new TextBlock { Text = "← " + parent, Foreground = Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            return row;
        });
        list.SelectionChanged += (_, _) => LoadSelected();
        rename.Click += (_, _) => Try(Rename);
        save.Click += (_, _) => Try(SaveSettings);
        delete.Click += async (_, _) => await DeleteAsync();
        create.Click += async (_, _) => await CreateAsync();
        Populate(current);
    }

    private string? Selected => list.SelectedItem as string;
    private void Populate(string? select)
    {
        var profiles = project.Profiles.ToArray(); list.ItemsSource = profiles;
        list.SelectedItem = select != null && profiles.Contains(select) ? select : profiles.FirstOrDefault();
    }
    private void Try(Action action)
    {
        error.IsVisible = false; error.Foreground = Warn;
        try { action(); }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException) { error.Text = e.Message; error.IsVisible = true; }
    }
    /// <summary>Profiles that may be a parent of <paramref name="id"/>: not itself and none of the profiles based on it.</summary>
    private IEnumerable<string> Parents(string id)
    {
        var descendants = new HashSet<string> { id };
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var p in project.Profiles)
                try { if (!descendants.Contains(p) && ProfileManager.Load(project, p).BasedOn is { } parent && descendants.Contains(parent)) grew = descendants.Add(p); } catch (Exception) { }
        }
        return project.Profiles.Where(p => !descendants.Contains(p));
    }
    private void LoadSelected()
    {
        error.IsVisible = false;
        if (Selected is not { } id) { summary.Text = ""; return; }
        loading = true;
        try
        {
            var settings = ProfileManager.Load(project, id);
            name.Text = id;
            basedOn.ItemsSource = Parents(id).Prepend(None).ToArray(); basedOn.SelectedItem = settings.BasedOn ?? None;
            runtime.SelectedItem = settings.Runtime ?? Inherit; strings.SelectedItem = settings.StringMode ?? Inherit;
            modName.Text = settings.ModName ?? ""; savePath.Text = settings.SavePath ?? "";
            modName.PlaceholderText = settings.BasedOn == null ? "from modinfo.json" : "inherited from " + settings.BasedOn;
            savePath.PlaceholderText = settings.BasedOn == null ? "from modinfo.json (e.g. MyModLadder/)" : "inherited from " + settings.BasedOn;
            color = settings.Color; BuildSwatches(id);
            Describe(id);
        }
        catch (Exception e) { summary.Text = ""; error.Text = e.Message; error.IsVisible = true; }
        finally { loading = false; }
    }
    private void BuildSwatches(string id)
    {
        colors.Children.Clear();
        void Swatch(string? value, string tip)
        {
            var shown = ProfileColors.For(id, value);
            var button = new Button
            {
                Width = 24, Height = 24, MinWidth = 0, Padding = new(0), Margin = new(0, 0, 6, 0), Background = ProfileColors.Brush(shown),
                BorderThickness = new(color == value ? 2 : 0), BorderBrush = Brushes.White, Content = value == null ? new TextBlock { Text = "A", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Brushes.Black } : null,
            };
            ToolTip.SetTip(button, tip);
            button.Click += (_, _) => { color = value; BuildSwatches(id); };
            colors.Children.Add(button);
        }
        Swatch(null, "Automatic: picked from the profile's name");
        foreach (var value in ProfileColors.Palette) Swatch(value, value);
    }
    private void Describe(string id)
    {
        var lines = new List<string>();
        try
        {
            var chain = ProfileChain.Load(project.Root, id);
            lines.Add(chain.Layers.Count == 1 ? "Builds the shared source with its own overrides." : "Builds " + string.Join(" → ", chain.Layers.Select(l => l.Id)) + ", each applying its overrides to the one before.");
            lines.Add($"Runtime: {(chain.Runtime == ProfileChain.LoaderRuntime ? "D2RLoader (may ship d2rloader/ assets)" : "plain D2R")} · strings: {chain.StringMode}" + (chain.ModInfo is { } info ? $" · {string.Join(", ", info.Select(p => $"{p.Key} {p.Value}"))}" : ""));
            var own = chain.Own;
            lines.Add($"{own.RuleFiles.Count} override file(s) of its own with {own.Rules.Length} rule(s)" + (own.RuleFiles.Any(f => f.Generated) ? $", {own.RuleFiles.Count(f => f.Generated)} generated by scripts" : "") + $"; {((System.Text.Json.Nodes.JsonArray?)own.Settings["assetOverrides"])?.Count ?? 0} asset override(s).");
            var children = ProfileManager.Children(project, id);
            if (children.Count > 0) lines.Add("Profiles based on it: " + string.Join(", ", children) + ".");
            summary.Foreground = Brushes.White;
        }
        catch (Exception e) { lines.Add("This profile does not load: " + e.Message); summary.Foreground = Warn; }
        summary.Text = string.Join("\n", lines);
    }
    private static string? Choice(ComboBox box, string unset) => box.SelectedItem is string s && s != unset ? s : null;
    private void SaveSettings()
    {
        if (Selected is not { } id || loading) return;
        ProfileManager.Update(project, id, new(Choice(basedOn, None), Choice(strings, Inherit), Choice(runtime, Inherit), modName.Text, savePath.Text, color));
        changed = true; Populate(id); Describe(id);
    }
    private void Rename()
    {
        if (Selected is not { } id) return;
        var next = (name.Text ?? "").Trim(); if (next == id) return;
        if (blocker(id) is { } reason) throw new InvalidDataException(reason);
        ProfileManager.Rename(project, id, next);
        if (selectAfter == id) selectAfter = next;
        changed = true; Populate(next);
    }
    private async Task DeleteAsync()
    {
        if (Selected is not { } id) return;
        error.IsVisible = false;
        if (blocker(id) is { } reason) { error.Text = reason; error.IsVisible = true; return; }
        var children = ProfileManager.Children(project, id);
        if (children.Count > 0) { error.Text = $"{string.Join(", ", children)} {(children.Count == 1 ? "is" : "are")} based on {id}. Change {(children.Count == 1 ? "its" : "their")} parent first."; error.IsVisible = true; return; }
        if (!await Confirm($"Delete profile {id}?", $"Its folder (compatibility/{id}/) and its run settings move to .studio/deleted-profiles/, so you can put them back. Scripts that build {id} by name will stop working.", "Delete profile")) return;
        Try(() =>
        {
            var moved = ProfileManager.Delete(project, id);
            if (selectAfter == id) selectAfter = project.Profiles.FirstOrDefault();
            changed = true; Populate(selectAfter); error.Text = "Moved to " + moved; error.Foreground = Muted; error.IsVisible = true;
        });
    }
    private async Task<bool> Confirm(string title, string text, string action)
    {
        var dialog = new Window { Title = title, Width = 480, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new(20), Spacing = 14 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 18, Foreground = Accent });
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => dialog.Close(false);
        var ok = new Button { Content = action, Classes = { "accent" } }; ok.Click += (_, _) => dialog.Close(true);
        buttons.Children.Add(cancel); buttons.Children.Add(ok); panel.Children.Add(buttons);
        dialog.Content = panel;
        return await dialog.ShowDialog<bool>(this);
    }
    private async Task CreateAsync()
    {
        var dialog = new Window { Title = "New profile", Width = 520, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new(20), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "New profile", FontSize = 18, Foreground = Accent });
        var id = new TextBox { PlaceholderText = "ladder" };
        var profiles = project.Profiles.ToArray();
        var based = new RadioButton { Content = "Based on (inherits its overrides and settings)", GroupName = "start", IsChecked = true };
        var copy = new RadioButton { Content = "Copy of (a one-time copy of its files)", GroupName = "start" };
        var empty = new RadioButton { Content = "Empty (the shared source as is; plain runtime, full strings)", GroupName = "start" };
        var parent = new ComboBox { ItemsSource = profiles, SelectedItem = Selected ?? profiles.FirstOrDefault(), HorizontalAlignment = HorizontalAlignment.Stretch };
        var source = new ComboBox { ItemsSource = profiles, SelectedItem = Selected ?? profiles.FirstOrDefault(), HorizontalAlignment = HorizontalAlignment.Stretch };
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Warn };
        panel.Children.Add(new TextBlock { Text = "Name (lowercase letters, digits, - or _)", Foreground = Muted }); panel.Children.Add(id);
        panel.Children.Add(based); panel.Children.Add(parent); panel.Children.Add(copy); panel.Children.Add(source); panel.Children.Add(empty); panel.Children.Add(message);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => dialog.Close(null);
        var ok = new Button { Content = "Create", Classes = { "accent" } };
        ok.Click += (_, _) =>
        {
            try
            {
                var newId = (id.Text ?? "").Trim();
                ProfileManager.Create(project, newId, based.IsChecked == true ? parent.SelectedItem as string : null, copy.IsChecked == true ? source.SelectedItem as string : null);
                dialog.Close(newId);
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { message.Text = e.Message; }
        };
        buttons.Children.Add(cancel); buttons.Children.Add(ok); panel.Children.Add(buttons);
        dialog.Content = panel; dialog.Opened += (_, _) => id.Focus();
        var created = await dialog.ShowDialog<string?>(this);
        if (created == null) return;
        changed = true; Populate(created);
    }
}

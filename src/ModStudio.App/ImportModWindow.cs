using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ModStudio.Core;
using static ModStudio.Core.Storage;

namespace ModStudio.App;

/// <summary>Everything the user decided in the Import mod window. Backup is set only for in-place conversion; game paths only when an installation was detected or entered.</summary>
public sealed record ImportPlan(LegacyProject Source, string Name, string Destination, string? Backup, string GameDirectory, string DeploymentDirectory);

/// <summary>
/// One window for importing any existing mod: source, name, mode, project location and deployment target together,
/// pre-filled from detection so the common case is "pick the mod, press Import". Replaces the earlier chain of
/// mode / name / parent-folder / review dialogs, and makes the project-vs-deployment split visible up front.
/// </summary>
public sealed class ImportModWindow : Window
{
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#D8BC86")), Muted = new SolidColorBrush(Color.Parse("#B9AD97")), Warn = new SolidColorBrush(Color.Parse("#E39A6B"));
    private readonly TextBox source = new() { PlaceholderText = "Mod folder, unpacked .mpq, data folder or legacy project" }, name = new(), destination = new(), deployment = new();
    private readonly TextBlock sourceStatus = Note("Choose the mod you want to edit. Studio finds the native data inside it."), destinationStatus = Note(), deploymentStatus = Note(), summary = new() { TextWrapping = TextWrapping.Wrap }, error = new() { TextWrapping = TextWrapping.Wrap, Foreground = Warn, IsVisible = false };
    private readonly ComboBox candidates = new() { HorizontalAlignment = HorizontalAlignment.Stretch, IsVisible = false };
    private readonly RadioButton convert = new() { Content = "Convert this folder in place, keeping a timestamped backup beside it (recommended)", IsChecked = true, GroupName = "mode" }, copy = new() { Content = "Create a new project in a separate folder", GroupName = "mode" };
    private readonly Button import = new() { Content = "Import", Classes = { "accent" } };
    private readonly Button browseDestination;
    private readonly DispatcherTimer detectTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly string projectsFolder;
    private IReadOnlyList<LegacyProject> detected = [];
    private IReadOnlyList<GameInstallation> installations = [];
    private bool destinationEdited, deploymentEdited, updating;
    private int detectVersion;

    public ImportModWindow(string projectsFolder, string? initialSource = null)
    {
        this.projectsFolder = projectsFolder;
        Title = "Import mod"; Width = 760; SizeToContent = SizeToContent.Height; MinWidth = 640; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new(26), Spacing = 14 };
        panel.Children.Add(new TextBlock { Text = "Import an existing mod", FontSize = 24, Foreground = Accent });
        panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Muted, Text = "Studio turns the mod's native files into an editable JSON-source project. You edit the project; Build, Deploy and Play write the native files the game needs into the game's mods folder. Those are two different places." });

        panel.Children.Add(Row("Original mod", source, Browse("Select the mod to import", path => source.Text = path)));
        panel.Children.Add(candidates); panel.Children.Add(sourceStatus);
        panel.Children.Add(Row("Mod name", name, null));
        var modes = new StackPanel { Spacing = 4 }; modes.Children.Add(convert); modes.Children.Add(copy); panel.Children.Add(modes);
        browseDestination = Browse("Choose where the project should live", path => { destinationEdited = true; destination.Text = ChooseInside(path, name.Text ?? ""); });
        panel.Children.Add(Row("Project folder\n(where you edit)", destination, browseDestination));
        panel.Children.Add(destinationStatus);
        panel.Children.Add(Row("Game mods folder\n(where Studio deploys)", deployment, Browse("Choose the game's mods/<mod-name> folder", path => { deploymentEdited = true; deployment.Text = path; })));
        panel.Children.Add(deploymentStatus);
        panel.Children.Add(new Border { Padding = new(14), Background = new SolidColorBrush(Color.Parse("#292727")), Child = summary });
        panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => Close(null); buttons.Children.Add(cancel); buttons.Children.Add(import); panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

        detectTimer.Tick += async (_, _) => { detectTimer.Stop(); await DetectAsync(); };
        source.TextChanged += (_, _) => { if (!updating) { detectTimer.Stop(); detectTimer.Start(); } };
        candidates.SelectionChanged += (_, _) => { if (!updating && candidates.SelectedItem is LegacyProject picked) ApplyCandidate(picked); };
        name.TextChanged += (_, _) => { if (!updating) Refresh(); };
        destination.TextChanged += (_, _) => { if (!updating) { destinationEdited = true; Refresh(); } };
        deployment.TextChanged += (_, _) => { if (!updating) { deploymentEdited = true; Refresh(); } };
        copy.IsCheckedChanged += (_, _) => Refresh(); convert.IsCheckedChanged += (_, _) => Refresh();
        import.Click += (_, _) => { var plan = Validate(); if (plan != null) Close(plan); };
        Opened += async (_, _) =>
        {
            try { installations = await Task.Run(() => GameInstallDetector.Detect()); } catch { installations = []; }
            Refresh();
            if (initialSource != null) { source.Text = initialSource; detectTimer.Stop(); await DetectAsync(); }
            else source.Focus();
        };
        Refresh();
    }

    private static TextBlock Note(string text = "") => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Muted, FontSize = 12, Margin = new(150, -8, 0, 0) };
    private static Grid Row(string label, TextBox box, Button? browse)
    {
        var row = new Grid { ColumnDefinitions = new("150,*,Auto") };
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 12, 0) });
        Grid.SetColumn(box, 1); row.Children.Add(box);
        if (browse != null) { Grid.SetColumn(browse, 2); browse.Margin = new(6, 0, 0, 0); row.Children.Add(browse); }
        return row;
    }
    private Button Browse(string title, Action<string> apply)
    {
        var button = new Button { Content = "Browse…" };
        button.Click += async (_, _) =>
        {
            var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
            var path = result.FirstOrDefault()?.TryGetLocalPath(); if (path != null) apply(path);
        };
        return button;
    }
    /// <summary>Folder pickers cannot select a folder that does not exist yet: an empty selection becomes the project itself; anything else gets a named subfolder.</summary>
    private static string ChooseInside(string picked, string name)
        => !Directory.EnumerateFileSystemEntries(picked).Any() || string.IsNullOrWhiteSpace(name) ? picked : Path.Combine(picked, name);

    private async Task DetectAsync()
    {
        var version = ++detectVersion; var path = source.Text?.Trim() ?? "";
        detected = []; candidates.IsVisible = false;
        if (path.Length == 0) { sourceStatus.Text = "Choose the mod you want to edit. Studio finds the native data inside it."; Refresh(); return; }
        sourceStatus.Text = "Looking for mod data…";
        List<LegacyProject> found; string? failure = null;
        try { found = await Task.Run(() => LegacyMigration.Detect(path)); }
        catch (Exception ex) { found = []; failure = ex.Message; }
        if (version != detectVersion) return;
        detected = found;
        if (failure != null) sourceStatus.Text = failure;
        else if (found.Count == 0) sourceStatus.Text = "No mod data found here. Select a mod folder, an unpacked .mpq folder, a data folder containing global/, hd/ or local/, or a legacy Studio project.";
        else if (found.Count == 1) ApplyCandidate(found[0]);
        else
        {
            updating = true; candidates.ItemsSource = found; candidates.IsVisible = true; updating = false;
            sourceStatus.Text = $"{found.Count} mods found; choose the one to import."; candidates.SelectedIndex = 0;
        }
        Refresh();
    }
    private void ApplyCandidate(LegacyProject picked)
    {
        updating = true;
        sourceStatus.Text = picked.SplitRecords ? "Found a legacy Studio project with individual JSON records." : "Found native data: " + picked.DataRoot;
        name.Text = picked.Name; destinationEdited = false; deploymentEdited = false;
        updating = false; Refresh();
    }
    private LegacyProject? Selected => detected.Count switch { 0 => null, 1 => detected[0], _ => candidates.SelectedItem as LegacyProject ?? detected[0] };

    private void Refresh()
    {
        updating = true;
        var modName = name.Text?.Trim() ?? ""; var selected = Selected; var inPlace = convert.IsChecked == true;
        convert.IsEnabled = selected is { SplitRecords: false } || selected == null;
        // Old one-file-per-record projects can only be imported as a copy; fall back before the rest of the form reads the mode.
        if (!convert.IsEnabled && convert.IsChecked == true) { updating = false; copy.IsChecked = true; return; }
        if (!destinationEdited) destination.Text = inPlace && selected != null ? selected.Root : Path.Combine(projectsFolder, modName);
        // In-place conversion has no separate project folder: the box and its Browse button both go inactive, so a picked folder cannot be silently ignored.
        destination.IsEnabled = !inPlace; browseDestination.IsEnabled = !inPlace;
        var install = installations.FirstOrDefault();
        if (!deploymentEdited) deployment.Text = install == null ? "" : Path.Combine(install.Directory, "mods", modName);
        destinationStatus.Text = inPlace ? "The converted project replaces this folder after verification. Close other tools using it first." : "Editable source files, migration report and local build output live here. Git-friendly.";
        deploymentStatus.Text = install != null && !deploymentEdited ? $"Auto-detected Diablo II: Resurrected at {install.Directory} ({install.Source}). Studio creates this folder on first Deploy."
            : install == null && deployment.Text?.Length == 0 ? "No Diablo II: Resurrected installation detected. Leave empty and set it later in Run settings."
            : "Studio writes the built mod here on Deploy and Play.";
        summary.Text = selected == null ? "Select a mod to see what Studio will do."
            : inPlace ? $"Convert {selected.Root} in place as \"{modName}\". A complete backup is kept beside it as {Path.GetFileName(selected.Root)}.backup-<timestamp>."
            : $"Create \"{modName}\" at {destination.Text} from {selected.DataRoot}. The original mod stays untouched." + (deployment.Text?.Length > 0 ? $"\nDeploy target: {deployment.Text}" : "");
        error.IsVisible = false; import.IsEnabled = selected != null;
        updating = false;
    }

    private ImportPlan? Validate()
    {
        try
        {
            var selected = Selected; Require(selected != null, "Select a mod to import.");
            var modName = name.Text?.Trim() ?? ""; ModProject.ValidateName(modName);
            var inPlace = convert.IsChecked == true; Require(!inPlace || !selected!.SplitRecords, "Legacy JSON projects can only be imported as a new project.");
            var target = inPlace ? selected!.Root : Path.GetFullPath((destination.Text ?? "").Trim());
            var deploy = (deployment.Text ?? "").Trim(); deploy = deploy.Length == 0 ? "" : Path.GetFullPath(deploy);
            var install = installations.FirstOrDefault(i => deploy.Length > 0 && Contains(i.Directory, deploy)) ?? installations.FirstOrDefault();
            if (!inPlace)
            {
                Require(!Contains(selected!.Root, target) && !Contains(target, selected.Root), "The project folder must be outside the original mod.");
                Require(!Directory.Exists(target) || !Directory.EnumerateFileSystemEntries(target).Any(), "The project folder must be new or empty.");
                Require(deploy.Length == 0 || !Contains(deploy, target) && !Contains(target, deploy), "The project folder cannot be the game mods folder: Studio deploys built files there for you. Choose a separate folder for the editable project, for example under Documents.");
                foreach (var i in installations) Require(!Contains(Path.Combine(i.Directory, "mods"), target), $"{target} is inside the game's mods folder. Keep the editable project elsewhere; Studio deploys to mods/{modName} for you.");
            }
            string? backup = inPlace ? selected!.Root + ".backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") : null;
            return new(selected!, modName, target, backup, install?.Directory ?? "", deploy);
        }
        catch (Exception ex) { error.Text = ex.Message; error.IsVisible = true; return null; }
    }
}

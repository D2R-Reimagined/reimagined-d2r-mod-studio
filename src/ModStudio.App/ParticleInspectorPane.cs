using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ModStudio.Core;
using Path = System.IO.Path;

namespace ModStudio.App;

/// <summary>Tab content that edits a file outside the text <see cref="Document"/> model, so Save all, closing and builds see its edits.</summary>
public interface IEditedFile
{
    string FilePath { get; }
    bool IsDirty { get; }
    void Save();
    event Action? Changed;
}

/// <summary>What the particle editor needs from the window: where files resolve, how to open one, and whether one has unsaved edits.</summary>
public sealed record ParticleHost(Func<IReadOnlyList<string>> ProjectRoots, Func<IReadOnlyList<string>> GameRoots, Action<string>? Open = null, Func<string, bool>? HasUnsavedEdits = null);

/// <summary>
/// Inspector and editor for a baked PopcornFX effect (.particles): the effect's layers per platform, each layer's
/// renderers (material, enabled features, settings, texture thumbnails, meshes), the curves it samples, its colour
/// constants and attributes, and the HD JSON files that use it. Edits keep the compiled script intact: texture and mesh
/// paths, number settings, curve keys and colours (one at a time or recoloured together). A game-data effect saves as a
/// project copy at the same path, which overrides it in the mod; Save as new effect writes a renamed copy and can point
/// the JSON files that use the original at it. Nothing here animates the effect.
/// </summary>
public sealed partial class ParticleInspectorPane : Grid, IEditedFile
{
    public static bool Supports(string file) => Path.GetExtension(file).Equals(".particles", StringComparison.OrdinalIgnoreCase);
    private static readonly IBrush Text = new SolidColorBrush(Color.Parse("#E6E6E6")), Muted = new SolidColorBrush(Color.Parse("#A8A29A")),
        Heading = new SolidColorBrush(Color.Parse("#D8BC86")), CardBackground = new SolidColorBrush(Color.Parse("#1D1E20")),
        CardBorder = new SolidColorBrush(Color.Parse("#34363A")), Chip = new SolidColorBrush(Color.Parse("#2C3A33")), Faint = new SolidColorBrush(Color.Parse("#6F6A64")),
        Link = new SolidColorBrush(Color.Parse("#8FB3FF")), Warning = new SolidColorBrush(Color.Parse("#D9924A")), PlotBackground = new SolidColorBrush(Color.Parse("#121315"));
    private static readonly IBrush[] Channels = [new SolidColorBrush(Color.Parse("#E5533D")), new SolidColorBrush(Color.Parse("#69C66B")), new SolidColorBrush(Color.Parse("#5B8DEF")), new SolidColorBrush(Color.Parse("#D0D0D0"))];
    private static readonly PreviewWorkQueue Work = new();

    private string file;
    private readonly ParticleHost host;
    private readonly ComboBox platforms = new() { MinWidth = 110 };
    private readonly ListBox layers = new() { Background = Brushes.Transparent };
    private readonly ScrollViewer details = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private readonly TextBox hex = new() { IsReadOnly = true, AcceptsReturn = true, FontFamily = new("Consolas, Menlo, monospace"), IsVisible = false };
    private readonly TextBlock status = new() { Foreground = Muted, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock banner = new() { Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new(10, 0, 10, 6) };
    private readonly CheckBox showDisabled = new() { Content = "Settings of disabled features", VerticalAlignment = VerticalAlignment.Center };
    private readonly Button undoButton = new() { Content = "Undo" }, redoButton = new() { Content = "Redo" }, saveButton = new() { Content = "Save" }, cloneButton = new() { Content = "Save as new effect…" };
    private readonly Dictionary<string, Task<Bitmap?>> thumbnails = new(StringComparer.OrdinalIgnoreCase);
    private readonly Grid body = new() { ColumnDefinitions = new("240,*") };
    private readonly List<byte[]> undo = [], redo = [];
    private CancellationTokenSource? pending;
    private List<(string Root, string File)>? usages;
    private byte[]? current, saved;
    private bool attached;

    public ParticleEffectInfo? Info { get; private set; }
    public string? Error { get; private set; }
    public string FilePath => file;
    public bool IsDirty => current != null && saved != null && !current.AsSpan().SequenceEqual(saved);
    public event Action? Changed;
    public ParticlePlatform? Platform => Info is { } info && platforms.SelectedIndex >= 0 && platforms.SelectedIndex < info.Platforms.Length ? info.Platforms[platforms.SelectedIndex] : null;
    internal Task Loading { get; private set; } = Task.CompletedTask;
    internal IEnumerable<string> LayerItems => layers.Items.OfType<ListBoxItem>().Select(i => i.Tag as string ?? "");
    internal void SelectLayer(int index) => layers.SelectedIndex = index;
    internal int ShownThumbnails => thumbnails.Values.Count(t => t.IsCompletedSuccessfully && t.Result != null);
    internal Task Thumbnails => Task.WhenAll(thumbnails.Values);
    internal Task? UsageSearch { get; private set; }
    internal IReadOnlyList<(string Root, string File)> Usages => usages ?? [];
    internal string StatusText => status.Text ?? "";
    internal bool CanUndo => undo.Count > 0;
    internal void Undo() => Step(undo, redo, "Undone");
    internal void Redo() => Step(redo, undo, "Redone");
    /// <summary>Every text line currently shown in the details panel, for smoke checks.</summary>
    internal string DetailText => string.Join("\n", (details.Content as Control)?.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text) ?? []);
    internal Control? DetailContent => details.Content as Control;

    public ParticleInspectorPane(string path, ParticleHost host)
    {
        file = path; this.host = host;
        RowDefinitions = new("Auto,Auto,Auto,*");
        var toolbar = new ContentControl { Margin = new(8, 6) };
        var buttons = new WrapPanel { ItemSpacing = 8, LineSpacing = 6 };
        buttons.Children.Add(new TextBlock { Text = "Platform", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center });
        buttons.Children.Add(platforms); buttons.Children.Add(showDisabled);
        ToolTip.SetTip(platforms, "The game bakes each effect once per platform. Edits change the platform shown; PC is what Windows players, and so mods, use.");
        ToolTip.SetTip(showDisabled, "Also list settings that belong to renderer features this effect leaves off.");
        foreach (var b in new[] { undoButton, redoButton, saveButton, cloneButton }) buttons.Children.Add(b);
        undoButton.Click += (_, _) => Undo(); redoButton.Click += (_, _) => Redo();
        saveButton.Click += (_, _) => { try { Save(); } catch (Exception ex) { status.Text = "Not saved: " + ex.Message; } };
        cloneButton.Click += async (_, _) => { try { await SaveAsNewAsync(); } catch (Exception ex) { status.Text = "Not saved: " + ex.Message; } };
        ToolTip.SetTip(cloneButton, "Write this effect, with any edits, to a new file in the project, and optionally point the JSON files that use it at the copy.");
        var toggle = new Button { Content = "Hex" }; buttons.Children.Add(toggle);
        toggle.Click += (_, _) => { hex.IsVisible = !hex.IsVisible; body.IsVisible = !hex.IsVisible; toggle.Content = hex.IsVisible ? "Inspector" : "Hex"; };
        var reload = new Button { Content = "Reload" }; buttons.Children.Add(reload);
        reload.Click += async (_, _) =>
        {
            if (IsDirty && await ParticleDialogs.ChooseAsync(this, "Reload effect", "Discard the unsaved edits and read the file again?", "Discard and reload", "Cancel") != "Discard and reload") return;
            Loading = LoadAsync();
        };
        toolbar.Content = buttons;
        status.Margin = new(10, 0, 10, 4);
        Children.Add(toolbar); SetRow(status, 1); Children.Add(status); SetRow(banner, 2); Children.Add(banner);
        layers.Margin = new(6, 0, 0, 6);
        var left = new Border { BorderBrush = CardBorder, BorderThickness = new(0, 0, 1, 0), Child = layers };
        body.Children.Add(left); SetColumn(details, 1); details.Padding = new(14, 4, 14, 14); body.Children.Add(details);
        SetRow(body, 3); Children.Add(body); SetRow(hex, 3); Children.Add(hex);
        platforms.SelectionChanged += (_, _) => { if (Info != null) FillLayers(null); };
        layers.SelectionChanged += (_, _) => ShowSelection();
        showDisabled.IsCheckedChanged += (_, _) => ShowSelection();
        // Switching tabs detaches the pane; unsaved edits live here, so only the first attach reads the file.
        AttachedToVisualTree += (_, _) => { attached = true; if (current == null && Error == null) Loading = LoadAsync(); else ShowSelection(); };
        DetachedFromVisualTree += (_, _) => { attached = false; pending?.Cancel(); thumbnails.Clear(); };
        UpdateButtons();
    }

    private bool InProject(string path) => host.ProjectRoots().Any(r => Storage.Contains(r, path));
    /// <summary>Where Save writes: the file itself in a project, else the project path that overrides this game-data file.</summary>
    private string? SaveTarget() => InProject(file) ? file : host.ProjectRoots().FirstOrDefault() is { } root && ParticleUsage.RelativePath(file) is { } relative ? Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)) : null;

    private void UpdateButtons()
    {
        undoButton.IsEnabled = undo.Count > 0; redoButton.IsEnabled = redo.Count > 0;
        var target = SaveTarget(); bool inProject = InProject(file);
        saveButton.Content = inProject ? "Save" : "Save to project"; saveButton.IsEnabled = Info != null && target != null && (IsDirty || !inProject);
        cloneButton.IsEnabled = Info != null && host.ProjectRoots().Count > 0 && ParticleUsage.RelativePath(file) != null;
        banner.IsVisible = !inProject;
        banner.Text = inProject ? "" : target == null
            ? "This effect is outside the project. Open a project to save edits."
            : $"Game data effect. Save to project writes {ParticleUsage.RelativePath(file)} into the project, which replaces this effect in your mod.";
    }

    private async Task LoadAsync()
    {
        pending?.Cancel(); var work = pending = new CancellationTokenSource(); var token = work.Token;
        status.Text = "Reading effect…"; Error = null; thumbnails.Clear(); usages = null;
        try
        {
            var (bytes, info, dump) = await Work.RunAsync(_ =>
            {
                Storage.NoLinks(file); Storage.Require(new FileInfo(file).Length <= 64 * 1024 * 1024, "Particle files over 64 MiB are not opened.");
                var data = File.ReadAllBytes(file); var loaded = ParticleEffectInfo.Read(data);
                return (data, loaded, HexDump(data));
            }, token);
            if (!attached || token.IsCancellationRequested) return;
            hex.Text = dump; current = saved = bytes; undo.Clear(); redo.Clear();
            Info = info; status.Text = $"PopcornFX {info.Version} · {info.File.Chunks.Count:N0} chunks · {info.File.Strings.Count:N0} strings";
            platforms.ItemsSource = info.Platforms.Select(p => p.Name).ToArray();
            platforms.SelectedIndex = Math.Max(0, Array.FindIndex(info.Platforms, p => p.Name == "PC"));
            FillLayers(null); UpdateButtons(); Changed?.Invoke();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!attached) return;
            Error = ex.Message; Info = null; current = saved = null; status.Text = "Effect could not be read: " + ex.Message;
            layers.Items.Clear(); details.Content = new TextBlock { Text = "This file is not a D2R baked effect this inspector understands. " + ex.Message + "\nUse Hex to look at its bytes.", TextWrapping = TextWrapping.Wrap, Foreground = Warning, Margin = new(0, 8) };
            UpdateButtons();
        }
    }
    private static string HexDump(byte[] bytes)
    {
        var prefix = bytes.AsSpan(0, Math.Min(bytes.Length, 4096)); var text = new StringBuilder($"{bytes.Length:N0} bytes · first {prefix.Length:N0} bytes\n\n");
        for (int offset = 0; offset < prefix.Length; offset += 16)
        {
            var line = prefix.Slice(offset, Math.Min(16, prefix.Length - offset)).ToArray();
            text.AppendLine($"{offset:X8}  {string.Join(' ', line.Select(b => b.ToString("X2"))),-48} {new string([.. line.Select(b => b is >= 32 and <= 126 ? (char)b : '.')])}");
        }
        return text.ToString();
    }

    // ── Editing ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Applies one edit to a fresh copy of the effect; the result must read back before it replaces the current state.</summary>
    internal bool Edit(string description, Action<ParticleFile> change)
    {
        if (current == null) return false;
        try
        {
            var working = ParticleFile.Read(current); change(working);
            var bytes = working.Write(); var info = ParticleEffectInfo.Read(bytes);
            if (bytes.AsSpan().SequenceEqual(current)) { status.Text = description + " (no change)"; return false; }
            undo.Add(current); if (undo.Count > 100) undo.RemoveAt(0); redo.Clear();
            Apply(bytes, info); status.Text = description; return true;
        }
        catch (Exception ex) { status.Text = "Not changed: " + ex.Message; ShowSelection(); return false; }
    }
    private void Step(List<byte[]> from, List<byte[]> to, string verb)
    {
        if (from.Count == 0 || current == null) return;
        var bytes = from[^1]; from.RemoveAt(from.Count - 1); to.Add(current);
        Apply(bytes, ParticleEffectInfo.Read(bytes)); status.Text = verb;
    }
    private void Apply(byte[] bytes, ParticleEffectInfo info)
    {
        current = bytes; Info = info; hex.Text = HexDump(bytes);
        var selected = (layers.SelectedItem as ListBoxItem)?.Tag as string; var offset = details.Offset;
        FillLayers(selected); details.Offset = offset;
        UpdateButtons(); Changed?.Invoke();
    }

    public void Save()
    {
        if (current == null) return;
        var target = SaveTarget() ?? throw new InvalidOperationException("Open a project to save a copy of this effect.");
        Write(target, current);
        bool copied = !string.Equals(Path.GetFullPath(target), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase);
        file = target; saved = current; UpdateButtons(); Changed?.Invoke();
        status.Text = copied ? "Saved a project copy: " + ParticleUsage.RelativePath(target) : "Saved";
    }
    private static void Write(string target, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + ".saving"; File.WriteAllBytes(temporary, bytes); File.Move(temporary, target, true);
    }

    /// <summary>
    /// Writes the effect under a new name beside where it would live in the project, points the chosen JSON files at the
    /// copy (game-data JSON is copied into the project first), and continues editing the copy.
    /// </summary>
    internal async Task SaveAsNewAsync(string? name = null, IReadOnlyCollection<string>? retarget = null)
    {
        if (current == null) return;
        var root = host.ProjectRoots().FirstOrDefault() ?? throw new InvalidOperationException("Open a project first.");
        var relative = ParticleUsage.RelativePath(file) ?? throw new InvalidOperationException("This effect is not under an hd folder, so its game path is unknown.");
        if (usages == null) await (UsageSearch = SearchUsagesAsync());
        if (name == null)
        {
            var choice = await ParticleDialogs.CloneAsync(this, Path.GetFileNameWithoutExtension(file) + "_copy", [.. Usages.Select(u => u.File)], f => Storage.Relative(Usages.First(u => u.File == f).Root, f));
            if (choice == null) return;
            (name, retarget) = choice.Value;
        }
        Storage.Require(name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'), "Use letters, digits, - and _ for the effect name.");
        var copyRelative = relative[..(relative.LastIndexOf('/') + 1)] + name.ToLowerInvariant() + ".particles";
        var target = Path.Combine(root, copyRelative.Replace('/', Path.DirectorySeparatorChar));
        Storage.Require(!File.Exists(target), "The project already has " + copyRelative + ".");
        var edits = new List<(string Path, byte[] Bytes)>();
        foreach (var json in retarget ?? [])
        {
            var jsonRelative = ParticleUsage.RelativePath(json) ?? throw new InvalidOperationException(json + " is not under an hd folder.");
            var jsonTarget = InProject(json) ? json : Path.Combine(root, jsonRelative.Replace('/', Path.DirectorySeparatorChar));
            Storage.Require(host.HasUnsavedEdits?.Invoke(jsonTarget) != true, $"Save or close {Path.GetFileName(jsonTarget)} first; it has unsaved edits.");
            var bytes = File.ReadAllBytes(json); bool bom = bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]);
            var text = ParticleEdits.Retarget(Storage.Utf8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), relative, copyRelative, out var count);
            Storage.Require(count > 0, Path.GetFileName(json) + " no longer names this effect.");
            edits.Add((jsonTarget, [.. bom ? new byte[] { 0xEF, 0xBB, 0xBF } : [], .. Storage.Utf8.GetBytes(text)]));
        }
        Write(target, current);
        foreach (var (path, bytes) in edits) Write(path, bytes);
        file = target; saved = current; usages = null; UpdateButtons(); Changed?.Invoke(); ShowSelection();
        status.Text = $"Saved {copyRelative}" + (edits.Count > 0 ? $" and pointed {edits.Count} JSON file{(edits.Count == 1 ? "" : "s")} at it" : "");
    }

    // ── Lists ──────────────────────────────────────────────────────────────────────────────────────────

    private void FillLayers(string? keep)
    {
        var platform = Platform; layers.Items.Clear();
        layers.Items.Add(Item("Effect", "overview", $"{Count(platform?.Layers.Length ?? 0, "layer")} · {Count(Info!.Attributes.Length, "attribute")}"));
        foreach (var layer in platform?.Layers ?? [])
        {
            var kinds = layer.Renderers.Length == 0 ? "no renderer" : string.Join(", ", layer.Renderers.Select(r => r.Kind));
            var extras = new[] { layer.Curves.Length > 0 ? Count(layer.Curves.Length, "curve") : null, layer.Colors.Length > 0 ? Count(layer.Colors.Length, "colour") : null }.OfType<string>();
            layers.Items.Add(Item(layer.Name, "layer:" + layer.Reference, string.Join(" · ", extras.Prepend(kinds)), layer.Renderers.Length == 0));
        }
        var index = keep == null ? 0 : Math.Max(0, layers.Items.OfType<ListBoxItem>().ToList().FindIndex(i => i.Tag as string == keep));
        layers.SelectedIndex = index; ShowSelection();
        static ListBoxItem Item(string title, string tag, string note, bool quiet = false)
        {
            var panel = new StackPanel { Margin = new(2, 3) };
            panel.Children.Add(new TextBlock { Text = title, FontSize = 13, Foreground = quiet ? Muted : Text, TextTrimming = TextTrimming.CharacterEllipsis });
            panel.Children.Add(new TextBlock { Text = note, FontSize = 11, Foreground = Faint, TextTrimming = TextTrimming.CharacterEllipsis });
            return new ListBoxItem { Content = panel, Tag = tag };
        }
    }

    private void ShowSelection()
    {
        if (Info == null || layers.SelectedItem is not ListBoxItem { Tag: string tag }) return;
        details.Content = tag == "overview" ? Overview() : Platform?.Layers.FirstOrDefault(l => "layer:" + l.Reference == tag) is { } layer ? LayerView(layer) : null;
    }

    // ── Overview ───────────────────────────────────────────────────────────────────────────────────────

    private Control Overview()
    {
        var info = Info!; var root = new StackPanel { Spacing = 12 };
        root.Children.Add(Title(Path.GetFileName(file), ParticleUsage.RelativePath(file) ?? file));
        var summary = new StackPanel { Spacing = 4 };
        summary.Children.Add(Line($"Baked with PopcornFX {info.Version}"));
        foreach (var p in info.Platforms) summary.Children.Add(Line($"{p.Name}: {Count(p.Layers.Length, "layer")}, {p.Layers.Count(l => l.Renderers.Length > 0)} drawing ({string.Join(", ", p.Layers.SelectMany(l => l.Renderers).GroupBy(r => r.Kind).Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}"))})", Muted));
        summary.Children.Add(Line("Layers, renderers and settings are read from the baked file. Spawning and motion are compiled PopcornFX bytecode, so they are listed but not simulated or edited.", Faint));
        root.Children.Add(Card("Effect", summary));
        if (Platform is { } platform && RecolourTargets(platform).Count > 0) root.Children.Add(RecolourCard(platform));
        var materials = new StackPanel { Spacing = 4 };
        foreach (var m in info.Materials) materials.Children.Add(Line(m));
        if (materials.Children.Count > 0) root.Children.Add(Card("Materials", materials, "Built into the game; the effect names them."));
        var files = new StackPanel { Spacing = 6 };
        foreach (var path in info.Paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) files.Children.Add(FileRow(path));
        if (files.Children.Count > 0) root.Children.Add(Card("Textures and meshes", files, "Every file the effect's renderers load, on any platform. Change them on a layer's page."));
        if (info.Attributes.Length > 0)
        {
            var grid = Table(["Attribute", "Type", "Default", "Range"]);
            foreach (var a in info.Attributes)
                AddRow(grid, [a.Name, a.Type, Numbers(a.Default), a.Min != null || a.Max != null ? $"{(a.Min != null ? Numbers(a.Min) : "…")} to {(a.Max != null ? Numbers(a.Max) : "…")}" : ""], a.Description);
            root.Children.Add(Card("Attributes", grid, "Inputs the game can set when it spawns the effect."));
        }
        root.Children.Add(Card("Used by", UsageView(), "HD JSON files (missiles, overlays, presets, units) that name this effect."));
        var classes = new StackPanel { Spacing = 2 };
        foreach (var (name, count) in info.Classes) classes.Children.Add(Line($"{count,6:N0}  {name}", Muted, mono: true));
        root.Children.Add(new Expander { Header = "Chunk classes", Content = classes, HorizontalAlignment = HorizontalAlignment.Stretch });
        return root;
    }

    private Control UsageView()
    {
        var holder = new ContentControl();
        void Render()
        {
            if (usages == null)
            {
                var find = new Button { Content = "Find usages", HorizontalAlignment = HorizontalAlignment.Left };
                ToolTip.SetTip(find, "Searches the project and your extracted game data. The first search of the game data reads every HD JSON file and can take a few seconds.");
                find.Click += async (_, _) => { find.IsEnabled = false; find.Content = "Searching…"; await (UsageSearch = SearchUsagesAsync()); Render(); };
                holder.Content = find; return;
            }
            var list = new StackPanel { Spacing = 4 };
            if (usages.Count == 0) list.Children.Add(Line(host.GameRoots().Count == 0 ? "Not named by any JSON in the project. Choose your extracted game data folder to search the base game too." : "No HD JSON in the project or game data names this effect.", Muted));
            foreach (var (root, json) in usages) list.Children.Add(LinkRow(Storage.Relative(root, json), json, host.ProjectRoots().Contains(root, StringComparer.OrdinalIgnoreCase) ? "project" : "game data"));
            holder.Content = list;
        }
        Render(); return holder;
    }

    private async Task SearchUsagesAsync()
    {
        var relative = ParticleUsage.RelativePath(file);
        if (relative == null) { usages = []; return; }
        IReadOnlyList<string> projects = host.ProjectRoots(), games = host.GameRoots();
        try
        {
            var found = await Task.Run(() => ParticleUsage.Find(projects, games, relative));
            // A project JSON overrides the game's copy at the same path; list only the one the game reads.
            usages = [.. found.GroupBy(u => ParticleUsage.RelativePath(u.File) ?? u.File, StringComparer.OrdinalIgnoreCase).Select(g => g.First())];
        }
        catch (Exception ex) { usages = []; status.Text = "Usage search failed: " + ex.Message; }
    }

    // ── Files ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Where a data-relative path resolves: next to this effect, then the project, then the game data.</summary>
    private (string? Path, string Origin) Resolve(string relative)
    {
        var projects = host.ProjectRoots(); var games = host.GameRoots();
        foreach (var root in projects.Concat(new[] { ParticleUsage.DataRoot(file) }.OfType<string>()).Concat(games).Distinct(StringComparer.OrdinalIgnoreCase))
            if (HdAppearance.Locate(root, relative) is { } found)
                return (found, projects.Any(p => Storage.Contains(p, found)) ? "project" : games.Any(g => Storage.Contains(g, found)) ? "game data" : "beside this effect");
        return (null, games.Count == 0 ? "not in the project; choose your extracted game data to resolve base-game files" : "missing");
    }

    private Control FileRow(string relative, ParticleProperty? property = null)
    {
        var (path, origin) = Resolve(relative);
        var row = new DockPanel();
        var thumb = new Border { Width = 48, Height = 48, Background = PlotBackground, CornerRadius = new(3), Margin = new(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(thumb, Dock.Left); row.Children.Add(thumb);
        if (property != null)
        {
            var change = new Button { Content = "Change…", VerticalAlignment = VerticalAlignment.Center, Margin = new(10, 0, 0, 0) };
            ToolTip.SetTip(change, "Point this setting at another file.");
            change.Click += async (_, _) =>
            {
                var extension = Path.GetExtension(relative);
                var picked = await ParticleDialogs.PickResourceAsync(this, relative, extension, ResourceCandidates(extension), (p, border) => { if (Resolve(p).Path is { } found && SpecialistPreview.Supports(found)) _ = ShowThumbnailAsync(found, border); });
                if (picked != null && !picked.Equals(relative, StringComparison.OrdinalIgnoreCase)) Edit($"{property.Name} → {picked}", f => ParticleEdits.SetPath(f, property.Reference, picked));
            };
            DockPanel.SetDock(change, Dock.Right); row.Children.Add(change);
        }
        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        if (property != null) text.Children.Add(new TextBlock { Text = property.Name, FontSize = 11, Foreground = Muted });
        text.Children.Add(PathLink(relative, path));
        text.Children.Add(new TextBlock { Text = origin, FontSize = 11, Foreground = path == null ? Warning : Faint });
        row.Children.Add(text);
        if (path != null && SpecialistPreview.Supports(path)) _ = ShowThumbnailAsync(path, thumb);
        return row;
    }

    /// <summary>Files of one type the game can load: the project's and the game data's copies of hd/vfx (textures or meshes), by data-relative path.</summary>
    private List<string> ResourceCandidates(string extension)
    {
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in host.ProjectRoots().Concat(host.GameRoots()).Concat(new[] { ParticleUsage.DataRoot(file) }.OfType<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var folder in new[] { "data/hd/vfx", "hd/vfx" }.Select(f => Path.Combine(root, f)).Where(Directory.Exists).Take(1))
                foreach (var candidate in Directory.EnumerateFiles(folder, "*" + extension, SearchOption.AllDirectories))
                    if (ParticleUsage.RelativePath(candidate) is { } relative) found.Add(relative);
        return [.. found];
    }

    private Control LinkRow(string text, string path, string note)
    {
        var panel = new StackPanel { Spacing = 0 };
        panel.Children.Add(PathLink(text, path)); panel.Children.Add(new TextBlock { Text = note, FontSize = 11, Foreground = Faint });
        return panel;
    }

    private Control PathLink(string text, string? path)
    {
        var block = new TextBlock { Text = text, Foreground = path != null && host.Open != null ? Link : Text, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        if (path == null || host.Open == null) return block;
        var button = new Button { Content = block, Padding = new(0), Background = Brushes.Transparent, BorderThickness = new(0), Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
        ToolTip.SetTip(button, "Open " + path); button.Click += (_, _) => host.Open(path);
        return button;
    }

    internal async Task ShowThumbnailAsync(string path, Border target)
    {
        if (!thumbnails.TryGetValue(path, out var task)) thumbnails[path] = task = Work.RunAsync(_ =>
        {
            try
            {
                var asset = SpecialistPreview.Load(path);
                // Mips halve down to 1×1; seven above the last is about 64 pixels.
                return ToBitmap(asset.Decode(Math.Max(asset.InitialFrame, asset.Frames.Length - 7)));
            }
            catch (Exception) { return null; }
        }, CancellationToken.None);
        var bitmap = await task;
        if (bitmap != null) target.Child = new Image { Source = bitmap, Stretch = Stretch.Uniform };
    }

    private static Bitmap ToBitmap(PreviewPixels pixels)
    {
        var bitmap = new WriteableBitmap(new PixelSize(pixels.Width, pixels.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using (var target = bitmap.Lock()) for (int y = 0; y < pixels.Height; y++) Marshal.Copy(pixels.Rgba, y * pixels.Width * 4, target.Address + y * target.RowBytes, pixels.Width * 4);
        return bitmap;
    }

    // ── Layout helpers ─────────────────────────────────────────────────────────────────────────────────

    private static string Count(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";
    internal static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Numbers(float[] values) => string.Join(", ", values.Select(F));
    private static TextBlock Line(string text, IBrush? brush = null, bool mono = false) => new() { Text = text, Foreground = brush ?? Text, TextWrapping = TextWrapping.Wrap, FontSize = 12, FontFamily = mono ? new FontFamily("Consolas, Menlo, monospace") : FontFamily.Default };
    private static Control Title(string title, string note)
    {
        var panel = new StackPanel { Spacing = 2, Margin = new(0, 6, 0, 0) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 18, Foreground = Heading, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = note, FontSize = 12, Foreground = Muted, TextWrapping = TextWrapping.Wrap });
        return panel;
    }
    private static Border Card(string heading, Control content, string? note = null)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = heading.ToUpperInvariant(), Foreground = Heading, FontSize = 11, FontWeight = FontWeight.SemiBold });
        if (note != null) panel.Children.Add(new TextBlock { Text = note, Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(content);
        return new Border { Background = CardBackground, BorderBrush = CardBorder, BorderThickness = new(1), CornerRadius = new(6), Padding = new(16, 12), Child = panel };
    }
    private static Grid Table(string[] headings)
    {
        var grid = new Grid { ColumnDefinitions = new(string.Join(",", headings.Select((_, i) => i == headings.Length - 1 ? "*" : "Auto"))) };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (int i = 0; i < headings.Length; i++) { var h = new TextBlock { Text = headings[i], FontSize = 11, Foreground = Faint, Margin = new(0, 0, 18, 4) }; SetColumn(h, i); grid.Children.Add(h); }
        return grid;
    }
    private static void AddRow(Grid grid, string[] cells, string? tip = null, bool dim = false) =>
        AddRow(grid, [.. cells.Select((c, i) => (Control)new TextBlock { Text = c, FontSize = 12, Foreground = dim ? Faint : i == 0 ? Text : Muted, TextWrapping = i == cells.Length - 1 ? TextWrapping.Wrap : TextWrapping.NoWrap })], tip);
    private static void AddRow(Grid grid, Control[] cells, string? tip = null)
    {
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); int row = grid.RowDefinitions.Count - 1;
        for (int i = 0; i < cells.Length; i++)
        {
            var cell = cells[i]; cell.Margin = new(0, 1, 18, 1); cell.VerticalAlignment = VerticalAlignment.Center;
            if (tip != null) ToolTip.SetTip(cell, tip);
            SetRow(cell, row); SetColumn(cell, i); grid.Children.Add(cell);
        }
    }
    /// <summary>A colour as a brush; HDR values (above 1) are scaled down to their brightest channel so the hue still shows.</summary>
    internal static Color ToColor(float[] rgba)
    {
        float peak = Math.Max(1, Math.Max(rgba[0], Math.Max(rgba.Length > 1 ? rgba[1] : 0, rgba.Length > 2 ? rgba[2] : 0)));
        static byte B(float v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
        return Color.FromArgb(rgba.Length > 3 ? B(Math.Clamp(rgba[3], 0.15f, 1)) : (byte)255, B(rgba[0] / peak), B((rgba.Length > 1 ? rgba[1] : 0) / peak), B((rgba.Length > 2 ? rgba[2] : 0) / peak));
    }
    internal static Border Swatch(float[] rgba, double width = 22, double height = 16) => new() { Width = width, Height = height, CornerRadius = new(3), Background = new SolidColorBrush(ToColor(rgba)), BorderBrush = CardBorder, BorderThickness = new(1) };
}

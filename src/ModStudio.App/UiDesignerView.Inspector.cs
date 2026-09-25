using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ModStudio.Core;

namespace ModStudio.App;

/// <summary>The designer's inspector: what the selected widget is, where each of its values comes from, and editors for them.</summary>
internal sealed partial class UiDesignerView
{
    private readonly Dictionary<string, Control> inspectorFields = new(StringComparer.Ordinal);
    private bool inspectorPending;
    private UiWidget? inspected;

    /// <summary>Named inspector editors ("rect.x", "text", "field:filename"), for tests and for focusing one from the canvas.</summary>
    internal Control? InspectorField(string name) => inspectorFields.GetValueOrDefault(name);

    private void RefreshInspector(bool force = false)
    {
        // A field being typed in keeps its text; the inspector catches up when focus leaves it.
        if (!force && inspector.IsKeyboardFocusWithin && inspected?.Path == canvas.Selected?.Path) { inspectorPending = true; return; }
        inspectorPending = false;
        var offset = inspectorScroll.Offset;
        bool same = inspected?.Path == canvas.Selected?.Path;
        inspectorFields.Clear();
        inspected = canvas.Selected;
        inspector.Content = canvas.Selected is { } w ? WidgetPanel(w) : scene != null ? LayoutPanel(scene) : null;
        if (same) Dispatcher.UIThread.Post(() => inspectorScroll.Offset = offset, DispatcherPriority.Background);
        else inspectorScroll.Offset = default;
    }

    private void FocusInspectorField(string name)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (InspectorField(name) is not { } field) return;
            field.BringIntoView(); field.Focus();
            if (field is TextBox box) box.SelectAll();
        }, DispatcherPriority.Background);
    }

    // ── Layout summary (nothing selected) ────────────────────────────────────────────────────────

    private Control LayoutPanel(UiScene s)
    {
        var root = Panel();
        root.Children.Add(new TextBlock { Text = s.Document.Name, FontSize = 20, Foreground = Heading, TextWrapping = TextWrapping.Wrap });
        root.Children.Add(Line($"{s.Root.Type} · {s.Widgets.Count} widgets · {s.Mode.Label} · {s.Width:0} × {s.Height:0}", Muted));
        if (host.Project() is { } project && !Storage.Contains(project.Root, Document.FilePath))
        {
            // Edits here would change the extracted game data, not the mod.
            var outside = new StackPanel { Spacing = 6 };
            outside.Children.Add(Line("This layout is not in the project (it is your game data's copy). Changes saved here do not go into the mod.", Warning, 11));
            var copy = new Button { Content = "Copy into the project and open it", Padding = new(8, 3), MinHeight = 0, FontSize = 12 };
            copy.Click += async (_, _) =>
            {
                try
                {
                    var target = Path.Combine(project.Root, "data", "global", "ui", "layouts", s.Document.Relative.Replace('/', Path.DirectorySeparatorChar));
                    Storage.Require(!File.Exists(target), $"The project already has {s.Document.Relative}; open that one.");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllText(target, Document.Text, Storage.Utf8);
                    SetStatus($"Copied {s.Document.Relative} into the project.");
                    if (host.OpenFileAt != null) await host.OpenFileAt(target, 0);
                }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { SetStatus(e.Message, true); }
            };
            outside.Children.Add(copy);
            root.Children.Add(new Border { BorderBrush = Warning, BorderThickness = new(1), CornerRadius = new(6), Padding = new(10, 8), Child = outside });
        }
        if (s.Mode.Legacy)
            root.Children.Add(Line("Legacy (SD) layouts draw DC6 art from global/ui, which the designer outlines rather than draws; positions, sizes and text are shown as in game.", Muted, 11));
        var files = new StackPanel { Spacing = 6 };
        foreach (var file in s.Files.Where(f => !f.IsDocument)) files.Children.Add(FileRow(file, "based on"));
        foreach (var file in s.ProfileFiles) files.Children.Add(FileRow(file, "profile"));
        if (files.Children.Count == 0) files.Children.Add(Line("This layout is not based on another and no profile was found.", Muted));
        root.Children.Add(Card("Files it reads", files, note: "Drawn as the game loads them: this project first, then your game data. Parent layouts and profiles are only read; edits here stay in this file."));
        var tips = new StackPanel { Spacing = 3 };
        foreach (var tip in new[] {
            "Click to select · Alt+click picks the widget underneath", "Drag to move · handles resize (or scale pictures) · Shift keeps the axis or the aspect",
            "Arrow keys nudge 1 px · Shift+arrow 10 px", "Snapping to siblings and the parent: hold Ctrl to place freely",
            "Wheel zooms · middle-drag or Space+drag pans · F fits · Z zooms to the selection", "Ctrl+D duplicates · Del deletes · Esc selects the parent · Ctrl+Z / Ctrl+Y undo and redo",
            "Right-click a widget for more: add a child, draw order, show in source" })
            tips.Children.Add(Line("• " + tip, WhiteBrush, 11));
        root.Children.Add(Card("Working on the canvas", tips));
        root.Children.Add(Card("Root widget", RootSummary(s.Root)));
        return root;
    }

    private Control RootSummary(UiWidget root)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Line($"{root.Name} ({root.Type})", WhiteBrush));
        var select = new Button { Content = "Select the root", Padding = new(8, 3), MinHeight = 0, FontSize = 12 };
        select.Click += (_, _) => Select(root);
        panel.Children.Add(select);
        return panel;
    }

    private Control FileRow(UiLayoutFile file, string role)
    {
        var row = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (host.OpenFileAt != null)
        {
            var open = new Button { Content = "Open", Padding = new(8, 2), MinHeight = 0, FontSize = 11 };
            ToolTip.SetTip(open, file.InProject ? "Open it in a tab" : "Open the game data copy (read it; edit a project copy instead)");
            open.Click += async (_, _) => { try { await host.OpenFileAt(file.Path, 0); } catch (Exception e) { SetStatus(e.Message, true); } };
            buttons.Children.Add(open);
        }
        if (!file.InProject && host.Project() is { } project)
        {
            var copy = new Button { Content = "Copy to project", Padding = new(8, 2), MinHeight = 0, FontSize = 11 };
            ToolTip.SetTip(copy, $"Copy {file.Name} into this project's data/global/ui/layouts so the mod can change it; the designer reads the project's copy from then on.");
            copy.Click += async (_, _) =>
            {
                try
                {
                    var target = Path.Combine(project.Root, "data", "global", "ui", "layouts", file.Relative.Replace('/', Path.DirectorySeparatorChar));
                    Storage.Require(!File.Exists(target), $"The project already has {file.Relative}.");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file.Path, target);
                    SetStatus($"Copied {file.Relative} into the project.");
                    Invalidate();
                    if (host.OpenFileAt != null) await host.OpenFileAt(target, 0);
                }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { SetStatus(e.Message, true); }
            };
            buttons.Children.Add(copy);
        }
        DockPanel.SetDock(buttons, Dock.Right); row.Children.Add(buttons);
        var text = new StackPanel { Spacing = 1 };
        text.Children.Add(new TextBlock { Text = file.Relative, Foreground = WhiteBrush, FontSize = 12 });
        text.Children.Add(new TextBlock { Text = $"{role} · {file.Origin}", Foreground = file.InProject ? Own : Inherited, FontSize = 10 });
        ToolTip.SetTip(text, file.Path);
        row.Children.Add(text);
        return row;
    }

    // ── A widget ─────────────────────────────────────────────────────────────────────────────────

    private Control WidgetPanel(UiWidget w)
    {
        var root = Panel();
        root.Children.Add(Header(w));
        root.Children.Add(LayoutCard(w));
        if (UiLayout.SpriteField(w) is { } spriteField || w.Fields.ContainsKey("filename")) root.Children.Add(PictureCard(w, UiLayout.SpriteField(w) ?? "filename"));
        if (w.Fields.ContainsKey("text") || w.Fields.ContainsKey("textString") || w.Type is "TextBoxWidget") root.Children.Add(TextCard(w));
        if (w.Type == "RectangleWidget" || w.Fields.ContainsKey("color")) root.Children.Add(ColorCard(w, "color"));
        root.Children.Add(FieldsCard(w));
        root.Children.Add(ActionsCard(w));
        return root;
    }

    private Control Header(UiWidget w)
    {
        var panel = new StackPanel { Spacing = 4 };
        var top = new DockPanel();
        var glyph = new TextBlock { Text = Glyph(w), FontSize = 18, Foreground = w.InDocument ? Own : Inherited, Margin = new(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(glyph, Dock.Left); top.Children.Add(glyph);
        var name = new TextBox { Text = w.Name, FontSize = 16, IsReadOnly = w.Inherited && w.Parent != null, MinHeight = 30, VerticalContentAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(name, w.Inherited && w.Parent != null ? $"The name ties this widget to {w.Definitions[0].File.Name}'s; it cannot be renamed here." : "Rename (Enter)");
        AutomationProperties.SetName(name, "Widget name");
        name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Rename(w, name.Text?.Trim() ?? ""); e.Handled = true; } };
        name.LostFocus += (_, _) => { if (!name.IsReadOnly && (name.Text?.Trim() ?? "") != w.Name && (name.Text?.Trim().Length ?? 0) > 0) Rename(w, name.Text!.Trim()); AfterEdit(); };
        inspectorFields["name"] = name;
        top.Children.Add(name);
        panel.Children.Add(top);
        panel.Children.Add(new SelectableTextBlock { Text = $"{w.Type} · {w.Path}", Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = Provenance(w), Foreground = w.InDocument ? Own : Inherited, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        var b = w.Bounds;
        panel.Children.Add(new TextBlock { Text = $"On screen at {b.X:0}, {b.Y:0} · {b.Width:0.#} × {b.Height:0.#}" + (Math.Abs(w.Scale - 1) > 0.001 ? $" · scale {w.Scale:0.###}" : ""), Foreground = Muted, FontSize = 11 });
        return panel;
    }

    private Control LayoutCard(UiWidget w)
    {
        var body = new StackPanel { Spacing = 10 };
        var rect = w.Value("rect") as JsonObject;
        bool fit = w.Flag("fitToParent");
        var grid = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 10, LineSpacing = 8 };
        grid.Children.Add(Number("X", "rect.x", rect?["x"], v => SetRect(w, ("x", v)), "Offset from the anchor point, in the parent's units", fit || w.Parent == null && rect == null));
        grid.Children.Add(Number("Y", "rect.y", rect?["y"], v => SetRect(w, ("y", v)), "Offset from the anchor point, in the parent's units", fit || w.Parent == null && rect == null));
        grid.Children.Add(Number("Width", "rect.width", rect?["width"], v => SetRect(w, ("width", v)), "Size before scaling. Pictures without a size are drawn at their own size.", fit));
        grid.Children.Add(Number("Height", "rect.height", rect?["height"], v => SetRect(w, ("height", v)), "Size before scaling", fit));
        grid.Children.Add(Number("Scale", "rect.scale", rect?["scale"], v => SetRect(w, ("scale", v)), "Scales the widget and everything inside it", fit, "1"));
        body.Children.Add(grid);
        if (w.Fields.TryGetValue("rect", out var rectField)) body.Children.Add(OriginLine(w, rectField));
        // Anchor: a point on the parent the rect offset is measured from.
        var anchor = w.Value("anchor") as JsonObject;
        double ax = UiLayout.Member(anchor, "x"), ay = UiLayout.Member(anchor, "y");
        var anchorRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        var presets = new UniformGrid { Rows = 3, Columns = 3, Width = 66, Height = 66 };
        foreach (var y in new[] { 0, 0.5, 1 })
            foreach (var x in new[] { 0, 0.5, 1 })
            {
                bool on = Math.Abs(ax - x) < 0.001 && Math.Abs(ay - y) < 0.001;
                var dot = new Button
                {
                    Width = 20, Height = 20, MinWidth = 0, MinHeight = 0, Padding = new(0), Margin = new(1),
                    Background = on ? Own : new SolidColorBrush(Color.Parse("#2A2C30")), BorderBrush = CardBorder, BorderThickness = new(1)
                };
                ToolTip.SetTip(dot, $"Anchor at {x:0.#}, {y:0.#} of the parent" + (keepOnScreen ? " (the widget stays where it is)" : ""));
                AutomationProperties.SetName(dot, $"Anchor {x} {y}");
                double cx = x, cy = y;
                dot.Click += (_, _) => SetAnchor(w, cx, cy);
                dot.IsEnabled = !fit;
                presets.Children.Add(dot);
            }
        anchorRow.Children.Add(presets);
        var anchorFields = new StackPanel { Spacing = 6 };
        var anchorNumbers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        anchorNumbers.Children.Add(Number("Anchor X", "anchor.x", anchor?["x"], v => SetAnchor(w, v, ay), "0 = parent's left edge, 0.5 = centre, 1 = right edge", fit, "0"));
        anchorNumbers.Children.Add(Number("Anchor Y", "anchor.y", anchor?["y"], v => SetAnchor(w, ax, v), "0 = parent's top edge, 0.5 = centre, 1 = bottom edge", fit, "0"));
        anchorFields.Children.Add(anchorNumbers);
        var keep = new CheckBox { Content = "Keep on screen", IsChecked = keepOnScreen, FontSize = 11 };
        ToolTip.SetTip(keep, "Changing the anchor adjusts the rect offset so the widget does not move. Untick to let it jump to the new anchor.");
        keep.IsCheckedChanged += (_, _) => keepOnScreen = keep.IsChecked == true;
        anchorFields.Children.Add(keep);
        anchorRow.Children.Add(anchorFields);
        body.Children.Add(anchorRow);
        if (w.Parent != null)
        {
            var fitBox = new CheckBox { Content = "Fit to parent", IsChecked = fit, FontSize = 12 };
            ToolTip.SetTip(fitBox, "fitToParent: fill the parent exactly, ignoring rect and anchor");
            fitBox.IsCheckedChanged += (_, _) =>
            {
                bool on = fitBox.IsChecked == true; if (on == fit) return;
                if (!on && w.Fields.TryGetValue("fitToParent", out var f) && f.InDocument && !w.Definitions.Any(d => !d.File.IsDocument && d.Node["fields"]?["fitToParent"] != null)) RemoveField(w, "fitToParent");
                else SetField(w, "fitToParent", on ? "true" : "false");
            };
            body.Children.Add(fitBox);
        }
        return Card("Position and size", body, note: w.Parent == null ? "The root is placed on the screen: its anchor is a fraction of the screen." : null);
    }

    private bool keepOnScreen = true;

    private void SetRect(UiWidget w, params (string Member, double Value)[] changes)
    {
        w = Live(w);
        Apply(text => UiLayoutEdit.SetMembers(text, w, "rect", changes), $"{w.Name}: {string.Join(", ", changes.Select(c => $"{c.Member} {c.Value:0.###}"))}{RectNote(w)}");
    }

    private void SetAnchor(UiWidget w, double x, double y)
    {
        w = Live(w);
        var anchor = w.Value("anchor") as JsonObject;
        double ox = UiLayout.Member(anchor, "x"), oy = UiLayout.Member(anchor, "y");
        if (Math.Abs(ox - x) < 1e-9 && Math.Abs(oy - y) < 1e-9) return;
        var parent = w.Parent;
        double pw = parent?.LocalWidth ?? scene?.Width ?? 0, ph = parent?.LocalHeight ?? scene?.Height ?? 0;
        var rect = w.Value("rect") as JsonObject;
        Apply(text =>
        {
            var anchorChanges = new List<(string, double)>();
            if (Math.Abs(ox - x) > 1e-9) anchorChanges.Add(("x", x));
            if (Math.Abs(oy - y) > 1e-9) anchorChanges.Add(("y", y));
            text = UiLayoutEdit.SetMembers(text, w, "anchor", anchorChanges);
            if (!keepOnScreen) return text;
            // The same spot on screen from the new anchor: the offset takes up what the anchor moved.
            var rectChanges = new List<(string, double)>();
            if (Math.Abs(ox - x) > 1e-9) rectChanges.Add(("x", Math.Round(UiLayout.Member(rect, "x") + (ox - x) * pw)));
            if (Math.Abs(oy - y) > 1e-9) rectChanges.Add(("y", Math.Round(UiLayout.Member(rect, "y") + (oy - y) * ph)));
            return UiLayoutEdit.SetMembers(text, w, "rect", rectChanges);
        }, $"{w.Name}: anchor {x:0.###}, {y:0.###}" + (keepOnScreen ? " (offset adjusted so it stays put)" : ""));
    }

    /// <summary>Where a field's value comes from, with what can be done about it.</summary>
    private Control OriginLine(UiWidget w, UiField field)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        string text;
        IBrush brush;
        if (field.Variable != null)
        {
            text = field.VariableEntry != null ? $"{field.Key} is {field.Variable} from {field.VariableEntry.File.Name} ({field.VariableEntry.File.Origin})" : $"{field.Key} is {field.Variable}, which no profile defines";
            brush = field.VariableEntry != null ? Inherited : Warning;
        }
        else if (field.InDocument) { text = $"{field.Key} is set in this file"; brush = Own; }
        else { text = $"{field.Key} comes from {field.File.Name} ({field.File.Origin})"; brush = Inherited; }
        row.Children.Add(new TextBlock { Text = text, Foreground = brush, FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxWidth = 250, VerticalAlignment = VerticalAlignment.Center });
        if (field.VariableEntry is { } entry && host.OpenFileAt != null)
        {
            var open = new Button { Content = "Go to", Padding = new(6, 1), MinHeight = 0, FontSize = 10 };
            ToolTip.SetTip(open, $"Open {entry.File.Name} at {field.Variable}. Changing it there changes every layout that uses it.");
            open.Click += async (_, _) => { try { await host.OpenFileAt(entry.File.Path, entry.Raw.Start); } catch (Exception e) { SetStatus(e.Message, true); } };
            row.Children.Add(open);
        }
        if (field.InDocument)
        {
            var source = new Button { Content = "Source", Padding = new(6, 1), MinHeight = 0, FontSize = 10 };
            ToolTip.SetTip(source, "Show this field in the Source view");
            source.Click += (_, _) => ShowInSource(w, field.Key);
            row.Children.Add(source);
        }
        return row;
    }

    // ── Picture ──────────────────────────────────────────────────────────────────────────────────

    private Control PictureCard(UiWidget w, string field)
    {
        var body = new StackPanel { Spacing = 8 };
        var filename = w.String(field) ?? "";
        var row = new DockPanel();
        var browse = new Button { Content = "Browse…", Padding = new(8, 3), MinHeight = 0, FontSize = 12, Margin = new(6, 0, 0, 0) };
        ToolTip.SetTip(browse, "Pick a UI sprite from the project and the game data, with a preview");
        DockPanel.SetDock(browse, Dock.Right); row.Children.Add(browse);
        var box = new TextBox { Text = w.Fields.TryGetValue(field, out var raw) && raw.Variable != null ? raw.Variable : filename, FontFamily = Mono, FontSize = 12, MinHeight = 28 };
        AutomationProperties.SetName(box, "Sprite filename");
        CommitOnEnter(box, text => { if (text != (raw?.Variable ?? filename)) SetField(w, field, text.StartsWith('$') ? UiJsonEditor.Quote(text) : UiJsonEditor.Quote(text.Replace('/', '\\')), $"{w.Name}.{field} = {text}"); });
        inspectorFields["field:" + field] = box;
        row.Children.Add(box);
        body.Children.Add(Labeled(field, row));
        browse.Click += (_, _) => ShowSpritePicker(browse, filename, picked => SetField(w, field, UiJsonEditor.Quote(picked), $"{w.Name}.{field} = {picked}"));
        var sprite = assets?.Sprite(filename);
        if (sprite == null)
        {
            body.Children.Add(Line(filename.Length == 0 ? "No picture set." : $"No sprite at {UiAssets.SpritePath(filename)}.sprite in the project{(assets?.HasGameData == true ? " or the game data" : "; choose the game data folder to read the base game's")}.", Warning, 11));
            return Card("Picture", body);
        }
        body.Children.Add(Line($"{sprite.Relative} · {sprite.FrameWidth} × {sprite.Height}{(sprite.LowEnd ? " (low-end copy, drawn at 2×)" : "")} · {sprite.Frames} frame{(sprite.Frames == 1 ? "" : "s")} · {sprite.Origin}", Muted, 11));
        int current = UiLayout.SpriteFrame(w);
        if (sprite.Frames > 1)
        {
            // Frames as a strip; the ones the widget names (frame, hoveredFrame…) are labelled.
            var named = w.Fields.Keys.Where(k => k.EndsWith("Frame", StringComparison.Ordinal) || k == "frame").Select(k => (Key: k, Value: (int)(w.Number(k) ?? -1))).Where(p => p.Value >= 0).ToList();
            var strip = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 6 };
            for (int k = 0; k < Math.Min(sprite.Frames, 48); k++)
            {
                int index = k;
                var labels = named.Where(n => n.Value == index).Select(n => n.Key).ToList();
                var tile = new StackPanel { Spacing = 2, Width = 64 };
                tile.Children.Add(FrameImage(sprite, index, 64, 48, index == current));
                tile.Children.Add(new TextBlock { Text = labels.Count > 0 ? $"{index} · {string.Join(", ", labels)}" : index.ToString(CultureInfo.InvariantCulture), FontSize = 9, Foreground = index == current ? Own : Muted, TextTrimming = TextTrimming.CharacterEllipsis });
                var button = new Button { Content = tile, Padding = new(2), MinHeight = 0, Background = Brushes.Transparent };
                ToolTip.SetTip(button, $"Frame {index}" + (labels.Count > 0 ? $" ({string.Join(", ", labels)})" : "") + " · click to show this frame (sets frame)");
                button.Click += (_, _) => SetField(w, "frame", index.ToString(CultureInfo.InvariantCulture), $"{w.Name} shows frame {index}");
                strip.Children.Add(button);
            }
            body.Children.Add(strip);
        }
        else body.Children.Add(FrameImage(sprite, 0, 290, 200, false));
        return Card("Picture", body);
    }

    private Control FrameImage(UiSpriteInfo sprite, int frame, double maxWidth, double maxHeight, bool selected)
    {
        var image = new Image { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, MaxWidth = maxWidth, MaxHeight = maxHeight, HorizontalAlignment = HorizontalAlignment.Left };
        void Load() => image.Source = UiBitmaps.Get(sprite, frame, Load);
        Load();
        return new Border
        {
            Child = image, Background = new SolidColorBrush(Color.Parse("#0D0C0B")), BorderBrush = selected ? Own : CardBorder, BorderThickness = new(selected ? 2 : 1),
            CornerRadius = new(3), Padding = new(3), HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 24, MinWidth = 24
        };
    }

    private static List<string>? spriteNames;
    private static string spriteRoots = "";

    /// <summary>Every UI sprite name (as layouts write them) in the project and the game data.</summary>
    private List<string> SpriteNames()
    {
        var roots = new List<string>();
        if (host.Project() is { } project && HdAppearance.LocateFolder(project.Root, "data/hd/global/ui") is { } own) roots.Add(own);
        foreach (var folder in host.GameData()) if (HdAppearance.LocateFolder(folder, "data/hd/global/ui") is { } game) roots.Add(game);
        var key = string.Join("|", roots);
        if (spriteNames != null && key == spriteRoots) return spriteNames;
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
            foreach (var file in Directory.EnumerateFiles(root, "*.sprite", SearchOption.AllDirectories))
            {
                var relative = Storage.Relative(root, file);
                if (relative.StartsWith("items/", StringComparison.OrdinalIgnoreCase)) continue;
                var name = relative[..^(relative.EndsWith(".lowend.sprite", StringComparison.OrdinalIgnoreCase) ? ".lowend.sprite".Length : ".sprite".Length)];
                names.Add(name.Replace('/', '\\'));
            }
        spriteRoots = key;
        return spriteNames = [.. names];
    }

    private void ShowSpritePicker(Control anchor, string current, Action<string> pick)
    {
        List<string> all;
        try { all = SpriteNames(); } catch (IOException e) { SetStatus(e.Message, true); return; }
        if (all.Count == 0) { SetStatus("No UI sprites found. Choose the game data folder (the extracted data holding hd/).", true); return; }
        var search = new TextBox { PlaceholderText = $"Search {all.Count:N0} sprites", Text = Path.GetFileName(current.Replace('\\', '/')) };
        var list = new ListBox { Height = 320, Width = 360 };
        var preview = new ContentControl { Width = 260, Height = 320 };
        var use = new Button { Content = "Use this sprite", IsEnabled = false, Padding = new(10, 4) };
        var panel = new Grid { ColumnDefinitions = new("Auto,10,Auto"), RowDefinitions = new("Auto,8,*,8,Auto"), Margin = new(10) };
        Grid.SetColumnSpan(search, 3); panel.Children.Add(search);
        Grid.SetRow(list, 2); panel.Children.Add(list);
        Grid.SetRow(preview, 2); Grid.SetColumn(preview, 2); panel.Children.Add(preview);
        Grid.SetRow(use, 4); Grid.SetColumn(use, 2); use.HorizontalAlignment = HorizontalAlignment.Right; panel.Children.Add(use);
        void Filter()
        {
            var q = search.Text?.Trim() ?? "";
            list.ItemsSource = (q.Length == 0 ? all : all.Where(n => VisualBuilder.Matches(n.ToLowerInvariant(), q))).Take(400).ToList();
        }
        Filter();
        search.TextChanged += (_, _) => Filter();
        var flyout = new Flyout { Content = panel, Placement = PlacementMode.LeftEdgeAlignedTop };
        list.SelectionChanged += (_, _) =>
        {
            use.IsEnabled = list.SelectedItem is string;
            if (list.SelectedItem is not string name || assets?.Sprite(name) is not { } sprite) { preview.Content = null; return; }
            var info = new StackPanel { Spacing = 6 };
            info.Children.Add(FrameImage(sprite, 0, 250, 250, false));
            info.Children.Add(new TextBlock { Text = $"{sprite.FrameWidth} × {sprite.Height} · {sprite.Frames} frame(s) · {sprite.Origin}", FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap });
            preview.Content = info;
        };
        void Use() { if (list.SelectedItem is string name) { flyout.Hide(); pick(name); } }
        use.Click += (_, _) => Use();
        list.DoubleTapped += (_, _) => Use();
        flyout.ShowAt(anchor);
        Dispatcher.UIThread.Post(() => { search.Focus(); search.SelectAll(); }, DispatcherPriority.Background);
    }

    // ── Text ─────────────────────────────────────────────────────────────────────────────────────

    private Control TextCard(UiWidget w)
    {
        var body = new StackPanel { Spacing = 8 };
        var key = w.Fields.ContainsKey("textString") && !w.Fields.ContainsKey("text") ? "textString" : "text";
        var raw = w.String(key) ?? "";
        var box = new TextBox { Text = raw, AcceptsReturn = false, MinHeight = 28, FontSize = 13 };
        AutomationProperties.SetName(box, "Widget text");
        var shown = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap };
        void Explain(string value)
        {
            if (value.StartsWith('@'))
            {
                var text = assets?.Localize(value[1..]);
                shown.Text = text != null ? $"Shows “{text}”" : $"No string catalog has the key {value[1..]}; the game shows the key itself.";
                shown.Foreground = text != null ? Muted : Warning;
            }
            else { shown.Text = value.Length == 0 ? "Empty: the game fills this in at runtime (a value, a name) or shows nothing." : "Literal text. Use @key to show a localized string."; shown.Foreground = Muted; }
        }
        Explain(raw);
        box.TextChanged += (_, _) => Explain(box.Text ?? "");
        CommitOnEnter(box, text => { if (text != raw) SetField(w, key, UiJsonEditor.Quote(text), $"{w.Name}.{key} = {text}"); });
        inspectorFields["text"] = box;
        body.Children.Add(Labeled(key, box));
        body.Children.Add(shown);
        // The style: a profile style picked by name, or one written out.
        var styleKey = w.Fields.ContainsKey("text/style") ? "text/style" : "style";
        if (scene != null)
        {
            var styles = scene.Profile.Keys.Where(k => k.StartsWith("Style", StringComparison.Ordinal) || k.EndsWith("Style", StringComparison.Ordinal)).Order(StringComparer.OrdinalIgnoreCase).ToList();
            var currentField = w.Fields.GetValueOrDefault(styleKey);
            var choice = new ComboBox { ItemsSource = new[] { "(written in the layout)" }.Concat(styles.Select(s => "$" + s)).ToList(), MinWidth = 250, FontSize = 12 };
            choice.SelectedItem = currentField?.Variable is { } v && styles.Contains(v[1..]) ? v : "(written in the layout)";
            AutomationProperties.SetName(choice, "Text style");
            ToolTip.SetTip(choice, "Pick one of the profile's text styles. Styles written out in the layout are edited in the fields below.");
            choice.SelectionChanged += (_, _) => { if (choice.SelectedItem is string s && s.StartsWith('$') && s != currentField?.Variable) SetField(w, styleKey, UiJsonEditor.Quote(s), $"{w.Name} uses {s}"); };
            body.Children.Add(Labeled(styleKey, choice));
        }
        var style = UiDesignerCanvas.StyleOf(w);
        if (style != null)
        {
            var summary = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var color = UiLayout.Color(style["fontColor"]) ?? UiLayout.Color(w.Value("textColor"));
            if (color is { } c) summary.Children.Add(new Border { Width = 14, Height = 14, CornerRadius = new(2), Background = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B)), BorderBrush = CardBorder, BorderThickness = new(1), VerticalAlignment = VerticalAlignment.Center });
            var face = (style["fontFace"] as JsonValue)?.TryGetValue<string>(out var f) == true ? f : "Exocet";
            var alignment = style["alignment"] as JsonObject;
            summary.Children.Add(new TextBlock
            {
                Text = $"{face} · {UiLayout.Member(style, "pointSize", 26):0.#} pt · {(alignment?["h"] as JsonValue)?.ToString() ?? "left"} / {(alignment?["v"] as JsonValue)?.ToString() ?? "top"}" + (style["dropShadow"] != null ? " · shadow" : "") + ((style["options"] as JsonObject)?["lineWrap"] is JsonValue lw && lw.ToString() == "true" ? " · wraps" : ""),
                Foreground = Muted, FontSize = 11, VerticalAlignment = VerticalAlignment.Center
            });
            body.Children.Add(summary);
        }
        if (!UiFonts.HasGameFont(assets)) body.Children.Add(Line("The game's fonts were not found in the project or the game data; text is set in a stand-in serif face.", Warning, 11));
        return Card("Text", body);
    }

    private Control ColorCard(UiWidget w, string key)
    {
        var body = new StackPanel { Spacing = 8 };
        var color = UiLayout.Color(w.Value(key)) ?? (0, 0, 0, 128);
        // Written the way RectangleWidget colours are: [r, g, b, a] from 0 to 1.
        static string Literal(Color c) => string.Create(CultureInfo.InvariantCulture, $"[ {c.R / 255.0:0.###}, {c.G / 255.0:0.###}, {c.B / 255.0:0.###}, {c.A / 255.0:0.###} ]");
        void Write(Color c)
        {
            if (c.R == color.R && c.G == color.G && c.B == color.B && c.A == color.A) return;
            SetField(w, key, Literal(c), $"{w.Name}.{key} = #{c.R:X2}{c.G:X2}{c.B:X2}{c.A:X2}");
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new Border { Width = 28, Height = 28, CornerRadius = new(3), BorderBrush = CardBorder, BorderThickness = new(1), Background = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B)) });
        var hex = new TextBox { Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}{color.A:X2}", Width = 100, FontFamily = Mono, FontSize = 12, MinHeight = 28 };
        ToolTip.SetTip(hex, "#RRGGBBAA (Enter writes it)");
        AutomationProperties.SetName(hex, "Colour");
        CommitOnEnter(hex, text =>
        {
            var t = text.Trim().TrimStart('#');
            if (t.Length == 6) t += "FF";
            if (t.Length != 8 || !uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) { SetStatus("Write the colour as #RRGGBB or #RRGGBBAA.", true); return; }
            Write(Color.FromArgb((byte)v, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8)));
        });
        inspectorFields["colour"] = hex;
        row.Children.Add(hex);
        row.Children.Add(new TextBlock { Text = $"{color.A * 100 / 255}% opaque", Foreground = Muted, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        body.Children.Add(row);
        var swatches = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 4, LineSpacing = 4 };
        foreach (var preset in new[] { "#00000080", "#000000CC", "#000000F2", "#FFFFFF33", "#C7B377FF", "#78622FFF", "#FC4646FF", "#6E6EFFFF" })
        {
            var c = Color.Parse(preset[..7]); c = Color.FromArgb(Convert.ToByte(preset[7..], 16), c.R, c.G, c.B);
            var b = new Button { Width = 22, Height = 22, MinWidth = 0, MinHeight = 0, Padding = new(0), Background = new SolidColorBrush(c), BorderBrush = CardBorder, BorderThickness = new(1) };
            ToolTip.SetTip(b, preset);
            b.Click += (_, _) => Write(c);
            swatches.Children.Add(b);
        }
        body.Children.Add(swatches);
        return Card("Colour", body);
    }

    // ── All fields ───────────────────────────────────────────────────────────────────────────────

    private Control FieldsCard(UiWidget w)
    {
        var body = new StackPanel { Spacing = 10 };
        foreach (var field in w.Fields.Values)
        {
            var row = new StackPanel { Spacing = 3 };
            var top = new DockPanel();
            var badge = new TextBlock
            {
                Text = field.Variable != null ? field.Variable : field.InDocument ? "this file" : field.File.Name,
                Foreground = field.InDocument && field.Variable == null ? Own : Inherited, FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new(6, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 170
            };
            ToolTip.SetTip(badge, field.Variable != null ? $"{field.Variable} from {field.VariableEntry?.File.Name ?? "no profile"} · written in {(field.InDocument ? "this file" : field.File.Name)}" : $"Written in {(field.InDocument ? "this file" : $"{field.File.Name} ({field.File.Origin})")}");
            bool inheritedToo = w.Definitions.Any(d => !d.File.IsDocument && d.Node["fields"]?[field.Key] != null);
            if (field.InDocument)
            {
                var remove = new Button { Content = inheritedToo ? "⟲" : "✕", Width = 22, Height = 20, MinWidth = 0, MinHeight = 0, Padding = new(0), FontSize = 11, Background = Brushes.Transparent, Foreground = Muted };
                ToolTip.SetTip(remove, inheritedToo ? "Remove this file's value: the inherited one applies again" : "Remove this field from the file");
                var key = field.Key;
                remove.Click += (_, _) => RemoveField(w, key);
                DockPanel.SetDock(remove, Dock.Right); top.Children.Add(remove);
            }
            DockPanel.SetDock(badge, Dock.Right); top.Children.Add(badge);
            top.Children.Add(new TextBlock { Text = field.Key, Foreground = WhiteBrush, FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(top);
            var literal = field.RawText.Contains('\n') ? field.RawText : field.RawText;
            var box = new TextBox { Text = literal, FontFamily = Mono, FontSize = 11, AcceptsReturn = literal.Contains('\n'), TextWrapping = TextWrapping.Wrap, MinHeight = 26, Padding = new(6, 3) };
            AutomationProperties.SetName(box, "Field " + field.Key);
            ToolTip.SetTip(box, "The value as JSON. Enter writes it (Ctrl+Enter in multi-line values); it must parse.");
            var fieldKey = field.Key;
            CommitOnEnter(box, text => { if (text != literal) SetField(w, fieldKey, text.Trim()); }, multiline: box.AcceptsReturn);
            inspectorFields["field:" + field.Key] = box;
            row.Children.Add(box);
            if (field.Variable != null && field.Value != null)
                row.Children.Add(new TextBlock { Text = "= " + UiJsonEditor.Literal(field.Value), Foreground = Muted, FontSize = 10, FontFamily = Mono, TextWrapping = TextWrapping.Wrap, MaxHeight = 60 });
            body.Children.Add(row);
        }
        body.Children.Add(AddFieldRow(w));
        return Card($"Fields ({w.Fields.Count})", body, note: "Every field as JSON. Values from a parent layout are written into this file when you change them.");
    }

    private static Dictionary<string, SortedSet<string>>? knownFields;
    private static Task? knownFieldsLoading;
    /// <summary>The background read of field suggestions (tests await it; it never faults).</summary>
    internal static Task KnownFieldsLoading => knownFieldsLoading ?? Task.CompletedTask;

    /// <summary>Field names the game's layouts use, by widget type, read once from every layout in the background.</summary>
    private void LoadKnownFields()
    {
        if (knownFields != null || knownFieldsLoading != null) return;
        // Files only, without the open-tab lookup Sources() adds: that reads the window's tabs, which only the UI thread may touch.
        // Suggestions need field names, not another tab's unsaved edits.
        var sources = new UiLayoutSources(host.Project(), host.GameData(), Document.FilePath);
        knownFieldsLoading = Task.Run(() =>
        {
            var map = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            IReadOnlyList<string> names;
            try { names = sources.Available(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { names = []; }
            foreach (var name in names)
            {
                try
                {
                    if (sources.Open(name) is not { } file) continue;
                    void Walk(UiJsonNode node)
                    {
                        if (node.IsObject && node["type"]?.String is { } type && node["fields"] is { IsObject: true } fields)
                        {
                            if (!map.TryGetValue(type, out var set)) map[type] = set = new(StringComparer.Ordinal);
                            foreach (var m in fields.Members) set.Add(m.Key);
                        }
                        foreach (var child in node.Members.Select(m => m.Value).Concat(node.Items)) if (child.IsObject || child.IsArray) Walk(child);
                    }
                    Walk(file.Root);
                }
                catch (Exception e) when (e is UiJsonException or IOException or UnauthorizedAccessException) { }
            }
            knownFields = map;
        });
    }

    private Control AddFieldRow(UiWidget w)
    {
        LoadKnownFields();
        var row = new DockPanel { Margin = new(0, 4, 0, 0) };
        var add = new Button { Content = "Add", Padding = new(8, 3), MinHeight = 0, FontSize = 12, Margin = new(6, 0, 0, 0) };
        DockPanel.SetDock(add, Dock.Right); row.Children.Add(add);
        var key = new AutoCompleteBox
        {
            PlaceholderText = "field", Width = 130, FilterMode = AutoCompleteFilterMode.ContainsOrdinal, MinimumPrefixLength = 0, MaxDropDownHeight = 280,
            ItemsSource = (knownFields?.GetValueOrDefault(w.Type) ?? []).Where(k => !w.Fields.ContainsKey(k)).ToList()
        };
        AutomationProperties.SetName(key, "New field name");
        ToolTip.SetTip(key, $"A field name. Suggestions are the fields the game's own layouts give a {w.Type}.");
        DockPanel.SetDock(key, Dock.Left); row.Children.Add(key);
        var value = new TextBox { PlaceholderText = "value (JSON)", FontFamily = Mono, FontSize = 11, Margin = new(6, 0, 0, 0) };
        AutomationProperties.SetName(value, "New field value");
        row.Children.Add(value);
        void Add()
        {
            var k = key.Text?.Trim() ?? ""; var v = value.Text?.Trim() ?? "";
            if (k.Length == 0 || v.Length == 0) { SetStatus("Give the new field a name and a JSON value (\"text\", 12, true, { \"x\": 0 }).", true); return; }
            if (!IsJson(v)) v = UiJsonEditor.Quote(v);
            SetField(w, k, v, $"Added {k} to {w.Name}");
        }
        add.Click += (_, _) => Add();
        value.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Add(); e.Handled = true; } };
        return row;
    }

    private static bool IsJson(string text) { try { UiJsonParser.Parse(text); return true; } catch (UiJsonException) { return false; } }

    private Control ActionsCard(UiWidget w)
    {
        var body = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 6 };
        Button Act(string label, string tip, Action action, bool enabled = true)
        {
            var b = new Button { Content = label, Padding = new(8, 3), MinHeight = 0, FontSize = 12, IsEnabled = enabled };
            ToolTip.SetTip(b, tip); AutomationProperties.SetName(b, label);
            b.Click += (_, _) => { try { action(); } catch (Exception e) { SetStatus(e.Message, true); } };
            body.Children.Add(b);
            return b;
        }
        Act("Duplicate", "Copy it as a new sibling under a new name (Ctrl+D)", () => Duplicate(w), w.Parent != null);
        var add = Act("Add child ▾", "Add a new widget inside this one", () => { });
        add.Click += (_, _) =>
        {
            var menu = new ContextMenu { ItemsSource = NewWidgets.Select(k => { var item = new MenuItem { Header = k.Label }; item.Click += (_, _) => AddChild(w, k); return item; }).ToArray() };
            add.ContextMenu = menu; menu.Closed += (_, _) => add.ContextMenu = null; menu.Open(add);
        };
        Act("Draw earlier", "Move it before its previous sibling in this file (drawn below it)", () => Move(w, -1), w.Parent != null && w.InDocument);
        Act("Draw later", "Move it after its next sibling in this file (drawn above it)", () => Move(w, 1), w.Parent != null && w.InDocument);
        Act("Show in source", "Open the Source view on this widget's entry", () => ShowInSource(w), w.InDocument);
        Act("Delete", w.Inherited && w.Parent != null ? $"It comes from {w.Definitions[0].File.Name}; this file can override it but not remove it" : "Remove it from this file (Del)", () => Delete(w), w.Parent != null && !w.Inherited);
        return Card("Actions", body);
    }

    // ── Building blocks ──────────────────────────────────────────────────────────────────────────

    private static StackPanel Panel() => new() { Spacing = 12, Margin = new(10, 6, 14, 18) };

    private static TextBlock Line(string text, IBrush brush, double size = 12) => new() { Text = text, Foreground = brush, FontSize = size, TextWrapping = TextWrapping.Wrap };

    private static Control Labeled(string label, Control input)
    {
        var panel = new StackPanel { Spacing = 3 };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = Muted });
        panel.Children.Add(input);
        return panel;
    }

    private static Border Card(string heading, Control body, string? note = null)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = heading.ToUpperInvariant(), Foreground = Heading, FontSize = 11, FontWeight = FontWeight.SemiBold });
        if (note != null) panel.Children.Add(new TextBlock { Text = note, Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(body);
        return new Border { Background = CardBackground, BorderBrush = CardBorder, BorderThickness = new(1), CornerRadius = new(6), Padding = new(12, 10), Child = panel };
    }

    /// <summary>A number field: Enter or leaving it writes the value; Up and Down step it (Shift ×10).</summary>
    private Control Number(string label, string name, JsonNode? value, Action<double> commit, string tip, bool disabled = false, string placeholder = "0")
    {
        var current = UiLayout.Number(value);
        var box = new TextBox { Text = current?.ToString("0.###", CultureInfo.InvariantCulture) ?? "", PlaceholderText = placeholder, Width = 62, MinHeight = 28, IsEnabled = !disabled, FontSize = 12 };
        if (value is JsonValue v && v.TryGetValue<string>(out var s)) { box.Text = s; box.IsEnabled = false; }
        ToolTip.SetTip(box, tip);
        AutomationProperties.SetName(box, label);
        void Write()
        {
            var text = box.Text?.Trim() ?? "";
            if (text.Length == 0 || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) { if (text.Length > 0) SetStatus($"{label}: {text} is not a number.", true); return; }
            if (current is { } c && Math.Abs(c - number) < 1e-9) return;
            commit(number);
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Write(); e.Handled = true; }
            else if (e.Key is Key.Up or Key.Down)
            {
                double step = (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1) * (name.EndsWith("scale", StringComparison.Ordinal) || name.StartsWith("anchor", StringComparison.Ordinal) ? 0.05 : 1);
                double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n);
                box.Text = Math.Round(n + (e.Key == Key.Up ? step : -step), 3).ToString("0.###", CultureInfo.InvariantCulture);
                Write(); e.Handled = true;
            }
        };
        box.LostFocus += (_, _) => { Write(); AfterEdit(); };
        inspectorFields[name] = box;
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 10, Foreground = Muted });
        panel.Children.Add(box);
        return panel;
    }

    /// <summary>Text fields write on Enter (Ctrl+Enter when multi-line) and when left; Escape puts the value back.</summary>
    private void CommitOnEnter(TextBox box, Action<string> commit, bool multiline = false)
    {
        var original = box.Text ?? "";
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && (!multiline || e.KeyModifiers.HasFlag(KeyModifiers.Control))) { commit(box.Text ?? ""); e.Handled = true; }
            else if (e.Key == Key.Escape) { box.Text = original; e.Handled = true; canvas.Focus(); }
        };
        box.LostFocus += (_, _) => { if ((box.Text ?? "") != original) commit(box.Text ?? ""); AfterEdit(); };
    }

    /// <summary>Focus left an inspector field: show anything that changed while it was being typed in.</summary>
    private void AfterEdit()
    {
        if (!inspectorPending) return;
        Dispatcher.UIThread.Post(() => { if (inspectorPending && !inspector.IsKeyboardFocusWithin) RefreshInspector(); }, DispatcherPriority.Background);
    }
}

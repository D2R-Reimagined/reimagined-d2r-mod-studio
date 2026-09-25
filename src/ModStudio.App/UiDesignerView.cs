using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Input.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ModStudio.Core;

namespace ModStudio.App;

/// <summary>A row of the designer's widget tree: a widget at its depth, and whether its children are shown.</summary>
internal sealed record UiTreeRow(UiWidget Widget, int Depth, bool HasChildren, bool Expanded, bool Hidden);

/// <summary>
/// The UI Designer: the Visual Builder of a layout file under global/ui/layouts. The layout is drawn as the game would draw
/// it (the basedOn parent merged in, profile $variables applied, real sprites and fonts) on the reference screen, beside a
/// tree of its widgets and an inspector. Every change is written into the document as the smallest text edit that makes it,
/// so comments and formatting survive, Source view shows it at once, and Undo, Save and the dirty marker are the document's.
/// </summary>
internal sealed partial class UiDesignerView : Grid, IVisualBuilder
{
    private static readonly IBrush WhiteBrush = new SolidColorBrush(Color.Parse("#E6E6E6")), Muted = new SolidColorBrush(Color.Parse("#A8A29A")),
        Heading = new SolidColorBrush(Color.Parse("#D8BC86")), CardBackground = new SolidColorBrush(Color.Parse("#1D1E20")),
        CardBorder = new SolidColorBrush(Color.Parse("#34363A")), Inherited = new SolidColorBrush(Color.Parse("#7FB2E0")),
        Own = new SolidColorBrush(Color.Parse("#D8BC86")), Warning = new SolidColorBrush(Color.Parse("#E8A04C")), ErrorBrush = Brushes.Salmon;
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Menlo, monospace");

    private readonly EditorPane pane;
    private readonly VisualBuilderHost host;
    private Document Document => pane.Document;
    private readonly UiDesignerCanvas canvas = new();
    private readonly ListBox tree = new() { SelectionMode = SelectionMode.Single, Background = Brushes.Transparent };
    private readonly ObservableCollection<UiTreeRow> rows = [];
    private readonly TextBox treeFilter = new() { PlaceholderText = "Filter widgets", FontSize = 12 };
    private readonly ContentControl inspector = new();
    private readonly ScrollViewer inspectorScroll;
    private readonly ComboBox modeChoice = new() { MinWidth = 150, ItemsSource = UiLayoutMode.All.Select(m => m.Label).ToArray() };
    private readonly ComboBox screenChoice = new() { MinWidth = 70, ItemsSource = UiScreen.All.Select(s => s.Label).ToArray(), SelectedIndex = 0 };
    private readonly TextBlock zoomLabel = new() { Width = 52, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    private readonly TextBlock statusLine = new() { FontSize = 11, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock pointerLine = new() { FontSize = 11, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new(12, 0, 0, 0) };
    private readonly Button issuesButton = new() { Padding = new(8, 1), MinHeight = 0, FontSize = 11, IsVisible = false };
    private readonly Border errorBanner = new() { IsVisible = false, Background = new SolidColorBrush(Color.FromArgb(235, 60, 24, 20)), BorderBrush = Brushes.Salmon, BorderThickness = new(1), CornerRadius = new(4), Padding = new(12, 8), Margin = new(16), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock errorText = new() { Foreground = WhiteBrush, TextWrapping = TextWrapping.Wrap, MaxWidth = 620 };
    private readonly ContentControl gameDataNotice = new();
    private readonly HashSet<string> collapsed = new(StringComparer.Ordinal);
    private readonly List<string> referenceNames = [];

    private UiScene? scene;
    private UiAssets? assets;
    private UiLayoutMode? mode;
    private string? resolvedText;
    private (string? Project, int Workspace, string Data) assetContext;
    private bool stale = true, syncingTree, resolveQueued;
    private string status = "";
    private bool statusError;

    internal UiScene? Scene => scene;
    internal UiDesignerCanvas Canvas => canvas;
    internal UiWidget? Selected => canvas.Selected;
    internal IReadOnlyList<UiTreeRow> TreeRows => rows;
    internal string StatusText => status;
    internal Control InspectorContent => inspector;

    public UiDesignerView(EditorPane pane, VisualBuilderHost host)
    {
        this.pane = pane; this.host = host;
        RowDefinitions = new("Auto,*,Auto");
        ColumnDefinitions = new("250,4,*,4,340");
        Background = new SolidColorBrush(Color.Parse("#161718"));

        // Toolbar
        var bar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new(8, 6), ItemSpacing = 6, LineSpacing = 4 };
        bar.Children.Add(Caption("Profile"));
        ToolTip.SetTip(modeChoice, "Which profile supplies the $variables: PC or controller, normal or large font. The layout's own folder picks the default.");
        AutomationProperties.SetName(modeChoice, "UI Designer profile");
        bar.Children.Add(modeChoice);
        bar.Children.Add(Caption("Screen"));
        ToolTip.SetTip(screenChoice, "Aspect ratio to preview at. HD layouts are authored for a 2160 pixel tall screen; anchors follow its width.");
        bar.Children.Add(screenChoice);
        bar.Children.Add(Separator());
        bar.Children.Add(SmallButton("−", "Zoom out", () => canvas.ZoomAt(1 / 1.25)));
        bar.Children.Add(zoomLabel);
        bar.Children.Add(SmallButton("+", "Zoom in", () => canvas.ZoomAt(1.25)));
        bar.Children.Add(SmallButton("Screen", "Show the whole screen (F)", canvas.Fit));
        bar.Children.Add(SmallButton("Panel", "Zoom onto the layout's panel (P)", canvas.FitPanel));
        bar.Children.Add(SmallButton("Selection", "Zoom to the selected widget (Z)", ZoomToSelection));
        bar.Children.Add(SmallButton("1:1", "One layout pixel per screen pixel (1)", () => canvas.SetZoom(1)));
        bar.Children.Add(Separator());
        bar.Children.Add(Caption("State"));
        var state = new ComboBox { ItemsSource = new[] { "Normal", "Hovered", "Pressed", "Disabled", "Toggled" }, SelectedIndex = 0, MinWidth = 100 };
        ToolTip.SetTip(state, "Which frame buttons show: their hovered, pressed, disabled or toggled frame. In Normal, the button under the pointer shows its hovered frame, as in game.");
        AutomationProperties.SetName(state, "Button state");
        state.SelectionChanged += (_, _) => { canvas.PreviewState = (state.SelectedItem as string ?? "Normal").ToLowerInvariant(); canvas.InvalidateVisual(); };
        bar.Children.Add(state);
        bar.Children.Add(Separator());
        bar.Children.Add(Toggle("Outlines", "Outline every widget: gold for widgets this file defines, dashed blue for ones it inherits", canvas.ShowOutlines, v => canvas.ShowOutlines = v));
        bar.Children.Add(Toggle("Names", "Label every widget with its name", canvas.ShowNames, v => canvas.ShowNames = v));
        bar.Children.Add(Toggle("Snap", "Snap edges and centres to siblings and the parent while dragging (hold Ctrl to place freely)", canvas.Snap, v => canvas.Snap = v));
        var layers = new Button { Content = "Layers ▾", Padding = new(8, 3), MinHeight = 0, FontSize = 12 };
        ToolTip.SetTip(layers, "Draw other layouts underneath, dimmed, for context: the HUD under an inventory, a stash beside it.");
        layers.Click += (_, _) => ShowLayersMenu(layers);
        bar.Children.Add(layers);
        bar.Children.Add(gameDataNotice);
        SetColumnSpan(bar, 5); Children.Add(bar);

        // Tree
        var side = new DockPanel { Margin = new(8, 0, 4, 6) };
        var treeHeading = new TextBlock { Text = "WIDGETS", Foreground = Heading, FontSize = 11, Margin = new(2, 2, 0, 6) };
        DockPanel.SetDock(treeHeading, Dock.Top); side.Children.Add(treeHeading);
        AutomationProperties.SetName(treeFilter, "UI Designer widget filter");
        DockPanel.SetDock(treeFilter, Dock.Top); side.Children.Add(treeFilter);
        tree.Margin = new(0, 6, 0, 0);
        // Compact rows: a layout can have hundreds of widgets.
        tree.Styles.Add(new Style(x => x.OfType<ListBoxItem>()) { Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(2, 0)), new Setter(MinHeightProperty, 0d) } });
        tree.ItemsSource = rows;
        tree.ItemTemplate = new FuncDataTemplate<UiTreeRow>((row, _) => row == null ? new TextBlock() : TreeItem(row));
        tree.SelectionChanged += (_, _) => { if (!syncingTree && tree.SelectedItem is UiTreeRow row) Select(row.Widget, fromTree: true); };
        side.Children.Add(tree);
        SetRow(side, 1); Children.Add(side);
        treeFilter.TextChanged += (_, _) => RebuildTree();

        var leftSplitter = new GridSplitter { Background = CardBorder, ResizeDirection = GridResizeDirection.Columns }; SetRow(leftSplitter, 1); SetColumn(leftSplitter, 1); Children.Add(leftSplitter);

        // Canvas
        var stage = new Grid();
        stage.Children.Add(canvas);
        errorBanner.Child = errorText;
        stage.Children.Add(errorBanner);
        SetRow(stage, 1); SetColumn(stage, 2); Children.Add(stage);
        AutomationProperties.SetName(canvas, "UI Designer canvas");

        var rightSplitter = new GridSplitter { Background = CardBorder, ResizeDirection = GridResizeDirection.Columns }; SetRow(rightSplitter, 1); SetColumn(rightSplitter, 3); Children.Add(rightSplitter);

        // Inspector
        inspectorScroll = new ScrollViewer { Content = inspector, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        SetRow(inspectorScroll, 1); SetColumn(inspectorScroll, 4); Children.Add(inspectorScroll);

        // Status line
        var foot = new DockPanel { Margin = new(10, 4) };
        DockPanel.SetDock(issuesButton, Dock.Right); foot.Children.Add(issuesButton);
        DockPanel.SetDock(pointerLine, Dock.Right); foot.Children.Add(pointerLine);
        foot.Children.Add(statusLine);
        SetRow(foot, 2); SetColumnSpan(foot, 5); Children.Add(foot);
        issuesButton.Click += (_, _) => ShowIssues();

        canvas.SelectionRequested += w => Select(w);
        canvas.Committed += CommitCanvasEdit;
        canvas.MenuRequested += (w, _) => ShowContextMenu(w, canvas);
        canvas.EditRequested += w => { Select(w); FocusInspectorField(w.String("text") != null ? "text" : "rect.x"); };
        canvas.ViewChanged += UpdatePointer;
        modeChoice.SelectionChanged += (_, _) => { if (modeChoice.SelectedIndex >= 0 && UiLayoutMode.All[modeChoice.SelectedIndex] != mode) { mode = UiLayoutMode.All[modeChoice.SelectedIndex]; Resolve(force: true); } };
        screenChoice.SelectionChanged += (_, _) => { Resolve(force: true); Dispatcher.UIThread.Post(canvas.Fit, DispatcherPriority.Background); };
        AddHandler(KeyDownEvent, OnKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        Document.Changed += DocumentChanged;
        pane.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty && Visible && stale) Shown(); };
        SetStatus("Click a widget to select it · drag to move · handles resize · arrows nudge (Shift ×10) · Alt+click picks what is underneath · wheel zooms, middle-drag or Space+drag pans");
    }

    private bool Visible => pane.IsVisible && pane.VisualBuilderVisible;

    // ── IVisualBuilder ───────────────────────────────────────────────────────────────────────────

    public void Shown()
    {
        stale = false;
        Resolve(force: true);
        // Pick up where the Source view's caret was.
        if (canvas.Selected == null && scene != null && pane.Source.CaretOffset > 0 && scene.AtOffset(pane.Source.CaretOffset) is { } atCaret) Select(atCaret);
        Dispatcher.UIThread.Post(() => canvas.Focus(), DispatcherPriority.Background);
    }

    public void Invalidate()
    {
        assets = null;
        if (!Visible) { stale = true; return; }
        Resolve(force: true);
    }

    public bool SelectWhere(string column, string value)
    {
        var target = scene?.Widgets.FirstOrDefault(w => w.Path == value || w.Name == value);
        if (target == null) return false;
        Select(target); return true;
    }

    private void DocumentChanged()
    {
        if (!Visible) { stale = true; return; }
        // SetRaw and ApplySource both raise Changed; resolve once for the pair.
        if (resolveQueued) return;
        resolveQueued = true;
        Dispatcher.UIThread.Post(() => { resolveQueued = false; Resolve(); }, DispatcherPriority.Background);
    }

    // ── Resolving ────────────────────────────────────────────────────────────────────────────────

    internal void Resolve(bool force = false)
    {
        var text = Document.Text;
        if (!force && text == resolvedText && scene != null) return;
        var project = host.Project(); var gameData = host.GameData();
        var context = (project?.Root, host.Workspace(), string.Join("|", gameData));
        if (assets == null || context != assetContext) { assets = new UiAssets(project, gameData, Document.FilePath); assetContext = context; }
        mode ??= UiLayoutMode.For(UiLayoutSources.RelativeName(Document.FilePath));
        if (modeChoice.SelectedIndex != Array.IndexOf(UiLayoutMode.All, mode)) modeChoice.SelectedIndex = Array.IndexOf(UiLayoutMode.All, mode);
        var screen = UiScreen.All[Math.Max(0, screenChoice.SelectedIndex)];
        try
        {
            var sources = Sources();
            var resolved = UiLayout.Resolve(sources, Document.FilePath, text, mode, screen, f => assets.Sprite(f) is { } s ? (s.Width, s.DrawHeight) : null);
            scene = resolved; resolvedText = text;
            errorBanner.IsVisible = false;
            var selectedPath = canvas.Selected?.Path;
            canvas.Assets = assets;
            canvas.Localize = key => assets.Localize(key);
            canvas.References = [.. referenceNames.Select(name => ResolveReference(sources, name, screen)).OfType<UiScene>()];
            canvas.Scene = resolved;
            RebuildTree();
            if (selectedPath != null && canvas.Selected == null && resolved.Find(selectedPath) is { } again) canvas.Select(again);
            SyncTreeSelection();
            RefreshInspector();
            UpdateIssues();
            UpdateGameDataNotice();
        }
        catch (Exception e) when (e is UiJsonException or InvalidDataException or IOException)
        {
            resolvedText = null;
            errorText.Text = e is UiJsonException json
                ? $"The layout's JSON does not parse: {json.Message}. The designer shows the last good version; fix it in the Source view (or Undo)."
                : "The layout cannot be drawn: " + e.Message;
            errorBanner.IsVisible = true;
        }
        UpdatePointer();
    }

    private UiLayoutSources Sources() => new(host.Project(), host.GameData(), Document.FilePath, path => host.FindFile?.Invoke(path) is { } open && open != Document ? open.Text : null);

    private UiScene? ResolveReference(UiLayoutSources sources, string name, UiScreen screen)
    {
        try
        {
            var file = sources.Open(name);
            if (file == null) return null;
            return UiLayout.Resolve(sources, file.Path, file.Text, mode!, screen, f => assets!.Sprite(f) is { } s ? (s.Width, s.DrawHeight) : null);
        }
        catch (Exception e) when (e is UiJsonException or InvalidDataException or IOException) { SetStatus($"Layer {name} cannot be drawn: {e.Message}", true); return null; }
    }

    private void UpdateGameDataNotice()
    {
        if (assets?.HasGameData == true || scene?.Widgets.Any(w => UiLayout.SpriteField(w) is { } f && assets?.Sprite(w.String(f)) == null) != true) { gameDataNotice.Content = null; return; }
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = "Pictures the mod does not ship need the game data:", Foreground = Warning, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(Bitmaps.ChooseGameDataButton(host, (m, e) => SetStatus(m, e)));
        gameDataNotice.Content = panel;
    }

    private void UpdateIssues()
    {
        var issues = scene?.Issues ?? [];
        issuesButton.IsVisible = issues.Count > 0;
        issuesButton.Content = $"⚠ {issues.Count} issue{(issues.Count == 1 ? "" : "s")}";
        issuesButton.Foreground = Warning;
        ToolTip.SetTip(issuesButton, string.Join("\n", issues.Take(20)));
    }

    private void ShowIssues()
    {
        if (scene == null || scene.Issues.Count == 0) return;
        var list = new StackPanel { Spacing = 4, Margin = new(12), MaxWidth = 640 };
        foreach (var issue in scene.Issues) list.Children.Add(new SelectableTextBlock { Text = "• " + issue, TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        var flyout = new Flyout { Content = new ScrollViewer { Content = list, MaxHeight = 400 }, Placement = PlacementMode.TopEdgeAlignedRight };
        flyout.ShowAt(issuesButton);
    }

    // ── Selection and tree ───────────────────────────────────────────────────────────────────────

    internal void Select(UiWidget? widget, bool fromTree = false)
    {
        if (widget == canvas.Selected) return;
        canvas.Select(widget);
        // Make sure the tree shows it: its ancestors expanded.
        for (var at = widget?.Parent; at != null; at = at.Parent) collapsed.Remove(at.Path);
        if (!fromTree) { RebuildTree(); SyncTreeSelection(); }
        RefreshInspector(force: true);
        UpdatePointer();
    }

    private void SyncTreeSelection()
    {
        syncingTree = true;
        try
        {
            var row = rows.FirstOrDefault(r => r.Widget.Path == canvas.Selected?.Path);
            tree.SelectedItem = row;
            if (row != null) tree.ScrollIntoView(row);
        }
        finally { syncingTree = false; }
    }

    private void RebuildTree()
    {
        if (scene == null) return;
        var filter = treeFilter.Text?.Trim() ?? "";
        var next = new List<UiTreeRow>();
        void Add(UiWidget w, int depth)
        {
            bool expanded = !collapsed.Contains(w.Path);
            next.Add(new(w, depth, w.Children.Count > 0, expanded, canvas.Hidden.Contains(w.Path)));
            if (expanded) foreach (var c in w.Children) Add(c, depth + 1);
        }
        if (filter.Length == 0) Add(scene.Root, 0);
        else
            foreach (var w in scene.Widgets.Where(w => VisualBuilder.Matches((w.Name + " " + w.Type).ToLowerInvariant(), filter)))
                next.Add(new(w, 0, false, false, canvas.Hidden.Contains(w.Path)));
        syncingTree = true;
        try
        {
            // Replace rows in place when the tree kept its shape, so the list keeps its scroll position.
            if (next.Count == rows.Count && next.Select(r => r.Widget.Path).SequenceEqual(rows.Select(r => r.Widget.Path)))
            { for (int k = 0; k < next.Count; k++) if (!Same(rows[k], next[k])) rows[k] = next[k]; }
            else { rows.Clear(); foreach (var r in next) rows.Add(r); }
        }
        finally { syncingTree = false; }
        SyncTreeSelection();
        static bool Same(UiTreeRow a, UiTreeRow b) => ReferenceEquals(a.Widget, b.Widget) && a.Expanded == b.Expanded && a.Hidden == b.Hidden && a.Depth == b.Depth;
    }

    private static readonly IBrush EyeBrush = new SolidColorBrush(Color.Parse("#6F6A62"));

    private Control TreeItem(UiTreeRow row)
    {
        var w = row.Widget;
        var panel = new DockPanel { Margin = new(row.Depth * 14, 0, 0, 0), Background = Brushes.Transparent, Height = 22 };
        var chevron = new TextBlock
        {
            Text = row.HasChildren ? (row.Expanded ? "▾" : "▸") : "", Width = 16, FontSize = 11, Foreground = Muted, Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, Cursor = row.HasChildren ? new Cursor(StandardCursorType.Hand) : null
        };
        if (row.HasChildren) chevron.PointerPressed += (_, e) => { if (!collapsed.Remove(w.Path)) collapsed.Add(w.Path); RebuildTree(); e.Handled = true; };
        DockPanel.SetDock(chevron, Dock.Left); panel.Children.Add(chevron);
        var eye = new TextBlock
        {
            Text = row.Hidden ? "◌" : "●", Width = 18, FontSize = 9, Foreground = row.Hidden ? Muted : EyeBrush, Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(eye, row.Hidden ? "Show on the canvas (view only; the file is not changed)" : "Hide on the canvas to see what is behind it (view only; the file is not changed)");
        eye.PointerPressed += (_, e) => { if (!canvas.Hidden.Remove(w.Path)) canvas.Hidden.Add(w.Path); canvas.InvalidateVisual(); RebuildTree(); e.Handled = true; };
        DockPanel.SetDock(eye, Dock.Right); panel.Children.Add(eye);
        panel.DoubleTapped += (_, _) => { Select(w); ZoomToSelection(); };
        panel.ContextRequested += (_, e) => { Select(w); ShowContextMenu(w, panel); e.Handled = true; };
        var text = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = Glyph(w), Foreground = w.InDocument ? Own : Inherited, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Width = 12 });
        text.Children.Add(new TextBlock { Text = w.Name, Foreground = row.Hidden ? Muted : WhiteBrush, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        text.Children.Add(new TextBlock { Text = ShortType(w.Type), Foreground = Muted, FontSize = 10, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(text);
        ToolTip.SetTip(panel, $"{w.Type} · {w.Path}\n{Provenance(w)}");
        return panel;
    }

    /// <summary>A one-character hint of what a widget is.</summary>
    internal static string Glyph(UiWidget w) => w.Type switch
    {
        "TextBoxWidget" or "InputTextBoxWidget" or "TextBoxWithLinksWidget" => "T",
        var t when t.Contains("Button", StringComparison.Ordinal) => "◉",
        "ImageWidget" or "AnimatedImageWidget" or "GrowableImageWidget" or "GridImageWidget" or "NineTileImageWidget" => "▣",
        "RectangleWidget" => "■",
        "InventoryGridWidget" or "InventorySlotWidget" => "▦",
        _ when w.Parent == null => "◆",
        _ when w.Children.Count > 0 => "▢",
        _ => "·"
    };

    internal static string ShortType(string type) => type.EndsWith("Widget", StringComparison.Ordinal) && type.Length > 6 ? type[..^6] : type;

    internal static string Provenance(UiWidget w)
    {
        if (w.Parent == null) return w.Definitions.Count > 1 ? $"Root · based on {string.Join(" → ", w.Definitions.Where(d => !d.File.IsDocument).Select(d => d.File.Name))}" : "Root of this file";
        var inherited = w.Definitions.Where(d => !d.File.IsDocument).Select(d => d.File).ToList();
        if (inherited.Count == 0) return "Added by this file";
        var from = string.Join(", ", inherited.Select(f => $"{f.Name} ({f.Origin})"));
        int own = w.Fields.Values.Count(f => f.InDocument);
        return !w.InDocument ? $"From {from} · not overridden here"
            : own == 0 ? $"From {from} · listed here without changes" : $"From {from} · this file overrides {own} field{(own == 1 ? "" : "s")}";
    }

    // ── Editing ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Applies an edit to the document text. The edit gets the current text and returns the new one; the document records it
    /// as one undo step, and the designer re-resolves from the document's Changed event.
    /// </summary>
    internal bool Apply(Func<string, string> edit, string description)
    {
        canvas.CommitNudge();
        try
        {
            var before = Document.Text;
            var after = edit(before);
            if (after == before) return false;
            // Refuse an edit that would leave the file unreadable; nothing is written.
            UiJsonParser.Parse(after);
            Document.SetRaw(after); Document.ApplySource();
            SetStatus(description + " · Undo reverts it · Save writes the file");
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or UiJsonException or InvalidOperationException or ArgumentException)
        {
            SetStatus(e.Message, true);
            return false;
        }
    }

    private void CommitCanvasEdit(UiCanvasEdit edit)
    {
        var w = edit.Widget;
        var changes = new List<(string, double)>();
        if (edit.X is { } x) changes.Add(("x", x));
        if (edit.Y is { } y) changes.Add(("y", y));
        if (edit.Width is { } width) changes.Add(("width", width));
        if (edit.Height is { } height) changes.Add(("height", height));
        if (edit.Scale is { } s) changes.Add(("scale", s));
        var note = RectNote(w);
        var verb = edit.Kind switch { "move" => "Moved", "resize" => "Resized", _ => "Scaled" };
        Apply(text => UiLayoutEdit.SetMembers(text, w, "rect", changes), $"{verb} {w.Name}{note}");
    }

    /// <summary>What writing a rect will do beyond the numbers: replace a profile variable, or add an override of an inherited widget.</summary>
    private static string RectNote(UiWidget w)
    {
        if (w.Fields.TryGetValue("rect", out var rect) && rect.Variable != null) return $" (its rect was {rect.Variable}; this file now has its own rect)";
        if (!w.InDocument) return $" (this file now overrides {w.Name} from {w.Definitions[0].File.Name})";
        if (w.Fields.TryGetValue("rect", out var r) && !r.InDocument) return $" (this file now sets its own rect; {r.File.Name}'s is overridden)";
        return "";
    }

    /// <summary>
    /// The widget as the current scene has it. Inspector fields keep the widget they were built for while one of them has
    /// focus, and every edit re-resolves the scene, so edits look the widget up again by its path before planning.
    /// </summary>
    private UiWidget Live(UiWidget w) => scene?.Find(w.Path) ?? w;

    internal void SetField(UiWidget w, string key, string literal, string? description = null)
    {
        w = Live(w);
        Apply(text => UiLayoutEdit.SetField(text, w, key, literal), description ?? $"{w.Name}.{key} = {Shorten(literal)}");
    }

    internal void RemoveField(UiWidget w, string key)
    {
        w = Live(w);
        Apply(text => UiLayoutEdit.RemoveField(text, w, key), w.Definitions.Any(d => !d.File.IsDocument && d.Node["fields"]?[key] != null)
            ? $"Removed this file's {key} on {w.Name}; the inherited value applies again" : $"Removed {key} from {w.Name}");
    }

    private static string Shorten(string s) => s.Length > 60 ? s[..57] + "…" : s;

    private void Duplicate(UiWidget w)
    {
        w = Live(w);
        if (w.Parent == null) { SetStatus("The root widget cannot be duplicated.", true); return; }
        var name = UiLayoutEdit.FreeName(w.Parent, w.Name + "_copy");
        var path = w.Parent.Path + "/" + name;
        if (Apply(text => UiLayoutEdit.Duplicate(text, w, name), $"Duplicated {w.Name} as {name}")) SelectAfterResolve(path);
    }

    private void Delete(UiWidget w)
    {
        w = Live(w);
        var parent = w.Parent;
        if (Apply(text => UiLayoutEdit.Delete(text, w), $"Deleted {w.Name}") && parent != null) SelectAfterResolve(parent.Path);
    }

    private void Move(UiWidget w, int direction) =>
        Apply(text => UiLayoutEdit.Move(text, Live(w), direction), direction < 0 ? $"{w.Name} is now drawn earlier (below its next sibling)" : $"{w.Name} is now drawn later (above its previous sibling)");

    private static readonly (string Label, string Type, string Name, Func<UiWidget, (string, string)[]> Fields)[] NewWidgets =
    [
        ("Image", "ImageWidget", "image", _ => [("rect", "{ \"x\": 0, \"y\": 0 }"), ("filename", "\"PANEL\\\\goldbutton\"")]),
        ("Text", "TextBoxWidget", "text", _ => [("rect", "{ \"x\": 0, \"y\": 0, \"width\": 300, \"height\": 60 }"), ("text", "\"New text\""), ("style", "{ \"pointSize\": \"$MediumFontSize\", \"fontColor\": \"$FontColorWhite\", \"alignment\": { \"h\": \"center\", \"v\": \"center\" } }")]),
        ("Button", "ButtonWidget", "button", _ => [("rect", "{ \"x\": 0, \"y\": 0 }"), ("filename", "\"PANEL\\\\closebtn_4x\""), ("hoveredFrame", "3"), ("tooltipString", "\"@strClose\"")]),
        ("Rectangle", "RectangleWidget", "rectangle", _ => [("rect", "{ \"x\": 0, \"y\": 0, \"width\": 200, \"height\": 100 }"), ("color", "[ 0.0, 0.0, 0.0, 0.5 ]")]),
        ("Container", "Widget", "container", _ => [("rect", "{ \"x\": 0, \"y\": 0, \"width\": 400, \"height\": 300 }")])
    ];

    private void AddChild(UiWidget parent, (string Label, string Type, string Name, Func<UiWidget, (string, string)[]> Fields) kind)
    {
        parent = Live(parent);
        var name = UiLayoutEdit.FreeName(parent, kind.Name);
        if (Apply(text => UiLayoutEdit.AddChild(text, parent, kind.Type, name, kind.Fields(parent)), $"Added {kind.Type} {name} to {parent.Name}")) SelectAfterResolve(parent.Path + "/" + name);
    }

    private void Rename(UiWidget w, string name)
    {
        w = Live(w);
        if (name == w.Name || name.Length == 0) return;
        var path = w.Parent == null ? name : w.Parent.Path + "/" + name;
        if (Apply(text => UiLayoutEdit.Rename(text, w, name), $"Renamed {w.Name} to {name}")) SelectAfterResolve(path);
    }

    private void SelectAfterResolve(string path)
    {
        Resolve();
        if (scene?.Find(path) is { } found) Select(found);
    }

    /// <summary>Opens the Source view with the widget's definition in this file selected.</summary>
    internal void ShowInSource(UiWidget w, string? field = null)
    {
        var definition = w.Definitions.LastOrDefault(d => d.File.IsDocument);
        if (definition.Node == null) { SetStatus($"{w.Name} is not written in this file; it comes from {w.Definitions[0].File.Name}.", true); return; }
        var node = definition.Node;
        int start = node.Start, end = node.End;
        if (field != null && node["fields"]?.Member(field) is { } member) { start = member.Start; end = member.Value.End; }
        pane.ShowSource();
        Dispatcher.UIThread.Post(() =>
        {
            pane.Source.Focus();
            pane.Source.Select(start, end - start);
            var location = pane.Source.Document.GetLocation(start);
            pane.Source.ScrollTo(location.Line, location.Column);
        }, DispatcherPriority.Background);
    }

    // ── Menus ────────────────────────────────────────────────────────────────────────────────────

    private void ShowContextMenu(UiWidget? w, Control target)
    {
        var items = new List<Control>();
        MenuItem Item(string header, Action action, bool enabled = true, string? tip = null)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            if (tip != null) ToolTip.SetTip(item, tip);
            item.Click += (_, _) => { try { action(); } catch (Exception e) { SetStatus(e.Message, true); } };
            return item;
        }
        if (w != null)
        {
            items.Add(new MenuItem { Header = $"{w.Name}  ·  {w.Type}", IsEnabled = false });
            items.Add(new Separator());
            if (w.Parent != null) items.Add(Item("Select parent", () => Select(w.Parent)));
            items.Add(Item("Zoom to widget", () => { Select(w); ZoomToSelection(); }));
            items.Add(Item("Show in source", () => ShowInSource(w), w.InDocument, "Opens the Source view on this widget's entry"));
            items.Add(new Separator());
            items.Add(Item("Duplicate  (Ctrl+D)", () => Duplicate(w), w.Parent != null));
            var add = new MenuItem { Header = "Add child" };
            add.ItemsSource = NewWidgets.Select(k => Item(k.Label, () => AddChild(w, k))).ToArray();
            items.Add(add);
            items.Add(Item("Draw earlier (move back)", () => Move(w, -1), w.Parent != null && w.InDocument));
            items.Add(Item("Draw later (move forward)", () => Move(w, 1), w.Parent != null && w.InDocument));
            items.Add(new Separator());
            items.Add(Item(canvas.Hidden.Contains(w.Path) ? "Show on canvas" : "Hide on canvas", () => { if (!canvas.Hidden.Remove(w.Path)) canvas.Hidden.Add(w.Path); canvas.InvalidateVisual(); RebuildTree(); }));
            items.Add(Item("Copy path", async () => { if (TopLevel.GetTopLevel(this)?.Clipboard is { } clip) await clip.SetTextAsync(w.Path); }));
            items.Add(new Separator());
            items.Add(Item("Delete  (Del)", () => Delete(w), w.Parent != null && !w.Inherited,
                w.Inherited ? $"{w.Name} comes from {w.Definitions[0].File.Name}; a layout based on it can override its fields but not remove it." : null));
        }
        else if (scene != null)
        {
            var add = new MenuItem { Header = "Add widget to the root" };
            add.ItemsSource = NewWidgets.Select(k => Item(k.Label, () => AddChild(scene.Root, k))).ToArray();
            items.Add(add);
            items.Add(Item("Fit screen", canvas.Fit));
        }
        var menu = new ContextMenu { ItemsSource = items, Placement = PlacementMode.Pointer };
        target.ContextMenu = menu;
        menu.Closed += (_, _) => { if (target.ContextMenu == menu) target.ContextMenu = null; };
        menu.Open(target);
    }

    /// <summary>Draws another layout underneath, dimmed (as the Layers menu does).</summary>
    internal void AddReference(string name)
    {
        if (!referenceNames.Contains(name, StringComparer.OrdinalIgnoreCase)) referenceNames.Add(name);
        Resolve(force: true);
    }

    /// <summary>Common companions for context, then every other layout.</summary>
    private static readonly string[] CommonLayers = ["hudpanelhd.json", "playerinventoryexpansionlayouthd.json", "bankexpansionlayouthd.json", "characterstatspanelhd.json", "skillstreepanelhd.json", "horadriccubelayouthd.json", "partypanelhd.json", "questlogpanelexpansionhd.json", "waypointspanelexpansionhd.json", "chatpanelhd.json"];

    private void ShowLayersMenu(Control anchor)
    {
        var own = UiLayoutSources.RelativeName(Document.FilePath);
        MenuItem Layer(string name)
        {
            var item = new MenuItem { Header = name, ToggleType = MenuItemToggleType.CheckBox, IsChecked = referenceNames.Contains(name, StringComparer.OrdinalIgnoreCase) };
            item.Click += (_, _) =>
            {
                if (referenceNames.RemoveAll(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)) == 0) referenceNames.Add(name);
                Resolve(force: true);
                SetStatus(referenceNames.Count == 0 ? "No reference layers." : "Reference layers (drawn dimmed, not editable): " + string.Join(", ", referenceNames));
            };
            return item;
        }
        IReadOnlyList<string> available;
        try { available = Sources().Available(); } catch (IOException) { available = []; }
        var items = new List<Control>();
        foreach (var name in CommonLayers.Where(n => n != own && available.Contains(n, StringComparer.OrdinalIgnoreCase))) items.Add(Layer(name));
        var all = new MenuItem { Header = $"All layouts ({available.Count})" };
        all.ItemsSource = available.Where(n => n != own && (mode?.Legacy == true ? !n.EndsWith("hd.json") : n.EndsWith("hd.json") || n.StartsWith("controller/"))).Select(Layer).ToArray();
        items.Add(new Separator()); items.Add(all);
        if (referenceNames.Count > 0) { items.Add(new Separator()); var clear = new MenuItem { Header = "Clear layers" }; clear.Click += (_, _) => { referenceNames.Clear(); Resolve(force: true); }; items.Add(clear); }
        if (available.Count == 0) items.Insert(0, new MenuItem { Header = "No other layouts found (choose the game data folder)", IsEnabled = false });
        var menu = new ContextMenu { ItemsSource = items, Placement = PlacementMode.Bottom };
        anchor.ContextMenu = menu; menu.Closed += (_, _) => anchor.ContextMenu = null; menu.Open(anchor);
    }

    // ── Keyboard ─────────────────────────────────────────────────────────────────────────────────

    private void OnKey(object? sender, KeyEventArgs e)
    {
        // Text fields keep their own keys.
        if (e.Source is TextBox || (e.Source as Visual)?.FindAncestorOfType<TextBox>() != null) return;
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var w = canvas.Selected;
        switch (e.Key)
        {
            case Key.Z when ctrl && e.KeyModifiers.HasFlag(KeyModifiers.Shift): canvas.CommitNudge(); pane.Redo(); break;
            case Key.Z when ctrl: canvas.CommitNudge(); pane.Undo(); break;
            case Key.Y when ctrl: canvas.CommitNudge(); pane.Redo(); break;
            case Key.D when ctrl && w != null: Duplicate(w); break;
            case Key.Delete when w != null: Delete(w); break;
            case Key.Escape when w?.Parent != null: Select(w.Parent); break;
            case Key.F when !ctrl: canvas.Fit(); break;
            case Key.P when !ctrl: canvas.FitPanel(); break;
            case Key.Z when !ctrl && w != null: ZoomToSelection(); break;
            case Key.D0 when ctrl: canvas.Fit(); break;
            case Key.D1 when !ctrl: canvas.SetZoom(1); break;
            case Key.OemPlus or Key.Add when ctrl: canvas.ZoomAt(1.25); break;
            case Key.OemMinus or Key.Subtract when ctrl: canvas.ZoomAt(1 / 1.25); break;
            case Key.Left or Key.Right or Key.Up or Key.Down when e.Source is not UiDesignerCanvas && w != null && !(e.Source is ListBoxItem or ListBox):
                double step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
                canvas.Nudge(e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0, e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0); break;
            default: return;
        }
        e.Handled = true;
    }

    private void ZoomToSelection()
    {
        if (canvas.Selected is { } w) canvas.ZoomTo(canvas.DrawnBounds(w).Width > 0 ? canvas.DrawnBounds(w) : w.Bounds, 80);
    }

    // ── Chrome helpers ───────────────────────────────────────────────────────────────────────────

    internal void SetStatus(string text, bool error = false)
    {
        status = text; statusError = error;
        statusLine.Text = text; statusLine.Foreground = error ? ErrorBrush : Muted;
        ToolTip.SetTip(statusLine, text);
    }

    private void UpdatePointer()
    {
        zoomLabel.Text = $"{canvas.Zoom * 100:0}%";
        var parts = new List<string>();
        if (canvas.Hovered is { } h) parts.Add($"{h.Name} ({ShortType(h.Type)})");
        if (canvas.PointerLayout is { } p) parts.Add($"{p.X:0}, {p.Y:0}");
        if (scene != null) parts.Add($"{scene.Width:0} × {scene.Height:0}");
        pointerLine.Text = string.Join("  ·  ", parts);
    }

    private static TextBlock Caption(string text) => new() { Text = text, Foreground = Muted, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new(4, 0, 0, 0) };
    private static Control Separator() => new Border { Width = 1, Height = 22, Background = CardBorder, Margin = new(4, 0) };

    private Button SmallButton(string content, string tip, Action action)
    {
        var b = new Button { Content = content, Padding = new(8, 3), MinHeight = 0, FontSize = 12 };
        ToolTip.SetTip(b, tip); AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => { try { action(); } catch (Exception e) { SetStatus(e.Message, true); } };
        return b;
    }

    private ToggleButton Toggle(string content, string tip, bool initial, Action<bool> changed)
    {
        var t = new ToggleButton { Content = content, IsChecked = initial, Padding = new(8, 3), MinHeight = 0, FontSize = 12 };
        ToolTip.SetTip(t, tip); AutomationProperties.SetName(t, content);
        t.IsCheckedChanged += (_, _) => { changed(t.IsChecked == true); canvas.InvalidateVisual(); };
        return t;
    }
}

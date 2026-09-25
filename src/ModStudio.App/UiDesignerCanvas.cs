using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ModStudio.Core;

namespace ModStudio.App;

/// <summary>A widget drag the canvas finished: the rect (in the widget's own units) it should now have.</summary>
internal sealed record UiCanvasEdit(UiWidget Widget, string Kind, double? X, double? Y, double? Width, double? Height, double? Scale);

/// <summary>
/// Draws a resolved layout the way the game would (sprites at their reference size, text in the game's fonts) on a zoomable
/// view of the reference screen, and lets widgets be picked, dragged, resized and nudged. It never edits anything itself:
/// finished gestures are raised as <see cref="Committed"/> and the designer writes them into the document.
/// </summary>
internal sealed class UiDesignerCanvas : Control
{
    private static readonly IBrush Outside = new SolidColorBrush(Color.Parse("#111112"));
    private static readonly IBrush ScreenFill = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#1B1814"), 0), new GradientStop(Color.Parse("#0D0C0B"), 1) }
    };
    private static readonly IPen ScreenPen = new Pen(new SolidColorBrush(Color.Parse("#4A4030")), 1);
    private static readonly IPen OwnPen = new Pen(new SolidColorBrush(Color.FromArgb(120, 216, 188, 134)), 1);
    private static readonly IPen InheritedPen = new Pen(new SolidColorBrush(Color.FromArgb(90, 110, 170, 220)), 1, new DashStyle([3, 3], 0));
    private static readonly IPen HoverPen = new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), 1, new DashStyle([4, 3], 0));
    private static readonly IPen SelectedPen = new Pen(new SolidColorBrush(Color.Parse("#F0B84A")), 2);
    private static readonly IPen ParentPen = new Pen(new SolidColorBrush(Color.FromArgb(150, 240, 184, 74)), 1, new DashStyle([6, 4], 0));
    private static readonly IPen GuidePen = new Pen(new SolidColorBrush(Color.Parse("#FF4FD2")), 1);
    private static readonly IPen AnchorPen = new Pen(new SolidColorBrush(Color.Parse("#5FD7FF")), 1.5);
    private static readonly IBrush HandleFill = new SolidColorBrush(Color.Parse("#F0B84A"));
    private static readonly IBrush LabelBack = new SolidColorBrush(Color.FromArgb(200, 17, 17, 18));
    private static readonly IBrush LabelText = new SolidColorBrush(Color.Parse("#E6E6E6"));
    private static readonly IBrush Missing = new SolidColorBrush(Color.FromArgb(60, 220, 80, 80));
    private static readonly IBrush Loading = new SolidColorBrush(Color.FromArgb(30, 216, 188, 134));
    private static readonly Typeface LabelFace = new(FontFamily.Default);
    /// <summary>Types that only catch input or group others: picked last, so a click lands on what is drawn.</summary>
    private static readonly HashSet<string> Catchers = new(StringComparer.Ordinal) { "ClickCatcherWidget", "FocusableWidget", "ControllerCursorBoundsWidget", "Widget", "ListWidgetFocusReceiver", "ListWidgetFocusRedirector" };

    private UiScene? scene;
    private (UiWidget Widget, double Dx, double Dy)? nudge;

    public UiAssets? Assets { get; set; }
    public IReadOnlyList<UiScene> References { get; set; } = [];
    public Func<string, string?>? Localize { get; set; }
    public ISet<string> Hidden { get; } = new HashSet<string>(StringComparer.Ordinal);
    public bool ShowOutlines { get; set; } = true;
    public bool ShowNames { get; set; }
    public bool Snap { get; set; } = true;
    /// <summary>The state buttons are drawn in: "normal" (the widget under the pointer shows its hovered frame, as in game), "hovered", "pressed", "disabled" or "toggled".</summary>
    public string PreviewState { get; set; } = "normal";
    public UiWidget? Selected { get; private set; }
    public UiWidget? Hovered { get; private set; }
    public double Zoom { get; private set; } = 0.25;
    public Vector Pan { get; private set; }
    /// <summary>The layout point under the pointer, for the status line.</summary>
    public Point? PointerLayout { get; private set; }

    public event Action<UiWidget?>? SelectionRequested;
    public event Action<UiCanvasEdit>? Committed;
    public event Action<UiWidget?, Point>? MenuRequested;
    public event Action<UiWidget>? EditRequested;
    public event Action? ViewChanged;

    public UiDesignerCanvas()
    {
        Focusable = true; ClipToBounds = true;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
        nudgeTimer.Tick += (_, _) => CommitNudge();
    }

    public UiScene? Scene
    {
        get => scene;
        set
        {
            scene = value; runs.Clear();
            if (Selected != null) Selected = value?.Find(Selected.Path);
            if (Hovered != null) Hovered = value?.Find(Hovered.Path);
            InvalidateVisual();
        }
    }

    public void Select(UiWidget? widget) { CommitNudge(); Selected = widget; InvalidateVisual(); }

    // ── View ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The fit the view keeps while the canvas is resized ("screen" or "panel"); null once the user zooms or pans.</summary>
    private string? fit = "panel";

    /// <summary>Shows the whole reference screen.</summary>
    public void Fit()
    {
        if (scene == null || Bounds.Width < 10 || Bounds.Height < 10) return;
        ZoomTo(new UiRect(0, 0, scene.Width, scene.Height), 16);
        fit = "screen";
    }

    /// <summary>Zooms onto the layout's panel (its root), or the whole screen when the panel fills most of it.</summary>
    public void FitPanel()
    {
        if (scene == null || Bounds.Width < 10 || Bounds.Height < 10) return;
        var root = scene.Root.Bounds;
        if (root.Width * root.Height > scene.Width * scene.Height * 0.6 || root.Width <= 0 || root.Height <= 0) { Fit(); fit = "panel"; return; }
        ZoomTo(root, 40);
        fit = "panel";
    }

    /// <summary>Zooms so a layout rectangle fills the view (with a margin in view pixels).</summary>
    public void ZoomTo(UiRect rect, double margin = 48)
    {
        if (rect.Width <= 0 || rect.Height <= 0 || Bounds.Width < 10) return;
        fit = null;
        Zoom = Math.Clamp(Math.Min((Bounds.Width - margin * 2) / rect.Width, (Bounds.Height - margin * 2) / rect.Height), 0.02, 8);
        Pan = new Vector(Bounds.Width / 2 - (rect.X + rect.Width / 2) * Zoom, Bounds.Height / 2 - (rect.Y + rect.Height / 2) * Zoom);
        InvalidateVisual(); ViewChanged?.Invoke();
    }

    public void ZoomAt(double factor, Point? around = null)
    {
        fit = null;
        var at = around ?? new Point(Bounds.Width / 2, Bounds.Height / 2);
        var layout = ToLayout(at);
        Zoom = Math.Clamp(Zoom * factor, 0.02, 8);
        Pan = new Vector(at.X - layout.X * Zoom, at.Y - layout.Y * Zoom);
        InvalidateVisual(); ViewChanged?.Invoke();
    }

    public void SetZoom(double zoom) => ZoomAt(zoom / Zoom);

    private Point ToLayout(Point view) => new((view.X - Pan.X) / Zoom, (view.Y - Pan.Y) / Zoom);
    private Point ToView(double x, double y) => new(x * Zoom + Pan.X, y * Zoom + Pan.Y);
    private Rect ToView(UiRect r) => new(ToView(r.X, r.Y), ToView(r.Right, r.Bottom));

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (fit == "panel") FitPanel(); else if (fit == "screen") Fit();
    }

    // ── Geometry helpers ──────────────────────────────────────────────────────────────────────────

    private UiSpriteInfo? SpriteOf(UiWidget w) => UiLayout.SpriteField(w) is { } field ? Assets?.Sprite(w.String(field)) : null;

    /// <summary>The offset a pending gesture (drag or nudge) adds to a widget or one of its descendants, in layout pixels.</summary>
    private (double X, double Y) Offset(UiWidget w)
    {
        for (var at = w; at != null; at = at.Parent)
        {
            if (drag is { Moved: true } d && d.Widget == at && d.Kind == "move") return (d.Dx * at.ParentScale, d.Dy * at.ParentScale);
            if (nudge is { } n && n.Widget == at) return (n.Dx * at.ParentScale, n.Dy * at.ParentScale);
        }
        return (0, 0);
    }

    /// <summary>A widget's rectangle on screen as drawn now, including a gesture in progress.</summary>
    internal UiRect DrawnBounds(UiWidget w)
    {
        var (ox, oy) = Offset(w);
        var b = w.Bounds;
        if (drag is { Moved: true } d && d.Widget == w && d.Kind != "move") b = d.Preview;
        else b = new UiRect(b.X + ox, b.Y + oy, b.Width, b.Height);
        if (b.Width <= 0 && b.Height <= 0) return new UiRect(b.X - 8 / Zoom, b.Y - 8 / Zoom, 16 / Zoom, 16 / Zoom);
        return b;
    }

    private bool IsHidden(UiWidget w) { for (var at = w; at != null; at = at.Parent) if (Hidden.Contains(at.Path)) return true; return false; }

    /// <summary>The sprite frame a widget shows in the previewed state, falling back to its resting frame.</summary>
    internal int FrameFor(UiWidget w, bool live = true)
    {
        var state = live && PreviewState == "normal" && w == Hovered && drag == null ? "hovered" : PreviewState;
        string[] keys = state switch
        {
            "hovered" => ["hoveredFrame", "untoggledHoveredFrame", "inactiveHoveredFrame"],
            "pressed" => ["pressedFrame", "untoggledPressedFrame", "inactivePressedFrame"],
            "disabled" => ["disabledFrame", "untoggledDisabledFrame"],
            "toggled" => ["toggledFrame", "activeNormalFrame"],
            _ => []
        };
        foreach (var key in keys) if (w.Number(key) is { } n) return Math.Max(0, (int)n);
        return UiLayout.SpriteFrame(w);
    }

    private bool DrawsSomething(UiWidget w) => SpriteOf(w) != null || TextOf(w) != null || w.Type is "RectangleWidget" or "NineTileImageWidget" or "InventorySlotWidget" or "InventoryGridWidget";

    // ── Rendering ─────────────────────────────────────────────────────────────────────────────────

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Outside, new Rect(Bounds.Size));
        if (scene == null) return;
        var screen = ToView(new UiRect(0, 0, scene.Width, scene.Height));
        context.FillRectangle(ScreenFill, screen);
        using (context.PushTransform(Matrix.CreateScale(Zoom, Zoom) * Matrix.CreateTranslation(Pan.X, Pan.Y)))
        {
            foreach (var reference in References)
                using (context.PushOpacity(0.45))
                    foreach (var w in reference.Widgets) DrawWidget(context, w, reference, false);
            foreach (var w in scene.Widgets) if (!IsHidden(w)) DrawWidget(context, w, scene, true);
        }
        context.DrawRectangle(null, ScreenPen, screen);
        DrawOverlays(context);
    }

    private void DrawWidget(DrawingContext context, UiWidget w, UiScene owner, bool live)
    {
        var b = live ? DrawnBounds(w) : w.Bounds;
        double scale = w.Scale;
        if (live && drag is { Moved: true } d && d.Widget == w && d.Kind == "scale") scale = d.Preview.Width / Math.Max(1, w.LocalWidth);
        switch (w.Type)
        {
            case "RectangleWidget" when UiLayout.Color(w.Value("color")) is { } c:
                context.FillRectangle(new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B)), ToRect(b));
                break;
            case "NineTileImageWidget":
                if (UiLayout.Color(w.Value("backgroundColor")) is { } bg) context.FillRectangle(new SolidColorBrush(Color.FromArgb(bg.A, bg.R, bg.G, bg.B)), ToRect(b));
                context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(200, 110, 96, 70)), 3 * scale), ToRect(b));
                return;
        }
        if (SpriteOf(w) is { } sprite)
        {
            var target = SpriteRect(w, sprite, b, scale);
            int frame = FrameFor(w, live);
            var bitmap = UiBitmaps.Get(sprite, frame, InvalidateVisual);
            if (bitmap != null) context.DrawImage(bitmap, new Rect(bitmap.Size), target);
            else context.FillRectangle(UiBitmaps.Failed(sprite, frame) ? Missing : Loading, target);
        }
        else if (live && UiLayout.SpriteField(w) == null && w.String("filename") is { Length: > 0 } name && !name.Contains('%') && Assets != null)
            context.FillRectangle(Missing, ToRect(b));
        if (TextOf(w) is { } text) DrawText(context, w, text, b, scale);
    }

    /// <summary>Where a widget's sprite is drawn: at its position and scale; an inventory slot's background centred in the slot.</summary>
    private static Rect SpriteRect(UiWidget w, UiSpriteInfo sprite, UiRect b, double scale)
    {
        double sw = sprite.Width * scale, sh = sprite.DrawHeight * scale;
        if (w.Type == "InventorySlotWidget" && b.Width > 0) return new(b.X + (b.Width - sw) / 2, b.Y + (b.Height - sh) / 2, sw, sh);
        return new(b.X, b.Y, sw, sh);
    }

    /// <summary>Whether the widget draws something at a layout point: its picture's opaque pixels, its text, its fill.</summary>
    private bool DrawnAt(UiWidget w, Point layout)
    {
        if (TextOf(w) != null || w.Type is "RectangleWidget" or "NineTileImageWidget" or "InventorySlotWidget" or "InventoryGridWidget") return true;
        if (SpriteOf(w) is not { } sprite) return false;
        var r = SpriteRect(w, sprite, DrawnBounds(w), w.Scale);
        if (!r.Contains(layout)) return false;
        return UiBitmaps.Opaque(sprite, FrameFor(w), (layout.X - r.X) / r.Width, (layout.Y - r.Y) / r.Height) != false;
    }

    private static Rect ToRect(UiRect r) => new(r.X, r.Y, Math.Max(0, r.Width), Math.Max(0, r.Height));

    /// <summary>What a widget shows as text: a text box's text, a button's label; "@key" strings localized.</summary>
    internal string? TextOf(UiWidget w)
    {
        var raw = w.String("text") ?? (w.Type.Contains("Button", StringComparison.Ordinal) ? w.String("textString") : null);
        if (string.IsNullOrEmpty(raw)) return null;
        return raw.StartsWith('@') ? Localize?.Invoke(raw[1..]) ?? raw : raw;
    }

    internal static JsonObject? StyleOf(UiWidget w) => w.Value("style") as JsonObject ?? w.Value("text/style") as JsonObject ?? w.Value("textStyle") as JsonObject;

    /// <summary>A widget's text set once per scene: the run, its drop shadow, and how it sits in the widget's rect.</summary>
    private sealed record TextRun(FormattedText Main, FormattedText? Shadow, string Horizontal, string Vertical);

    private TextRun BuildText(UiWidget w, string text, UiRect b, double scale)
    {
        var style = StyleOf(w);
        bool button = w.Type.Contains("Button", StringComparison.Ordinal);
        double size = Math.Max(1, UiLayout.Member(style, "pointSize", 26) * scale);
        var color = UiLayout.Color(style?["fontColor"]) ?? UiLayout.Color(w.Value("textColor")) ?? UiLayout.Color(w.Value("fontColor")) ?? (240, 240, 240, 255);
        var face = (style?["fontFace"] as JsonValue)?.TryGetValue<string>(out var f) == true ? f : null;
        var alignment = style?["alignment"] as JsonObject;
        string h = (alignment?["h"] as JsonValue)?.TryGetValue<string>(out var hv) == true ? hv : button ? "center" : "left";
        string v = (alignment?["v"] as JsonValue)?.TryGetValue<string>(out var vv) == true ? vv : button ? "center" : "top";
        bool wrap = (style?["options"] as JsonObject)?["lineWrap"] is JsonValue lw && lw.TryGetValue<bool>(out var wraps) && wraps;
        var typeface = new Typeface(UiFonts.For(Assets, face));
        var content = text.Replace("\\n", "\n");
        FormattedText Make(IBrush brush)
        {
            var run = new FormattedText(content, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush)
            { TextAlignment = h switch { "center" => TextAlignment.Center, "right" => TextAlignment.Right, _ => TextAlignment.Left } };
            if (wrap && b.Width > 0) run.MaxTextWidth = b.Width;
            return run;
        }
        var main = Make(new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B)));
        var shadow = style?["dropShadow"] != null ? Make(new SolidColorBrush(Color.FromArgb(200, 0, 0, 0))) : null;
        return new(main, shadow, h, v);
    }

    private readonly Dictionary<UiWidget, TextRun> runs = [];

    private void DrawText(DrawingContext context, UiWidget w, string text, UiRect b, double scale)
    {
        // Runs are rebuilt when the widget's size or scale changes under a gesture, since wrapping and size follow them.
        if (!runs.TryGetValue(w, out var run) || drag is { Moved: true } d && d.Widget == w && d.Kind != "move") runs[w] = run = BuildText(w, text, b, scale);
        var main = run.Main;
        // Multi-line text aligns inside its wrap width; a single run is placed by its own width.
        double width = main.MaxTextWidth > 0 && !double.IsInfinity(main.MaxTextWidth) ? main.MaxTextWidth : main.WidthIncludingTrailingWhitespace;
        double x = run.Horizontal switch { "center" => b.X + b.Width / 2 - width / 2, "right" => b.Right - width, _ => b.X };
        double y = run.Vertical switch { "center" => b.Y + b.Height / 2 - main.Height / 2, "bottom" => b.Bottom - main.Height, _ => b.Y };
        if (run.Shadow != null) context.DrawText(run.Shadow, new Point(x + 2 * scale, y + 2 * scale));
        context.DrawText(main, new Point(x, y));
    }

    private void DrawOverlays(DrawingContext context)
    {
        if (scene == null) return;
        if (ShowOutlines)
            foreach (var w in scene.Widgets)
            {
                if (w.Parent == null || IsHidden(w) || w == Selected) continue;
                var r = ToView(DrawnBounds(w));
                if (r.Width < 2 && r.Height < 2) continue;
                context.DrawRectangle(null, w.InDocument ? OwnPen : InheritedPen, r);
            }
        if (ShowNames)
            foreach (var w in scene.Widgets)
                if (w.Parent != null && !IsHidden(w) && ToView(DrawnBounds(w)) is var r && r.Width > 50 && r.Height > 14) Label(context, w.Name, r.TopLeft, 10);
        if (Hovered != null && Hovered != Selected && !IsHidden(Hovered)) context.DrawRectangle(null, HoverPen, ToView(DrawnBounds(Hovered)));
        if (Selected is { } s)
        {
            if (s.Parent is { Parent: not null } parent) context.DrawRectangle(null, ParentPen, ToView(DrawnBounds(parent)));
            var r = ToView(DrawnBounds(s));
            context.DrawRectangle(null, SelectedPen, r);
            // The anchor: where on the parent the rect offset is measured from.
            var (ax, ay) = Offset(s.Parent ?? s);
            var anchor = ToView(s.AnchorPoint.X + ax, s.AnchorPoint.Y + ay);
            context.DrawLine(AnchorPen, new Point(anchor.X - 6, anchor.Y), new Point(anchor.X + 6, anchor.Y));
            context.DrawLine(AnchorPen, new Point(anchor.X, anchor.Y - 6), new Point(anchor.X, anchor.Y + 6));
            context.DrawEllipse(null, AnchorPen, anchor, 3.5, 3.5);
            foreach (var (_, handle) in Handles(s, r)) context.DrawRectangle(HandleFill, new Pen(Brushes.Black, 1), handle);
            var caption = s.Name + "  " + SizeCaption(s);
            Label(context, caption, new Point(r.X, r.Y - 18), 11);
        }
        foreach (var (from, to) in guides) context.DrawLine(GuidePen, ToView(from.X, from.Y), ToView(to.X, to.Y));
        if (Selected != null && Hovered != null && Hovered != Selected && drag == null && !IsHidden(Hovered)) DrawDistances(context, DrawnBounds(Selected), DrawnBounds(Hovered));
    }

    private static readonly IPen MeasurePen = new Pen(new SolidColorBrush(Color.Parse("#FF7A45")), 1);
    private static readonly IBrush MeasureText = new SolidColorBrush(Color.Parse("#FFB38F"));

    /// <summary>
    /// The gaps between the selection and the widget under the pointer, in layout pixels: to each edge of it when it
    /// surrounds the selection (a parent), otherwise the horizontal and vertical space between them.
    /// </summary>
    private void DrawDistances(DrawingContext context, UiRect a, UiRect b)
    {
        void Measure(double x1, double y1, double x2, double y2, double length)
        {
            if (length < 0.5) return;
            var p1 = ToView(x1, y1); var p2 = ToView(x2, y2);
            context.DrawLine(MeasurePen, p1, p2);
            bool horizontal = Math.Abs(p1.Y - p2.Y) < 0.5;
            // End ticks.
            var tick = horizontal ? new Vector(0, 4) : new Vector(4, 0);
            context.DrawLine(MeasurePen, p1 - tick, p1 + tick); context.DrawLine(MeasurePen, p2 - tick, p2 + tick);
            var ft = new FormattedText(length.ToString("0.#", CultureInfo.InvariantCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, 10, MeasureText);
            var mid = new Point((p1.X + p2.X) / 2, (p1.Y + p2.Y) / 2);
            var at = horizontal ? new Point(mid.X - ft.Width / 2, mid.Y - ft.Height - 2) : new Point(mid.X + 4, mid.Y - ft.Height / 2);
            context.FillRectangle(LabelBack, new Rect(at.X - 2, at.Y, ft.Width + 4, ft.Height), 2);
            context.DrawText(ft, at);
        }
        double cy = (Math.Max(a.Y, b.Y) + Math.Min(a.Bottom, b.Bottom)) / 2, cx = (Math.Max(a.X, b.X) + Math.Min(a.Right, b.Right)) / 2;
        if (b.X <= a.X && b.Y <= a.Y && b.Right >= a.Right && b.Bottom >= a.Bottom)
        {
            double my = a.Y + a.Height / 2, mx = a.X + a.Width / 2;
            Measure(b.X, my, a.X, my, a.X - b.X); Measure(a.Right, my, b.Right, my, b.Right - a.Right);
            Measure(mx, b.Y, mx, a.Y, a.Y - b.Y); Measure(mx, a.Bottom, mx, b.Bottom, b.Bottom - a.Bottom);
            return;
        }
        if (b.X >= a.Right) Measure(a.Right, cy, b.X, cy, b.X - a.Right);
        else if (b.Right <= a.X) Measure(b.Right, cy, a.X, cy, a.X - b.Right);
        if (b.Y >= a.Bottom) Measure(cx, a.Bottom, cx, b.Y, b.Y - a.Bottom);
        else if (b.Bottom <= a.Y) Measure(cx, b.Bottom, cx, a.Y, a.Y - b.Bottom);
    }

    private string SizeCaption(UiWidget s)
    {
        var b = drag is { Moved: true } d && d.Widget == s && d.Kind != "move" ? d.Preview : s.Bounds;
        var rect = s.Value("rect") as JsonObject;
        double x = UiLayout.Member(rect, "x"), y = UiLayout.Member(rect, "y");
        if (drag is { Moved: true } m && m.Widget == s && m.Kind == "move") { x += m.Dx; y += m.Dy; }
        if (nudge is { } n && n.Widget == s) { x += n.Dx; y += n.Dy; }
        return $"x {x:0.#}  y {y:0.#}  ·  {b.Width:0.#} × {b.Height:0.#}";
    }

    private void Label(DrawingContext context, string text, Point at, double size)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, size, LabelText);
        var y = Math.Max(0, at.Y);
        context.FillRectangle(LabelBack, new Rect(at.X, y, ft.Width + 8, ft.Height + 2), 3);
        context.DrawText(ft, new Point(at.X + 4, y + 1));
    }

    // ── Handles ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The handles a selected widget offers, in view coordinates. A widget with a rect width and height is resized; one that is
    /// only as big as its picture is scaled from its corners (rect scale).
    /// </summary>
    private IEnumerable<(string Kind, Rect Handle)> Handles(UiWidget w, Rect r)
    {
        if (w.Parent == null || w.Flag("fitToParent") || r.Width < 8 || r.Height < 8) yield break;
        var rect = w.Value("rect") as JsonObject;
        bool sized = UiLayout.Member(rect, "width") > 0 || UiLayout.Member(rect, "height") > 0;
        const double k = 7;
        Rect At(double x, double y) => new(x - k / 2, y - k / 2, k, k);
        if (!sized && SpriteOf(w) == null) yield break;
        yield return ("se", At(r.Right, r.Bottom));
        yield return ("nw", At(r.X, r.Y)); yield return ("ne", At(r.Right, r.Y)); yield return ("sw", At(r.X, r.Bottom));
        if (!sized) yield break;
        yield return ("n", At(r.X + r.Width / 2, r.Y)); yield return ("s", At(r.X + r.Width / 2, r.Bottom));
        yield return ("w", At(r.X, r.Y + r.Height / 2)); yield return ("e", At(r.Right, r.Y + r.Height / 2));
    }

    private static StandardCursorType HandleCursor(string kind) => kind switch
    {
        "n" or "s" => StandardCursorType.SizeNorthSouth, "e" or "w" => StandardCursorType.SizeWestEast,
        "nw" => StandardCursorType.TopLeftCorner, "se" => StandardCursorType.BottomRightCorner, "ne" => StandardCursorType.TopRightCorner, "sw" => StandardCursorType.BottomLeftCorner,
        _ => StandardCursorType.SizeAll
    };

    // ── Picking ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every widget under a layout point, topmost first, ranked so a click lands on what is seen there: widgets drawing
    /// at the point (opaque picture pixels, text, fills), then containers, then input catchers, then pictures that are
    /// transparent at the point (an idle animation over a panel). Alt+click walks the whole list.
    /// </summary>
    internal List<UiWidget> HitsAt(Point layout)
    {
        if (scene == null) return [];
        var hits = new List<(UiWidget Widget, int Rank)>();
        for (int k = scene.Widgets.Count - 1; k >= 1; k--)
        {
            var w = scene.Widgets[k];
            if (IsHidden(w) || !DrawnBounds(w).Contains(layout.X, layout.Y)) continue;
            bool fills = w.Flag("fitToParent");
            int rank = DrawsSomething(w) ? (DrawnAt(w, layout) ? (fills ? 3 : 0) : 4) : Catchers.Contains(w.Type) || fills ? 2 : 1;
            hits.Add((w, rank));
        }
        return [.. hits.OrderBy(h => h.Rank).Select(h => h.Widget)];
    }

    // ── Input ─────────────────────────────────────────────────────────────────────────────────────

    private sealed class Drag
    {
        public required UiWidget Widget { get; init; }
        public required string Kind { get; init; }
        public required Point Start { get; init; }
        public required UiRect Original { get; init; }
        public double Dx { get; set; }
        public double Dy { get; set; }
        public UiRect Preview { get; set; }
        public bool Moved { get; set; }
    }
    private Drag? drag;
    private Point? panFrom;
    private bool spaceHeld;
    private readonly List<(Point From, Point To)> guides = [];

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        CommitNudge();
        var point = e.GetCurrentPoint(this); var view = point.Position; var layout = ToLayout(view);
        if (point.Properties.IsMiddleButtonPressed || point.Properties.IsLeftButtonPressed && spaceHeld)
        {
            panFrom = view; e.Pointer.Capture(this); Cursor = new Cursor(StandardCursorType.SizeAll); e.Handled = true; return;
        }
        if (scene == null) return;
        if (point.Properties.IsRightButtonPressed)
        {
            var target = HitsAt(layout).FirstOrDefault();
            if (target != null && target != Selected) SelectionRequested?.Invoke(target);
            MenuRequested?.Invoke(target ?? Selected, view); e.Handled = true; return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;
        // A handle of the selection first; then the widget under the pointer (Alt cycles through everything stacked there).
        if (Selected != null)
            foreach (var (kind, handle) in Handles(Selected, ToView(DrawnBounds(Selected))))
                if (handle.Inflate(3).Contains(view)) { StartDrag(Selected, kind, layout, e); return; }
        var hits = HitsAt(layout);
        UiWidget? hit;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && hits.Count > 0) hit = hits[(hits.IndexOf(Selected!) + 1) % hits.Count];
        else if (Selected != null && hits.Contains(Selected) && !e.KeyModifiers.HasFlag(KeyModifiers.Control)) hit = Selected;
        else hit = hits.FirstOrDefault();
        if (e.ClickCount == 2 && hit != null) { EditRequested?.Invoke(hit); e.Handled = true; return; }
        if (hit != Selected) SelectionRequested?.Invoke(hit);
        if (hit != null) StartDrag(hit, "move", layout, e);
        e.Handled = true;
    }

    private void StartDrag(UiWidget widget, string kind, Point layout, PointerPressedEventArgs e)
    {
        drag = new Drag { Widget = widget, Kind = kind, Start = layout, Original = widget.Bounds, Preview = widget.Bounds };
        e.Pointer.Capture(this); e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var view = e.GetPosition(this); var layout = ToLayout(view);
        PointerLayout = layout;
        if (panFrom is { } from)
        {
            Pan += view - from; panFrom = view; fit = null; InvalidateVisual(); ViewChanged?.Invoke(); return;
        }
        if (drag is { } d)
        {
            double dx = layout.X - d.Start.X, dy = layout.Y - d.Start.Y;
            if (!d.Moved && Math.Abs(dx) * Zoom < 3 && Math.Abs(dy) * Zoom < 3) return;
            d.Moved = true;
            if (d.Kind == "move") Move(d, dx, dy, e.KeyModifiers);
            else Resize(d, dx, dy, e.KeyModifiers);
            InvalidateVisual(); ViewChanged?.Invoke();
            return;
        }
        var hover = scene == null ? null : HitsAt(layout).FirstOrDefault();
        if (Selected != null && Handles(Selected, ToView(DrawnBounds(Selected))).FirstOrDefault(h => h.Handle.Inflate(3).Contains(view)) is { Kind: not null } over)
            Cursor = new Cursor(HandleCursor(over.Kind));
        else Cursor = spaceHeld ? new Cursor(StandardCursorType.Hand) : hover != null ? new Cursor(StandardCursorType.SizeAll) : Cursor.Default;
        if (hover != Hovered) { Hovered = hover; InvalidateVisual(); }
        ViewChanged?.Invoke();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        PointerLayout = null;
        if (Hovered != null) { Hovered = null; InvalidateVisual(); }
        ViewChanged?.Invoke();
    }

    private void Move(Drag d, double dx, double dy, KeyModifiers modifiers)
    {
        guides.Clear();
        if (modifiers.HasFlag(KeyModifiers.Shift)) { if (Math.Abs(dx) > Math.Abs(dy)) dy = 0; else dx = 0; }
        var moved = new UiRect(d.Original.X + dx, d.Original.Y + dy, d.Original.Width, d.Original.Height);
        if (Snap && !modifiers.HasFlag(KeyModifiers.Control))
        {
            var (sx, sy) = Snapping(d.Widget, moved);
            dx += sx; dy += sy;
        }
        double ps = d.Widget.ParentScale;
        // The rect offset is in the parent's units; whole numbers, as the files write them.
        d.Dx = Math.Round(dx / ps); d.Dy = Math.Round(dy / ps);
    }

    /// <summary>How far to move a rectangle so an edge or centre meets a sibling's or the parent's (within a few view pixels); draws the guides.</summary>
    private (double X, double Y) Snapping(UiWidget w, UiRect r)
    {
        double threshold = 6 / Zoom;
        var others = new List<UiRect>();
        if (w.Parent != null)
        {
            others.Add(w.Parent.Bounds);
            foreach (var sibling in w.Parent.Children) if (sibling != w && !IsHidden(sibling) && (sibling.HasSize || SpriteOf(sibling) != null)) others.Add(sibling.Bounds);
        }
        if (scene != null) others.Add(new UiRect(0, 0, scene.Width, scene.Height));
        double[] mineX = [r.X, r.X + r.Width / 2, r.Right], mineY = [r.Y, r.Y + r.Height / 2, r.Bottom];
        (double Delta, double At, UiRect Other)? bestX = null, bestY = null;
        foreach (var o in others)
        {
            foreach (var target in new[] { o.X, o.X + o.Width / 2, o.Right })
                foreach (var mine in mineX)
                    if (Math.Abs(target - mine) < threshold && (bestX == null || Math.Abs(target - mine) < Math.Abs(bestX.Value.Delta))) bestX = (target - mine, target, o);
            foreach (var target in new[] { o.Y, o.Y + o.Height / 2, o.Bottom })
                foreach (var mine in mineY)
                    if (Math.Abs(target - mine) < threshold && (bestY == null || Math.Abs(target - mine) < Math.Abs(bestY.Value.Delta))) bestY = (target - mine, target, o);
        }
        if (bestX is { } bx) guides.Add((new Point(bx.At, Math.Min(r.Y, bx.Other.Y)), new Point(bx.At, Math.Max(r.Bottom, bx.Other.Bottom))));
        if (bestY is { } by) guides.Add((new Point(Math.Min(r.X, by.Other.X), by.At), new Point(Math.Max(r.Right, by.Other.Right), by.At)));
        return (bestX?.Delta ?? 0, bestY?.Delta ?? 0);
    }

    private void Resize(Drag d, double dx, double dy, KeyModifiers modifiers)
    {
        var o = d.Original; var k = d.Kind;
        var rect = d.Widget.Value("rect") as JsonObject;
        bool sized = UiLayout.Member(rect, "width") > 0 || UiLayout.Member(rect, "height") > 0;
        if (!sized)
        {
            // Picture-sized widgets scale uniformly from the opposite corner.
            double w = k.Contains('e') ? o.Width + dx : o.Width - dx, h = k.Contains('s') ? o.Height + dy : o.Height - dy;
            double factor = Math.Max(0.05, Math.Max(w / Math.Max(1, o.Width), h / Math.Max(1, o.Height)));
            double scale = Math.Round(UiLayout.Member(rect, "scale", 1) * factor, 2);
            factor = scale / UiLayout.Member(rect, "scale", 1);
            double nw = o.Width * factor, nh = o.Height * factor;
            d.Preview = new UiRect(k.Contains('w') ? o.Right - nw : o.X, k.Contains('n') ? o.Bottom - nh : o.Y, nw, nh);
            return;
        }
        double left = o.X, top = o.Y, right = o.Right, bottom = o.Bottom;
        if (k.Contains('w')) left += dx;
        if (k.Contains('e')) right += dx;
        if (k.Contains('n')) top += dy;
        if (k.Contains('s')) bottom += dy;
        if (modifiers.HasFlag(KeyModifiers.Shift) && k.Length == 2 && o.Width > 0 && o.Height > 0)
        {
            double ratio = o.Width / o.Height, width = right - left, height = bottom - top;
            if (Math.Abs(width / ratio) > Math.Abs(height)) { var nh = width / ratio; if (k.Contains('n')) top = bottom - nh; else bottom = top + nh; }
            else { var nw = height * ratio; if (k.Contains('w')) left = right - nw; else right = left + nw; }
        }
        guides.Clear();
        if (Snap && !modifiers.HasFlag(KeyModifiers.Control))
        {
            var (sx, sy) = Snapping(d.Widget, new UiRect(k.Contains('w') ? left : right, k.Contains('n') ? top : bottom, 0, 0));
            if (k.Contains('w')) left += sx; else if (k.Contains('e')) right += sx;
            if (k.Contains('n')) top += sy; else if (k.Contains('s')) bottom += sy;
        }
        if (right - left < 1) { if (k.Contains('w')) left = right - 1; else right = left + 1; }
        if (bottom - top < 1) { if (k.Contains('n')) top = bottom - 1; else bottom = top + 1; }
        d.Preview = new UiRect(left, top, right - left, bottom - top);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (panFrom != null) { panFrom = null; e.Pointer.Capture(null); Cursor = Cursor.Default; return; }
        var d = drag; drag = null; guides.Clear();
        e.Pointer.Capture(null);
        if (d is not { Moved: true }) { InvalidateVisual(); return; }
        var w = d.Widget; var rect = w.Value("rect") as JsonObject;
        double ps = w.ParentScale, x0 = UiLayout.Member(rect, "x"), y0 = UiLayout.Member(rect, "y");
        UiCanvasEdit edit;
        if (d.Kind == "move") edit = new(w, "move", x0 + d.Dx, y0 + d.Dy, null, null, null);
        else
        {
            bool sized = UiLayout.Member(rect, "width") > 0 || UiLayout.Member(rect, "height") > 0;
            double nx = Math.Round(x0 + (d.Preview.X - d.Original.X) / ps), ny = Math.Round(y0 + (d.Preview.Y - d.Original.Y) / ps);
            edit = sized ? new(w, "resize", nx, ny, Math.Round(d.Preview.Width / w.Scale), Math.Round(d.Preview.Height / w.Scale), null)
                : new(w, "scale", nx, ny, null, null, Math.Round(UiLayout.Member(rect, "scale", 1) * d.Preview.Width / Math.Max(1, d.Original.Width), 2));
        }
        InvalidateVisual();
        Committed?.Invoke(edit);
    }

    // ── Keyboard ──────────────────────────────────────────────────────────────────────────────────

    private readonly DispatcherTimer nudgeTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };

    /// <summary>Arrow keys nudge the selection; a burst of presses is written as one edit once they stop.</summary>
    public bool Nudge(double dx, double dy)
    {
        if (Selected is not { Parent: not null } s || s.Flag("fitToParent")) return false;
        nudge = nudge is { } n && n.Widget == s ? (s, n.Dx + dx, n.Dy + dy) : (s, dx, dy);
        nudgeTimer.Stop(); nudgeTimer.Start();
        InvalidateVisual(); ViewChanged?.Invoke();
        return true;
    }

    /// <summary>Writes a pending nudge now (before anything else changes the document or the selection).</summary>
    public void CommitNudge()
    {
        nudgeTimer.Stop();
        if (nudge is not { } n) return;
        nudge = null;
        if (n.Dx == 0 && n.Dy == 0) return;
        var rect = n.Widget.Value("rect") as JsonObject;
        Committed?.Invoke(new(n.Widget, "move", UiLayout.Member(rect, "x") + n.Dx, UiLayout.Member(rect, "y") + n.Dy, null, null, null));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Space && !spaceHeld) { spaceHeld = true; Cursor = new Cursor(StandardCursorType.Hand); e.Handled = true; return; }
        double step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
        bool handled = e.Key switch
        {
            Key.Left => Nudge(-step, 0), Key.Right => Nudge(step, 0), Key.Up => Nudge(0, -step), Key.Down => Nudge(0, step),
            _ => false
        };
        if (handled) { e.Handled = true; return; }
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.Key == Key.Space) { spaceHeld = false; Cursor = Cursor.Default; e.Handled = true; return; }
        base.OnKeyUp(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { Pan += new Vector(e.Delta.Y * 60, 0); fit = null; InvalidateVisual(); ViewChanged?.Invoke(); }
        else ZoomAt(Math.Pow(1.15, e.Delta.Y), e.GetPosition(this));
        e.Handled = true;
    }
}

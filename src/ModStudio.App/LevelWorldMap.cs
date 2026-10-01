using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ModStudio.Core;

namespace ModStudio.App;

/// <summary>
/// An act's world seen from above, in tiles: each level a rectangle at its offset and size, links between them, the level
/// being edited outlined. Hovering a level describes it; clicking opens it, double-clicking opens its map in Level Editor;
/// dragging across free space draws a new level. Ctrl+wheel zooms, right- or middle-drag pans.
/// </summary>
internal sealed class LevelWorldMap : Control
{
    private static readonly Typeface Face = new("Segoe UI, Inter, sans-serif");
    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#101113")), GridText = new SolidColorBrush(Color.Parse("#5C5E63")),
        Label = new SolidColorBrush(Color.Parse("#E6E6E6")), SubLabel = new SolidColorBrush(Color.Parse("#A8A29A")), CardFill = new SolidColorBrush(Color.FromArgb(240, 23, 24, 26));
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1), CardPen = new Pen(new SolidColorBrush(Color.Parse("#4A4030")), 1),
        SelectedPen = new Pen(Brushes.White, 2.5), HoverPen = new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), 1.5, new DashStyle([4, 3], 0)),
        OverlapPen = new Pen(Brushes.Salmon, 2), LinkPen = new Pen(new SolidColorBrush(Color.FromArgb(70, 199, 179, 119)), 1),
        OneWayPen = new Pen(new SolidColorBrush(Color.FromArgb(80, 217, 146, 74)), 1, new DashStyle([4, 3], 0)),
        StrongLinkPen = new Pen(new SolidColorBrush(Color.Parse("#C7B377")), 2), StrongOneWayPen = new Pen(new SolidColorBrush(Color.Parse("#D9924A")), 2, new DashStyle([4, 3], 0));
    internal static readonly Color PresetColor = Color.Parse("#C7B377"), MazeColor = Color.Parse("#8FB3FF"), OutdoorColor = Color.Parse("#69C6B3"), OtherColor = Color.Parse("#A8A29A");

    private LevelWorldLayout? layout;
    private IReadOnlyDictionary<string, string> titles = new Dictionary<string, string>();
    private string? selected;
    private Point? panFrom;
    private Point? pressAt, pointer;
    private (int X, int Y)? drawFrom;

    /// <summary>A level was clicked.</summary>
    public event Action<string>? LevelClicked;
    /// <summary>A level was double-clicked.</summary>
    public event Action<string>? LevelOpened;
    /// <summary>A rectangle was drawn across free space: its offset and size in tiles.</summary>
    public event Action<int, int, int, int>? CreateRequested;
    /// <summary>The lines the hover card shows for a level (its name first).</summary>
    public Func<string, IReadOnlyList<string>>? Describe { get; set; }
    /// <summary>The levels a drawn rectangle would overlap, in any difficulty.</summary>
    public Func<int, int, int, int, IReadOnlyList<string>>? Collisions { get; set; }

    internal double Zoom { get; private set; } = 1;
    internal Vector Pan { get; private set; }
    internal string? Hovered { get; private set; }
    /// <summary>The rectangle being drawn, in tiles.</summary>
    internal (int X, int Y, int Width, int Height)? Drawing { get; private set; }
    internal LevelWorldLayout? Layout => layout;
    private bool fitPending = true;

    public LevelWorldMap()
    {
        ClipToBounds = true; Focusable = true; Cursor = new Cursor(StandardCursorType.Cross);
        // Clicking the map focuses it (for Escape); that must not scroll the builder under the pointer mid-drag.
        ScrollViewer.SetBringIntoViewOnFocusChange(this, false);
    }

    /// <summary>Shows a layout. The view fits the act when <paramref name="refit"/> or the first time it has a size.</summary>
    public void SetLayout(LevelWorldLayout value, IReadOnlyDictionary<string, string> names, string? selectedId, bool refit)
    {
        layout = value; titles = names; selected = selectedId;
        if (Hovered != null && value.Find(Hovered) == null) Hovered = null;
        if (refit) fitPending = true;
        if (fitPending && Bounds.Width > 0) Fit();
        InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (fitPending && layout != null) Fit();
    }

    /// <summary>Zooms to show the whole act with a margin.</summary>
    public void Fit()
    {
        if (layout == null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        fitPending = false;
        var (x, y, width, height) = layout.Bounds();
        double margin = Math.Max(20, Math.Max(width, height) * 0.06);
        Zoom = Math.Clamp(Math.Min((Bounds.Width - 40) / (width + 2 * margin), (Bounds.Height - 40) / (height + 2 * margin)), 0.02, 40);
        Pan = new Vector(Bounds.Width / 2 - (x + width / 2.0) * Zoom, Bounds.Height / 2 - (y + height / 2.0) * Zoom);
        InvalidateVisual();
    }

    /// <summary>Zooms about the middle of the view (buttons) or a point (Ctrl+wheel).</summary>
    public void ZoomBy(double factor, Point? about = null)
    {
        var at = about ?? new Point(Bounds.Width / 2, Bounds.Height / 2);
        var world = ToWorld(at);
        Zoom = Math.Clamp(Zoom * factor, 0.02, 40);
        Pan = new Vector(at.X - world.X * Zoom, at.Y - world.Y * Zoom);
        InvalidateVisual();
    }

    /// <summary>Brings a level into view when it is outside it.</summary>
    public void Reveal(string id)
    {
        if (layout?.Find(id) is not { } level || Bounds.Width <= 0) return;
        var rect = ToView(level);
        if (new Rect(Bounds.Size).Intersects(rect)) return;
        Pan += new Vector(Bounds.Width / 2 - rect.Center.X, Bounds.Height / 2 - rect.Center.Y);
        InvalidateVisual();
    }

    private Point ToWorld(Point view) => new((view.X - Pan.X) / Zoom, (view.Y - Pan.Y) / Zoom);
    private Rect ToView(WorldLevel level) => ToView(level.X, level.Y, level.Width, level.Height);
    private Rect ToView(double x, double y, double width, double height) => new(x * Zoom + Pan.X, y * Zoom + Pan.Y, width * Zoom, height * Zoom);
    private (int X, int Y) Tile(Point view) { var world = ToWorld(view); return ((int)Math.Floor(world.X), (int)Math.Floor(world.Y)); }

    /// <summary>The smallest level under a point, so a treasure room inside a big maze rectangle can still be picked.</summary>
    internal WorldLevel? LevelAt(Point view)
    {
        if (layout == null) return null;
        var world = ToWorld(view);
        return layout.Placed.Where(l => world.X >= l.X && world.X < l.X + l.Width && world.Y >= l.Y && world.Y < l.Y + l.Height).MinBy(l => (long)l.Width * l.Height);
    }

    internal static Color ColorOf(int drlgType) => drlgType switch { 1 => MazeColor, 2 => PresetColor, 3 => OutdoorColor, _ => OtherColor };

    // ── Drawing ────────────────────────────────────────────────────────────────────────────────────

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Background, bounds);
        if (layout == null) return;
        DrawGrid(context, bounds);

        var overlapping = layout.Overlaps.SelectMany(o => new[] { o.A, o.B }).ToHashSet(StringComparer.Ordinal);
        var focus = new[] { selected, Hovered }.Where(f => f != null).ToHashSet(StringComparer.Ordinal);
        // Links under the levels: faint, except those of the level hovered or being edited.
        foreach (var link in layout.Links)
        {
            if (layout.Find(link.From) is not { } from || layout.Find(link.To) is not { } to || link.Back && string.CompareOrdinal(link.From, link.To) > 0 && !focus.Contains(link.From)) continue;
            bool strong = focus.Contains(link.From) || focus.Contains(link.To);
            context.DrawLine(link.Back ? strong ? StrongLinkPen : LinkPen : strong ? StrongOneWayPen : OneWayPen, ToView(from).Center, ToView(to).Center);
        }
        // Big levels first, so rooms placed inside a maze's reserved square stay visible.
        foreach (var level in layout.Placed.OrderByDescending(l => (long)l.Width * l.Height))
        {
            var rect = ToView(level);
            if (!rect.Intersects(bounds)) continue;
            var color = ColorOf(level.Source.DrlgType);
            bool dashed = level.Placement is WorldPlacement.Outdoor or WorldPlacement.Relative;
            context.DrawRectangle(new SolidColorBrush(color, level.Id == selected ? 0.42 : 0.2), new Pen(new SolidColorBrush(color), 1, dashed ? new DashStyle([5, 3], 0) : null), rect);
            if (overlapping.Contains(level.Id)) context.DrawRectangle(null, OverlapPen, rect.Deflate(1));
            if (rect.Width >= 46 && rect.Height >= 16)
            {
                using (context.PushClip(rect.Deflate(2)))
                {
                    var name = Text(titles.GetValueOrDefault(level.Id) is { Length: > 0 } title ? title : level.Source.Name, 12, Label);
                    context.DrawText(name, rect.TopLeft + new Point(5, 3));
                    if (rect.Height >= 34) context.DrawText(Text($"Id {level.Id} · {level.Width}×{level.Height}", 10, SubLabel), rect.TopLeft + new Point(5, 18));
                }
            }
        }
        if (selected != null && layout.Find(selected) is { } current) context.DrawRectangle(null, SelectedPen, ToView(current).Inflate(2));
        if (Hovered != null && Hovered != selected && layout.Find(Hovered) is { } hover) context.DrawRectangle(null, HoverPen, ToView(hover).Inflate(1));

        if (Drawing is { } draw)
        {
            var blocked = Collisions?.Invoke(draw.X, draw.Y, draw.Width, draw.Height) ?? [];
            var tint = blocked.Count > 0 ? Colors.Salmon : Colors.White;
            var rect = ToView(draw.X, draw.Y, draw.Width, draw.Height);
            context.DrawRectangle(new SolidColorBrush(tint, 0.15), new Pen(new SolidColorBrush(tint), 1.5, new DashStyle([6, 3], 0)), rect);
            var lines = new List<string> { $"{draw.Width} × {draw.Height} tiles at {draw.X}, {draw.Y}" };
            lines.Add(blocked.Count > 0 ? "Overlaps " + string.Join(", ", blocked.Take(3)) + (blocked.Count > 3 ? "…" : "") : "Release to add a level here");
            DrawCard(context, bounds, rect.BottomRight + new Point(8, 8), lines, blocked.Count > 0 ? Brushes.Salmon : Label);
        }
        else if (Hovered != null && pointer is { } at && Describe?.Invoke(Hovered) is { Count: > 0 } description)
            DrawCard(context, bounds, at + new Point(16, 16), description, Label);
    }

    private void DrawGrid(DrawingContext context, Rect bounds)
    {
        // Lines at a round number of tiles, at least ~70 pixels apart.
        double step = new[] { 10.0, 20, 50, 100, 200, 500, 1000, 2000, 5000 }.FirstOrDefault(s => s * Zoom >= 70, 10000);
        var topLeft = ToWorld(bounds.TopLeft); var bottomRight = ToWorld(bounds.BottomRight);
        for (double x = Math.Floor(topLeft.X / step) * step; x <= bottomRight.X; x += step)
        {
            double vx = x * Zoom + Pan.X;
            context.DrawLine(GridPen, new Point(vx, 0), new Point(vx, bounds.Height));
            context.DrawText(Text(x.ToString("0", CultureInfo.InvariantCulture), 10, GridText), new Point(vx + 3, 2));
        }
        for (double y = Math.Floor(topLeft.Y / step) * step; y <= bottomRight.Y; y += step)
        {
            double vy = y * Zoom + Pan.Y;
            context.DrawLine(GridPen, new Point(0, vy), new Point(bounds.Width, vy));
            context.DrawText(Text(y.ToString("0", CultureInfo.InvariantCulture), 10, GridText), new Point(3, vy + 2));
        }
    }

    private static void DrawCard(DrawingContext context, Rect bounds, Point at, IReadOnlyList<string> lines, IBrush first)
    {
        var texts = lines.Select((line, i) => Text(line, i == 0 ? 13 : 11, i == 0 ? first : SubLabel)).ToArray();
        double width = texts.Max(t => t.Width) + 20, height = texts.Sum(t => t.Height + 2) + 14;
        // Keep the card inside the view: flip it to the other side of the pointer when it would run off.
        double x = at.X + width > bounds.Width ? Math.Max(4, at.X - width - 24) : at.X, y = at.Y + height > bounds.Height ? Math.Max(4, at.Y - height - 24) : at.Y;
        var card = new Rect(x, y, width, height);
        context.DrawRectangle(CardFill, CardPen, card, 4, 4);
        double top = y + 7;
        foreach (var text in texts) { context.DrawText(text, new Point(x + 10, top)); top += text.Height + 2; }
    }

    private static FormattedText Text(string text, double size, IBrush brush) => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, size, brush);

    // ── Pointer ────────────────────────────────────────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsMiddleButtonPressed || point.Properties.IsRightButtonPressed)
        {
            panFrom = point.Position; e.Pointer.Capture(this); Cursor = new Cursor(StandardCursorType.SizeAll); e.Handled = true; return;
        }
        if (!point.Properties.IsLeftButtonPressed || layout == null) return;
        e.Handled = true;
        if (LevelAt(point.Position) is { } level)
        {
            if (e.ClickCount == 2) LevelOpened?.Invoke(level.Id);
            else LevelClicked?.Invoke(level.Id);
            return;
        }
        pressAt = point.Position; drawFrom = Tile(point.Position);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var view = e.GetPosition(this);
        pointer = view;
        if (panFrom is { } from) { Pan += view - from; panFrom = view; InvalidateVisual(); return; }
        if (drawFrom is { } start && pressAt is { } press)
        {
            if (Drawing == null && Math.Abs(view.X - press.X) < 4 && Math.Abs(view.Y - press.Y) < 4) return;
            var end = Tile(view);
            int x = Math.Min(start.X, end.X), y = Math.Min(start.Y, end.Y);
            Drawing = (x, y, Math.Max(1, Math.Abs(end.X - start.X) + 1), Math.Max(1, Math.Abs(end.Y - start.Y) + 1));
            InvalidateVisual(); return;
        }
        var hover = LevelAt(view)?.Id;
        if (hover != Hovered || hover != null) { Hovered = hover; InvalidateVisual(); }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        pointer = null;
        if (Hovered != null && drawFrom == null) { Hovered = null; InvalidateVisual(); }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        // Read the gesture before releasing capture: losing capture cancels whatever is in progress.
        bool panned = panFrom != null;
        var drawn = Drawing; Drawing = null; drawFrom = null; pressAt = null; panFrom = null;
        e.Pointer.Capture(null);
        InvalidateVisual();
        if (panned) { Cursor = new Cursor(StandardCursorType.Cross); return; }
        if (drawn is { } rect && (Collisions?.Invoke(rect.X, rect.Y, rect.Width, rect.Height) ?? []).Count == 0) CreateRequested?.Invoke(rect.X, rect.Y, rect.Width, rect.Height);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        panFrom = null; Drawing = null; drawFrom = null; pressAt = null; InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Drawing != null) { Drawing = null; drawFrom = null; pressAt = null; InvalidateVisual(); e.Handled = true; return; }
        base.OnKeyDown(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        // A plain wheel keeps scrolling the builder; Ctrl+wheel zooms the map.
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        ZoomBy(Math.Pow(1.2, e.Delta.Y), e.GetPosition(this));
        e.Handled = true;
    }
}

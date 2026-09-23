using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using ModStudio.Core;

namespace ModStudio.App;

/// <summary>Raised by a preview link: the cells the clicked text was read from.</summary>
public sealed class PreviewLinkEventArgs(RoutedEvent routedEvent, CellLink[] targets, Control source) : RoutedEventArgs(routedEvent)
{
    public CellLink[] Targets { get; } = targets;
    public Control Anchor { get; } = source;
}

/// <summary>
/// Selectable preview text whose linked runs open the cells they were read from. A click without a drag follows the link
/// (<see cref="LinkClickedEvent"/> bubbles to the window); dragging still selects text to copy.
/// </summary>
public sealed class PreviewLinkText : SelectableTextBlock
{
    public static readonly RoutedEvent<PreviewLinkEventArgs> LinkClickedEvent =
        RoutedEvent.Register<PreviewLinkText, PreviewLinkEventArgs>("LinkClicked", RoutingStrategies.Bubble);
    public static readonly IBrush LinkBrush = new SolidColorBrush(Color.Parse("#8FC1FF"));
    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);
    private readonly List<LinkSpan> links = [];
    private readonly List<Run> runs = [];
    private LinkSpan? hovered;
    public IReadOnlyList<LinkSpan> Links => links;
    /// <summary>The whole text, links included; <see cref="TextBlock.Text"/> is empty once the text is built from runs.</summary>
    public string PlainText { get; }
    private static readonly Lazy<Cursor> Hand = new(() => new Cursor(StandardCursorType.Hand));
    private Point? pressed;

    public PreviewLinkText(IReadOnlyList<PreviewText> lines)
    {
        PlainText = string.Join("\n", lines.Select(l => l.Text));
        // Text without links stays plain Text, the same as any other card text.
        if (lines.All(l => l.Links.Length == 0)) { Text = PlainText; return; }
        var inlines = new InlineCollection();
        int offset = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            // Lines are joined with "\n" inside the runs, so text positions count one character per line break.
            var line = lines[i]; var text = i < lines.Count - 1 ? line.Text + "\n" : line.Text;
            int at = 0;
            foreach (var link in line.Links)
            {
                if (link.Start > at) inlines.Add(new Run(text[at..link.Start]));
                var run = new Run(text.Substring(link.Start, link.Length)) { Foreground = LinkBrush };
                inlines.Add(run); runs.Add(run);
                links.Add(link with { Start = offset + link.Start });
                at = link.Start + link.Length;
            }
            if (at < text.Length) inlines.Add(new Run(text[at..]));
            offset += text.Length;
        }
        Inlines = inlines;
    }

    /// <summary>The link under a point in this control's coordinates, if any.</summary>
    public LinkSpan? LinkAt(Point point)
    {
        if (links.Count == 0) return null;
        // HitTestPoint snaps a point past the end of a line to its last character, so the point must also fall inside
        // the link's own rectangles.
        var local = point - new Point(Padding.Left, Padding.Top);
        int position = TextLayout.HitTestPoint(local).TextPosition;
        return links.FirstOrDefault(l => position >= l.Start && position < l.Start + l.Length && TextLayout.HitTestTextRange(l.Start, l.Length).Any(r => r.Contains(local)));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Hover(LinkAt(e.GetPosition(this)));
    }
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        Hover(null);
    }
    /// <summary>Links read as colored text; the one under the pointer is underlined and names the cells it opens.</summary>
    private void Hover(LinkSpan? link)
    {
        if (ReferenceEquals(link, hovered)) return;
        if (hovered != null) runs[links.IndexOf(hovered)].TextDecorations = null;
        hovered = link;
        if (link != null) runs[links.IndexOf(link)].TextDecorations = Avalonia.Media.TextDecorations.Underline;
        Cursor = link != null ? Hand.Value : null;
        ToolTip.SetTip(this, link == null ? null : string.Join("\n", link.Targets.Select(t => "Open " + t)));
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        pressed = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ? e.GetPosition(this) : null;
        base.OnPointerPressed(e);
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var start = pressed; pressed = null;
        if (start is not { } from || e.InitialPressMouseButton != MouseButton.Left) return;
        var at = e.GetPosition(this);
        // A drag is a text selection, not a click.
        if (Math.Abs(at.X - from.X) > 4 || Math.Abs(at.Y - from.Y) > 4 || LinkAt(at) is not { } link || LinkAt(from) != link) return;
        ClearSelection();
        RaiseEvent(new PreviewLinkEventArgs(LinkClickedEvent, link.Targets, this));
    }
}

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using ModStudio.Core;

namespace ModStudio.App;

/// <summary>
/// JetBrains-style change viewer: HEAD on the left, the working copy on the right, scrolled together, with filler rows keeping
/// matching lines level. A unified patch view is one toggle away for people who prefer it.
/// </summary>
public sealed class DiffPane : Grid
{
    private static readonly IBrush AddedBack = new SolidColorBrush(Color.Parse("#1E3A21")), RemovedBack = new SolidColorBrush(Color.Parse("#452424")), ModifiedBack = new SolidColorBrush(Color.Parse("#3B3618")),
        FillerBack = new SolidColorBrush(Color.Parse("#242526")), FillerHatch = new SolidColorBrush(Color.Parse("#2E2F31")), NumberBrush = new SolidColorBrush(Color.Parse("#6F6A60")), Divider = new SolidColorBrush(Color.Parse("#494038"));
    /// <summary>Whether the unified patch is shown instead of two columns; remembered for the session.</summary>
    public static bool Unified { get; set; }

    private readonly TextBlock title = new() { FontWeight = FontWeight.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock summary = new() { Classes = { "muted" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new(10, 0, 0, 0) };
    private readonly Button previous = new() { Classes = { "explorerTool" } }, next = new() { Classes = { "explorerTool" } }, layout = new() { Padding = new(8, 3), MinHeight = 0, FontSize = 12 };
    private readonly Grid sides = new() { ColumnDefinitions = new("*,1,*") };
    private readonly TextEditor left = Editor(), right = Editor(), unified = Editor();
    private readonly TextBlock leftHeader = Header("HEAD"), rightHeader = Header("Working copy");
    private readonly TextBlock notice = new() { Classes = { "muted" }, Margin = new(20), TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private IReadOnlyList<DiffRow> rows = [];
    private IReadOnlyList<int> changeStarts = [];
    private int current = -1;
    private bool syncing;
    public GitChange Change { get; private set; }
    internal IReadOnlyList<DiffRow> Rows => rows;
    internal string Summary => summary.Text ?? "";
    internal string LeftText => left.Document.Text;
    internal string RightText => right.Document.Text;
    internal string UnifiedText => unified.Document.Text;
    internal void ToggleLayoutForTests() => ApplyLayout();

    public DiffPane(GitChange change)
    {
        Change = change;
        RowDefinitions = new("Auto,*");
        var toolbar = new DockPanel { Margin = new(10, 6, 6, 6) };
        var nav = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(nav, Dock.Right);
        previous.Content = ExplorerIcons.Tool("M4,10 L8,6 L12,10"); ToolTip.SetTip(previous, "Previous change (Shift+F7)");
        next.Content = ExplorerIcons.Tool("M4,6 L8,10 L12,6"); ToolTip.SetTip(next, "Next change (F7)");
        ToolTip.SetTip(layout, "Switch between side-by-side and unified patch views");
        nav.Children.Add(previous); nav.Children.Add(next); nav.Children.Add(layout);
        toolbar.Children.Add(nav); toolbar.Children.Add(title); toolbar.Children.Add(summary);
        Children.Add(toolbar);
        var body = new Grid(); SetRow(body, 1); Children.Add(body);
        var leftColumn = new DockPanel(); DockPanel.SetDock(leftHeader, Dock.Top); leftColumn.Children.Add(leftHeader); leftColumn.Children.Add(left);
        var rightColumn = new DockPanel(); DockPanel.SetDock(rightHeader, Dock.Top); rightColumn.Children.Add(rightHeader); rightColumn.Children.Add(right);
        var divider = new Border { Background = Divider }; SetColumn(divider, 1); SetColumn(rightColumn, 2);
        sides.Children.Add(leftColumn); sides.Children.Add(divider); sides.Children.Add(rightColumn);
        body.Children.Add(sides); body.Children.Add(unified); body.Children.Add(notice);
        foreach (var (editor, side) in new[] { (left, 0), (right, 1) })
        {
            editor.TextArea.TextView.BackgroundRenderers.Add(new RowBackground(this, side));
            editor.TextArea.LeftMargins.Insert(0, new RowNumberMargin(this, side));
            editor.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(editor == left ? left : right, editor == left ? right : left);
        }
        unified.TextArea.TextView.LineTransformers.Add(new DiffColorizer());
        previous.Click += (_, _) => Navigate(-1); next.Click += (_, _) => Navigate(1);
        layout.Click += (_, _) => { Unified = !Unified; ApplyLayout(); };
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.F7) { Navigate(e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift) ? -1 : 1); e.Handled = true; } };
        ApplyLayout();
    }

    private static TextEditor Editor() => new()
    {
        IsReadOnly = true, ShowLineNumbers = false, FontFamily = new("Consolas, Menlo, monospace"), FontSize = 12, Background = new SolidColorBrush(Color.Parse("#202122")), WordWrap = false,
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Options = { EnableHyperlinks = false, EnableEmailHyperlinks = false, AllowScrollBelowDocument = false },
    };
    private static TextBlock Header(string text) => new() { Text = text, FontSize = 11, Classes = { "muted" }, Margin = new(10, 4), FontWeight = FontWeight.SemiBold };

    /// <summary>Replaces the content; the unified text is git's own patch, the rows come from a full-context diff.</summary>
    public void Show(GitChange change, string unifiedText, IReadOnlyList<DiffRow> diffRows, bool binary)
    {
        Change = change;
        title.Text = change.Path; ToolTip.SetTip(title, change.Kind + (change.OriginalPath == null ? "" : " from " + change.OriginalPath));
        leftHeader.Text = change.Kind is GitChangeKind.Untracked or GitChangeKind.Added ? "Not in HEAD" : "HEAD" + (change.OriginalPath == null ? "" : " · " + change.OriginalPath);
        rightHeader.Text = change.Kind == GitChangeKind.Deleted ? "Deleted" : change.Staged && !change.Unstaged ? "Staged" : "Working copy";
        var verticalBefore = left.TextArea.TextView.ScrollOffset.Y;
        rows = diffRows; changeStarts = UnifiedDiff.ChangeStarts(rows); current = -1;
        left.Document = new TextDocument(string.Join('\n', rows.Select(r => r.Left ?? "")));
        right.Document = new TextDocument(string.Join('\n', rows.Select(r => r.Right ?? "")));
        unified.Document = new TextDocument(unifiedText);
        int added = rows.Count(r => r.Kind == DiffRowKind.Added), removed = rows.Count(r => r.Kind == DiffRowKind.Removed), modified = rows.Count(r => r.Kind == DiffRowKind.Modified);
        summary.Text = binary ? "Binary file" : changeStarts.Count == 0 ? "No differences" : $"{changeStarts.Count} change{(changeStarts.Count == 1 ? "" : "s")} · +{added + modified} −{removed + modified}";
        notice.Text = binary ? $"{change.Path} is a binary file; git cannot show a text diff." : rows.Count == 0 && unifiedText.Trim().Length == 0 ? "No differences between HEAD and the working copy." : "";
        notice.IsVisible = notice.Text.Length > 0;
        previous.IsEnabled = next.IsEnabled = changeStarts.Count > 0;
        ApplyLayout();
        if (verticalBefore > 0) { left.ScrollToVerticalOffset(verticalBefore); right.ScrollToVerticalOffset(verticalBefore); }
        else if (changeStarts.Count > 0) Navigate(1);
    }

    private void ApplyLayout()
    {
        bool showSides = !Unified && !notice.IsVisible;
        sides.IsVisible = showSides; unified.IsVisible = Unified && !notice.IsVisible;
        layout.Content = Unified ? "Side by side" : "Unified";
        previous.IsVisible = next.IsVisible = !Unified;
    }

    private void Navigate(int direction)
    {
        if (changeStarts.Count == 0) return;
        current = ((current + direction) % changeStarts.Count + changeStarts.Count) % changeStarts.Count;
        int line = changeStarts[current] + 1;
        foreach (var editor in new[] { left, right })
        {
            editor.TextArea.Caret.Line = line; editor.TextArea.Caret.Column = 1;
            var top = editor.TextArea.TextView.GetVisualTopByDocumentLine(line);
            editor.ScrollToVerticalOffset(Math.Max(0, top - editor.TextArea.TextView.Bounds.Height / 3));
        }
        var text = summary.Text ?? ""; int cut = text.IndexOf(" · ", StringComparison.Ordinal);
        summary.Text = (cut < 0 ? text : text[..cut]) + $" · {current + 1} of {changeStarts.Count}";
    }

    private void Sync(TextEditor source, TextEditor target)
    {
        if (syncing) return; syncing = true;
        try
        {
            var offset = source.TextArea.TextView.ScrollOffset;
            if (Math.Abs(target.TextArea.TextView.ScrollOffset.Y - offset.Y) > 0.5) target.ScrollToVerticalOffset(offset.Y);
            if (Math.Abs(target.TextArea.TextView.ScrollOffset.X - offset.X) > 0.5) target.ScrollToHorizontalOffset(offset.X);
        }
        finally { syncing = false; }
    }

    private DiffRow? Row(int lineNumber) => lineNumber >= 1 && lineNumber <= rows.Count ? rows[lineNumber - 1] : null;

    /// <summary>Paints each row by kind; filler rows (no line on this side) get a hatched, darker band.</summary>
    private sealed class RowBackground(DiffPane pane, int side) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;
        public void Draw(TextView textView, DrawingContext context)
        {
            if (textView.VisualLinesValid == false) return;
            double width = textView.Bounds.Width + textView.ScrollOffset.X;
            foreach (var visual in textView.VisualLines)
            {
                var row = pane.Row(visual.FirstDocumentLine.LineNumber); if (row == null || row.Kind == DiffRowKind.Equal) continue;
                bool filler = side == 0 ? row.Left == null : row.Right == null;
                var brush = filler ? FillerBack : row.Kind == DiffRowKind.Modified ? ModifiedBack : row.Kind == DiffRowKind.Added ? AddedBack : RemovedBack;
                var rect = new Rect(0, visual.VisualTop - textView.ScrollOffset.Y, width, visual.Height);
                context.FillRectangle(brush, rect);
                if (filler) for (double x = -rect.Height; x < width; x += 8) context.DrawLine(new Pen(FillerHatch, 1), new Point(x, rect.Bottom), new Point(x + rect.Height, rect.Top));
            }
        }
    }

    /// <summary>Line numbers of the underlying file for this side; filler rows show nothing.</summary>
    private sealed class RowNumberMargin(DiffPane pane, int side) : AbstractMargin
    {
        private static readonly Typeface Face = new("Consolas, Menlo, monospace");
        protected override Size MeasureOverride(Size availableSize)
        {
            int digits = Math.Max(3, (pane.rows.Count == 0 ? 1 : pane.rows.Max(r => (side == 0 ? r.LeftLine : r.RightLine) ?? 1)).ToString(CultureInfo.InvariantCulture).Length);
            return new Size(Text(new string('9', digits)).Width + 14, 0);
        }
        private static FormattedText Text(string s) => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 11, NumberBrush);
        protected override void OnTextViewChanged(TextView oldTextView, TextView newTextView)
        {
            if (oldTextView != null) oldTextView.VisualLinesChanged -= Redraw;
            base.OnTextViewChanged(oldTextView, newTextView);
            if (newTextView != null) newTextView.VisualLinesChanged += Redraw;
            InvalidateVisual();
        }
        private void Redraw(object? sender, EventArgs e) => InvalidateVisual();
        public override void Render(DrawingContext context)
        {
            var view = TextView; if (view == null || !view.VisualLinesValid) return;
            context.FillRectangle(new SolidColorBrush(Color.Parse("#1D1E1F")), new Rect(Bounds.Size));
            foreach (var visual in view.VisualLines)
            {
                var row = pane.Row(visual.FirstDocumentLine.LineNumber); var number = row == null ? null : side == 0 ? row.LeftLine : row.RightLine;
                if (number == null) continue;
                var text = Text(number.Value.ToString(CultureInfo.InvariantCulture));
                context.DrawText(text, new Point(Bounds.Width - text.Width - 7, visual.VisualTop - view.ScrollOffset.Y + (visual.Height - text.Height) / 2));
            }
        }
    }

    /// <summary>Colors unified diff lines the way IDE diff views do: additions green, removals red, hunk headers muted blue.</summary>
    public sealed class DiffColorizer : DocumentColorizingTransformer
    {
        private static readonly IBrush Add = new SolidColorBrush(Color.Parse("#8FCB7F")), Remove = new SolidColorBrush(Color.Parse("#E07A7A")), Hunk = new SolidColorBrush(Color.Parse("#7FB7C8")), Meta = new SolidColorBrush(Color.Parse("#8E8578"));
        protected override void ColorizeLine(DocumentLine line)
        {
            if (line.Length == 0) return;
            var text = CurrentContext.Document.GetText(line.Offset, Math.Min(line.Length, 4));
            IBrush? fore = null, back = null;
            if (text.StartsWith("+++") || text.StartsWith("---")) fore = Meta;
            else if (text.StartsWith('+')) { fore = Add; back = AddedBack; }
            else if (text.StartsWith('-')) { fore = Remove; back = RemovedBack; }
            else if (text.StartsWith("@@")) fore = Hunk;
            else if (text.StartsWith("diff") || text.StartsWith("inde") || text.StartsWith("comm") || text.StartsWith("Auth") || text.StartsWith("Date")) fore = Meta;
            if (fore == null) return;
            ChangeLinePart(line.Offset, line.EndOffset, element => { element.TextRunProperties.SetForegroundBrush(fore); if (back != null) element.TextRunProperties.SetBackgroundBrush(back); });
        }
    }
}

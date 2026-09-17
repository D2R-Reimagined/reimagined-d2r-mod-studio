using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using AvaloniaEdit.Rendering;
using System.Xml;

namespace ModStudio.App;

internal static class SourceCodeEditing
{
    /// <summary>Cut, copy, paste, undo, redo and select all on right-click, as the text editor has no menu of its own.</summary>
    public static void AttachContextMenu(TextEditor editor)
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, string gesture, Action action, Func<bool>? enabled = null)
        {
            var item = new MenuItem { Header = header, InputGesture = Avalonia.Input.KeyGesture.Parse(gesture) };
            item.Click += (_, _) => action();
            if (enabled != null) menu.Opening += (_, _) => item.IsEnabled = enabled();
            return item;
        }
        menu.ItemsSource = new Control[]
        {
            Item("Undo", "Ctrl+Z", () => editor.Undo(), () => editor.CanUndo), Item("Redo", "Ctrl+Y", () => editor.Redo(), () => editor.CanRedo), new Separator(),
            Item("Cut", "Ctrl+X", () => editor.Cut(), () => !editor.IsReadOnly && editor.SelectionLength > 0), Item("Copy", "Ctrl+C", () => editor.Copy(), () => editor.SelectionLength > 0),
            Item("Paste", "Ctrl+V", () => editor.Paste(), () => !editor.IsReadOnly), new Separator(),
            Item("Select all", "Ctrl+A", () => editor.SelectAll()),
        };
        editor.TextArea.ContextMenu = menu;
    }

    /// <summary>Highlights the bracket at the caret and its partner, the way code editors do, so a formula or JSON block can be followed by eye.</summary>
    public static void AttachBracketHighlighting(TextEditor editor)
    {
        var renderer = new BracketRenderer();
        editor.TextArea.TextView.BackgroundRenderers.Add(renderer);
        void Update()
        {
            var document = editor.Document; var next = document == null ? null : ModStudio.Core.TextChecks.MatchBracket(document.Text, editor.CaretOffset);
            if (next == renderer.Pair) return;
            renderer.Pair = next; editor.TextArea.TextView.InvalidateLayer(renderer.Layer);
        }
        editor.TextArea.Caret.PositionChanged += (_, _) => Update();
        editor.TextChanged += (_, _) => Update();
    }

    /// <summary>Marks lines that fail validation; the returned object is fed by the pane's live JSON check.</summary>
    public static ErrorMarks AttachErrorMarks(TextEditor editor)
    {
        var marks = new ErrorMarks(editor);
        editor.TextArea.TextView.BackgroundRenderers.Add(marks);
        return marks;
    }

    /// <summary>Red band under the offending line plus a marker at the error position.</summary>
    public sealed class ErrorMarks(TextEditor editor) : IBackgroundRenderer
    {
        private static readonly IBrush Band = new SolidColorBrush(Color.Parse("#33C0392B")), Marker = new SolidColorBrush(Color.Parse("#E06C5A"));
        private static readonly Pen Underline = new(Marker, 2);
        private readonly List<(int Line, int Column, string Message)> errors = [];
        public IReadOnlyList<(int Line, int Column, string Message)> Errors => errors;
        public KnownLayer Layer => KnownLayer.Selection;
        public void Set(IEnumerable<(int Line, int Column, string Message)> next)
        {
            var list = next.ToList();
            if (list.SequenceEqual(errors)) return;
            errors.Clear(); errors.AddRange(list); editor.TextArea.TextView.InvalidateLayer(Layer);
        }
        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            var document = textView.Document; if (document == null || errors.Count == 0) return;
            foreach (var (line, column, _) in errors)
            {
                if (line < 1 || line > document.LineCount) continue;
                var documentLine = document.GetLineByNumber(line);
                var builder = new BackgroundGeometryBuilder { AlignToWholePixels = true };
                builder.AddSegment(textView, documentLine);
                if (builder.CreateGeometry() is { } geometry) drawingContext.DrawGeometry(Band, null, geometry);
                // Underline from the error position to the end of the line (the parser stops at the first problem).
                int at = Math.Clamp(documentLine.Offset + Math.Max(0, column), documentLine.Offset, documentLine.EndOffset);
                var span = new TextSegment { StartOffset = at, Length = Math.Max(1, documentLine.EndOffset - at) };
                foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, span))
                    drawingContext.DrawLine(Underline, new Point(rect.Left, rect.Bottom - 1), new Point(rect.Right, rect.Bottom - 1));
            }
        }
    }

    private sealed class BracketRenderer : IBackgroundRenderer
    {
        private static readonly IBrush Fill = new SolidColorBrush(Color.Parse("#40D8BC86"));
        private static readonly Pen Outline = new(new SolidColorBrush(Color.Parse("#D8BC86")), 1);
        public (int Open, int Close)? Pair { get; set; }
        public KnownLayer Layer => KnownLayer.Selection;
        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (Pair is not { } pair || textView.Document == null) return;
            foreach (var offset in new[] { pair.Open, pair.Close })
            {
                if (offset < 0 || offset >= textView.Document.TextLength) continue;
                var builder = new BackgroundGeometryBuilder { AlignToWholePixels = true, CornerRadius = 1 };
                builder.AddSegment(textView, new TextSegment { StartOffset = offset, Length = 1 });
                if (builder.CreateGeometry() is { } geometry) drawingContext.DrawGeometry(Fill, Outline, geometry);
            }
        }
    }

    public static IHighlightingDefinition? Highlighting(string file)
    {
        var extension = System.IO.Path.GetExtension(file).ToLowerInvariant();
        if (extension is ".mjs" or ".cjs" or ".ts" or ".tsx" or ".jsx") extension = ".js";
        var definition = HighlightingManager.Instance.GetDefinitionByExtension(extension);
        if (extension is ".bat" or ".cmd")
        {
            const string batch = """
                <SyntaxDefinition name="Batch" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
                  <Color name="Comment" foreground="#6A9955" />
                  <Color name="Keyword" foreground="#569CD6" />
                  <Color name="String" foreground="#CE9178" />
                  <Color name="Variable" foreground="#9CDCFE" />
                  <RuleSet ignoreCase="true">
                    <Span color="Comment" begin="^\s*(?:rem\b|::)" />
                    <Span color="String" begin="&quot;" end="&quot;" />
                    <Rule color="Variable">%[^%\r\n]+%|%[0-9*]|![^!\r\n]+!</Rule>
                    <Keywords color="Keyword"><Word>echo</Word><Word>off</Word><Word>set</Word><Word>setlocal</Word><Word>endlocal</Word><Word>if</Word><Word>else</Word><Word>for</Word><Word>in</Word><Word>do</Word><Word>call</Word><Word>goto</Word><Word>exit</Word><Word>copy</Word><Word>xcopy</Word><Word>robocopy</Word><Word>del</Word><Word>mkdir</Word><Word>cd</Word><Word>pause</Word></Keywords>
                  </RuleSet>
                </SyntaxDefinition>
                """;
            using var reader = XmlReader.Create(new StringReader(batch));
            return HighlightingLoader.Load(reader, HighlightingManager.Instance);
        }
        if (definition != null)
            foreach (var color in definition.NamedHighlightingColors)
            {
                var name = color.Name.ToLowerInvariant();
                var hex = name.Contains("comment") ? "#6A9955" : name.Contains("string") || name.Contains("char") ? "#CE9178" : name.Contains("number") || name.Contains("digit") ? "#B5CEA8" : "#569CD6";
                color.Foreground = new SimpleHighlightingBrush(Color.Parse(hex));
            }
        return definition;
    }
}

using ModStudio.Core;
using Avalonia.Controls;
using AvaloniaEdit.Folding;
using Avalonia.Threading;

namespace ModStudio.App;

public sealed partial class EditorPane
{
    private FoldingManager? folding;
    private CancellationTokenSource? foldingWork;
    private int foldingRevision, appliedFoldingRevision = -1;
    private bool foldingAttached;
    internal int FoldingScanCount { get; private set; }
    internal Task PendingFolding { get; private set; } = Task.CompletedTask;
    internal int FoldedBlockCount => folding?.AllFoldings.Count(f => f.IsFolded) ?? 0;
    private void InitializeSourceFeatures(WrapPanel toolbar)
    {
        // Keep the line-number separator and folding gutter clear of the first character.
        Source.TextArea.TextView.Margin = new(12, 0, 0, 0);
        if (!System.IO.Path.GetExtension(Document.FilePath).Equals(".json", StringComparison.OrdinalIgnoreCase)) return;
        folding = FoldingManager.Install(Source.TextArea);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        timer.Tick += (_, _) => { timer.Stop(); PendingFolding = UpdateJsonFoldsAsync(); };
        Source.TextChanged += (_, _) => { foldingRevision++; foldingWork?.Cancel(); timer.Stop(); if (foldingAttached && Source.IsVisible) timer.Start(); };
        DetachedFromVisualTree += (_, _) => { foldingAttached = false; timer.Stop(); foldingWork?.Cancel(); };
        AttachedToVisualTree += (_, _) => { foldingAttached = true; PendingFolding = UpdateJsonFoldsAsync(); };
        Source.PropertyChanged += (_, e) => {
            if (e.Property.Name != nameof(Source.IsVisible)) return;
            timer.Stop();
            if (Source.IsVisible) PendingFolding = UpdateJsonFoldsAsync(); else foldingWork?.Cancel();
        };
        foreach (var collapse in new[] { true, false })
        {
            var button = EditorToolbarIcons.Create(collapse ? "Collapse JSON" : "Expand JSON");
            button.Click += (_, _) => { timer.Stop(); PendingFolding = UpdateJsonFoldsAsync(collapse); };
            button.IsVisible = Source.IsVisible; Source.PropertyChanged += (_, e) => { if (e.Property.Name == "IsVisible") button.IsVisible = Source.IsVisible; };
            toolbar.Children.Add(button);
        }
    }

    private async Task UpdateJsonFoldsAsync(bool? collapse = null)
    {
        if (folding == null || !foldingAttached || !Source.IsVisible) return;
        foldingWork?.Cancel();
        if (appliedFoldingRevision == foldingRevision)
        {
            if (collapse.HasValue) foreach (var fold in folding.AllFoldings) fold.IsFolded = collapse.Value;
            return;
        }
        var work = foldingWork = new CancellationTokenSource(); var token = work.Token;
        int current = foldingRevision;
        // AvaloniaEdit's immutable snapshot can be scanned away from the dispatcher while typing continues.
        var snapshot = Source.Document.CreateSnapshot();
        try
        {
            FoldingScanCount++;
            var folds = await Task.Run(() => JsonFolds(snapshot.Text, token).ToArray(), token);
            if (token.IsCancellationRequested || current != foldingRevision || !foldingAttached || !Source.IsVisible) return;
            folding.UpdateFoldings(folds, -1); appliedFoldingRevision = current;
            if (collapse.HasValue) foreach (var fold in folding.AllFoldings) fold.IsFolded = collapse.Value;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { error(ex); }
        finally { if (ReferenceEquals(foldingWork, work)) foldingWork = null; work.Dispose(); }
    }

    internal static IEnumerable<NewFolding> JsonFolds(string text, CancellationToken cancellation = default)
    {
        var stack = new Stack<(int Offset, char Kind, int Line)>();
        var folds = new List<NewFolding>();
        bool quoted = false, escaped = false; int line = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
            char c = text[i];
            if (c == '\n') line++;
            if (quoted) { if (escaped) escaped = false; else if (c == '\\') escaped = true; else if (c == '"') quoted = false; continue; }
            if (c == '"') { quoted = true; continue; }
            if (c is '{' or '[') stack.Push((i, c, line));
            else if (c is '}' or ']')
            {
                if (stack.Count == 0) continue;
                var start = stack.Pop();
                if ((start.Kind == '{') != (c == '}')) { stack.Clear(); continue; }
                if (line > start.Line) folds.Add(new NewFolding(start.Offset, i + 1) { Name = start.Kind == '{' ? "{ … }" : "[ … ]" });
            }
        }
        cancellation.ThrowIfCancellationRequested();
        return folds.OrderBy(f => f.StartOffset);
    }

    internal ContextMenu CreateColumnMenu(int index)
    {
        string name = Document.Table!.Columns[index];
        MenuItem Item(string label, Action action)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => { try { action(); } catch (Exception ex) { error(ex); } };
            return item;
        }
        var guide = Item("Column guide… (F1)", () => ShowColumnGuide(name));
        guide.IsEnabled = !Document.Table.IsCatalog && ColumnGuide.Find(Document.Table.Name, name) != null;
        var clearHighlights = Item("Clear all highlights", ClearHighlights);
        clearHighlights.IsEnabled = HasHighlights;
        return new ContextMenu { ItemsSource = new Control[] {
            guide, new Separator(),
            Item((frozenColumns.Contains(index) ? "Unfreeze " : "Freeze ") + name, () => ToggleFrozenColumn(name)),
            Item((Document.LockedColumns.Contains(name) ? "Unlock " : "Lock ") + name + " against edits", () => { if (!Document.LockedColumns.Remove(name)) Document.LockedColumns.Add(name); Refresh(); }),
            new Separator(),
            Item((highlightedColumns.Contains(index) ? "Remove highlight from " : "Highlight ") + name, () => ToggleColumnHighlight(index)),
            clearHighlights, new Separator(),
            Item("Fit this column to contents", () => { widths.Remove(index); fittedWidths.Remove(index); ApplyColumnWidths(); }),
            Item("Set column width…", async () => {
                if (TopLevel.GetTopLevel(this) is not Window owner) return;
                var input = new NumericUpDown { Minimum = 40, Maximum = 2000, Value = (decimal)(TableGrid.Columns.FirstOrDefault(c => columnMap.TryGetValue(c, out var ci) && ci == index)?.ActualWidth ?? FitColumn(index)), Increment = 10 };
                var apply = new Button { Content = "Apply width" };
                var dialog = new Window { Title = "Column width: " + name, Width = 330, Height = 170, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                dialog.Content = new StackPanel { Margin = new(18), Spacing = 10, Children = { input, apply } };
                apply.Click += (_, _) => { widths[index] = new((double)(input.Value ?? 80)); ApplyColumnWidths(); dialog.Close(); };
                await dialog.ShowDialog(owner);
            }),
            Item("Clear sorting", () => { sortColumn = null; Refresh(); })
        } };
    }
}

using Avalonia.Controls;
using AvaloniaEdit.Folding;
using Avalonia.Threading;

namespace ModStudio.App;

public sealed partial class EditorPane
{
    private FoldingManager? folding;
    internal int FoldedBlockCount => folding?.AllFoldings.Count(f => f.IsFolded) ?? 0;
    private void InitializeSourceFeatures(WrapPanel toolbar)
    {
        // Keep the line-number separator and folding gutter clear of the first character.
        Source.TextArea.TextView.Margin = new(12, 0, 0, 0);
        if (!System.IO.Path.GetExtension(Document.FilePath).Equals(".json", StringComparison.OrdinalIgnoreCase)) return;
        folding = FoldingManager.Install(Source.TextArea);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        timer.Tick += (_, _) => { timer.Stop(); UpdateJsonFolds(); };
        Source.TextChanged += (_, _) => { timer.Stop(); timer.Start(); };
        DetachedFromVisualTree += (_, _) => timer.Stop();
        AttachedToVisualTree += (_, _) => UpdateJsonFolds();
        foreach (var collapse in new[] { true, false })
        {
            var button = EditorToolbarIcons.Create(collapse ? "Collapse JSON" : "Expand JSON");
            button.Click += (_, _) => { timer.Stop(); UpdateJsonFolds(); foreach (var fold in folding.AllFoldings) fold.IsFolded = collapse; };
            button.IsVisible = Source.IsVisible; Source.PropertyChanged += (_, e) => { if (e.Property.Name == "IsVisible") button.IsVisible = Source.IsVisible; };
            toolbar.Children.Add(button);
        }
    }

    internal void UpdateJsonFolds()
    {
        if (folding == null) return;
        folding.UpdateFoldings(JsonFolds(Source.Text), -1);
    }

    internal static IEnumerable<NewFolding> JsonFolds(string text)
    {
        var stack = new Stack<(int Offset, char Kind, int Line)>();
        var folds = new List<NewFolding>();
        bool quoted = false, escaped = false; int line = 0;
        for (int i = 0; i < text.Length; i++)
        {
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
        return new ContextMenu { ItemsSource = new Control[] {
            Item((frozenColumns.Contains(index) ? "Unfreeze " : "Freeze ") + name, () => ToggleFrozenColumn(name)),
            Item((Document.LockedColumns.Contains(name) ? "Unlock " : "Lock ") + name + " against edits", () => { if (!Document.LockedColumns.Remove(name)) Document.LockedColumns.Add(name); Refresh(); }),
            new Separator(),
            Item("Fit this column to contents", () => { widths.Remove(index); fittedWidths.Remove(index); RefreshColumns(); }),
            Item("Set column width…", async () => {
                if (TopLevel.GetTopLevel(this) is not Window owner) return;
                var input = new NumericUpDown { Minimum = 40, Maximum = 2000, Value = (decimal)TableGrid.Columns.First(c => columnMap[c] == index).ActualWidth, Increment = 10 };
                var apply = new Button { Content = "Apply width" };
                var dialog = new Window { Title = "Column width: " + name, Width = 330, Height = 170, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                dialog.Content = new StackPanel { Margin = new(18), Spacing = 10, Children = { input, apply } };
                apply.Click += (_, _) => { widths[index] = new((double)(input.Value ?? 80)); RefreshColumns(); dialog.Close(); };
                await dialog.ShowDialog(owner);
            }),
            Item("Clear sorting", () => { sortColumn = null; Refresh(); })
        } };
    }
}

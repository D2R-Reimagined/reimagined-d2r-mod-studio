using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.VisualTree;
using ModStudio.Core;

namespace ModStudio.App;

public sealed partial class EditorPane
{
    /// <summary>
    /// The DataContext of a row's cells presenter, standing in for the row's item. Recycling a row would otherwise push the
    /// new item through every object of every cell (several per column, all of them AvaloniaObjects with change
    /// notification), which is the single largest cost of scrolling a wide table. The proxy is set once per row control and
    /// never changes, so nothing below the presenter is notified; cells learn about the row through the proxy's events, and
    /// the editing TextBox's indexer binding resolves against the proxy exactly as it did against the item.
    /// </summary>
    internal sealed class RowProxy(Func<int, int> slotColumn)
    {
        public RowView? Row { get; private set; }
        public event Action? RowChanged;
        public event Action? ValuesChanged;
        public string this[int column] { get => Row?[column] ?? ""; set { if (Row != null) Row[column] = value; } }
        /// <summary>What the editing TextBox binds to: column controls are recycled across table columns as the view scrolls, so the binding names the control's slot and the slot is mapped to the current table column.</summary>
        public SlotIndexer Slot { get; } = new(slotColumn);
        public sealed class SlotIndexer(Func<int, int> slotColumn)
        {
            public RowProxy? Owner { get; set; }
            public string this[int slot] { get => Owner?.Row?[slotColumn(slot)] ?? ""; set { if (Owner?.Row is { } row) row[slotColumn(slot)] = value; } }
        }
        public void Attach(RowView? row)
        {
            if (ReferenceEquals(Row, row)) return;
            if (Row != null) Row.PropertyChanged -= Forward;
            Row = row;
            if (row != null) row.PropertyChanged += Forward;
            RowChanged?.Invoke();
        }
        private void Forward(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => ValuesChanged?.Invoke();
    }
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<DataGridRow, RowProxy> rowProxies = new();
    /// <summary>Called for every LoadingRow: points the row control's proxy at the row it now shows, creating the proxy (and shielding the presenter) on first use.</summary>
    private void AttachRowProxy(DataGridRow rowControl)
    {
        if (!rowProxies.TryGetValue(rowControl, out var proxy))
        {
            proxy = new RowProxy(slot => slotColumns[slot]); proxy.Slot.Owner = proxy; rowProxies.Add(rowControl, proxy);
            void Shield(DataGridCellsPresenter presenter) => presenter.DataContext = proxy;
            if (rowControl.GetVisualDescendants().OfType<DataGridCellsPresenter>().FirstOrDefault() is { } presenter) Shield(presenter);
            else rowControl.TemplateApplied += (_, e) => { if (e.NameScope.Find<DataGridCellsPresenter>("PART_CellsPresenter") is { } applied) Shield(applied); };
        }
        proxy.Attach(rowControl.DataContext as RowView);
    }

    /// <summary>
    /// A text column whose display element is filled by <see cref="LiveCellDisplay"/> instead of a binding. A binding per
    /// cell is what makes scrolling a 300-column table choppy: recycling a row re-evaluates every one of them, including
    /// the hundreds of cells that are collapsed off the right edge. The editing element keeps the two-way binding.
    /// </summary>
    private sealed class LiveCellColumn : DataGridTextColumn
    {
        private readonly EditorPane owner;
        /// <summary>The table column this control currently shows; changes when the control is recycled for another column (<see cref="Retarget"/>).</summary>
        public int Index { get; private set; }
        /// <summary>Stable id of this control, used by the editing binding path ("Slot[n]") so the binding never has to change.</summary>
        public int Slot { get; }
        public LiveCellColumn(EditorPane owner, int index)
        {
            this.owner = owner; Index = index; Slot = owner.slotColumns.Count; owner.slotColumns.Add(index);
            Binding = new Binding($"Slot[{Slot}]") { Mode = BindingMode.TwoWay };
            // Always explicit. Left unset, the grid infers read-only from the binding path, and "Slot" is not a property of the
            // RowView items, so a column control created after the table opened (the column window growing on scroll or resize,
            // a freeze rebuilding the columns) refused to edit until the next Refresh happened to set it.
            IsReadOnly = false;
        }
        public void Retarget(int index) { Index = index; owner.slotColumns[Slot] = index; }
        protected override Control GenerateElement(DataGridCell cell, object dataItem)
        {
            var text = new TextBlock { Name = "CellTextBlock" };
            owner.cellTextTheme ??= owner.TableGrid.TryFindResource("DataGridCellTextBlockTheme", out var theme) ? theme as Avalonia.Styling.ControlTheme : null;
            if (owner.cellTextTheme != null) text.Theme = owner.cellTextTheme;
            return new LiveCellDisplay(owner, this, text, cell);
        }
    }
    private Avalonia.Styling.ControlTheme? cellTextTheme;
    /// <summary>Slot → table column, shared by every column control and row proxy of this pane.</summary>
    private readonly List<int> slotColumns = [];

    private sealed class LiveCellDisplay : Grid
    {
        private static readonly Geometry ReferenceIcon = Geometry.Parse("M1,7 L7,1 M2,1 L7,1 L7,6");
        private static readonly IBrush ReferenceIconBrush = new SolidColorBrush(Color.Parse("#D8BC86"));
        /// <summary>Translucent warm tint over frozen-column cells, so the pinned columns read as one block; selection painted on the cell underneath still shows through.</summary>
        private static readonly IBrush FrozenTint = new SolidColorBrush(Color.Parse("#1AD8BC86"));
        private readonly EditorPane owner;
        private readonly LiveCellColumn column;
        private readonly TextBlock display;
        private readonly DataGridCell cell;
        private Button? reference;
        private bool referenceMargin;
        private string? referenceKey;
        private RowProxy? proxy;
        private RowView? Row => proxy?.Row ?? DataContext as RowView;
        /// <summary>The row or its values changed while the cell was collapsed off screen; the text is refreshed when the cell is next shown.</summary>
        private bool stale = true;
        private string? measuredText;
        private double measuredWidth;
        private bool measuredClipped;
        // Created on first use: a TextBox per cell would dominate the cost of realizing a row with hundreds of columns.
        private TextBox? mirror;
        private static readonly IBrush MirrorBorder = new SolidColorBrush(Color.Parse("#D8BC86")), MirrorBackground = new SolidColorBrush(Color.Parse("#4A4123"));
        private TextBox Mirror => mirror ??= CreateMirror();
        private TextBox CreateMirror()
        {
            var box = new TextBox
            {
                IsReadOnly = true, IsHitTestVisible = false, Focusable = false,
                MinHeight = 0, Padding = new Thickness(4, 0), BorderThickness = new Thickness(1),
                BorderBrush = MirrorBorder, Background = MirrorBackground, IsVisible = false
            };
            Children.Add(box); return box;
        }
        public LiveCellDisplay(EditorPane owner, LiveCellColumn column, TextBlock display, DataGridCell cell)
        {
            this.owner = owner; this.column = column; this.display = display; this.cell = cell;
            Children.Add(display);
            // The DataContext changes at most twice in a cell's life: the row item on creation, then the row proxy once the presenter is shielded.
            DataContextChanged += (_, _) => ObserveRow();
            // Cells scrolled off the right edge are collapsed by the grid; their text catches up when they come back into view.
            cell.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty && cell.IsVisible && stale) Update(); };
            ObserveRow();
        }
        private void ObserveRow()
        {
            if (DataContext is RowProxy next && !ReferenceEquals(proxy, next))
            {
                if (proxy != null) { proxy.RowChanged -= Invalidate; proxy.ValuesChanged -= Invalidate; }
                proxy = next; proxy.RowChanged += Invalidate; proxy.ValuesChanged += Invalidate;
            }
            Invalidate();
        }
        /// <summary>The column control now shows another table column; everything displayed is recomputed when the cell is next visible.</summary>
        public void Invalidate() { stale = true; if (cell.IsVisible) Update(); }
        /// <summary>The reference arrow exists only in cells that have shown a reference column; its reserved space follows the current column.</summary>
        private void EnsureReference(bool wanted)
        {
            if (wanted && reference == null)
            {
                reference = new Button
                {
                    Content = new Avalonia.Controls.Shapes.Path
                    {
                        Data = ReferenceIcon, Stroke = ReferenceIconBrush,
                        StrokeThickness = 1.2, Width = 8, Height = 8, Stretch = Stretch.Uniform
                    },
                    Width = 14, Height = 14, MinWidth = 0, MinHeight = 0, Padding = new Thickness(2), Margin = new Thickness(0, 1, 1, 0),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                    Background = Brushes.Transparent, BorderThickness = new Thickness(0), Focusable = false
                };
                reference.Classes.Add("cellReference");
                reference.Click += (_, e) => { e.Handled = true; if (Row is { } row) owner.RequestCellReference(row.Row, column.Index, reference); };
                Children.Add(reference);
            }
            if (wanted != referenceMargin)
            {
                referenceMargin = wanted;
                // The theme may not have applied yet. Reading and replacing Margin here pins its
                // default zero value on newly realized cells, losing the theme's 12px inset.
                // Keep that margin theme-owned and reserve arrow space independently.
                display.Padding = new Thickness(0, 0, wanted ? 16 : 0, 0);
            }
        }
        public void Update()
        {
            stale = false;
            var row = Row; int index = column.Index;
            var text = row == null ? "" : row[index];
            if (display.Text != text) display.Text = text;
            var tint = owner.frozenColumns.Contains(index) ? FrozenTint : null;
            if (!ReferenceEquals(Background, tint)) Background = tint;
            var value = row != null ? owner.PreviewCellValue(row.Row, index) : null;
            if (value != null) { Mirror.Text = value; Mirror.IsVisible = true; } else if (mirror != null) mirror.IsVisible = false;
            display.IsVisible = value == null;
            EnsureReference(owner.HasCellReference(index));
            if (reference != null)
            {
                var key = row is { IsPlaceholder: false } ? text : "";
                var rule = referenceMargin ? CellReferences.Rule(owner.Document.Table!.Name, owner.Document.Table.Columns[index]) : null;
                reference.IsVisible = value == null && !owner.Document.PendingSource && rule != null && CellReferences.CanNavigate(rule, key);
                if (referenceKey != key)
                {
                    referenceKey = key;
                    ToolTip.SetTip(reference, $"Open reference ‘{key}’ (Alt+Enter)");
                    Avalonia.Automation.AutomationProperties.SetName(reference, $"Open reference {key}");
                }
            }
        }
        public bool TryGetClippedText(out string fullText)
        {
            Control visible = display.IsVisible || mirror == null ? display : mirror;
            fullText = visible switch { TextBlock text => text.Text ?? "", TextBox box => box.Text ?? "", _ => "" };
            if (fullText.Length == 0 || visible.Bounds.Width <= 0) return false;
            var padding = visible is TextBox editor ? editor.Padding : display.Padding;
            var available = visible.Bounds.Width - padding.Left - padding.Right;
            if (fullText != measuredText || Math.Abs(available - measuredWidth) > 0.5)
            {
                measuredText = fullText; measuredWidth = available;
                // Rows show one line. A second line is clipped even when the first fits horizontally.
                measuredClipped = fullText.IndexOfAny(['\r', '\n']) >= 0;
                if (!measuredClipped)
                {
                    // Very long values cannot fit the editor's bounded columns; avoid laying out thousands of glyphs.
                    if (fullText.Length > 512) measuredClipped = true;
                    else
                    {
                        var probe = new TextBlock { Text = fullText, TextTrimming = TextTrimming.None, TextWrapping = TextWrapping.NoWrap };
                        if (visible is TextBlock text)
                        {
                            probe.FontFamily = text.FontFamily; probe.FontSize = text.FontSize;
                            probe.FontWeight = text.FontWeight; probe.FontStyle = text.FontStyle;
                        }
                        else if (visible is TextBox box)
                        {
                            probe.FontFamily = box.FontFamily; probe.FontSize = box.FontSize;
                            probe.FontWeight = box.FontWeight; probe.FontStyle = box.FontStyle;
                        }
                        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        measuredClipped = probe.DesiredSize.Width > available + 1;
                    }
                }
            }
            return measuredClipped;
        }
    }
    /// <summary>Re-evaluates the live-edit mirror of the given cells (every realized cell when null).</summary>
    private void RefreshLiveCells(IEnumerable<(int Row, int Col)>? cells = null)
    {
        foreach (var grid in new[] { TableGrid, FrozenGrid })
        {
            if (cells == null) { foreach (var rowControl in RealizedRows(grid)) foreach (var column in grid.Columns) if (column.GetCellContent(rowControl) is LiveCellDisplay display) display.Update(); continue; }
            var rows = new Dictionary<int, DataGridRow>();
            foreach (var rowControl in RealizedRows(grid)) if (rowControl.IsVisible && rowControl.DataContext is RowView { IsPlaceholder: false } item) rows[item.Row] = rowControl;
            var columns = new Dictionary<int, DataGridColumn>();
            foreach (var column in grid.Columns) if (columnMap.TryGetValue(column, out var col)) columns[col] = column;
            foreach (var (row, col) in cells)
                if (rows.TryGetValue(row, out var rowControl) && columns.TryGetValue(col, out var column) && column.GetCellContent(rowControl) is Visual content)
                    (content as LiveCellDisplay ?? content.GetVisualDescendants().OfType<LiveCellDisplay>().FirstOrDefault())?.Update();
        }
    }
}

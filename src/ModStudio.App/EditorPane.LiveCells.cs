using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;
using ModStudio.Core;

namespace ModStudio.App;

public sealed partial class EditorPane
{
    private sealed class LiveCellColumn(EditorPane owner, int index) : DataGridTextColumn
    {
        protected override Control GenerateElement(DataGridCell cell, object dataItem)
            => new LiveCellDisplay(owner, index, base.GenerateElement(cell, dataItem));
    }

    private sealed class LiveCellDisplay : Grid
    {
        private static readonly Geometry ReferenceIcon = Geometry.Parse("M1,7 L7,1 M2,1 L7,1 L7,6");
        private static readonly IBrush ReferenceIconBrush = new SolidColorBrush(Color.Parse("#D8BC86"));
        private readonly EditorPane owner;
        private readonly int column;
        private readonly Control display;
        private readonly Button? reference;
        private string? referenceKey;
        private RowView? observedRow;
        private string? measuredText;
        private double measuredWidth;
        private bool measuredClipped;
        private readonly TextBox mirror = new()
        {
            IsReadOnly = true, IsHitTestVisible = false, Focusable = false,
            MinHeight = 0, Padding = new Thickness(4, 0), BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.Parse("#D8BC86")),
            Background = new SolidColorBrush(Color.Parse("#4A4123")), IsVisible = false
        };
        public LiveCellDisplay(EditorPane owner, int column, Control display)
        {
            this.owner = owner; this.column = column; this.display = display;
            Children.Add(display); Children.Add(mirror);
            if (owner.HasCellReference(column))
            {
                display.Margin = new Thickness(display.Margin.Left, display.Margin.Top, display.Margin.Right + 16, display.Margin.Bottom);
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
                reference.Click += (_, e) => { e.Handled = true; if (DataContext is RowView row) owner.RequestCellReference(row.Row, column, reference); };
                Children.Add(reference);
            }
            DataContextChanged += (_, _) => ObserveRow();
            AttachedToVisualTree += (_, _) => ObserveRow();
            DetachedFromVisualTree += (_, _) => { if (observedRow != null) observedRow.PropertyChanged -= RowChanged; observedRow = null; };
            Update();
        }
        private void ObserveRow()
        {
            if (observedRow != null) observedRow.PropertyChanged -= RowChanged;
            observedRow = reference != null ? DataContext as RowView : null;
            if (observedRow != null) observedRow.PropertyChanged += RowChanged;
            Update();
        }
        private void RowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Update();
        public void Update()
        {
            var value = DataContext is RowView row ? owner.PreviewCellValue(row.Row, column) : null;
            mirror.IsVisible = value != null; display.IsVisible = value == null;
            if (value != null) mirror.Text = value;
            if (reference != null)
            {
                var key = DataContext is RowView { IsPlaceholder: false } item ? item[column] : "";
                var rule = CellReferences.Rule(owner.Document.Table!.Name, owner.Document.Table.Columns[column]);
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
            var visible = display.IsVisible ? display : mirror;
            fullText = visible switch { TextBlock text => text.Text ?? "", TextBox box => box.Text ?? "", _ => "" };
            if (fullText.Length == 0 || visible.Bounds.Width <= 0) return false;
            var available = visible.Bounds.Width - (visible is TextBox editor ? editor.Padding.Left + editor.Padding.Right : 0);
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
    private void RefreshLiveCells()
    {
        foreach (var grid in new[] { TableGrid, FrozenGrid })
            foreach (var display in grid.GetVisualDescendants().OfType<LiveCellDisplay>()) display.Update();
    }
}

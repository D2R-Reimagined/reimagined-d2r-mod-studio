using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;

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
        private readonly EditorPane owner;
        private readonly int column;
        private readonly Control display;
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
            DataContextChanged += (_, _) => Update();
            AttachedToVisualTree += (_, _) => Update();
            Update();
        }
        public void Update()
        {
            var value = DataContext is RowView row ? owner.PreviewCellValue(row.Row, column) : null;
            mirror.IsVisible = value != null; display.IsVisible = value == null;
            if (value != null) mirror.Text = value;
        }
    }
    private void RefreshLiveCells()
    {
        foreach (var grid in new[] { TableGrid, FrozenGrid })
            foreach (var display in grid.GetVisualDescendants().OfType<LiveCellDisplay>()) display.Update();
    }
}

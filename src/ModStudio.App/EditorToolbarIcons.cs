using Avalonia;
using Avalonia.Controls;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Media;

namespace ModStudio.App;

internal static class EditorToolbarIcons
{
    public static Button Create(string action)
    {
        var (description, geometry) = action switch
        {
            "Save" => ("Save this file", "M3,3 H18 L21,6 V21 H3 Z M7,3 V9 H17 V3 M7,21 V14 H17 V21"),
            "Collapse JSON" => ("Collapse JSON blocks", "M4,8 L12,12 L20,8 M4,16 L12,12 L20,16"),
            "Expand JSON" => ("Expand JSON blocks", "M4,8 L12,4 L20,8 M4,16 L12,20 L20,16"),
            "Table" => ("Table view", "M3,3 H21 V21 H3 Z M3,9 H21 M3,15 H21 M9,3 V21"),
            "Format JSON" => ("Format JSON (Undo available)", "M3,4 H21 M7,9 H17 M7,14 H21 M3,19 H17"),
            "Source" => ("Edit source text", "M8,5 L2,12 L8,19 M16,5 L22,12 L16,19 M14,3 L10,21"),
            "Visual Builder" => ("Visual Builder: edit the item as it looks in game", "M20,3 L21,4 L11,14 L9,15 L10,13 Z M6,12 L12,18 M8,16 L3,21 M17,15 L21,11 M13,3 L9,7"),
            "UI Designer" => ("UI Designer: see the layout as it looks in game, and move, resize and edit its widgets", "M2,4 H22 V20 H2 Z M2,8 H22 M5,11 H11 V17 H5 Z M14,11 H19 M14,14 H19 M14,17 H17"),
            "Apply source" => ("Apply and validate source changes", "M5,3 H15 L20,8 V13 M15,3 V8 H20 M5,3 V21 H11 M12,17 L15,20 L22,13"),
            "Undo" => ("Undo", "M8,4 L3,9 L8,14 M3,9 H14 C23,9 23,21 14,21"),
            "Redo" => ("Redo", "M16,4 L21,9 L16,14 M21,9 H10 C1,9 1,21 10,21"),
            "Fit columns" => ("Fit column widths to contents", "M2,3 V21 M22,3 V21 M9,8 L5,12 L9,16 M15,8 L19,12 L15,16 M5,12 H19"),
            "Column guide" => ("Column guide: what the selected column means and its documented values (F1)", "M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 Z M9,9.5 C9,7 15,7 15,9.5 C15,11.5 12,11.5 12,14 M12,17.5 V17.6"),
            "Clear highlights" => ("Clear the highlighted rows and columns", "M3,21 H21 M6,17 L4,17 L4,13 L13,4 L17,8 L9,17 M11,6 L15,10"),
            "Freeze / Lock" => ("Freeze, lock, and sorting options", "M5,3 H15 M7,3 V8 L4,12 V14 H16 V12 L13,8 V3 M10,14 V22 M18,17 L21,20 L24,17"),
            "View" => ("View options: column letters, hover cards", "M3,5 H21 V19 H3 Z M3,10 H21 M9,5 V19 M15,5 V19"),
            "Schema…" => ("Edit table schema", "M8,3 H4 V21 H8 M16,3 H20 V21 H16 M9,8 H15 M9,12 H15 M9,16 H15"),
            "Preview" => ("Preview rendered Markdown", "M1,12 C6,2 18,2 23,12 C18,22 6,22 1,12 Z M16,12 A4,4 0 1 1 8,12 A4,4 0 1 1 16,12"),
            "Refresh preview" => ("Refresh Markdown preview", "M20,9 A9,9 0 1 0 21,15 M20,3 V9 H14"),
            _ => throw new ArgumentException("Unknown editor action: " + action)
        };
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(new Path { Data = Geometry.Parse(geometry), Stroke = new SolidColorBrush(Color.Parse("#F0E9DF")), StrokeThickness = 1.7,
            StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round });
        var button = new Button { Content = canvas, Width = 38, Height = 36, MinWidth = 38, MinHeight = 36, Padding = new Thickness(7, 6) };
        ToolTip.SetTip(button, description);
        Avalonia.Automation.AutomationProperties.SetName(button, description);
        return button;
    }
}

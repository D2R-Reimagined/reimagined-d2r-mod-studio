using Avalonia.Controls;
using Avalonia.VisualTree;
using ModStudio.Core;
using static ModStudio.Core.Storage;

namespace ModStudio.App;

public partial class MainWindow
{
    /// <summary>
    /// Each file reopens in the view last chosen for it: the Visual Builder, the source, or the table. Choosing is done with
    /// the toolbar buttons; a navigation jump to a cell does not count as a choice.
    /// </summary>
    private async Task SmokeRememberedViewsAsync()
    {
        var file = TableData.FileFor(project!, "tables", "uniqueitems");
        Require(!File.Exists(file), "Remembered view smoke needs a fresh fixture: " + file);
        TableData.Write(file, TableData.FromTsv(Utf8.GetBytes("index\tcode\tlvl req\nFirst\thax\t5\n"), "uniqueitems", "global/excel/uniqueitems.txt"));
        // The item builder smoke opened this same file in the Visual Builder earlier, which is remembered: start over.
        smokeViews.RememberView(file, "table");
        try
        {
            async Task<EditorPane> Reopen(EditorPane? open)
            {
                if (open != null) await CloseTabAsync(tabs.First(t => t.Content == open));
                return (await OpenDocumentAsync(file))!;
            }
            void Press(EditorPane pane, string name) =>
                pane.GetVisualDescendants().OfType<Button>().First(b => Avalonia.Automation.AutomationProperties.GetName(b)?.StartsWith(name) == true)
                    .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

            var pane = await Reopen(null);
            Require(pane.ViewMode == "table" && smokeViews.ViewFor(file) == null, "A file with no remembered view does not open as a table.");
            Press(pane, "Visual Builder");
            Require(pane.ViewMode == "visual" && smokeViews.ViewFor(file) == "visual", "Choosing the Visual Builder was not remembered.");
            pane.Jump(0, "index");
            Require(pane.ViewMode == "table" && smokeViews.ViewFor(file) == "visual", "Jumping to a cell replaced the remembered view.");
            pane = await Reopen(pane);
            Require(pane.ViewMode == "visual" && pane.VisualBuilder is VisualBuilderView, "The file did not reopen in the Visual Builder.");
            Press(pane, "Edit source text");
            pane = await Reopen(pane);
            Require(pane.ViewMode == "source" && pane.Source.IsVisible, "The file did not reopen in the source view.");
            Press(pane, "Table view");
            Require(smokeViews.ViewFor(file) == null, "Choosing the table did not forget the remembered view.");
            pane = await Reopen(pane);
            Require(pane.ViewMode == "table", "The file did not reopen as a table.");
            await CloseTabAsync(tabs.First(t => t.Content == pane));
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }
}

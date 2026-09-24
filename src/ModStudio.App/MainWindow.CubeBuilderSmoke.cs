using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using ModStudio.Core;
using static ModStudio.Core.Storage;

namespace ModStudio.App;

public partial class MainWindow
{
    /// <summary>
    /// The cube Visual Builder: open a recipe, see its inputs and outputs as cards, change a quantity, quality, ethereal
    /// flag and output through the controls (cells keep their token order), fix numinputs from the warning, and read an
    /// output mod's line. The fixture's synthetic cubemain is set aside for a real-shaped one and put back afterwards.
    /// </summary>
    private async Task SmokeCubeBuilderAsync(string output)
    {
        async Task Until(Func<bool> condition, string failure, Func<string>? state = null)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition()) { Require(DateTime.UtcNow < deadline, failure + (state == null ? "" : " " + state())); await Task.Delay(40); }
        }
        var cubeFile = TableData.FileFor(project!, "tables", "cubemain"); var backup = cubeFile + ".smoke";
        if (File.Exists(cubeFile)) File.Move(cubeFile, backup);
        var created = new List<string>();
        try
        {
            void Table(string name, string text)
            {
                var file = TableData.FileFor(project!, "tables", name); Require(!File.Exists(file), "Cube builder smoke needs a fresh fixture: " + file);
                TableData.Write(file, TableData.FromTsv(Utf8.GetBytes(text), name, "global/excel/" + name + ".txt")); created.Add(file);
            }
            Table("cubemain", "description\tenabled\tversion\tmin diff\tnuminputs\tinput 1\tinput 2\tinput 3\tinput 4\tinput 5\tinput 6\tinput 7\toutput\tlvl\tplvl\tilvl\tmod 1\tmod 1 chance\tmod 1 param\tmod 1 min\tmod 1 max\toutput b\n"
                + "3 Ber + magic Hand Axe -> The Gnasher\t1\t100\t0\t4\tr30,qty=3\thax,mag\t\t\t\t\t\tThe Gnasher\t\t\t\tstr\t\t\t5\t5\t\n"
                + "Reroll a rare\t1\t100\t0\t4\tany,rar\tgem4,qty=3\t\t\t\t\t\tusetype,rar\t\t40\t40\t\t\t\t\t\t\n");
            Table("weapons", "name\tnamestr\tcode\ttype\tnormcode\tubercode\tultracode\n" + "Hand Axe\thax\thax\taxe\thax\t9ha\t7ha\n");
            Table("misc", "name\tnamestr\tcode\ttype\n" + "Ber Rune\tr30\tr30\trune\n" + "Perfect Ruby\tgrp\tgrp\tgem4\n");
            Table("itemtypes", "ItemType\tCode\tEquiv1\nWeapon\tweap\t\nAxe\taxe\tweap\nRune\trune\t\nPerfect Gem\tgem4\t\n");
            Table("uniqueitems", "index\tcode\tlvl req\nThe Gnasher\thax\t5\n");
            Table("properties", "code\tfunc1\tstat1\t*Tooltip\nstr\t1\tstrength\t+# to Strength\n");
            var strings = TableData.FileFor(project!, "strings", "cube-test"); created.Add(strings);
            TableData.Write(strings, new TableData(new System.Text.Json.Nodes.JsonObject { ["schemaVersion"] = 1, ["category"] = "cube-test", ["locales"] = new System.Text.Json.Nodes.JsonArray("enUS"), ["target"] = "local/lng/strings/cube-test.json" },
                new System.Text.Json.Nodes.JsonArray(new[] { ("hax", "Hand Axe"), ("r30", "Ber Rune"), ("grp", "Perfect Ruby"), ("The Gnasher", "The Gnasher") }
                    .Select(x => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject { ["Key"] = x.Item1, ["translations"] = new System.Text.Json.Nodes.JsonObject { ["enUS"] = x.Item2 } }).ToArray())));
            bool realGameData = GameDataFolders().Count > 0;

            var pane = (await OpenDocumentAsync(cubeFile, true))!;
            var button = pane.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => Avalonia.Automation.AutomationProperties.GetName(b)?.StartsWith("Visual Builder") == true);
            Require(button != null, "The cubemain table has no Visual Builder button.");
            button!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Require(pane.VisualBuilder is CubeBuilderView, "The cubemain table did not open the cube builder.");
            var view = (CubeBuilderView)pane.VisualBuilder!;
            await Until(() => view.Results.Count == 2, "The builder did not list the recipes.");
            Require(view.Results[0].Summary == "3 × Ber Rune + Hand Axe → The Gnasher (Hand Axe)", "Recipes are not summarized by name: " + view.Results[0].Summary);
            view.Search.Text = "reroll";
            await Until(() => view.Results.Count == 1, "Recipe search does not narrow.");
            view.Search.Text = "";
            await Until(() => view.Results.Count == 2, "Clearing the search did not list both recipes again.");
            view.Select(0);
            await Until(() => view.LastPreview != null, "The recipe did not resolve.");
            Button Card(string column) => view.Cube.GetVisualDescendants().OfType<Button>().First(b => Avalonia.Automation.AutomationProperties.GetName(b) == "cube " + column);
            await Until(() => view.Cube.GetVisualDescendants().OfType<Button>().Count() == 3, "The cube does not show two inputs and one output.");
            await Until(() => Card("input 1").GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "×3") && Card("input 2").GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Magic"),
                "Input cards lack their quantity or quality.", () => string.Join(" | ", view.Cube.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)));
            if (realGameData) { await Until(() => Card("input 2").GetVisualDescendants().OfType<Image>().Any(), "The Hand Axe card has no picture."); }
            Require(view.Cell("input 1", "head") is AutoCompleteBox { Text: "r30" } && view.Cell("input 1", "qty") is TextBox { Text: "3" } && view.Cell("input 2", "quality") is ComboBox { SelectedIndex: 4 },
                "The input controls do not show the cells.");

            // A quantity changes numinputs' count; the warning offers to fix it.
            ((TextBox)view.Cell("input 1", "qty")!).Text = "2";
            await Until(() => pane.Document.Table!.Cell(0, "input 1") == "r30,qty=2", "Changing the quantity did not rewrite the cell.");
            await Until(() => view.LastPreview?.Issues.Any(i => i.Contains("numinputs")) == true, "A numinputs mismatch is not warned about.");
            view.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "Set numinputs to this").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Until(() => pane.Document.Table!.Cell(0, "numinputs") == "3" && view.LastPreview?.Issues.Any(i => i.Contains("numinputs")) == false, "numinputs was not set to the input count.");
            // Quality and ethereal replace or add their token and keep the rest in place.
            ((ComboBox)view.Cell("input 2", "quality")!).SelectedIndex = 6;
            await Until(() => pane.Document.Table!.Cell(0, "input 2") == "hax,rar", "Choosing a quality did not replace mag with rar.", () => pane.Document.Table!.Cell(0, "input 2"));
            ((ComboBox)view.Cell("input 2", "eth")!).SelectedIndex = 1;
            await Until(() => pane.Document.Table!.Cell(0, "input 2") == "hax,rar,eth", "Choosing ethereal did not add eth.");
            // The output: a special output picked by name, and a flag.
            var head = (AutoCompleteBox)view.Cell("output", "head")!; head.Focus(); head.Text = "useitem";
            await Until(() => pane.Document.Table!.Cell(0, "output") == "useitem", "Picking useitem did not write the output.");
            ((CheckBox)view.Cell("output", "rep")!).IsChecked = true;
            await Until(() => pane.Document.Table!.Cell(0, "output") == "useitem,rep", "The repair flag was not added to the output.");
            await Until(() => view.GetVisualDescendants().OfType<PreviewLinkText>().Any(t => t.PlainText.Contains("to Strength")), "The output mod's line is not shown beside it.");
            await Until(() => Card("output").GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "The same item"), "The output card does not show useitem.");

            view.ScrollToTop(); await Task.Delay(250);
            var (width, height) = (Width, Height); Width = 2000; Height = 1100; await Task.Delay(400);
            using (var wide = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96)))
            { wide.Render(this); wide.Save(System.IO.Path.Combine(output, "cube-builder.png"), PngBitmapEncoderOptions.Default); }
            Width = width; Height = height; await Task.Delay(100);

            while (pane.Document.CanUndo) pane.Document.Undo();
            Require(!pane.Document.IsDirty && pane.Document.Table!.Cell(0, "input 1") == "r30,qty=3", "Undo did not restore the recipe.");
            await CloseTabAsync(tabs.First(t => t.Content == pane));
        }
        finally
        {
            foreach (var file in created) if (File.Exists(file)) File.Delete(file);
            if (File.Exists(backup)) File.Move(backup, cubeFile);
        }
    }
}

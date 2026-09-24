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
    /// The runeword Visual Builder: open a runeword, see it socketed into a base with the game's tooltip, change a rune and
    /// a property and see the tooltip follow, and preview it on another base.
    /// </summary>
    private async Task SmokeRunewordBuilderAsync(string output)
    {
        async Task Until(Func<bool> condition, string failure, Func<string>? state = null)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition()) { Require(DateTime.UtcNow < deadline, failure + (state == null ? "" : " " + state())); await Task.Delay(40); }
        }
        var created = new List<string>();
        try
        {
            void Table(string name, string text)
            {
                var file = TableData.FileFor(project!, "tables", name); Require(!File.Exists(file), "Runeword builder smoke needs a fresh fixture: " + file);
                TableData.Write(file, TableData.FromTsv(Utf8.GetBytes(text), name, "global/excel/" + name + ".txt")); created.Add(file);
            }
            Table("runes", "Name\t*Rune Name\tcomplete\titype1\titype2\tetype1\tRune1\tRune2\tRune3\tRune4\tRune5\tRune6\tT1Code1\tT1Param1\tT1Min1\tT1Max1\tT1Code2\tT1Param2\tT1Min2\tT1Max2\n"
                + "Runeword1\tSpirit\t1\tswor\t\t\tr07\tr10\tr09\tr11\t\t\tstr\t\t22\t22\t\t\t\t\n"
                + "Runeword2\tSteel\t1\tswor\t\t\tr03\tr01\t\t\t\t\t\t\t\t\t\t\t\t\n");
            Table("weapons", "name\tnamestr\tcode\ttype\tnormcode\tubercode\tultracode\tmindam\tmaxdam\tdurability\treqstr\tgemsockets\tlevel\tlevelreq\n"
                + "Crystal Sword\tcrs\tcrs\tswor\tcrs\t9cr\t7cr\t5\t15\t20\t43\t6\t26\t0\n"
                + "Long Sword\tlsd\tlsd\tswor\tlsd\t9ls\t7ls\t3\t19\t44\t55\t4\t20\t0\n"
                + "Short Sword\tssd\tssd\tswor\tssd\t9ss\t7ss\t2\t7\t24\t0\t2\t1\t0\n");
            Table("misc", "name\tnamestr\tcode\ttype\tlevelreq\n" + "El\tr01\tr01\trune\t11\nTir\tr03\tr03\trune\t13\nTal\tr07\tr07\trune\t17\nOrt\tr09\tr09\trune\t21\nThul\tr10\tr10\trune\t23\nAmn\tr11\tr11\trune\t25\n");
            Table("itemtypes", "ItemType\tCode\tEquiv1\tMaxSockets1\tMaxSocketsLevelThreshold1\tMaxSockets2\tMaxSocketsLevelThreshold2\tMaxSockets3\nWeapon\tweap\t\t\t\t\t\t\nSword\tswor\tweap\t3\t25\t4\t40\t6\nRune\trune\t\t\t\t\t\t\n");
            Table("properties", "code\tfunc1\tstat1\t*Tooltip\nstr\t1\tstrength\t+# to Strength\n");
            var strings = TableData.FileFor(project!, "strings", "runeword-test"); created.Add(strings);
            TableData.Write(strings, new TableData(new System.Text.Json.Nodes.JsonObject { ["schemaVersion"] = 1, ["category"] = "runeword-test", ["locales"] = new System.Text.Json.Nodes.JsonArray("enUS"), ["target"] = "local/lng/strings/runeword-test.json" },
                new System.Text.Json.Nodes.JsonArray(new[] { ("crs", "Crystal Sword"), ("lsd", "Long Sword"), ("ssd", "Short Sword"), ("r01", "El Rune"), ("r03", "Tir Rune"), ("r07", "Tal Rune"), ("r09", "Ort Rune"), ("r10", "Thul Rune"), ("r11", "Amn Rune"), ("Runeword1", "Spirit"), ("Runeword2", "Steel") }
                    .Select(x => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject { ["Key"] = x.Item1, ["translations"] = new System.Text.Json.Nodes.JsonObject { ["enUS"] = x.Item2 } }).ToArray())));
            bool realGameData = GameDataFolders().Count > 0;

            var pane = (await OpenDocumentAsync(TableData.FileFor(project!, "tables", "runes"), true))!;
            var button = pane.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => Avalonia.Automation.AutomationProperties.GetName(b)?.StartsWith("Visual Builder") == true);
            Require(button != null, "The runes table has no Visual Builder button.");
            button!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Require(pane.VisualBuilder is RunewordBuilderView, "The runes table did not open the runeword builder.");
            var view = (RunewordBuilderView)pane.VisualBuilder!;
            await Until(() => view.Results.Count == 2, "The builder did not list the runewords.");
            Require(view.Results[0] is { Name: "Spirit", Runes: "Tal Thul Ort Amn" }, "Runewords are not listed by name and runes.");
            view.Search.Text = "thul";
            await Until(() => view.Results.Count == 1, "Runeword search does not match rune names.");
            view.Search.Text = "";
            await Until(() => view.Results.Count == 2, "Clearing the search did not list both again.");
            view.Select(0);
            await Until(() => view.LastFit != null && view.LastPreview?.Name == "Spirit", "The runeword did not resolve.");
            Require(view.LastFit!.Bases.Select(b => b.Code).SequenceEqual(["lsd", "crs"]) && view.LastFit.NeverEnoughSockets.SequenceEqual(["Short Sword"]), "The runeword fits the wrong bases.");
            string[] Tooltip() => [.. view.Tooltip.GetVisualDescendants().OfType<PreviewLinkText>().SelectMany(t => t.PlainText.Split('\n'))];
            await Until(() => Tooltip().Contains("'TalThulOrtAmn'") && Tooltip().Contains("Long Sword") && Tooltip().Contains("One-Hand Damage: 3 to 19") && Tooltip().Contains("Required Level: 25") && Tooltip().Contains("Socketed (4)"),
                "The runeword tooltip lacks its runes, base, stats, level or sockets.", () => string.Join(" | ", Tooltip()));
            await Until(() => view.Socketed.GetVisualDescendants().OfType<Border>().Count(b => b.Width == 40) == 4, "The base does not show four sockets.");
            if (realGameData) await Until(() => view.Socketed.GetVisualDescendants().OfType<Image>().Count() == 5, "The base and its runes are not pictured.");

            // A rune changes the runes line and the level; a property changes its line.
            var rune = (AutoCompleteBox)view.Editor("Rune4")!; rune.Focus(); rune.Text = "r01";
            await Until(() => pane.Document.Table!.Cell(0, "Rune4") == "r01" && Tooltip().Contains("'TalThulOrtEl'") && Tooltip().Contains("Required Level: 23"), "Changing a rune did not reach the tooltip.", () => string.Join(" | ", Tooltip()));
            var min = (TextBox)view.Editor("T1Min1")!; min.Focus(); min.Text = "30";
            await Until(() => pane.Document.Table!.Cell(0, "T1Min1") == "30" && Tooltip().Any(l => l.Contains("30") && l.Contains("Strength")), "Changing a property did not reach the tooltip.", () => string.Join(" | ", Tooltip()));
            // Another base.
            view.ChosenBase = "crs";
            await Until(() => Tooltip().Contains("Crystal Sword") && Tooltip().Contains("One-Hand Damage: 5 to 15"), "Previewing on another base did not change the tooltip.");
            view.ScrollToTop(); await Task.Delay(250);
            var (width, height) = (Width, Height); Width = 2000; Height = 1100; await Task.Delay(400);
            using (var wide = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96)))
            { wide.Render(this); wide.Save(System.IO.Path.Combine(output, "runeword-builder.png"), PngBitmapEncoderOptions.Default); }
            Width = width; Height = height; await Task.Delay(100);

            while (pane.Document.CanUndo) pane.Document.Undo();
            Require(!pane.Document.IsDirty, "Undo did not restore the runes table.");
            await CloseTabAsync(tabs.First(t => t.Content == pane));
        }
        finally { foreach (var file in created) if (File.Exists(file)) File.Delete(file); }
    }
}

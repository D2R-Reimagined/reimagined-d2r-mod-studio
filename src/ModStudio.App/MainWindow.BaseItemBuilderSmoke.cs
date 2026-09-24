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
    /// The base item Visual Builder on weapons and armor: search, open an item, edit its damage and see the tooltip follow,
    /// step between tiers, open a unique made on it in its own builder, and roll its drops and affixes.
    /// MODSTUDIO_GAME_DATA pointing at extracted game data shows the real picture.
    /// </summary>
    private async Task SmokeBaseItemBuilderAsync(string output)
    {
        async Task Until(Func<bool> condition, string failure, Func<string>? state = null)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition()) { Require(DateTime.UtcNow < deadline, failure + (state == null ? "" : " " + state())); await Task.Delay(40); }
        }
        var created = new List<string>();
        void Table(string name, string text)
        {
            var file = TableData.FileFor(project!, "tables", name); Require(!File.Exists(file), "Base item builder smoke needs a fresh fixture: " + file);
            TableData.Write(file, TableData.FromTsv(Utf8.GetBytes(text), name, "global/excel/" + name + ".txt")); created.Add(file);
        }
        Table("weapons", "name\tnamestr\tcode\ttype\tnormcode\tubercode\tultracode\t2handed\tmindam\tmaxdam\t2handmindam\t2handmaxdam\tdurability\treqstr\tlevel\tlevelreq\tgemsockets\tspeed\tcost\tinvwidth\tinvheight\tspawnable\tCharsiMin\tCharsiMax\n"
            + "Giant Axe\tgix\tgix\taxe\tgix\t9gi\t7gi\t1\t\t\t22\t45\t50\t70\t27\t0\t4\t10\t445\t2\t3\t1\t1\t2\n"
            + "Ancient Axe\t9gi\t9gi\taxe\tgix\t9gi\t7gi\t1\t\t\t43\t85\t50\t125\t43\t40\t5\t10\t9000\t2\t3\t1\t\t\n"
            + "Glorious Axe\t7gi\t7gi\taxe\tgix\t9gi\t7gi\t1\t\t\t60\t124\t50\t164\t66\t66\t6\t10\t20000\t2\t3\t1\t\t\n");
        Table("armor", "name\tnamestr\tcode\ttype\tminac\tmaxac\tmindam\tmaxdam\tdurability\treqstr\tlevel\tinvwidth\tinvheight\n" + "Boots\tlbt\tlbt\tboot\t2\t3\t3\t8\t12\t0\t3\t2\t2\n");
        Table("itemtypes", "ItemType\tCode\tMaxSockets1\tMaxSocketsLevelThreshold1\tMaxSockets2\tMaxSocketsLevelThreshold2\tMaxSockets3\nAxe\taxe\t4\t25\t5\t40\t6\nBoots\tboot\t0\t25\t0\t40\t0\n");
        Table("uniqueitems", "index\tcode\tlvl req\nThe Humongous\tgix\t24\n");
        var strings = TableData.FileFor(project!, "strings", "base-test"); created.Add(strings);
        TableData.Write(strings, new TableData(new System.Text.Json.Nodes.JsonObject { ["schemaVersion"] = 1, ["category"] = "base-test", ["locales"] = new System.Text.Json.Nodes.JsonArray("enUS"), ["target"] = "local/lng/strings/base-test.json" },
            new System.Text.Json.Nodes.JsonArray(new[] { ("gix", "Giant Axe"), ("9gi", "Ancient Axe"), ("7gi", "Glorious Axe"), ("lbt", "Boots"), ("The Humongous", "The Humongous") }
                .Select(x => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject { ["Key"] = x.Item1, ["translations"] = new System.Text.Json.Nodes.JsonObject { ["enUS"] = x.Item2 } }).ToArray())));
        bool realGameData = GameDataFolders().Count > 0;

        var pane = (await OpenDocumentAsync(TableData.FileFor(project!, "tables", "weapons"), true))!;
        var button = pane.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => Avalonia.Automation.AutomationProperties.GetName(b)?.StartsWith("Visual Builder") == true);
        Require(button != null, "The weapons table has no Visual Builder button.");
        button!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Require(pane.VisualBuilder is BaseItemBuilderView, "The weapons table did not open the base item builder.");
        var view = (BaseItemBuilderView)pane.VisualBuilder!;
        await Until(() => view.Results.Count == 3, "The builder did not list the weapons.");
        view.Search.Text = "elite";
        await Until(() => view.Results.Count == 1 && view.Results[0].Code == "7gi", "Base item search does not match tiers.");
        view.Search.Text = "";
        await Until(() => view.Results.Count == 3, "Clearing the search did not list every weapon again.");
        view.Select(0);
        await Until(() => view.LastPreview is { Name: "Giant Axe" }, "The weapon did not resolve.");
        Require(view.LastPreview!.Tooltip.Any(t => t.Text == "Two-Hand Damage: 22 to 45") && view.LastPreview.Facts.Any(f => f.Label == "Sockets"), "The tooltip lacks damage or sockets: " + string.Join(" | ", view.LastPreview.Tooltip.Select(t => t.Text)));
        await Until(() => view.LastSprite != null, "The builder did not look for the weapon's picture.");
        Require(!realGameData || view.LastSprite!.Pixels != null, "The real Giant Axe picture was not found: " + string.Join(" ", view.LastSprite!.Notes));

        var max = (TextBox)view.Editor("2handmaxdam")!; max.Focus(); max.Text = "50";
        await Until(() => pane.Document.Table!.Cell(0, "2handmaxdam") == "50" && view.LastPreview?.Tooltip.Any(t => t.Text == "Two-Hand Damage: 22 to 50") == true, "Editing damage did not reach the tooltip.");
        Require(view.Editor("CharsiMax") is TextBox && view.Editor("type") is AutoCompleteBox && view.Editor("2handed") is CheckBox && view.Editor("minac") == null, "Vendor, type or flag fields are wrong for weapons.");
        await Until(() => view.Uses.GetVisualDescendants().OfType<Button>().Any(b => b.Content as string == "The Humongous"), "The unique made on this base is not listed.");

        // The tier buttons open the other versions of the item.
        view.Tiers.GetVisualDescendants().OfType<Button>().First(b => (b.Content as string)?.StartsWith("Elite") == true).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Require(view.SelectedRow == 2, "The elite tier button did not open the elite row.");
        await Until(() => view.LastPreview?.Name == "Glorious Axe", "The elite row did not resolve.");
        view.Select(0);
        await Until(() => view.LastPreview?.Name == "Giant Axe", "Returning to the normal row did not resolve it.");
        await Until(() => view.Uses.GetVisualDescendants().OfType<Button>().Any(b => b.Content as string == "The Humongous"), "The unique list did not come back.");

        // Drops and affixes are rolled when their expanders open.
        foreach (var expander in view.GetVisualDescendants().OfType<Expander>().Where(e => e.Header as string is "Where it drops" or "Magic and rare affixes it can roll")) expander.IsExpanded = true;
        await Task.Delay(50); await view.PendingDetails;
        var expanders = view.GetVisualDescendants().OfType<Expander>().Where(e => e.Header as string is "Where it drops" or "Magic and rare affixes it can roll").ToArray();
        Require(expanders.Length == 2 && expanders.All(e => e.Content is ContentControl { Content: Control shown } && shown is not TextBlock { Text: "Rolling drops…" or "Rolling affixes…" }), "Drops and affixes were not rolled.");

        view.ScrollToTop(); await Task.Delay(250);
        var (width, height) = (Width, Height); Width = 2000; Height = 1100; await Task.Delay(400);
        using (var wide = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96)))
        { wide.Render(this); wide.Save(System.IO.Path.Combine(output, "base-item-builder.png"), PngBitmapEncoderOptions.Default); }
        Width = width; Height = height; await Task.Delay(100);

        // A unique made on this base opens in the unique builder.
        view.Uses.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "The Humongous").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await Until(() => Active?.Document.Table?.Name == "uniqueitems" && Active.VisualBuilder is VisualBuilderView { SelectedRow: 0 }, "Opening the unique did not show it in the unique builder.");
        var uniques = Active!;

        // The same builder serves armor, with its own cards and tooltip.
        var armorPane = (await OpenDocumentAsync(TableData.FileFor(project!, "tables", "armor"), true))!;
        armorPane.ShowVisualBuilder();
        var armor = (BaseItemBuilderView)armorPane.VisualBuilder!;
        await Until(() => armor.Results.Count == 1, "The builder did not list the armor.");
        armor.Select(0);
        await Until(() => armor.LastPreview is { Name: "Boots" }, "The armor did not resolve.");
        Require(armor.LastPreview!.Tooltip.Select(t => t.Text).SequenceEqual(["Boots", "Defense: 2–3", "Kick Damage: 3 to 8", "Durability: 12 of 12"]) && armor.Editor("minac") is TextBox && armor.Editor("2handmindam") == null,
            "Armor lacks its tooltip or defense fields: " + string.Join(" | ", armor.LastPreview.Tooltip.Select(t => t.Text)));

        while (pane.Document.CanUndo) pane.Document.Undo();
        Require(!pane.Document.IsDirty, "Undo did not restore the weapons table.");
        foreach (var open in new[] { armorPane, uniques, pane }) await CloseTabAsync(tabs.First(t => t.Content == open));
        foreach (var file in created) File.Delete(file);
    }
}

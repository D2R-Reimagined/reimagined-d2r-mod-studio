using System.Buffers.Binary;
using System.Text.Json.Nodes;
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
    /// The Visual Builder on a unique item table: search, open an item, edit a range and a property through the form, undo,
    /// follow a tooltip link to its field, and show the item's picture. MODSTUDIO_GAME_DATA pointing at extracted game data
    /// shows the real picture; otherwise the project carries a synthetic one.
    /// </summary>
    private async Task SmokeVisualBuilderAsync(string output)
    {
        // Text set in code reaches TextChanged on a later dispatcher pass, and resolving runs on a worker: wait for the effect.
        async Task Until(Func<bool> condition, string failure, Func<string>? state = null)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition()) { Storage.Require(DateTime.UtcNow < deadline, failure + (state == null ? "" : " " + state())); await Task.Delay(40); }
        }
        var created = new List<string>();
        void Table(string name, string text)
        {
            var table = TableData.FromTsv(Utf8.GetBytes(text), name, "global/excel/" + name + ".txt");
            var file = TableData.FileFor(project!, "tables", name); Require(!File.Exists(file), "Visual Builder smoke needs a fresh fixture: " + file); TableData.Write(file, table); created.Add(file);
        }
        Table("uniqueitems", "index\tcode\tlvl\tlvl req\trarity\tprop1\tpar1\tmin1\tmax1\tprop2\tpar2\tmin2\tmax2\tprop3\tpar3\tmin3\tmax3\tinvfile\n"
            + "Expansion\t\t\t\t\t\t\t\t\t\t\t\t\t\t\t\t\t\n" + "The Gnasher\thax\t7\t5\t1\tstr\t\t2\t4\t\t\t\t\t\t\t\t\tinvaxeu\n" + "Deathspade\taxe\t15\t9\t1\tstr\t\t8\t8\t\t\t\t\t\t\t\t\t\n");
        Table("weapons", "code\tnamestr\tmindam\tmaxdam\tlevelreq\treqstr\treqdex\n" + "hax\thax\t3\t6\t1\t10\t0\n" + "axe\taxe\t4\t11\t1\t32\t0\n");
        Table("properties", "code\tfunc1\tstat1\t*Tooltip\t*Min\t*Max\nstr\t1\tstrength\t\t1\t30\ndmg%\t7\t\t\t\t\n");
        Table("setitems", "index\tset\titem\tlvl\tlvl req\tadd func\tprop1\tpar1\tmin1\tmax1\taprop1a\tapar1a\tamin1a\tamax1a\taprop1b\tapar1b\tamin1b\tamax1b\taprop2a\tapar2a\tamin2a\tamax2a\n"
            + "Gnasher Band\tGnasher Set\thax\t10\t8\t2\tstr\t\t3\t3\tstr\t\t5\t5\t\t\t\t\t\t\t\t\n");
        Table("sets", "index\tPCode2a\tPMin2a\tPMax2a\tFCode1\tFMin1\tFMax1\nGnasher Set\tstr\t2\t2\tstr\t4\t4\n");
        Table("itemstatcost", "Stat\tdescfunc\tdescstrpos\nstrength\t19\tstrength\nitem_mindamage_percent\t19\tdamage\nitem_maxdamage_percent\t19\tdamage\n");
        var catalogFile = TableData.FileFor(project!, "strings", "builder-test"); created.Add(catalogFile);
        TableData.Write(catalogFile, new TableData(new JsonObject { ["schemaVersion"] = 1, ["category"] = "builder-test", ["locales"] = new JsonArray("enUS"), ["target"] = "local/lng/strings/builder-test.json" },
            new JsonArray(new[] { ("The Gnasher", "The Gnasher"), ("Deathspade", "Deathspade"), ("Gnasher Band", "Gnasher Band"), ("Gnasher Set", "Gnasher Set"), ("hax", "Hand Axe"), ("axe", "Axe"), ("strength", "%+d to Strength"), ("damage", "+%d%% Enhanced Damage") }
                .Select(x => (JsonNode)new JsonObject { ["Key"] = x.Item1, ["translations"] = new JsonObject { ["enUS"] = x.Item2 } }).ToArray())));
        var hdItems = System.IO.Path.Combine(project!.Root, "data", "hd", "items");
        bool realGameData = GameDataFolders().Count > 0, hadData = Directory.Exists(System.IO.Path.Combine(project.Root, "data"));
        Require(realGameData || !hadData, "Visual Builder smoke expects a fixture without a data folder.");
        if (!realGameData)
        {
            // A synthetic picture: uniques.json names it by the item's key, the sprite is a gold 98 × 294 SpA1 image.
            Directory.CreateDirectory(hdItems); File.WriteAllText(System.IO.Path.Combine(hdItems, "uniques.json"), "[ { \"the_gnasher\": { \"normal\": \"axe/the_gnasher\" } } ]");
            var sprite = System.IO.Path.Combine(project.Root, "data", "hd", "global", "ui", "items", "weapon", "axe", "the_gnasher.sprite");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(sprite)!);
            int width = 98, height = 294; var bytes = new byte[40 + width * height * 4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, 0x31417053); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 31); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)width);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), width); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), height); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), 1);
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                if (Math.Abs(x - width / 2) < 10 + y / 12) { int i = 40 + (y * width + x) * 4; bytes[i] = 199; bytes[i + 1] = 179; bytes[i + 2] = 119; bytes[i + 3] = 255; }
            File.WriteAllBytes(sprite, bytes);
        }

        var pane = (await OpenDocumentAsync(TableData.FileFor(project, "tables", "uniqueitems"), true))!;
        var toolbarButton = pane.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => Avalonia.Automation.AutomationProperties.GetName(b)?.StartsWith("Visual Builder") == true);
        Require(toolbarButton != null, "Unique item tables have no Visual Builder button.");
        toolbarButton!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Require(pane.VisualBuilderVisible && pane.VisualBuilder is VisualBuilderView, "The Visual Builder button did not show the builder.");
        var view = (VisualBuilderView)pane.VisualBuilder!;
        await Until(() => view.Results.Count == 3, "The builder did not list the table's items: " + view.Results.Count);
        Require(view.Results[1] is { Name: "The Gnasher", BaseName: "Hand Axe" } && view.Results[0].Inactive, "Builder search entries lack localized names or header rows.");
        view.Search.Text = "hand gnash";
        await Until(() => view.Results.Count == 1 && view.Results[0].Index == "The Gnasher", "Builder search does not narrow by name and base.");
        view.Select(view.Results[0].Row);
        await Until(() => view.LastPreview?.Tooltip != null, "The builder did not resolve the selected item.");
        var tooltip = view.LastPreview!.Tooltip!;
        Require(tooltip.Properties.Any(p => p.Text == "+(2–4) to Strength") && tooltip.Stats.Any(s => s.Text == "Required Level: 5"), "Builder tooltip is missing properties or requirements: " + string.Join(" | ", view.LastPreview.Lines));

        // A range edit goes straight into the document and the tooltip follows.
        var max = (TextBox)view.Editor("max1")!; max.Focus(); max.Text = "9";
        await Until(() => pane.Document.Table!.Cell(1, "max1") == "9" && pane.Document.IsDirty, "Editing a field did not write its cell.");
        await Until(() => view.LastPreview?.Tooltip?.Properties.Any(p => p.Text == "+(2–9) to Strength") == true, "The tooltip did not follow an edit.");
        // Add property shows the next empty slot and focuses its code. A known code is written as soon as it is typed.
        var code = (AutoCompleteBox)view.Editor("prop2")!;
        Require(!code.IsEffectivelyVisible, "Empty property slots are shown before they are added.");
        view.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "+ Add property").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await Until(() => code.IsEffectivelyVisible && code.IsKeyboardFocusWithin, "Add property did not reveal and focus the next slot.");
        code.Text = "dmg"; await Task.Delay(150);
        Require(pane.Document.Table!.Cell(1, "prop2") == "", "A partial property code was written before it was complete.");
        code.Text = "dmg%";
        await Until(() => pane.Document.Table.Cell(1, "prop2") == "dmg%", "Typing a known property code did not write it.");
        foreach (var column in new[] { "min2", "max2" })
        {
            var box = (TextBox)view.Editor(column)!; box.Focus(); box.Text = "100";
            await Until(() => pane.Document.Table.Cell(1, column) == "100", "Typing a value did not write " + column + ".", () => view.StatusText);
        }
        await Until(() => view.LastPreview?.Tooltip?.Stats.Any(s => s.Text == "One-Hand Damage: 6 to 12") == true, "Enhanced damage from the form did not reach the tooltip:", () => string.Join(" | ", view.LastPreview?.Tooltip?.Stats.Select(s => s.Text) ?? []));
        Require(view.LastPreview!.Tooltip!.Properties.Count(p => p.Text.Contains("Enhanced Damage")) == 1, "Enhanced damage is listed twice in the game-style tooltip.");
        // Following a tooltip value moves to its field.
        var link = view.GetVisualDescendants().OfType<PreviewLinkText>().First(t => t.Links.Any(l => l.Targets.Any(c => c.Column == "min1")));
        link.RaiseEvent(new PreviewLinkEventArgs(PreviewLinkText.LinkClickedEvent, [.. link.Links.First(l => l.Targets.Any(c => c.Column == "min1")).Targets], link));
        await Until(() => view.Editor("min1")!.IsFocused, "A tooltip link did not focus its field.");
        Require(pane.VisualBuilderVisible, "Following a link into the item's own row left the builder.");
        await view.PendingSprite;
        await Until(() => view.LastSprite != null, "The builder did not look for the item's picture.");
        Require(view.LastSprite!.Pixels != null, "The item's picture was not found: " + string.Join(" ", view.LastSprite.Notes));
        await Task.Delay(250);
        using (var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96)))
        { bitmap.Render(this); bitmap.Save(System.IO.Path.Combine(output, "visual-builder-fields.png"), PngBitmapEncoderOptions.Default); }
        view.ScrollToTop(); await Task.Delay(150);
        using (var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96)))
        { bitmap.Render(this); bitmap.Save(System.IO.Path.Combine(output, "visual-builder.png"), PngBitmapEncoderOptions.Default); }

        // Undo restores the fields as well as the cells; the table view shows the same row.
        while (pane.Document.CanUndo) pane.Document.Undo();
        Require(((TextBox)view.Editor("max1")!).Text == "4" && ((AutoCompleteBox)view.Editor("prop2")!).Text == "", "Undo did not restore the builder's fields.");
        view.Search.Text = "";
        await Until(() => view.Results.Count == 3, "Clearing the search did not list every item again.");
        var added = view.Results.Count;
        view.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "+ New unique").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Require(pane.Document.Table.Records.Count == 4 && view.SelectedRow == 3 && pane.Document.Table.Cell(3, "index") == "New Unique", "New unique did not add and open a row.");
        await Until(() => view.Results.Count == added + 1, "The new row is not listed.");
        pane.Document.Undo();
        Require(pane.Document.Table.Records.Count == 3 && view.SelectedRow < 0, "Undoing a new row left the builder editing it.");
        pane.Jump(1, "max1");
        Require(!pane.VisualBuilderVisible && pane.SelectedRow == 1, "Jumping to a cell did not return to the table view.");
        await CloseTabAsync(tabs.First(t => t.Content == pane));

        // Set items: the item's own properties, its bonus slots by pieces worn, and the set section of the tooltip.
        var setPane = (await OpenDocumentAsync(TableData.FileFor(project, "tables", "setitems"), true))!;
        setPane.ShowVisualBuilder();
        var setView = (VisualBuilderView)setPane.VisualBuilder!;
        await Until(() => setView.Results.Count == 1 && setView.Results[0].Set == "Gnasher Set", "The builder did not list set items with their set.");
        setView.Select(0);
        await Until(() => setView.LastPreview?.Tooltip?.SetLines.Length > 0, "The set item did not resolve.");
        var setTooltip = setView.LastPreview!.Tooltip!;
        Require(setTooltip.Properties.Single().Text == "+3 to Strength" && setTooltip.SetLines.Any(l => l.Text == "+5 to Strength") && setTooltip.SetLines.Any(l => l.Text == "+4 to Strength"),
            "The set item tooltip does not separate its own properties from its set bonuses: " + string.Join(" | ", setView.LastPreview.Lines));
        Require(setView.Editor("aprop1a")!.IsEffectivelyVisible && !setView.Editor("aprop2a")!.IsEffectivelyVisible && setView.Editor("add func") is ComboBox { SelectedIndex: 2 },
            "Set bonus slots or the add func choice do not show the row's values.");
        await Task.Delay(200);
        using (var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96)))
        { bitmap.Render(this); bitmap.Save(System.IO.Path.Combine(output, "visual-builder-set.png"), PngBitmapEncoderOptions.Default); }
        Require(!setPane.Document.IsDirty, "Opening a set item in the builder changed the table.");
        await CloseTabAsync(tabs.First(t => t.Content == setPane));
        foreach (var file in created) File.Delete(file);
        if (!realGameData) Directory.Delete(System.IO.Path.Combine(project.Root, "data"), true);
    }
}

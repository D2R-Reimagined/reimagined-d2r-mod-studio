using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using ModStudio.Core;
using static ModStudio.Core.Storage;

namespace ModStudio.App;

public partial class MainWindow
{
    /// <summary>
    /// The level Visual Builder: open a level, see its banner, difficulty tiles and connection map, add a monster and see
    /// a slot open after it, change an area level and a link and see the preview follow, and open a linked level.
    /// </summary>
    private async Task SmokeLevelBuilderAsync(string output)
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
                var file = TableData.FileFor(project!, "tables", name); Require(!File.Exists(file), "Level builder smoke needs a fresh fixture: " + file);
                TableData.Write(file, TableData.FromTsv(Utf8.GetBytes(text), name, "global/excel/" + name + ".txt")); created.Add(file);
            }
            var vis = string.Join("\t", Enumerable.Range(0, 8).Select(i => "Vis" + i)); var warp = string.Join("\t", Enumerable.Range(0, 8).Select(i => "Warp" + i));
            var mons = string.Join("\t", Enumerable.Range(1, 25).Select(i => "mon" + i)); var nmons = string.Join("\t", Enumerable.Range(1, 25).Select(i => "nmon" + i));
            string Slots(params string[] ids) => string.Join("\t", Enumerable.Range(0, 25).Select(i => i < ids.Length ? ids[i] : ""));
            string Links(params string[] ids) => string.Join("\t", Enumerable.Range(0, 8).Select(i => i < ids.Length ? ids[i] : "0"));
            string Warps(params string[] ids) => string.Join("\t", Enumerable.Range(0, 8).Select(i => i < ids.Length ? ids[i] : "-1"));
            Table("levels", $"Name\tId\tAct\tLevelName\tLevelEntry\tWaypoint\tTeleport\tPreventTownPortal\tDrlgType\tMonLvl\tMonLvlEx\tMonLvlEx(N)\tMonLvlEx(H)\tMonDen\tMonDen(N)\tMonDen(H)\tMonUMin\tMonUMax\tSizeX\tSizeY\tNumMon\t{vis}\t{warp}\t{mons}\t{nmons}\tcmon1\tcpct1\tcamt1\tIntensity\tRed\tGreen\tBlue\tOffsetX\tOffsetY\n"
                + $"Act 1 - Town\t1\t0\tRogue Encampment\t\t0\t1\t1\t2\t0\t0\t25\t50\t0\t0\t0\t0\t0\t56\t40\t0\t{Links("2")}\t{Warps()}\t{Slots()}\t{Slots()}\tchicken\t30\t2\t0\t0\t0\t0\t-1\t-1\n"
                + $"Act 1 - Wilderness 1\t2\t0\tBlood Moor\tEnteringBloodMoor\t255\t1\t0\t3\t1\t1\t36\t67\t600\t600\t600\t1\t3\t80\t80\t2\t{Links("1", "8")}\t{Warps("-1", "0")}\t{Slots("zombie1")}\t{Slots("zombie1")}\t\t\t\t0\t255\t180\t120\t-1\t-1\n"
                + $"Act 1 - Cave 1\t8\t0\tDen of Evil\t\t255\t1\t0\t1\t1\t1\t36\t67\t900\t900\t900\t0\t0\t200\t200\t3\t{Links()}\t{Warps()}\t{Slots("fallen1")}\t{Slots("fallen1")}\t\t\t\t0\t0\t0\t0\t1500\t1000\n"
                + $"Act 1 - Wilderness 2\t3\t0\tCold Plains\t\t1\t1\t0\t3\t2\t2\t36\t68\t600\t600\t600\t1\t3\t80\t80\t2\t{Links("2")}\t{Warps("-1")}\t{Slots()}\t{Slots()}\t\t\t\t0\t0\t0\t0\t-1\t-1\n"
                + $"Act 1 - Wilderness 4\t5\t0\tDark Wood\t\t5\t1\t0\t3\t5\t5\t38\t68\t600\t600\t600\t1\t3\t80\t80\t2\t{Links("8")}\t{Warps("0")}\t{Slots()}\t{Slots()}\t\t\t\t0\t0\t0\t0\t-1\t-1\n"
                + $"Act 1 - Wilderness 5\t6\t0\tBlack Marsh\t\t6\t1\t0\t3\t6\t6\t38\t69\t600\t600\t600\t1\t3\t80\t80\t2\t{Links()}\t{Warps()}\t{Slots()}\t{Slots()}\t\t\t\t0\t0\t0\t0\t-1\t-1\n");
            Table("monstats", "Id\tNameStr\tLevel\tLevel(N)\tLevel(H)\nzombie1\tZombie\t1\t37\t68\nfallen1\tFallen\t2\t37\t68\nchicken\tChicken\t0\t0\t0\n");
            Table("lvlwarp", "Name\tId\nCave Entrance\t0\nCave Exit\t1\n");
            Table("lvlprest", "Name\tDef\tLevelId\tPopulate\tScan\tSizeX\tSizeY\tFiles\tFile1\tFile2\tFile3\tFile4\tFile5\tFile6\tDt1Mask\nAct 1 - Town 1\t1\t1\t1\t1\t0\t0\t0\tAct1/Town/TownN1.ds1\t0\t0\t0\t0\t0\t959\n");
            var strings = TableData.FileFor(project!, "strings", "level-test"); created.Add(strings);
            TableData.Write(strings, new TableData(new System.Text.Json.Nodes.JsonObject { ["schemaVersion"] = 1, ["category"] = "level-test", ["locales"] = new System.Text.Json.Nodes.JsonArray("enUS"), ["target"] = "local/lng/strings/level-test.json" },
                new System.Text.Json.Nodes.JsonArray(new[] { ("Rogue Encampment", "Rogue Encampment"), ("Blood Moor", "Blood Moor"), ("EnteringBloodMoor", "Entering Blood Moor"), ("Den of Evil", "Den of Evil"), ("Cold Plains", "Cold Plains"),
                    ("Zombie", "Zombie"), ("Fallen", "Fallen"), ("Chicken", "Chicken"), ("Dark Wood", "Dark Wood"), ("Black Marsh", "Black Marsh") }
                    .Select(x => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject { ["Key"] = x.Item1, ["translations"] = new System.Text.Json.Nodes.JsonObject { ["enUS"] = x.Item2 } }).ToArray())));

            var pane = (await OpenDocumentAsync(TableData.FileFor(project!, "tables", "levels"), true))!;
            var button = pane.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => Avalonia.Automation.AutomationProperties.GetName(b)?.StartsWith("Visual Builder") == true);
            Require(button != null, "The levels table has no Visual Builder button.");
            button!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Require(pane.VisualBuilder is LevelBuilderView, "The levels table did not open the level builder.");
            var view = (LevelBuilderView)pane.VisualBuilder!;
            await Until(() => view.Results.Count == 6, "The builder did not list the levels.");
            Require(view.Results[1] is { Title: "Blood Moor", AreaLevels: "1 / 36 / 67" }, "Levels are not listed by name and area levels.");
            view.Search.Text = "den";
            await Until(() => view.Results.Count == 1, "Level search does not match level names.");
            view.Search.Text = "";
            await Until(() => view.Results.Count == 6, "Clearing the search did not list every level again.");
            view.Select(1);
            await Until(() => view.LastPreview?.Title == "Blood Moor", "The level did not resolve.");

            string[] Texts(Control control) => [.. control.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "")];
            await Until(() => Texts(view.Banner).Contains("Blood Moor") && Texts(view.Banner).Contains("Entering Blood Moor") && Texts(view.Banner).Contains("No waypoint"),
                "The banner lacks the level's name, entry popup or waypoint.", () => string.Join(" | ", Texts(view.Banner)));
            Require(Texts(view.Tiles).Count(t => t is "1" or "36" or "67") == 3, "The difficulty tiles do not show the area levels.");
            await Until(() => Texts(view.Map).Contains("Rogue Encampment") && Texts(view.Map).Contains("Den of Evil") && Texts(view.Map).Contains("Cold Plains"),
                "The connection map lacks a linked or inbound level.", () => string.Join(" | ", Texts(view.Map)));
            Require(view.LastPreview!.Links.Single(l => l.Id == "1").Back && !view.LastPreview.Links.Single(l => l.Id == "8").Back
                && view.LastPreview.Inbound.Length == 0 && view.LastPreview.OutdoorLinks.Any(l => l.Level.Id == "3"), "Links back and outdoor borders are wrong.");
            await Until(() => view.Label("mon1") == "Zombie · lvl 1" && view.Label("Vis1").StartsWith("Den of Evil", StringComparison.Ordinal) && view.Label("Vis1").Contains("Cave Entrance"),
                "Slots do not name their monster or level.", () => view.Label("mon1") + " | " + view.Label("Vis1"));
            Require(view.ShownSlots("mon") == 2 && view.ShownSlots("nmon") == 2 && view.ShownSlots("umon") == 0, "Monster pools do not open one slot after the last one used.");

            // A new monster opens the next slot; an area level and a link reach the preview.
            var monster = (AutoCompleteBox)view.Editor("mon2")!; monster.Focus(); monster.Text = "fallen1";
            await Until(() => pane.Document.Table!.Cell(1, "mon2") == "fallen1" && view.ShownSlots("mon") == 3 && view.Label("mon2") == "Fallen · lvl 2", "Adding a monster did not open another slot.", () => view.Label("mon2"));
            var level = (TextBox)view.Editor("MonLvlEx(N)")!; level.Focus(); level.Text = "40";
            await Until(() => view.LastPreview?.Difficulties[1].AreaLevel == 40 && Texts(view.Tiles).Contains("40"), "Changing an area level did not reach the tiles.");
            var link = (AutoCompleteBox)view.Editor("Vis2")!; link.Focus(); link.Text = "3";
            await Until(() => pane.Document.Table!.Cell(1, "Vis2") == "3" && view.LastPreview!.Links.Any(l => l.Id == "3" && l.Back) && view.LastPreview.Inbound.Length == 0,
                "Linking to a level did not reach the map.");
            await Until(() => view.Label("Vis2") == "Cold Plains · Act 1 · waypoint · across the border", "A link without a warp does not say it crosses the border.", () => view.Label("Vis2"));
            var copy = view.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "Copy the Normal list");
            copy.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Until(() => pane.Document.Table!.Cell(1, "nmon2") == "fallen1" && view.ShownSlots("nmon") == 3, "Copying the Normal list did not fill the Nightmare list.");

            view.ScrollToTop(); await Task.Delay(250);
            var (width, height) = (Width, Height); Width = 2000; Height = 1100; await Task.Delay(400);
            using (var wide = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96)))
            { wide.Render(this); wide.Save(System.IO.Path.Combine(output, "level-builder.png"), PngBitmapEncoderOptions.Default); }
            var detail = view.GetVisualDescendants().OfType<ScrollViewer>().MaxBy(s => s.Extent.Height)!;
            detail.Offset = new Vector(0, 520); await Task.Delay(300);
            using (var fields = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96)))
            { fields.Render(this); fields.Save(System.IO.Path.Combine(output, "level-builder-fields.png"), PngBitmapEncoderOptions.Default); }
            Width = width; Height = height; await Task.Delay(100);

            // A linked level opens in place.
            Require(view.SelectWhere("Id", "8"), "Opening a linked level failed.");
            await Until(() => view.LastPreview?.Title == "Den of Evil" && view.Label("mon1") == "Fallen · lvl 2", "The linked level did not open.");

            while (pane.Document.CanUndo) pane.Document.Undo();
            Require(!pane.Document.IsDirty, "Undo did not restore the levels table.");

            Require(view.SelectWhere("Id", "5"), "Could not select Dark Wood.");
            view.ScrollToTop();
            await Until(() => view.LastPreview?.Title == "Dark Wood" && Texts(view.Map).Contains("Black Marsh") && Texts(view.Map).Contains("Den of Evil"),
                "Dark Wood did not show its outdoor border and authored cave together.");
            async Task ClickLevel(string title)
            {
                Border? node = null;
                Point? point = null;
                await Until(() =>
                {
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                    node = view.Map.GetVisualDescendants().OfType<Canvas>().SelectMany(c => c.Children.OfType<Border>())
                        .SingleOrDefault(b => Texts(b).Contains(title));
                    point = node?.TranslatePoint(new Point(node.Bounds.Width / 2, node.Bounds.Height / 2), this);
                    return point is { } p && this.InputHitTest(p) is Visual hit && node != null
                        && (hit == node || hit.GetVisualAncestors().Contains(node));
                }, "The outdoor navigation node is not clickable: " + title);
                this.MouseDown(point!.Value, MouseButton.Left); this.MouseUp(point.Value, MouseButton.Left);
                await Until(() => view.LastPreview?.Title == title && Texts(view.Banner).Contains(title), "Outdoor navigation did not open " + title);
            }
            await ClickLevel("Black Marsh");
            await ClickLevel("Dark Wood");
            Require(!pane.Document.IsDirty, "Outdoor navigation edited the levels table.");
            using (var outdoorImage = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96)))
            { outdoorImage.Render(this); outdoorImage.Save(System.IO.Path.Combine(output, "level-builder-outdoors.png"), PngBitmapEncoderOptions.Default); }

            // The World card: the act laid out by offset, the levels the game places listed under it, and a level drawn into free space.
            Require(view.SelectWhere("Id", "8"), "Could not open the Den of Evil.");
            await Until(() => view.WorldMap.Layout?.Find("8") is { X: 1500, Y: 1000, Width: 200, Height: 200 } && view.WorldTray.Any(t => t.StartsWith("Rogue Encampment", StringComparison.Ordinal)),
                "The world map did not place the Den or list the levels the game places.", () => string.Join(" | ", view.WorldTray));
            void Shot(string name) { using var image = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96)); image.Render(this); image.Save(System.IO.Path.Combine(output, name), PngBitmapEncoderOptions.Default); }
            var (beforeWidth, beforeHeight) = (Width, Height); Width = 2000; Height = 1100; await Task.Delay(300);
            view.WorldMap.BringIntoView(); await Task.Delay(400); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            Require(view.WorldMap.TranslatePoint(default, this) is { Y: > 100 } top && top.Y + view.WorldMap.Bounds.Height < Bounds.Height, "The world map did not scroll fully into view.");
            Point MapPoint(double x, double y) => view.WorldMap.TranslatePoint(new Point(x * view.WorldMap.Zoom + view.WorldMap.Pan.X, y * view.WorldMap.Zoom + view.WorldMap.Pan.Y), this)!.Value;
            var den = MapPoint(1600, 1100);
            Require(this.InputHitTest(den) is Visual under && (under == view.WorldMap || under.GetVisualAncestors().Contains(view.WorldMap)), "The world map is not under the pointer.");
            this.MouseMove(den);
            await Until(() => view.WorldMap.Hovered == "8", "Hovering the Den did not pick it.");
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); Shot("level-builder-world.png");
            var drawFrom = MapPoint(1720.5, 1000.5); var drawTo = MapPoint(1759.5, 1029.5);
            this.MouseDown(drawFrom, MouseButton.Left); this.MouseMove(MapPoint(1740, 1010), RawInputModifiers.LeftMouseButton); this.MouseMove(drawTo, RawInputModifiers.LeftMouseButton);
            Require(view.WorldMap.Drawing == (1720, 1000, 40, 30), "Dragging across free space did not draw a 40 × 30 level: " + view.WorldMap.Drawing);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); Shot("level-builder-world-draw.png");
            this.MouseUp(drawTo, MouseButton.Left);
            await Until(() => OwnedWindows.OfType<NewLevelWindow>().Any(), "Drawing a level did not open the new-level form.");
            var form = OwnedWindows.OfType<NewLevelWindow>().Single();
            // The Den is a maze, so the form starts from Maze; a preset gets a lvlprest row and a map.
            form.Generation.SelectedIndex = 0;
            await Until(() => form.CreateButton.IsEnabled && form.SummaryText.Contains("lvlprest: a row with LevelId 9", StringComparison.Ordinal),
                "The form did not offer a preset level with its map.", () => form.ErrorText + " | " + form.SummaryText);
            await Task.Delay(150); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            using (var formImage = new RenderTargetBitmap(new PixelSize((int)form.Bounds.Width, (int)form.Bounds.Height), new Vector(96, 96)))
            { formImage.Render(form); formImage.Save(System.IO.Path.Combine(output, "level-builder-new-level.png"), PngBitmapEncoderOptions.Default); }
            form.CreateButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Until(() => pane.Document.Table!.Records.Count == 7, "Creating the level did not add a levels row.");
            var levels = pane.Document.Table!;
            Require(levels.Cell(6, "Id") == "9" && levels.Cell(6, "OffsetX") == "1720" && levels.Cell(6, "OffsetY") == "1000" && levels.Cell(6, "SizeX") == "40" && levels.Cell(6, "SizeY") == "30"
                && levels.Cell(6, "DrlgType") == "2" && levels.Cell(6, "mon1") == "fallen1" && levels.Cell(6, "Vis0") == "0", "The new levels row is not placed, sized and copied from the Den.");
            var presets = FindOpenDocument(TableData.FileFor(project!, "tables", "lvlprest"));
            Require(presets?.Table is { } presetTable && presetTable.Records.Count == 2 && presetTable.Cell(1, "LevelId") == "9" && presetTable.Cell(1, "Def") == "2"
                && presetTable.Cell(1, "File1") == "Act1/Custom/act_1_new_level_9.ds1", "The new lvlprest row does not name the level's map.");
            var newMap = System.IO.Path.Combine(project!.Root, "data/global/tiles/Act1/Custom/act_1_new_level_9.ds1");
            var newPreset = System.IO.Path.Combine(project.Root, "data/hd/env/preset/act1/custom/act_1_new_level_9.json");
            created.Add(newMap); created.Add(newPreset);
            Require(File.Exists(newMap) && LevelWorld.Ds1Size(File.ReadAllBytes(newMap)) == (40, 30, 0) && File.Exists(newPreset)
                && System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(newPreset)).S("type") == "Preset", "The new level's blank map and HD preset were not written.");
            await Until(() => view.LastPreview?.Title == "Act 1 - New Level 9" && view.WorldMap.Layout?.Find("9") is { X: 1720, Y: 1000 }, "The new level did not open on the map.");
            await Task.Delay(150); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); Shot("level-builder-world-added.png");
            Width = beforeWidth; Height = beforeHeight;
            while (presets!.CanUndo) presets.Undo();
            while (pane.Document.CanUndo) pane.Document.Undo();
            Require(!presets.IsDirty && !pane.Document.IsDirty && pane.Document.Table!.Records.Count == 6, "Undo did not remove the new level's rows.");
            await CloseTabAsync(tabs.First(t => (t.Content as EditorPane)?.Document == presets));
            await CloseTabAsync(tabs.First(t => t.Content == pane));
            // Closing the modal form hands focus back to this window; settle it so later smokes' key presses land where they aim.
            Activate(); Focus(); await Task.Delay(100);
        }
        finally
        {
            foreach (var file in created) if (File.Exists(file)) File.Delete(file);
            // The new level's map folders, once empty again.
            var data = System.IO.Path.Combine(project!.Root, "data");
            foreach (var folder in created.Select(System.IO.Path.GetDirectoryName).OfType<string>().Distinct())
                for (var d = folder; d.StartsWith(data + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any(); d = System.IO.Path.GetDirectoryName(d)!)
                    Directory.Delete(d);
        }
    }
}

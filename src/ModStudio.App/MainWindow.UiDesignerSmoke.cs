using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ModStudio.Core;
using static ModStudio.Core.Storage;

namespace ModStudio.App;

public partial class MainWindow
{
    /// <summary>
    /// The UI Designer: open a layout based on another, see it drawn with its sprite, drag a widget with the mouse, nudge it
    /// with the arrow keys, edit a value in the inspector, override an inherited widget, duplicate and delete by keyboard,
    /// jump to its source, and undo back to the file as it was. With MODSTUDIO_GAME_DATA it also draws the game's inventory
    /// over the HUD for a screenshot.
    /// </summary>
    private async Task SmokeUiDesignerAsync(string output)
    {
        SmokePixelFormats();
        async Task Until(Func<bool> condition, string failure, Func<string>? state = null)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition())
            {
                Require(DateTime.UtcNow < deadline, failure + (state == null ? "" : " " + state()));
                await Dispatcher.UIThread.InvokeAsync(() => UpdateLayout(), DispatcherPriority.Background);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
                await Task.Delay(30);
            }
        }
        void Screenshot(string name)
        {
            UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            using var image = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96));
            image.Render(this); image.Save(System.IO.Path.Combine(output, name), PngBitmapEncoderOptions.Default);
        }
        var data = System.IO.Path.Combine(project!.Root, "data");
        bool hadData = Directory.Exists(data);
        var created = new List<string>();
        var (width, height) = (Width, Height);
        try
        {
            var layouts = System.IO.Path.Combine(data, "global", "ui", "layouts");
            void Write(string relative, string text)
            {
                var file = System.IO.Path.Combine(layouts, relative);
                Require(!File.Exists(file), "UI Designer smoke needs a fresh fixture: " + file);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!); File.WriteAllText(file, text, Utf8); created.Add(file);
            }
            // Synthetic sprites: a 600 × 400 panel in one colour and a four-frame 60 × 40 button.
            void Sprite(string relative, int frameWidth, int frameHeight, int frames, (byte R, byte G, byte B) colour)
            {
                var file = System.IO.Path.Combine(data, "hd", "global", "ui", relative);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
                int w = frameWidth * frames; var bytes = new byte[40 + w * frameHeight * 4];
                BitConverter.GetBytes(0x31417053).CopyTo(bytes, 0); BitConverter.GetBytes((ushort)31).CopyTo(bytes, 4); BitConverter.GetBytes((ushort)frameWidth).CopyTo(bytes, 6);
                BitConverter.GetBytes(w).CopyTo(bytes, 8); BitConverter.GetBytes(frameHeight).CopyTo(bytes, 12); BitConverter.GetBytes(frames).CopyTo(bytes, 20);
                for (int p = 0; p < w * frameHeight; p++) { int f = p % w / frameWidth; bytes[40 + p * 4] = (byte)Math.Min(255, colour.R + f * 40); bytes[41 + p * 4] = colour.G; bytes[42 + p * 4] = colour.B; bytes[43 + p * 4] = 255; }
                File.WriteAllBytes(file, bytes); created.Add(file);
            }
            Sprite("panel/test/background.sprite", 600, 400, 1, (40, 90, 160));
            Sprite("panel/test/button.sprite", 60, 40, 4, (160, 120, 40));
            Write("_profilehd.json", "{\n    // Test profile\n    \"PanelAnchor\": { \"x\": 0.5, \"y\": 0.5 },\n    \"PanelRect\": { \"x\": -300, \"y\": -200, \"width\": 600, \"height\": 400 },\n    \"StyleTitle\": { \"fontColor\": { \"r\": 199, \"g\": 179, \"b\": 119, \"a\": 255 }, \"pointSize\": 38, \"alignment\": { \"h\": \"center\", \"v\": \"center\" } },\n}\n");
            Write("testbasehd.json", """
                {
                    "type": "TestPanel", "name": "Base",
                    "fields": { "rect": "$PanelRect", "anchor": "$PanelAnchor" },
                    "children": [
                        { "type": "ImageWidget", "name": "background", "fields": { "filename": "PANEL\\Test\\Background" } },
                        { "type": "TextBoxWidget", "name": "title", "fields": { "rect": { "x": 50, "y": 20, "width": 500, "height": 60 }, "style": "$StyleTitle", "text": "Base title" } },
                    ]
                }
                """);
            // A children list replaces the parent's, so the parent's widgets it keeps are listed by type and name.
            var derivedText = "{\n    \"basedOn\": \"TestBaseHD.json\",\n    \"type\": \"TestPanel\", \"name\": \"Derived\",\n    \"children\": [\n        { \"type\": \"ImageWidget\", \"name\": \"background\" },\n        { \"type\": \"TextBoxWidget\", \"name\": \"title\" },\n        // An extra button\n        {\n            \"type\": \"ButtonWidget\", \"name\": \"extra\",\n            \"fields\": {\n                \"rect\": { \"x\": 100, \"y\": 200 },\n                \"filename\": \"PANEL\\\\Test\\\\Button\",\n                \"hoveredFrame\": 1,\n            },\n        },\n    ]\n}\n";
            Write("testderivedhd.json", derivedText);
            var file = System.IO.Path.Combine(layouts, "testderivedhd.json");

            var pane = (await OpenDocumentAsync(file, true))!;
            Require(pane.Document.Table == null && pane.HasVisualBuilder, "A layout file did not offer the UI Designer.");
            var button = pane.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => Avalonia.Automation.AutomationProperties.GetName(b)?.StartsWith("UI Designer") == true);
            Require(button != null, "The layout's toolbar has no UI Designer button.");
            button!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Require(pane.VisualBuilder is UiDesignerView, "The UI Designer did not open.");
            var view = (UiDesignerView)pane.VisualBuilder!;
            Width = 1800; Height = 1000;
            await Until(() => view.Scene != null && view.Canvas.Bounds.Width > 100, "The designer did not resolve the layout.");
            var scene = view.Scene!;
            Require(scene.Issues.Count == 0, "The synthetic layout has issues: " + string.Join(" | ", scene.Issues));
            Require(scene.Root.Children.Select(c => c.Name).SequenceEqual(["background", "title", "extra"]), "basedOn children were not merged: " + string.Join(", ", scene.Root.Children.Select(c => c.Name)));
            Require(view.TreeRows.Select(r => r.Widget.Name).SequenceEqual(["Derived", "background", "title", "extra"]), "The widget tree does not list the merged widgets.");
            Require(scene.Root.X == 3840 / 2 - 300 && scene.Root.Bounds.Width == 600, "The panel was not placed from its profile rect and anchor.");

            // The panel's sprite is decoded and drawn: the canvas centre (the panel) is the background's colour.
            view.Canvas.Fit();
            await UiBitmaps.Pending; await Until(() => true, "");
            Point ToWindow(double x, double y) => view.Canvas.TranslatePoint(new Point(x * view.Canvas.Zoom + view.Canvas.Pan.X, y * view.Canvas.Zoom + view.Canvas.Pan.Y), this)!.Value;
            var centre = ToWindow(scene.Root.X + 300, scene.Root.Y + 300);
            await Until(() => Pixel(centre) is var p && Math.Abs(p.R - 40) < 12 && Math.Abs(p.G - 90) < 12 && Math.Abs(p.B - 160) < 12, "The panel's sprite was not drawn on the canvas.", () => Pixel(centre).ToString());
            Screenshot("ui-designer.png");

            // Drag the button with the mouse: its rect is rewritten in place and nothing else in the file changes.
            view.Canvas.Snap = false;
            var extra = scene.Find("Derived/extra")!;
            var from = ToWindow(extra.X + 30, extra.Y + 20);
            this.MouseDown(from, MouseButton.Left); this.MouseMove(new Point(from.X + 40, from.Y + 20)); this.MouseMove(new Point(from.X + 80, from.Y + 40)); this.MouseUp(new Point(from.X + 80, from.Y + 40), MouseButton.Left);
            await Until(() => view.Selected?.Name == "extra" && pane.Document.Text != derivedText, "Dragging the button did not select and move it.");
            static UiJsonNode ExtraRect(string text) => UiJsonParser.Parse(text)["children"]!.Items.First(i => i["name"]?.String == "extra")["fields"]!["rect"]!;
            var dragged = ExtraRect(pane.Document.Text);
            double expectX = 100 + Math.Round(80 / view.Canvas.Zoom), expectY = 200 + Math.Round(40 / view.Canvas.Zoom);
            Require(Math.Abs(dragged["x"]!.Number!.Value - expectX) <= 1 && Math.Abs(dragged["y"]!.Number!.Value - expectY) <= 1, $"The drag wrote {pane.Document.Text[dragged.Start..dragged.End]}, expected about {expectX}, {expectY}.");
            Require(pane.Document.Text.Replace(pane.Document.Text[dragged.Start..dragged.End], "{ \"x\": 100, \"y\": 200 }") == derivedText, "Dragging changed more than the rect's numbers.");
            Require(pane.Document.IsDirty, "The drag did not mark the document edited.");
            await Until(() => view.Scene?.Find("Derived/extra")?.X == scene.Root.X + dragged["x"]!.Number, "The canvas did not follow the edit.");

            // The button under the pointer shows its hovered frame, as in game; the State preview shows it everywhere.
            var moved = view.Scene!.Find("Derived/extra")!;
            this.MouseMove(ToWindow(moved.X + 20, moved.Y + 20));
            await Until(() => view.Canvas.Hovered?.Name == "extra" && view.Canvas.FrameFor(view.Canvas.Hovered) == 1, "Hovering a button did not show its hovered frame.");
            this.MouseMove(ToWindow(view.Scene.Root.X + 5, view.Scene.Root.Y + 395));
            await Until(() => view.Canvas.Hovered?.Name != "extra", "Leaving the button did not end its hover.");
            Require(view.Canvas.FrameFor(moved) == 0, "A button away from the pointer did not rest on its first frame.");
            view.Canvas.PreviewState = "hovered";
            Require(view.Canvas.FrameFor(moved) == 1, "The Hovered state preview did not show hovered frames.");
            view.Canvas.PreviewState = "normal";

            // Arrow keys nudge; a burst is one edit.
            var afterDrag = pane.Document.Text; int history = 0;
            view.Canvas.Focus();
            for (int k = 0; k < 3; k++) this.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
            this.KeyPress(Key.Down, RawInputModifiers.Shift, PhysicalKey.ArrowDown, null);
            await Until(() => pane.Document.Text != afterDrag, "Arrow keys did not nudge the selection.");
            var nudged = ExtraRect(pane.Document.Text);
            Require(nudged["x"]!.Number == dragged["x"]!.Number + 3 && nudged["y"]!.Number == dragged["y"]!.Number + 10, "Nudging moved by the wrong amount: " + pane.Document.Text[nudged.Start..nudged.End]);
            pane.Undo(); history++;
            Require(pane.Document.Text == afterDrag, "A burst of nudges was not one undo step.");
            pane.Redo();

            // The inspector writes a value typed into it.
            await Until(() => view.InspectorField("rect.width") is TextBox, "The inspector has no width field.");
            // Field suggestions load in the background; that work must not touch the window (it once read the open tabs off the UI thread).
            try { await UiDesignerView.KnownFieldsLoading; } catch (Exception e) { throw new InvalidDataException("Loading field suggestions failed: " + e.Message, e); }
            var widthBox = (TextBox)view.InspectorField("rect.width")!;
            widthBox.Focus(); widthBox.Text = "120"; this.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await Until(() => ExtraRect(pane.Document.Text)["width"]?.Number == 120, "Typing a width in the inspector did not write it.");
            Require(pane.Document.Text.Contains("\"y\": " + nudged["y"]!.Text + ", \"width\": 120 }"), "The width was not added to the same rect: " + pane.Document.Text);

            // An inherited widget: moving it adds an override entry for it here; the parent file is untouched.
            var parentText = File.ReadAllText(System.IO.Path.Combine(layouts, "testbasehd.json"));
            view.Select(view.Scene!.Find("Derived/title"));
            await Until(() => view.InspectorField("rect.x") is TextBox, "The inspector did not show the inherited title.");
            var xBox = (TextBox)view.InspectorField("rect.x")!;
            xBox.Focus(); xBox.Text = "70"; this.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await Until(() => view.Scene?.Find("Derived/title") is { InDocument: true } t && t.X == view.Scene.Root.X + 70 && t.Bounds.Width == 500, "Moving an inherited widget did not override it here.");
            Require(File.ReadAllText(System.IO.Path.Combine(layouts, "testbasehd.json")) == parentText, "Overriding an inherited widget changed the parent layout.");
            Require(UiJsonParser.Parse(pane.Document.Text)["children"]!.Items.Count(i => i["name"]?.String == "title") == 1 && view.Scene!.Root.Children.Count == 3, "The override did not merge with the inherited title.");

            // Duplicate and delete by keyboard.
            view.Select(view.Scene!.Find("Derived/extra"));
            view.Canvas.Focus();
            this.KeyPress(Key.D, RawInputModifiers.Control, PhysicalKey.D, null);
            await Until(() => view.Scene?.Find("Derived/extra_copy") != null && view.Selected?.Name == "extra_copy", "Ctrl+D did not duplicate the button.");
            view.Canvas.Focus();
            this.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
            await Until(() => view.Scene?.Find("Derived/extra_copy") == null, "Delete did not remove the copy.");
            view.Select(view.Scene!.Find("Derived/background"));
            view.Canvas.Focus();
            // An inherited widget is deleted by leaving it out of this layout's list; the parent layout keeps it.
            this.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
            await Until(() => view.Scene?.Find("Derived/background") == null, "Deleting an inherited widget did not leave it out of this layout.", () => view.StatusText);
            Require(File.ReadAllText(System.IO.Path.Combine(layouts, "testbasehd.json")) == parentText, "Deleting an inherited widget changed the parent layout.");
            pane.Undo();
            await Until(() => view.Scene?.Find("Derived/background") != null, "Undo did not bring the inherited widget back.");

            view.Select(view.Scene!.Find("Derived/extra"));
            view.Canvas.ShowNames = true; view.Canvas.InvalidateVisual();
            Screenshot("ui-designer-edited.png");
            view.Canvas.ShowNames = false;

            // Show in source selects the widget's entry; the designer then follows the caret back.
            view.ShowInSource(view.Scene!.Find("Derived/extra")!);
            await Until(() => pane.Source.IsVisible && pane.Source.SelectedText.Contains("\"name\": \"extra\""), "Show in source did not select the widget's entry.", () => pane.Source.SelectedText);
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Until(() => pane.VisualBuilderVisible && view.Selected?.Name == "extra", "Returning to the designer lost the selection.");

            // A syntax error in the source is shown over the canvas, and Undo brings the drawing back.
            pane.Document.SetRaw(pane.Document.Text.Replace("\"extra\"", "\"extra\" oops")); pane.Document.ApplySource();
            await Until(() => view.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text?.StartsWith("The layout's JSON does not parse") == true), "A JSON error was not shown on the designer.");
            pane.Undo();
            while (pane.Document.CanUndo) pane.Undo();
            await Until(() => pane.Document.Text == derivedText && !pane.Document.IsDirty && view.Scene?.Find("Derived/extra")?.X == scene.Root.X + 100, "Undo did not bring the layout back.");
            await CloseTabAsync(tabs.First(t => t.Content == pane));

            // A stash-like panel keeps several screens in one file; the designer shows one tab's screen at a time.
            Sprite("panel/test/stash_a.sprite", 600, 400, 1, (200, 40, 40));
            Sprite("panel/test/stash_b.sprite", 600, 400, 1, (40, 200, 40));
            Sprite("panel/test/stash_c.sprite", 600, 400, 1, (40, 40, 200));
            Sprite("panel/test/tabs.sprite", 195, 72, 2, (90, 80, 60));
            Write("teststashhd.json", """
                {
                    "type": "BankPanel", "name": "Stash",
                    "fields": { "rect": "$PanelRect", "anchor": "$PanelAnchor", "backgroundFile": [ "PANEL\\Test\\Stash_A", "PANEL\\Test\\Stash_B", "PANEL\\Test\\Stash_C" ] },
                    "children": [
                        { "type": "ImageWidget", "name": "background", "fields": { "filename": "PANEL\\Test\\Stash_A" } },
                        { "type": "TabBarWidget", "name": "BankTabs", "fields": { "rect": { "x": 0, "y": 0 }, "tabCount": 3, "tabSize": { "x": 195, "y": 72 }, "filename": "PANEL\\Test\\Tabs",
                            "activeFrames": [ 1, 1, 1 ], "inactiveFrames": [ 0, 0, 0 ], "textStrings": [ "@personal", "@shared", "@gems" ] } },
                        { "type": "Widget", "name": "basicstash_container", "children": [
                            { "type": "ButtonWidget", "name": "grid", "fields": { "rect": { "x": 20, "y": 330 }, "filename": "PANEL\\Test\\Button" } },
                            { "type": "Widget", "name": "SharedStashTabContainer", "children": [ { "type": "ButtonWidget", "name": "next", "fields": { "rect": { "x": 100, "y": 330 }, "filename": "PANEL\\Test\\Button" } } ] } ] },
                        { "type": "Widget", "name": "advancedstash_gems", "children": [ { "type": "ButtonWidget", "name": "gcw", "fields": { "rect": { "x": 300, "y": 330 }, "filename": "PANEL\\Test\\Button" } } ] },
                    ]
                }
                """);
            var stashPane = (await OpenDocumentAsync(System.IO.Path.Combine(layouts, "teststashhd.json"), true))!;
            stashPane.ShowVisualBuilder();
            var stash = (UiDesignerView)stashPane.VisualBuilder!;
            await Until(() => stash.Scene != null && stash.Canvas.Bounds.Width > 100 && stash.CurrentPage != null, "The stash did not open on a page.");
            bool OnTab(string key) => string.Equals(stash.CurrentPage?.Label, key, StringComparison.OrdinalIgnoreCase);
            // Tab names are localized when the game's strings are there ("Personal"), else shown as their keys.
            Require(stash.PageItems.Select(i => i.Label.ToLowerInvariant()).SequenceEqual(["all pages", "personal", "shared", "gems"]) && OnTab("personal"), "The stash's pages are not its tabs, or it did not open on the first: " + string.Join(", ", stash.PageItems.Select(i => i.Label)));
            var stashScene = stash.Scene!;
            Require(stash.Canvas.IsHidden(stashScene.Find("Stash/advancedstash_gems/gcw")!) && stash.Canvas.IsHidden(stashScene.Find("Stash/basicstash_container/SharedStashTabContainer")!) && !stash.Canvas.IsHidden(stashScene.Find("Stash/basicstash_container/grid")!),
                "The personal tab shows other tabs' widgets.");
            Point StashPoint(double x, double y) => stash.Canvas.TranslatePoint(new Point(x * stash.Canvas.Zoom + stash.Canvas.Pan.X, y * stash.Canvas.Zoom + stash.Canvas.Pan.Y), this)!.Value;
            var inside = StashPoint(stashScene.Root.X + 300, stashScene.Root.Y + 200);
            bool Near(Avalonia.Media.Color c, int r, int g, int b) => Math.Abs(c.R - r) < 14 && Math.Abs(c.G - g) < 14 && Math.Abs(c.B - b) < 14;
            await UiBitmaps.Pending;
            await Until(() => Near(Pixel(inside), 200, 40, 40), "The personal tab's background is not drawn.", () => Pixel(inside).ToString());
            // Clicking a tab on the canvas shows its page, with its background.
            var bar = stashScene.Find("Stash/BankTabs")!;
            var gemsTab = StashPoint(bar.X + 2 * 195 + 97, bar.Y + 36);
            this.MouseDown(gemsTab, MouseButton.Left); this.MouseUp(gemsTab, MouseButton.Left);
            await Until(() => OnTab("gems"), "Clicking the gems tab did not show its page.", () => stash.CurrentPage?.Label ?? "all");
            await UiBitmaps.Pending;
            await Until(() => Near(Pixel(inside), 40, 40, 200) && !stash.Canvas.IsHidden(stash.Scene!.Find("Stash/advancedstash_gems/gcw")!), "The gems tab's background or slots are not drawn.", () => Pixel(inside).ToString());
            Screenshot("ui-designer-stash-pages.png");
            // Picking a widget another tab shows switches to that tab.
            stash.Select(stash.Scene!.Find("Stash/basicstash_container/SharedStashTabContainer/next"), fromTree: true);
            await Until(() => OnTab("shared") && stash.Selected?.Name == "next", "Picking the shared stash's button did not show the shared tab.", () => stash.CurrentPage?.Label ?? "all");
            await UiBitmaps.Pending;
            await Until(() => Near(Pixel(inside), 40, 200, 40), "The shared tab's background is not drawn.", () => Pixel(inside).ToString());
            Require(stash.ShowPage("All pages") && stash.CurrentPage == null && !stash.Canvas.IsHidden(stash.Scene!.Find("Stash/advancedstash_gems/gcw")!), "All pages did not show everything.");
            Require(!stashPane.Document.IsDirty, "Switching pages edited the file.");
            await CloseTabAsync(tabs.First(t => t.Content == stashPane));

            if (GameDataFolders().FirstOrDefault(f => HdAppearance.Locate(f, "data/global/ui/layouts/playerinventoryexpansionlayouthd.json") != null) is { } game)
            {
                // The game's own inventory, with the HUD drawn underneath for context.
                var inventory = (await OpenDocumentAsync(HdAppearance.Locate(game, "data/global/ui/layouts/playerinventoryexpansionlayouthd.json")!, true))!;
                inventory.ShowVisualBuilder();
                var real = (UiDesignerView)inventory.VisualBuilder!;
                await Until(() => real.Scene?.Widgets.Count > 20, "The game's inventory did not resolve.");
                Require(real.Scene!.Issues.Count == 0, "The game's inventory has issues: " + string.Join(" | ", real.Scene.Issues));
                real.AddReference("hudpanelhd.json");
                real.Select(real.Scene!.Find("PlayerInventoryExpansionLayout/grid"));
                real.Canvas.Fit();
                await UiBitmaps.Pending; await Task.Delay(300); await UiBitmaps.Pending; await Until(() => true, "");
                Screenshot("ui-designer-inventory.png");
                real.Canvas.FitPanel();
                await UiBitmaps.Pending; await Until(() => true, "");
                // Hovering the gold button with the grid selected measures the gap between them.
                var gold = real.Scene!.Find("PlayerInventoryExpansionLayout/gold_button")!;
                this.MouseMove(real.Canvas.TranslatePoint(new Point((gold.X + 10) * real.Canvas.Zoom + real.Canvas.Pan.X, (gold.Y + 10) * real.Canvas.Zoom + real.Canvas.Pan.Y), this)!.Value);
                await Until(() => real.Canvas.Hovered?.Name == "gold_button", "Hovering the gold button did not pick it.", () => real.Canvas.Hovered?.Name ?? "nothing");
                await UiBitmaps.Pending; await Until(() => true, "");
                Screenshot("ui-designer-inventory-zoomed.png");
                Require(!inventory.Document.IsDirty, "Looking at the game's inventory edited it.");
                await CloseTabAsync(tabs.First(t => t.Content == inventory));

                // The game's stash, one tab at a time.
                var bank = (await OpenDocumentAsync(HdAppearance.Locate(game, "data/global/ui/layouts/bankexpansionlayouthd.json")!, true))!;
                bank.ShowVisualBuilder();
                var bankView = (UiDesignerView)bank.VisualBuilder!;
                await Until(() => bankView.CurrentPage?.Label == "Personal", "The game's stash did not open on its Personal tab.", () => bankView.CurrentPage?.Label ?? "all");
                foreach (var page in new[] { "Personal", "Gems", "Materials" })
                {
                    Require(bankView.ShowPage(page), "The game's stash has no " + page + " page.");
                    bankView.Canvas.FitPanel();
                    await UiBitmaps.Pending; await Task.Delay(200); await UiBitmaps.Pending; await Until(() => true, "");
                    Screenshot($"ui-designer-stash-{page.ToLowerInvariant()}.png");
                }
                Require(!bank.Document.IsDirty, "Looking at the game's stash edited it.");
                await CloseTabAsync(tabs.First(t => t.Content == bank));
            }
        }
        finally
        {
            Width = width; Height = height;
            foreach (var f in created) if (File.Exists(f)) File.Delete(f);
            if (!hadData && Directory.Exists(data)) Directory.Delete(data, true);
        }
    }

    /// <summary>The colour of one window pixel as it renders now.</summary>
    private Avalonia.Media.Color Pixel(Point at)
    {
        UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
        using var image = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96));
        image.Render(this);
        return Pixel(image, new PixelPoint((int)at.X, (int)at.Y));
    }

    private static Avalonia.Media.Color Pixel(Bitmap image, PixelPoint at)
    {
        var format = image.Format;
        Require(format == PixelFormat.Bgra8888 || format == PixelFormat.Rgba8888,
            "Unsupported smoke bitmap pixel format: " + format);
        var buffer = new byte[4];
        var memory = System.Runtime.InteropServices.Marshal.AllocHGlobal(4);
        try { image.CopyPixels(new PixelRect(at.X, at.Y, 1, 1), memory, 4, 4); System.Runtime.InteropServices.Marshal.Copy(memory, buffer, 0, 4); }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(memory); }
        // CopyPixels preserves the bitmap's native channel order, which differs across platforms.
        return format == PixelFormat.Rgba8888
            ? Avalonia.Media.Color.FromArgb(buffer[3], buffer[0], buffer[1], buffer[2])
            : Avalonia.Media.Color.FromArgb(buffer[3], buffer[2], buffer[1], buffer[0]);
    }

    private static void SmokePixelFormats()
    {
        // Exercise both native channel orders on every OS, including a nonzero pixel offset.
        foreach (var format in new[] { PixelFormat.Bgra8888, PixelFormat.Rgba8888 })
        {
            using var image = new WriteableBitmap(new PixelSize(2, 2), new Vector(96, 96), format, AlphaFormat.Unpremul);
            Require(image.Format == format, "The pixel fixture did not preserve its requested format: " + format);
            using (var frame = image.Lock())
            {
                byte[] bytes = format == PixelFormat.Rgba8888 ? [40, 90, 160, 255] : [160, 90, 40, 255];
                for (int y = 0; y < 2; y++)
                    System.Runtime.InteropServices.Marshal.Copy(new byte[8], 0, frame.Address + y * frame.RowBytes, 8);
                System.Runtime.InteropServices.Marshal.Copy(bytes, 0, frame.Address + frame.RowBytes + 4, 4);
            }
            var actual = Pixel(image, new PixelPoint(1, 1));
            Require(actual == Avalonia.Media.Color.FromRgb(40, 90, 160), $"Pixel sampling misread {format}: {actual}.");
        }
    }
}

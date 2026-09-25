using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
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
            var derivedText = "{\n    \"basedOn\": \"TestBaseHD.json\",\n    \"type\": \"TestPanel\", \"name\": \"Derived\",\n    \"children\": [\n        // An extra button\n        {\n            \"type\": \"ButtonWidget\", \"name\": \"extra\",\n            \"fields\": {\n                \"rect\": { \"x\": 100, \"y\": 200 },\n                \"filename\": \"PANEL\\\\Test\\\\Button\",\n                \"hoveredFrame\": 1,\n            },\n        },\n    ]\n}\n";
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
            var dragged = UiJsonParser.Parse(pane.Document.Text)["children"]!.Items[0]["fields"]!["rect"]!;
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
            var nudged = UiJsonParser.Parse(pane.Document.Text)["children"]!.Items[0]["fields"]!["rect"]!;
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
            await Until(() => UiJsonParser.Parse(pane.Document.Text)["children"]!.Items[0]["fields"]!["rect"]!["width"]?.Number == 120, "Typing a width in the inspector did not write it.");
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
            this.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
            await Until(() => view.StatusText.Contains("cannot remove"), "Deleting an inherited widget was not refused.", () => view.StatusText);
            Require(view.Scene!.Find("Derived/background") != null, "An inherited widget was deleted.");

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
            }
            await CloseTabAsync(tabs.First(t => t.Content == pane));
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
        var buffer = new byte[4];
        var memory = System.Runtime.InteropServices.Marshal.AllocHGlobal(4);
        try { image.CopyPixels(new PixelRect((int)at.X, (int)at.Y, 1, 1), memory, 4, 4); System.Runtime.InteropServices.Marshal.Copy(memory, buffer, 0, 4); }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(memory); }
        // BGRA.
        return Avalonia.Media.Color.FromArgb(buffer[3], buffer[2], buffer[1], buffer[0]);
    }
}

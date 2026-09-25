using ModStudio.Core;
using static ModStudio.Core.Storage;

/// <summary>
/// UI Designer core: the lossless reader of the game's relaxed JSON, minimal text edits, basedOn merging, profile variables,
/// widget geometry, and the edits the designer writes (overrides of inherited widgets, duplicates, deletes, draw order).
/// </summary>
internal static class UiLayoutTests
{
    public static void Run(string root, Action<bool, string> check, Action<Action, string> throws)
    {
        var project = new ModProject(Path.Combine(root, "ui-layouts"), "test", "Test");
        var layouts = Directory.CreateDirectory(Path.Combine(project.Root, "data", "global", "ui", "layouts")).FullName;
        void Write(string name, string text) { var path = Path.Combine(layouts, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text, Utf8); }

        // ── Reader ─────────────────────────────────────────────────────────────────────────────────
        var relaxed = "{\r\n    // comment, \"with quotes\"\r\n    \"a\": 1, /* block */ \"b\": [ 1, 2, ],\r\n    \"s\": \"x\\\\y \\\"q\\\"\",\r\n}\r\n";
        var parsed = UiJsonParser.Parse(relaxed);
        check(parsed["a"]!.Number == 1 && parsed["b"]!.Items.Count == 2 && parsed["s"]!.String == "x\\y \"q\"", "UI JSON reads comments, trailing commas and escapes");
        check(relaxed[parsed["b"]!.Start..parsed["b"]!.End] == "[ 1, 2, ]", "UI JSON nodes remember their exact source span");
        throws(() => UiJsonParser.Parse("{ \"a\": 1 \"b\": 2 }"), "UI JSON rejects a missing comma");
        try { UiJsonParser.Parse("{\n  \"a\": [1,\n}"); check(false, "unreachable"); }
        catch (UiJsonException e) { check(e.Line == 3, "UI JSON errors name their line: " + e.Message); }

        // ── Minimal edits ──────────────────────────────────────────────────────────────────────────
        var multi = "{\r\n    \"x\": 1, // keep me\r\n    \"y\": 2,\r\n}";
        var obj = UiJsonParser.Parse(multi);
        check(UiJsonEditor.Apply(multi, UiJsonEditor.SetMember(multi, obj, "x", "5")) == multi.Replace("\"x\": 1", "\"x\": 5"), "Replacing a value touches only its characters");
        var added = UiJsonEditor.Apply(multi, UiJsonEditor.SetMember(multi, obj, "z", "3"));
        check(added == "{\r\n    \"x\": 1, // keep me\r\n    \"y\": 2,\r\n    \"z\": 3,\r\n}", "A new member follows the object's indentation, line endings and trailing commas: " + added);
        var single = "{ \"type\": \"A\", \"name\": \"b\" }";
        check(UiJsonEditor.Apply(single, UiJsonEditor.SetMember(single, UiJsonParser.Parse(single), "x", "1")) == "{ \"type\": \"A\", \"name\": \"b\", \"x\": 1 }", "A one-line object stays on one line");
        var commented = "{\n  \"a\": 1 // last\n}";
        check(UiJsonEditor.Apply(commented, UiJsonEditor.SetMember(commented, UiJsonParser.Parse(commented), "b", "2")) == "{\n  \"a\": 1, // last\n  \"b\": 2\n}", "A comment after the last member stays on its line");
        var removed = UiJsonEditor.Apply(multi, [UiJsonEditor.RemoveMember(multi, obj, "y")]);
        check(removed == "{\r\n    \"x\": 1, // keep me\r\n}", "Removing a member takes its whole line: " + removed);
        check(UiJsonEditor.Literal(System.Text.Json.Nodes.JsonNode.Parse("{\"x\":1,\"y\":0.5,\"s\":\"a\\\\b\"}")) == "{ \"x\": 1, \"y\": 0.5, \"s\": \"a\\\\b\" }", "Literals are written in the game files' one-line style");

        // ── Resolving: profile, basedOn, geometry ──────────────────────────────────────────────────
        Write("_profilehd.json", """
            {
                // Named globals.
                "RightPanelAnchor": { "x": 1.0, "y": 0.5 },
                "RightPanelRect": { "x": -1200, "y": -700, "width": 1162, "height": 1400 },
                "FontColorGold": { "r": 199, "g": 179, "b": 119, "a": 255 },
                "TitleColor": "$FontColorGold",
                "StyleTitle": { "fontColor": "$TitleColor", "pointSize": 38, "alignment": { "h": "center", "v": "center" } },
            }
            """);
        Write("_profilelv.json", "{ \"basedOn\": \"HD\", \"RightPanelRect\": { \"x\": -1300, \"y\": -800, \"width\": 1162, \"height\": 1400, \"scale\": 1.16 } }");
        Write("basepanelhd.json", """
            {
                "type": "TestPanel", "name": "Base",
                "fields": { "rect": "$RightPanelRect", "anchor": "$RightPanelAnchor" },
                "children": [
                    { "type": "ImageWidget", "name": "background", "fields": { "filename": "PANEL\\Test\\Background" } },
                    { "type": "TextBoxWidget", "name": "title", "fields": { "rect": { "x": 100, "y": 50, "width": 900, "height": 70 }, "style": "$StyleTitle", "text": "@strTitle" } },
                    { "type": "Widget", "name": "group", "fields": { "rect": { "x": 10, "y": 20, "width": 200, "height": 100, "scale": 2 } },
                      "children": [ { "type": "ButtonWidget", "name": "ok", "fields": { "anchor": { "x": 0.5 }, "rect": { "x": 5, "y": 6 }, "filename": "PANEL\\ok" } } ] },
                ]
            }
            """);
        var derivedText = "{\n    \"basedOn\": \"BasePanelHD.json\",\n    \"type\": \"TestPanel\", \"name\": \"Derived\",\n    \"children\": [\n        {\n            \"type\": \"TextBoxWidget\", \"name\": \"title\",\n            \"fields\": {\n                \"text\": \"Hello\",\n            },\n        },\n        {\n            \"type\": \"ImageWidget\", \"name\": \"extra\",\n            \"fields\": { \"rect\": { \"x\": 1, \"y\": 2 } },\n        },\n    ]\n}\n";
        Write("derivedpanelhd.json", derivedText);
        var derivedPath = Path.Combine(layouts, "derivedpanelhd.json");
        var sources = new UiLayoutSources(project, [], derivedPath);
        UiScene Resolve(string text, UiLayoutMode? mode = null) => UiLayout.Resolve(new UiLayoutSources(project, [], derivedPath), derivedPath, text, mode ?? UiLayoutMode.PcHd, UiScreen.All[0], f => f.Contains("Background") ? (1162, 1400) : f.Contains("ok") ? (50, 40) : null);

        check(UiLayoutSources.IsLayout(derivedPath) && !UiLayoutSources.IsLayout(Path.Combine(project.Root, "source", "tables", "x.json")), "Layouts are recognised by their global/ui/layouts folder");
        check(UiLayoutMode.For("derivedpanelhd.json") == UiLayoutMode.PcHd && UiLayoutMode.For("controller/xhd.json") == UiLayoutMode.ControllerHd && UiLayoutMode.For("hudpanel.json") == UiLayoutMode.Sd, "A layout's folder and name pick its default profile");
        var scene = Resolve(derivedText);
        check(scene.Issues.Count == 0, "A based-on layout resolves cleanly: " + string.Join(" | ", scene.Issues));
        check(scene.Root.Name == "Derived" && scene.Root.Children.Select(c => c.Name).SequenceEqual(["background", "title", "group", "extra"]), "basedOn keeps the parent's widgets in order and appends the file's new ones");
        var title = scene.Find("Derived/title")!;
        check(title.String("text") == "Hello" && title.Fields["text"].InDocument && !title.Fields["rect"].InDocument && title.Inherited && title.InDocument, "Merged fields remember which file wrote them");
        check(UiLayout.Color((title.Value("style") as System.Text.Json.Nodes.JsonObject)?["fontColor"]) == (199, 179, 119, 255), "Profile $variables resolve through other variables inside objects");
        check(scene.Root.Fields["rect"].Variable == "$RightPanelRect" && scene.Root.Fields["rect"].VariableEntry?.File.Name == "_profilehd.json", "A field that is a $variable names the profile entry");
        check(scene.Root.X == 3840 - 1200 && scene.Root.Y == 1080 - 700 && scene.Root.Bounds.Width == 1162, "The root sits at its screen anchor plus its rect offset");
        check(title.X == scene.Root.X + 100 && title.Y == scene.Root.Y + 50, "Children are placed from their parent's top left");
        var ok = scene.Find("Derived/group/ok")!; var group = scene.Find("Derived/group")!;
        check(group.Scale == 2 && group.Bounds.Width == 400 && ok.Scale == 2 && ok.X == group.X + (0.5 * 200 + 5) * 2 && ok.Bounds.Width == 100, "Anchors are fractions of the parent; offsets and sizes follow the parent's scale");
        check(scene.Find("Derived/background")!.Bounds.Height == 1400, "A picture with no size of its own takes its sprite's size");
        var large = Resolve(derivedText, UiLayoutMode.PcLargeFont);
        check(large.Root.Scale == 1.16 && large.Root.X == 3840 - 1300 && large.ProfileFiles.Select(f => f.Name).SequenceEqual(["_profilehd.json", "_profilelv.json"]), "The large-font profile is based on HD and overrides its variables");
        var wide = UiLayout.Resolve(sources, derivedPath, derivedText, UiLayoutMode.PcHd, UiScreen.All.First(s => s.Label == "21:9"), null);
        check(wide.Width == 5040 && wide.Root.X == 5040 - 1200, "A wider screen moves right-anchored panels with its edge");
        check(scene.AtOffset(derivedText.IndexOf("\"extra\"", StringComparison.Ordinal))?.Name == "extra", "A source offset finds the widget written there");
        var broken = Resolve(derivedText.Replace("\"Hello\"", "\"$NoSuchVariable\""));
        check(broken.Issues.Any(i => i.Contains("$NoSuchVariable")), "An undefined $variable is reported");

        // ── Designer edits ─────────────────────────────────────────────────────────────────────────
        // Moving a widget this file writes patches its rect members in place.
        var extra = scene.Find("Derived/extra")!;
        var moved = UiLayoutEdit.SetMembers(derivedText, extra, "rect", [("x", 40), ("y", 60)]);
        check(moved == derivedText.Replace("{ \"x\": 1, \"y\": 2 }", "{ \"x\": 40, \"y\": 60 }"), "Moving a widget rewrites only its rect numbers");
        var sized = UiLayoutEdit.SetMembers(derivedText, extra, "rect", [("width", 300), ("height", 20)]);
        check(sized.Contains("{ \"x\": 1, \"y\": 2, \"width\": 300, \"height\": 20 }"), "Resizing adds the missing members to the same rect: " + sized);
        // Moving an inherited widget the file lists without a rect writes the rect into its fields.
        var titleMoved = UiLayoutEdit.SetMembers(derivedText, title, "rect", [("x", 110)]);
        var titleScene = Resolve(titleMoved);
        check(titleScene.Find("Derived/title")!.Fields["rect"].InDocument && titleScene.Find("Derived/title")!.X == scene.Root.X + 110 && titleScene.Find("Derived/title")!.Bounds.Width == 900,
            "Moving an inherited widget writes its whole rect here, keeping the parent file's size");
        check(titleMoved.Contains("\"text\": \"Hello\",\n                \"rect\": { \"x\": 110, \"y\": 50, \"width\": 900, \"height\": 70 },"), "The override goes into the widget's fields in the file's style: " + titleMoved);
        // A widget the file does not list at all gets an entry, and so do its parents.
        var okMoved = UiLayoutEdit.SetMembers(derivedText, ok, "rect", [("y", 9)]);
        var okScene = Resolve(okMoved);
        check(okScene.Issues.Count == 0 && okScene.Find("Derived/group/ok")!.Fields["rect"].InDocument && okScene.Find("Derived/group")!.InDocument && okScene.Find("Derived/group")!.Bounds.Width == 400,
            "Overriding a nested inherited widget adds type-and-name entries for it and its parent");
        check(okScene.Root.Children.Select(c => c.Name).SequenceEqual(["background", "title", "group", "extra"]), "Override entries merge with the parent's widgets rather than adding new ones");
        // The root's rect is a profile variable: moving it writes a literal here, leaving the profile alone.
        var rootMoved = UiLayoutEdit.SetMembers(derivedText, scene.Root, "rect", [("x", -1000)]);
        var rootScene = Resolve(rootMoved);
        check(rootScene.Root.X == 3840 - 1000 && rootScene.Root.Fields["rect"].Variable == null && File.ReadAllText(Path.Combine(layouts, "_profilehd.json")).Contains("\"x\": -1200"), "Moving a panel whose rect is a $variable writes its own rect and leaves the profile unchanged");
        // Removing an override brings the parent's value back.
        var reverted = UiLayoutEdit.RemoveField(titleMoved, titleScene.Find("Derived/title")!, "rect");
        check(Resolve(reverted).Find("Derived/title")!.X == scene.Root.X + 100, "Removing this file's value restores the inherited one");
        // Duplicate, delete, reorder, add.
        var duplicated = UiLayoutEdit.Duplicate(derivedText, extra, UiLayoutEdit.FreeName(scene.Root, "extra_copy"));
        var dupScene = Resolve(duplicated);
        check(dupScene.Root.Children.Select(c => c.Name).SequenceEqual(["background", "title", "group", "extra", "extra_copy"]) && dupScene.Find("Derived/extra_copy")!.X == extra.X, "Duplicating copies a widget beside it under a new name");
        check(duplicated.Contains("        {\n            \"type\": \"ImageWidget\", \"name\": \"extra_copy\",\n            \"fields\": { \"rect\": { \"x\": 1, \"y\": 2 } },\n        },"), "The copy is written with the original's layout: " + duplicated);
        var dupInherited = UiLayoutEdit.Duplicate(derivedText, ok, "ok_copy");
        var dupInheritedScene = Resolve(dupInherited);
        check(dupInheritedScene.Find("Derived/group/ok_copy") is { } okCopy && okCopy.X == ok.X && okCopy.Fields["anchor"].InDocument, "Duplicating an inherited widget writes out its merged fields");
        throws(() => UiLayoutEdit.Delete(derivedText, title), "An inherited widget cannot be deleted");
        check(Resolve(UiLayoutEdit.Delete(duplicated, dupScene.Find("Derived/extra_copy")!)).Root.Children.Count == 4 && UiLayoutEdit.Delete(duplicated, dupScene.Find("Derived/extra_copy")!) == derivedText, "Deleting an added widget removes exactly its text");
        var reordered = UiLayoutEdit.Move(derivedText, extra, -1);
        check(UiJsonParser.Parse(reordered)["children"]!.Items[0]["name"]!.String == "extra", "Draw order swaps a widget with its sibling in this file");
        var withChild = UiLayoutEdit.AddChild(derivedText, extra, "TextBoxWidget", "label", [("text", "\"Hi\"")]);
        check(Resolve(withChild).Find("Derived/extra/label")?.String("text") == "Hi", "A child can be added to a widget with no children yet");
        var renamed = UiLayoutEdit.Rename(derivedText, extra, "bonus");
        check(Resolve(renamed).Find("Derived/bonus") != null, "A widget this file adds can be renamed");
        throws(() => UiLayoutEdit.Rename(derivedText, title, "heading"), "An inherited widget cannot be renamed");
        check(UiLayoutEdit.FreeName(scene.Root, "title") == "title_2", "New names avoid siblings' names");

        // ── Assets ─────────────────────────────────────────────────────────────────────────────────
        var sprites = Directory.CreateDirectory(Path.Combine(project.Root, "data", "hd", "global", "ui", "panel", "test")).FullName;
        // A 195 wide atlas of four 48 pixel frames: frames start at floor(i × 195 / 4).
        var sprite = new byte[40 + 195 * 2 * 4];
        BitConverter.GetBytes(0x31417053).CopyTo(sprite, 0); BitConverter.GetBytes((ushort)31).CopyTo(sprite, 4); BitConverter.GetBytes((ushort)48).CopyTo(sprite, 6);
        BitConverter.GetBytes(195).CopyTo(sprite, 8); BitConverter.GetBytes(2).CopyTo(sprite, 12); BitConverter.GetBytes(4).CopyTo(sprite, 20);
        for (int x = 0; x < 195; x++) for (int y = 0; y < 2; y++) sprite[40 + (y * 195 + x) * 4] = (byte)x;
        File.WriteAllBytes(Path.Combine(sprites, "background.sprite"), sprite);
        var assets = new UiAssets(project, [], derivedPath);
        var info = assets.Sprite("PANEL\\Test\\Background");
        check(info is { FrameWidth: 48, Height: 2, Frames: 4, InProject: true, LowEnd: false }, "UI sprites are found from a layout's filename, case and slashes aside");
        check(UiAssets.Decode(info!, 3).Rgba[0] == 146 && UiAssets.Decode(info!, 2).Rgba[0] == 97, "Frames of an uneven atlas are cut at floor(i × width / frames)");
        File.WriteAllBytes(Path.Combine(sprites, "small.lowend.sprite"), sprite);
        check(assets.Sprite("panel/test/small") is { LowEnd: true, Width: 96 }, "A low-end sprite stands in at twice its size");
        check(assets.Sprite("PANEL\\Test\\Missing") == null && assets.Sprite("$Variable") == null, "Missing sprites and unresolved variables are not found");
        var catalogs = Directory.CreateDirectory(Path.Combine(project.Root, "source", "strings")).FullName;
        File.WriteAllText(Path.Combine(catalogs, "ui.json"), "{ \"schema\": {}, \"records\": [ { \"Key\": \"strTitle\", \"translations\": { \"enUS\": \"ÿc4Inventory\" } } ] }");
        check(new UiAssets(project, [], derivedPath).Localize("strtitle") == "Inventory", "@strings come from the project's catalogs, any case, colour codes dropped");

        // ── Real data (MODSTUDIO_GAME_DATA): every vanilla layout resolves, and a no-op edit round-trips ──
        if (Environment.GetEnvironmentVariable("MODSTUDIO_GAME_DATA") is { Length: > 0 } game && HdAppearance.LocateFolder(game, "data/global/ui/layouts") is { } vanilla)
        {
            int count = 0, sprited = 0, found = 0; var failures = new List<string>(); var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var gameAssets = new UiAssets(null, [game]);
            foreach (var file in Directory.EnumerateFiles(vanilla, "*.json", SearchOption.AllDirectories).Where(f => !Path.GetFileName(f).StartsWith('_')))
            {
                try
                {
                    var text = File.ReadAllText(file);
                    var relative = UiLayoutSources.RelativeName(file);
                    var resolved = UiLayout.Resolve(new UiLayoutSources(null, [game], file), file, text, UiLayoutMode.For(relative), UiScreen.All[0], f => gameAssets.Sprite(f) is { } s ? (s.Width, s.DrawHeight) : null);
                    // Legacy (SD) layouts draw DC6 art, which the designer outlines instead of drawing.
                    if (!resolved.Mode.Legacy)
                        foreach (var w in resolved.Widgets) if (UiLayout.SpriteField(w) is { } f && w.String(f) is { } name && !name.StartsWith('$')) { sprited++; if (gameAssets.Sprite(name) != null) found++; else missing.Add(name); }
                    // A no-op edit on the first widget with a literal rect writes back the same text.
                    if (resolved.Widgets.FirstOrDefault(w => w.Fields.TryGetValue("rect", out var r) && r.InDocument && r.Raw.IsObject && r.Raw.Members.Any(m => m.Key == "x" && m.Value.Kind == UiJsonKind.Number)) is { } literal)
                    {
                        var x = literal.Fields["rect"].Raw["x"]!.Number!.Value;
                        if (UiLayoutEdit.SetMembers(text, literal, "rect", [("x", x)]) != text && literal.Fields["rect"].Raw["x"]!.Text == UiJsonEditor.Number(x)) failures.Add($"{relative}: no-op edit changed the text");
                    }
                    count++;
                }
                catch (Exception e) { failures.Add($"{Path.GetFileName(file)}: {e.Message}"); }
            }
            check(failures.Count == 0, $"Every vanilla layout resolves ({count}): " + string.Join(" | ", failures.Take(5)));
            check(found >= sprited * 0.95, $"Vanilla HD layouts find their sprites ({found} of {sprited}; missing {string.Join(", ", missing.Take(12))})");
        }
    }
}
